using Afl.Cli.Output;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Starts a workflow run on a ticket.
/// </summary>
[Verb("start", HelpText = "Start a workflow on a ticket: afl start <#123 | owner/repo#123 | PROJ-45 | ticket URL>, or afl start --name <name> for work with no ticket.")]
public class StartCommand : AbstractDrivingCommand
{
    /// <summary>
    ///     Gets or sets the ticket reference.
    /// </summary>
    [Value(0, MetaName = "ticket",
        HelpText = "The ticket: 123, #123, owner/repo#123, group/project#123, PROJ-45, a GitHub, GitLab or Jira URL, or local:<name> for work with no ticket.")]
    public string? Ticket { get; set; }

    /// <summary>
    ///     Gets or sets a name for work with no ticket.
    /// </summary>
    [Option('n', "name", HelpText = "Start work with no ticket under this name (the same as local:<name>).")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets whether to do one thing at a time: the same as <c>--max-parallel 1</c>.
    /// </summary>
    [Option("sequential", HelpText = "Run parallel paths and child runs one at a time (the same as --max-parallel 1).")]
    public bool Sequential { get; set; }

    /// <summary>
    ///     Gets or sets how many parallel paths or child runs may run at once.
    /// </summary>
    [Option("max-parallel", HelpText = "The most parallel paths or child runs that run at once.")]
    public int? MaxParallel { get; set; }

    /// <summary>
    ///     Gets or sets the workflow id.
    /// </summary>
    [Option('w', "workflow", HelpText = "The workflow to run (defaults to the repo's default workflow).")]
    public string? Workflow { get; set; }

    /// <summary>
    ///     Gets or sets the agent.
    /// </summary>
    [Option('a', "agent",
        HelpText = "Who does the AI work for steps that don't pick their own: claude (headless claude -p, the default), " +
                   "session (the calling Claude session), or any agent from afl agent list.")]
    public string? Agent { get; set; }

    /// <summary>
    ///     Gets or sets the agent under its old option name.
    /// </summary>
    [Option('e', "executor", Hidden = true, HelpText = "Same as --agent.")]
    public string? Executor { get; set; }

    /// <summary>
    ///     Gets or sets how agents' commands run.
    /// </summary>
    [Option('m', "mode",
        HelpText = "How agents that run a command run it: background (the default, see afl settings-show) or terminal " +
                   "(a window you can watch). Session and MCP agents ignore it.")]
    public string? Mode { get; set; }

    /// <summary>
    ///     Gets or sets the workflow's parameters, as <c>id=value</c>.
    /// </summary>
    [Option('p', "param", HelpText = "A value for one of the workflow's parameters: --param id=value (repeatable; value can be @file or -).")]
    public IEnumerable<string> Parameters { get; set; } = [];

    /// <summary>
    ///     Gets or sets the optional steps to include, by step id.
    /// </summary>
    [Option("include", Separator = ',',
        HelpText = "Include an optional step the workflow leaves out by default: --include <step id> (repeatable or comma-separated; see afl workflow-show).")]
    public IEnumerable<string> Include { get; set; } = [];

    /// <summary>
    ///     Gets or sets the optional steps to leave out, by step id.
    /// </summary>
    [Option("exclude", Separator = ',',
        HelpText = "Leave out an optional step the workflow includes by default: --exclude <step id> (repeatable or comma-separated).")]
    public IEnumerable<string> Exclude { get; set; } = [];

    /// <summary>
    ///     Gets or sets the permission policy.
    /// </summary>
    [Option("permissions",
        HelpText = "How agents get permission to run commands: ask (pause and ask when one is refused something), strict " +
                   "(note it and carry on) or auto (run anything except the guard rails; needs --yes-auto without a " +
                   "terminal). Defaults to the default-permissions setting.")]
    public string? Permissions { get; set; }

    /// <summary>
    ///     Gets or sets whether the caller has accepted the auto policy's warning.
    /// </summary>
    [Option("yes-auto", HelpText = "Accept the warning for --permissions auto without being asked.")]
    public bool YesAuto { get; set; }

    /// <summary>
    ///     Gets or sets the skills to use instead of the workflow's, comma-separated; empty for none.
    /// </summary>
    [Option("skills",
        HelpText = "The skills to give the run's agents instead of the workflow's, with commas between them (--skills \"\" for " +
                   "none; see afl skill-recommend). Steps' own skills and skipSkills still apply.")]
    public string? Skills { get; set; }

    /// <summary>
    ///     Gets or sets skills to add to the workflow's (or to <see cref="Skills" />).
    /// </summary>
    [Option("add-skills", Separator = ',', HelpText = "Skills to add to the workflow's (or to --skills), with commas between them.")]
    public IEnumerable<string> AddSkills { get; set; } = [];

    /// <summary>
    ///     Gets or sets skills to take out of the workflow's (or out of <see cref="Skills" />).
    /// </summary>
    [Option("drop-skills", Separator = ',', HelpText = "Skills to take out of the workflow's (or out of --skills), with commas between them.")]
    public IEnumerable<string> DropSkills { get; set; } = [];

    /// <inheritdoc />
    protected override async Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        this.ConfirmPermissions();
        var parameters = ParsePairs(this.Parameters, "--param");
        var reference = this.TicketReference();
        var repo = this.RequireRepoFor(reference);
        var workflowId = this.Workflow ?? repo.DefaultWorkflowId ?? Core.Repos.RepoRegistry.InitialDefaultWorkflowId;
        var workflow = new WorkflowLibrary(this.Paths, repo.Path).GetRequired(workflowId);
        var ticket = await TicketSourceRegistry.Instance.FetchAsync(repo, reference, ct);
        var run = engine.Create(repo, workflow, ticket, this.Agent ?? this.Executor ?? engine.Settings.DefaultAgent(), this.Mode,
            parameters, this.Sequential ? 1 : this.MaxParallel, this.OptionalSteps(), this.Permissions,
            SkillRecommender.Pick(workflow, this.Skills?.Split(','), this.AddSkills, this.DropSkills));
        this.Output.Info($"Started run {run.Id}: {workflow.Name} on {ticket.Key} ({ticket.Title}).");
        if (run.LeftOutSteps.Count > 0)
        {
            this.Output.Info($"Leaving out: {string.Join(", ", run.LeftOutSteps)}.");
        }
        return await engine.DriveAsync(run.Id, this.Output, ct);
    }

