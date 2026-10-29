using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Notifications;
using Afl.Core.Runs;
using Afl.Core.Steps;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for commands that move a run on (start, resume, approve, ...). After the action it prints the run, and when
///     the run stops at an interactive step in a terminal it opens the <c>claude</c> session there and offers to mark
///     the step done afterwards.
/// </summary>
public abstract class AbstractDrivingCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets whether to skip opening interactive sessions.
    /// </summary>
    [Option("no-interactive", HelpText = "Don't open interactive claude sessions; just stop at those steps.")]
    public bool NoInteractive { get; set; }

    /// <summary>
    ///     Gets the run engine, which notifies the user when a run needs them or finishes.
    /// </summary>
    protected RunEngine Engine => new(this.Paths) { Notifier = RunNotifier.ForThisMachine(this.Paths) };

    /// <summary>
    ///     Gets how the run is shown as text once the command has acted.
    /// </summary>
    protected virtual Action<RunView, TextWriter> RenderText => TextRendering.Run;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var engine = this.Engine;
        var run = await this.ActAsync(engine, ct);
        while (this.ShouldOpenInteractive(run))
        {
            var state = run.CurrentStep!;
            var launch = state.Interactive!;
            this.Output.Info($"Opening an interactive claude session for '{state.Name}'. Exit it when you're finished.");
            ProcessRunner.RunInteractive(launch.Executable, launch.Arguments, launch.WorkingDirectory, launch.Environment);
            if (!this.Output.Confirm($"Mark '{state.Name}' done?", true))
            {
                break;
            }

            var note = this.Output.Ask("Note for the step output (optional, enter to skip):");
            var answers = this.AskForAnswers(run, new Dictionary<string, string>());
            run = await engine.CompleteAsync(run.Id, note, answers, this.Output, ct);
        }

        // Acting on a branch of a fork shows the run the branch belongs to.
        while (run is { IsPathRun: true, ParentRunId: { } parent } && engine.Store.Get(parent) is { } owner)
        {
            run = owner;
        }

        this.Output.Result(RunView.From(run, engine.Store), this.RenderText);
        return run.Status == RunStatus.Failed ? ExitRunFailed : ExitSuccess;
    }

    /// <summary>
    ///     Performs the command's action on the run.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    protected abstract Task<Run> ActAsync(RunEngine engine, CancellationToken ct);

    /// <summary>
    ///     Returns whether to open the interactive session for the run's current step now.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>True to open it.</returns>
    protected virtual bool ShouldOpenInteractive(Run run)
    {
        return !this.NoInteractive && this.Output.IsInteractive && !Console.IsOutputRedirected &&
               run.CurrentStep is { Status: StepStatus.AwaitingUser, Interactive: not null };
    }
}

/// <summary>
///     Base for commands that act on an existing run, given by id.
/// </summary>
public abstract class AbstractRunCommand : AbstractDrivingCommand
{
    /// <summary>
    ///     Gets or sets the run id (or a unique prefix of it).
    /// </summary>
    [Value(0, MetaName = "run-id", Required = true, HelpText = "The run id, or a unique prefix or suffix of it (e.g. the last four characters).")]
    public string RunId { get; set; } = string.Empty;
}

/// <summary>
///     Base for commands that act on the step a run is waiting on. While the run waits on a fork, that step may be in
///     one of its branches, and <c>--step</c> says which when several are waiting.
/// </summary>
public abstract class AbstractStepRunCommand : AbstractRunCommand
{
    /// <summary>
    ///     Gets or sets the waiting step to act on.
    /// </summary>
    [Option('s', "step", HelpText = "The waiting step to act on, when a fork has several (see afl run-show).")]
    public string? Step { get; set; }

    /// <summary>
    ///     Returns the id of the run holding the step to act on: the run, or the path run the step is waiting in.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The run id.</returns>
    protected string TargetOf(RunEngine engine)
    {
        return engine.ResolveTarget(this.RunId, this.Step);
    }
}

/// <summary>
///     Base for commands that answer what the current step asks (approving it, marking it done, or answering its
///     questions): from <c>--input</c>, then by asking in a terminal for the rest.
/// </summary>
public abstract class AbstractAnsweringRunCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     Gets or sets the answers, as <c>id=value</c>.
    /// </summary>
    [Option('i', "input",
        HelpText = "Answer a step input: --input id=value (repeatable; value can be @file or -). Missing answers are asked for in a terminal.")]
    public IEnumerable<string> Inputs { get; set; } = [];

    /// <summary>
    ///     Collects the answers for the run's current step.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The answers, by input id.</returns>
    /// <exception cref="AflException">An <c>--input</c> isn't <c>id=value</c>.</exception>
    protected Dictionary<string, string> CollectAnswers(RunEngine engine)
    {
        var given = ParsePairs(this.Inputs, "--input");
        return this.AskForAnswers(engine.Store.GetRequired(this.TargetOf(engine)), given);
    }
}
