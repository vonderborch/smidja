using Afl.Core.Helpers;

namespace Afl.Core.Execution;

/// <summary>
///     The state of one headless turn of an agent that runs a command. An executor derives its own to keep what it
///     learns from the command's output as the turn goes.
/// </summary>
public class ProcessTurn
{
    /// <summary>
    ///     Gets or sets the request being run.
    /// </summary>
    public ExecutionRequest Request { get; set; } = new();

    /// <summary>
    ///     Gets or sets the mode the command runs in.
    /// </summary>
    public AbstractRunMode Mode { get; set; } = new BackgroundRunMode();

    /// <summary>
    ///     Gets or sets a private directory for the turn's files (prompt, answer, MCP configuration), deleted afterwards.
    /// </summary>
    public string ScratchDirectory { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets where live events go, if anywhere.
    /// </summary>
    public IProgress<ExecutorEvent>? Progress { get; set; }

    /// <summary>
    ///     Gets or sets the command to run, set by <see cref="AbstractProcessExecutor{TTurn}.PrepareAsync" />.
    /// </summary>
    public ProcessLaunch Launch { get; set; } = new();
}

/// <summary>
///     An executor whose agent is a command run once per headless turn. The base runs the turn: it makes a private
///     scratch directory, lets the executor build the command (<see cref="PrepareAsync" />), runs it in the request's
///     <see cref="AbstractRunMode" /> (in the background or in a terminal you can watch), hands each output line to
///     <see cref="OnOutputLine" />, then lets the executor read its answer (<see cref="FinishAsync" />). The turn state
///     <typeparamref name="TTurn" /> carries what the executor needs between those steps.
/// </summary>
/// <param name="kind">The agent kind.</param>
/// <typeparam name="TTurn">The executor's turn state.</typeparam>
public abstract class AbstractProcessExecutor<TTurn>(string kind) : AbstractStepExecutor(kind)
    where TTurn : ProcessTurn, new()
{
    /// <inheritdoc />
    public override bool CanRunInTerminal => true;

    /// <inheritdoc />
    public override async Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct)
    {
        var mode = RunModeRegistry.Instance.Resolve(request.RunMode);
        var scratch = CreateScratchDirectory();
        try
        {
            var turn = new TTurn { Request = request, Mode = mode, ScratchDirectory = scratch, Progress = progress };
            await this.PrepareAsync(turn, ct);
            if (mode.IsWatched)
            {
                AppendLog(request, $"(running in {mode.DisplayName.ToLowerInvariant()} mode)");
            }

            var result = await mode.RunAsync(turn.Launch, Path.Combine(scratch, "turn"), this.Terminal, line =>
            {
                AppendLog(request, line);
                this.OnOutputLine(turn, line);
            }, ct);
            return await this.FinishAsync(turn, result, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, true);
            }
            catch (IOException)
            {
                // A leftover temp directory is harmless.
            }
        }
    }

    /// <summary>
    ///     Builds the command for a turn, setting <see cref="ProcessTurn.Launch" />, and writes any files it needs into
    ///     <see cref="ProcessTurn.ScratchDirectory" />.
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected abstract Task PrepareAsync(TTurn turn, CancellationToken ct);

    /// <summary>
    ///     Handles one line of the command's output as it arrives (it's already in the step log).
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="line">The line.</param>
    protected virtual void OnOutputLine(TTurn turn, string line)
    {
    }

    /// <summary>
    ///     Reads the turn's answer once the command has finished.
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="result">The command's result.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The step's result.</returns>
    protected abstract Task<ExecutionResult> FinishAsync(TTurn turn, ProcessResult result, CancellationToken ct);

    /// <summary>
    ///     Creates a temp directory only the current user can read, for a turn's files.
    /// </summary>
    /// <returns>The directory.</returns>
    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "afl-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