    /// <summary>
    ///     Checks <see cref="Permissions" /> and, for a policy with a warning, shows it and makes sure the caller accepts
    ///     it: with <see cref="YesAuto" />, or by answering yes in a terminal.
    /// </summary>
    /// <exception cref="AflException">The policy is unknown, or its warning wasn't accepted.</exception>
    private void ConfirmPermissions()
    {
        if (this.Permissions is null)
        {
            return;
        }

        var policy = PermissionPolicyRegistry.Instance.Require(this.Permissions.Trim());
        this.Permissions = policy.Name;
        if (policy.Warning is not { } warning)
        {
            return;
        }

        this.Output.Info(warning);
        if (this.YesAuto)
        {
            return;
        }

        if (!this.Output.IsInteractive || Console.IsInputRedirected)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"--permissions {policy.Name} needs --yes-auto when afl can't ask you.");
        }

        if (!this.Output.Confirm($"Start with {policy.DisplayName}?", false))
        {
            throw new AflException(AflErrorKind.InvalidInput, "Not started.");
        }
    }

    /// <summary>
    ///     Returns the optional-step choices from <see cref="Include" /> and <see cref="Exclude" />.
    /// </summary>
    /// <returns>Whether to include each step named, by step id.</returns>
    /// <exception cref="AflException">A step is both included and excluded.</exception>
    private Dictionary<string, bool> OptionalSteps()
    {
        var include = this.Include.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        var exclude = this.Exclude.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        var both = include.Intersect(exclude).ToList();
        if (both.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"Step {string.Join(", ", both.Select(b => $"'{b}'"))} is in both --include and --exclude.");
        }

        var choices = include.Distinct().ToDictionary(i => i, _ => true);
        foreach (var step in exclude.Distinct())
        {
            choices[step] = false;
        }

        return choices;
    }

    /// <summary>
    ///     Returns the ticket reference to start from: the ticket, or a local one for <see cref="Name" />.
    /// </summary>
    /// <returns>The reference.</returns>
    /// <exception cref="AflException">Neither or both were given.</exception>
    private string TicketReference()
    {
        var hasTicket = !string.IsNullOrWhiteSpace(this.Ticket);
        var hasName = !string.IsNullOrWhiteSpace(this.Name);
        if (hasTicket == hasName)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                "Give a ticket (e.g. 123 or PROJ-45) or, for work with no ticket, --name \"short name\" - not both.");
        }

        return hasTicket ? this.Ticket!.Trim() : TicketRef.ForLocal(this.Name!);
    }
}

/// <summary>
///     Lists runs.
/// </summary>
[Verb("runs", HelpText = "List runs in this repo (or every repo with --all-repos).")]
public class RunsCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets whether to list runs in every repo.
    /// </summary>
    [Option('a', "all-repos", HelpText = "List runs in every registered repo.")]
    public bool AllRepos { get; set; }

    /// <summary>
    ///     Gets or sets whether to include finished runs.
    /// </summary>
    [Option("finished", HelpText = "Include done and cancelled runs.")]
    public bool IncludeFinished { get; set; }

    /// <summary>
    ///     Gets or sets the tags a run must have, every one.
    /// </summary>
    [Option("tag", Separator = ',', HelpText = "Only runs with these tags (every one), with commas between them.")]
    public IEnumerable<string> Tags { get; set; } = [];

    /// <summary>
    ///     Gets or sets the folder runs must be filed in.
    /// </summary>
    [Option("folder", HelpText = "Only runs filed in this folder or the folders in it (and the runs they started).")]
    public string? Folder { get; set; }

    /// <summary>
    ///     Gets or sets the states a run must be in one of.
    /// </summary>
    [Option("status", Separator = ',',
        HelpText = "Only runs in these states, with commas between them: needs-you, running, waiting, failed, done, cancelled. "
                   + "Naming done or cancelled lists finished runs without --finished.")]
    public IEnumerable<string> States { get; set; } = [];

    /// <summary>
    ///     Gets or sets the search.
    /// </summary>
    [Option("search", HelpText = "Only runs with every word of this in their id, title, ticket, tags, folder, workflow, repo, agent or state.")]
    public string? Search { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repoId = this.AllRepos ? null : this.RequireRepo().Id;
        var filter = new RunFilter { Search = this.Search, States = RunStates.Parse(this.States), Tags = RunLabels.NormalizeTags(this.Tags) };
        var finished = this.IncludeFinished || filter.States.Any(RunStates.IsFinished);
        var folder = RunLabels.NormalizeFolder(this.Folder);
        var all = new RunStore(this.Paths).List(repoId);
        var byId = all.ToDictionary(r => r.Id);

        // Child runs are filed with the run that started them.
        Run TopOf(Run run)
        {
            var seen = new HashSet<string>();
            while (run.ParentRunId is { } parentId && seen.Add(parentId) && byId.TryGetValue(parentId, out var parent))
            {
                run = parent;
            }

            return run;
        }

        var runs = all
            .Where(r => !r.IsPathRun && (finished || !r.IsFinished) && filter.Matches(r))
            .Where(r => folder is null || RunLabels.IsWithin(TopOf(r).Folder, folder))
            .Select(RunView.From)
            .ToList();
        this.Output.Result(runs, TextRendering.RunList);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Shows a run.
/// </summary>
[Verb("run-show", HelpText = "Show a run and what it's waiting on.")]
public class RunShowCommand : AbstractRunCommand
{
    /// <inheritdoc />
    protected override bool ShouldOpenInteractive(Run run)
    {
        return false;
    }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return Task.FromResult(engine.Store.GetRequired(this.RunId));
    }
}

/// <summary>
///     Continues a run: starts pending steps, or reopens the current interactive session.
/// </summary>
[Verb("run-resume", HelpText = "Continue a run (reopens the interactive session if the step has one).")]
public class RunResumeCommand : AbstractRunCommand
{
    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return engine.DriveAsync(this.RunId, this.Output, ct);
    }
}

/// <summary>
///     Approves the AI's output for the current step.
/// </summary>
[Verb("run-approve", HelpText = "Approve the AI's output for the current step and continue.")]
public class RunApproveCommand : AbstractAnsweringRunCommand
{
    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return engine.ApproveAsync(this.TargetOf(engine), this.CollectAnswers(engine), this.Output, ct);
    }
}

/// <summary>
///     Answers what the current step is asking: its <c>before</c> inputs, or the AI's questions.
/// </summary>
[Verb("run-answer", HelpText = "Answer what the current step is asking (its inputs before it starts, or the AI's questions) and continue.")]
public class RunAnswerCommand : AbstractAnsweringRunCommand
{
    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return engine.AnswerAsync(this.TargetOf(engine), this.CollectAnswers(engine), this.Output, ct);
    }
}

/// <summary>
///     Rejects the current step, which is waiting for approval.
/// </summary>
[Verb("run-reject", HelpText = "Send the step waiting for approval back with feedback, or with answers to its open questions; it's redone with them.")]
public class RunRejectCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     Gets or sets the feedback; with answers, a note added after them.
    /// </summary>
    [Option('f', "feedback", HelpText = "What to change (text, @file, or - for stdin). Needed unless --answer is given.")]
    public string? Feedback { get; set; }

    /// <summary>
    ///     Gets or sets the answers to the step's open questions, as <c>number=answer</c>.
    /// </summary>
    [Option('a', "answer",
        HelpText = "Answer one of the step's open questions (see openQuestions): --answer 2=\"use 1.x\" (repeatable; value can be @file or -). Unanswered ones keep the AI's assumption.")]
    public IEnumerable<string> Answers { get; set; } = [];

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        var feedback = this.Feedback is null ? null : ReadTextArgument(this.Feedback);
        var pairs = ParsePairs(this.Answers, "--answer");
        if (pairs.Count == 0)
        {
            return engine.RejectAsync(this.TargetOf(engine), feedback ?? string.Empty, this.Output, ct);
        }

        var answers = new Dictionary<int, string>();
        foreach (var (key, value) in pairs)
        {
            answers[int.TryParse(key.TrimStart('#'), out var number) && number > 0
                ? number
                : throw new AflException(AflErrorKind.InvalidInput, $"--answer '{key}' should start with a question number.")] = value;
        }

        return engine.AnswerQuestionsAsync(this.TargetOf(engine), answers, feedback, this.Output, ct);
    }
}

/// <summary>
///     Replays a step that has run: shows its output, then redoes it (with feedback, if given) and every later step.
/// </summary>
[Verb("run-replay", HelpText = "Redo a step that has run (and every step after it), optionally with feedback.")]
public class RunReplayCommand : AbstractRunCommand
{
    /// <summary>
    ///     Gets or sets the id of the step to replay.
    /// </summary>
    [Value(1, MetaName = "step", Required = true, HelpText = "The id of the step to replay (see afl run show).")]
    public string Step { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the feedback.
    /// </summary>
    [Option('f', "feedback", HelpText = "What to change (text, @file, or - for stdin). Without it the step starts again from scratch.")]
    public string? Feedback { get; set; }

    /// <summary>
    ///     Gets or sets whether to skip showing the output and confirming.
    /// </summary>
    [Option('y', "yes", HelpText = "Don't show the step's output or ask before replaying.")]
    public bool Yes { get; set; }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        var feedback = this.Feedback is null ? null : ReadTextArgument(this.Feedback);
        var run = engine.Store.GetRequired(this.RunId);
        if (!this.Yes && this.Output.IsInteractive && run.Steps.FirstOrDefault(s => s.StepId == this.Step) is { } state)
        {
            this.Output.Result(StepView.From(run, state), TextRendering.Step);
            var later = run.Steps.SkipWhile(s => s != state).Skip(1).Count(s => s.HasRun);
            var redoes = later > 0 ? $" The {later} step(s) after it will be redone too." : string.Empty;
            if (!this.Output.Confirm($"Replay '{state.Name}'?{redoes}", true))
            {
                this.Output.Info("Not replayed.");
                return Task.FromResult(run);
            }

            feedback ??= this.Output.Ask("Feedback for the redo (optional, enter to start it again from scratch):");
        }

        return engine.ReplayAsync(run.Id, this.Step, feedback, this.Output, ct);
    }
}

/// <summary>
///     Shows one step of a run in full.
/// </summary>
[Verb("run-step", HelpText = "Show one step of a run in full: its output, answers, feedback and earlier attempts.")]
public class RunStepCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the run id.
    /// </summary>
    [Value(0, MetaName = "run-id", Required = true, HelpText = "The run id, or a unique prefix or suffix of it.")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step id.
    /// </summary>
    [Value(1, MetaName = "step", Required = true, HelpText = "The id of the step to show.")]
    public string Step { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var run = new RunStore(this.Paths).GetRequired(this.RunId);
        var state = run.Steps.FirstOrDefault(s => s.StepId == this.Step) ?? throw new AflException(AflErrorKind.NotFound,
            $"Run {run.Id} has no step '{this.Step}'. Steps: {string.Join(", ", run.Steps.Select(s => s.StepId))}.");
        this.Output.Result(StepView.From(run, state), TextRendering.Step);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Marks the current user or interactive step done.
/// </summary>
[Verb("run-done", HelpText = "Mark the current user or interactive step done and continue.")]
public class RunDoneCommand : AbstractAnsweringRunCommand
{
    /// <summary>
    ///     Gets or sets the note.
    /// </summary>
    [Option('n', "note", HelpText = "What was done; becomes the step output (text, @file, or -).")]
    public string? Note { get; set; }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        var note = this.Note is null ? null : ReadTextArgument(this.Note);
        return engine.CompleteAsync(this.TargetOf(engine), note, this.CollectAnswers(engine), this.Output, ct);
    }
}

/// <summary>
///     Gives the current step the calling Claude session's output.
/// </summary>
[Verb("run-submit", HelpText = "Submit the output for a step done by the calling Claude session.")]
public class RunSubmitCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     Gets or sets the output.
    /// </summary>
    [Option('o', "output", Required = true, HelpText = "The step output (text, @file, or - for stdin).")]
    public string StepOutput { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the agent whose result this is, for a step several agents do.
    /// </summary>
    [Option('a', "agent", HelpText = "For a step several agents do: the session agent whose result this is (the first one waiting when unset).")]
    public string? Agent { get; set; }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return engine.SubmitAsync(this.TargetOf(engine), ReadTextArgument(this.StepOutput), this.Output, ct, this.Agent);
    }
}

/// <summary>
///     Fails the current step, which waits for the calling Claude session or a person, because it can't be done.
/// </summary>
[Verb("run-fail", HelpText = "Fail the step waiting for the calling Claude session or a person, saying why; its failure arrows are followed.")]
public class RunFailCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     Gets or sets why it failed.
    /// </summary>
    [Option('r', "reason", Required = true, HelpText = "Why the step can't be done (text, @file, or - for stdin).")]
    public string Reason { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override bool ShouldOpenInteractive(Run run)
    {
        return false;
    }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return engine.GiveUpAsync(this.TargetOf(engine), ReadTextArgument(this.Reason), this.Output, ct);
    }
}

/// <summary>
///     Uses one agent's result as the output of a step several agents did.
/// </summary>
[Verb("run-choose", HelpText = "Use one agent's result for the step waiting for a choice (optionally edited).")]
public class RunChooseCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     Gets or sets the agent whose result to use.
    /// </summary>
    [Value(1, MetaName = "agent", Required = true, HelpText = "The agent whose result to use (see afl run show).")]
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the edited output.
    /// </summary>
    [Option('o', "output", HelpText = "An edited version of the result to use instead (text, @file, or - for stdin).")]
    public string? StepOutput { get; set; }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        var output = this.StepOutput is null ? null : ReadTextArgument(this.StepOutput);
        return engine.ChooseAsync(this.TargetOf(engine), this.Agent, output, this.Output, ct);
    }
}

/// <summary>
///     Has an agent merge the results of a step several agents did.
/// </summary>
[Verb("run-merge", HelpText = "Have an agent merge the results of the step waiting for a choice.")]
public class RunMergeCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     Gets or sets the agent that merges.
    /// </summary>
    [Option('a', "agent", HelpText = "The agent that merges (the step's mergeAgent, else its first agent, when unset).")]
    public string? Agent { get; set; }

    /// <summary>
    ///     Gets or sets the note for the merging agent.
    /// </summary>
    [Option('n', "note", HelpText = "What to keep in mind when merging (text, @file, or - for stdin).")]
    public string? Note { get; set; }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        var note = this.Note is null ? null : ReadTextArgument(this.Note);
        return engine.MergeAsync(this.TargetOf(engine), this.Agent, note, this.Output, ct);
    }
}

/// <summary>
///     Picks which agent does a step: the one waiting for its agent to be chosen, or one that hasn't started.
/// </summary>
[Verb("run-use-agent", HelpText = "Pick the agent for the step choosing its agent (or for a step that hasn't started).")]
public class RunUseAgentCommand : AbstractRunCommand
{
    /// <summary>
    ///     Gets or sets the agent.
    /// </summary>
    [Value(1, MetaName = "agent", Required = true, HelpText = "The agent to use (see the step's candidates in afl run-show).")]
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step.
    /// </summary>
    [Option('s', "step", HelpText = "The step (the current one when unset).")]
    public string? Step { get; set; }

    /// <summary>
    ///     Gets or sets why.
    /// </summary>
    [Option('r', "reason", HelpText = "Why this agent, in a sentence.")]
    public string? Reason { get; set; }

    /// <summary>
    ///     Gets or sets who chose.
    /// </summary>
    [Option("by", Default = AgentConstants.ChosenByUser, HelpText = "Who chose: user or main-agent.")]
    public string By { get; set; } = AgentConstants.ChosenByUser;

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return engine.UseAgentAsync(this.RunId, this.Agent, this.Step, this.Reason, this.By, this.Output, ct);
    }
}

/// <summary>
///     Retries a failed step.
/// </summary>
[Verb("run-retry", HelpText = "Retry the current step after it failed.")]
public class RunRetryCommand : AbstractStepRunCommand
{
    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return engine.RetryAsync(this.TargetOf(engine), this.Output, ct);
    }
}

/// <summary>
///     Cancels a run.
/// </summary>
[Verb("run-cancel", HelpText = "Cancel a run.")]
public class RunCancelCommand : AbstractRunCommand
{
    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return Task.FromResult(engine.Cancel(this.RunId));
    }
}

/// <summary>
///     Deletes a finished run, with its logs and the child runs it started.
/// </summary>
[Verb("run-delete", HelpText = "Delete a finished run, its logs and the child runs it started (cancel it first if it's still going).")]
public class RunDeleteCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the run id.
    /// </summary>
    [Value(0, MetaName = "run-id", Required = true, HelpText = "The run id, or a unique prefix or suffix of it.")]
    public string RunId { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var deleted = new RunEngine(this.Paths).Delete(this.RunId);
        this.Output.Result(new { deleted }, (r, w) => w.WriteLine($"Deleted {string.Join(", ", r.deleted)}."));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Adds a note to the current step.
/// </summary>
[Verb("run-note", HelpText = "Add a note to the run's current step (e.g. a handoff summary).")]
public class RunNoteCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     Gets or sets the note.
    /// </summary>
    [Value(1, MetaName = "note", Required = true, HelpText = "The note (text, @file, or - for stdin).")]
    public string Note { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override bool ShouldOpenInteractive(Run run)
    {
        return false;
    }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return Task.FromResult(engine.AddNote(this.TargetOf(engine), ReadTextArgument(this.Note)));
    }
}
