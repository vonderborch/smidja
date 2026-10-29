using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Cli.Output;

/// <summary>
///     The view of a run the CLI prints: the run, its current step in full, and the commands that move it on. This is
///     the contract the plugin skill reads from <c>--json</c>.
/// </summary>
public class RunView
{
    /// <summary>
    ///     Gets or sets the run id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the run status.
    /// </summary>
    public RunStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets what the run is doing, as lists show it: its status, with waiting on a person split out.
    /// </summary>
    public RunState State { get; set; }

    /// <summary>
    ///     Gets or sets the repo name.
    /// </summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the workflow id.
    /// </summary>
    public string Workflow { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the run's default agent (kept under its old name for scripts that read it).
    /// </summary>
    public string Executor { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the id of the run's default agent.
    /// </summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets how the run's agents' commands run: <c>background</c> or <c>terminal</c>.
    /// </summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the name the user gave the run, or null when it's shown with its ticket's title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    ///     Gets or sets the run's tags.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    ///     Gets or sets the folder the run is filed in, or null for the top level.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    ///     Gets or sets the ticket key.
    /// </summary>
    public string TicketKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the ticket title.
    /// </summary>
    public string TicketTitle { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the ticket URL.
    /// </summary>
    public string? TicketUrl { get; set; }

    /// <summary>
    ///     Gets or sets the id of the run that started this one from a <c>workflow</c> step, or null for a top-level run.
    ///     Once this run is done, carry on with that one.
    /// </summary>
    public string? ParentRunId { get; set; }

    /// <summary>
    ///     Gets or sets the step of the parent run that started this one.
    /// </summary>
    public string? ParentStepId { get; set; }

    /// <summary>
    ///     Gets or sets the run's scratch directory, relative to the repo, once something has been put there.
    /// </summary>
    public string? ScratchDirectory { get; set; }

    /// <summary>
    ///     Gets or sets the checkout the run's agents work in when it isn't the repo itself (e.g. a git worktree).
    /// </summary>
    public string? WorkingPath { get; set; }

    /// <summary>
    ///     Gets or sets the run's own workspace, if its parent's step gave it one.
    /// </summary>
    public string? Workspace { get; set; }

    /// <summary>
    ///     Gets or sets the branch of the run's own workspace, if it has one.
    /// </summary>
    public string? WorkspaceBranch { get; set; }

    /// <summary>
    ///     Gets or sets the most parallel paths or child runs the run has going at once, or null for no limit.
    /// </summary>
    public int? MaxParallel { get; set; }

    /// <summary>
    ///     Gets or sets the optional steps left out when the run started.
    /// </summary>
    public List<string> LeftOutSteps { get; set; } = [];

    /// <summary>
    ///     Gets or sets the permission policy the run's agents work under: <c>ask</c>, <c>strict</c> or <c>auto</c>.
    /// </summary>
    public string PermissionPolicy { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the permission rules saved for this run only.
    /// </summary>
    public List<PermissionRule> PermissionRules { get; set; } = [];

    /// <summary>
    ///     Gets or sets the skills picked when the run started, or null when it uses its workflow's.
    /// </summary>
    public List<string>? Skills { get; set; }

    /// <summary>
    ///     Gets or sets the current step, or null when the run is done.
    /// </summary>
    public StepView? CurrentStep { get; set; }

    /// <summary>
    ///     Gets or sets every step's id, name and status.
    /// </summary>
    public List<StepSummaryView> Steps { get; set; } = [];

    /// <summary>
    ///     Gets or sets every step the run has been to, in order, with which time round it was; loops and routes make it
    ///     differ from the step list.
    /// </summary>
    public List<StepVisit> Path { get; set; } = [];

    /// <summary>
    ///     Gets or sets the path runs the run is waiting on, if it is.
    /// </summary>
    public List<ChildRun> Paths { get; set; } = [];

    /// <summary>
    ///     Gets or sets the steps its path runs are waiting on; act on one with <c>--step</c>.
    /// </summary>
    public List<StepView> PathSteps { get; set; } = [];

    /// <summary>
    ///     Gets or sets the commands that move the run on from here.
    /// </summary>
    public List<string> Next { get; set; } = [];

    /// <summary>
    ///     Gets or sets when the run was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Builds the view of a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The view.</returns>
    public static RunView From(Run run)
    {
        return From(run, null);
    }

    /// <summary>
    ///     Builds the view of a run, with the steps its fork's branches are waiting on when a store is given.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="store">The store to read path runs from, or null to leave them out.</param>
    /// <returns>The view.</returns>
    public static RunView From(Run run, RunStore? store)
    {
        var view = new RunView
        {
            Id = run.Id,
            Status = run.Status,
            State = run.State,
            Repo = run.RepoName,
            Workflow = run.WorkflowId,
            Executor = run.Executor,
            Agent = run.Executor,
            Mode = RunModeRegistry.Instance.Resolve(run.Mode).Name,
            Title = run.Title,
            Tags = run.Tags,
            Folder = run.Folder,
            TicketKey = run.Ticket.Key,
            TicketTitle = run.Ticket.Title,
            TicketUrl = run.Ticket.Url,
            CreatedAt = run.CreatedAt,
            Steps = run.Steps.Select(s => StepSummaryView.From(run, s)).ToList(),
            Path = run.Path,
            ParentRunId = run.ParentRunId,
            ParentStepId = run.ParentStepId,
            ScratchDirectory = run.ScratchDirectory is { } scratch && Directory.Exists(System.IO.Path.Combine(run.RepoPath, scratch))
                ? scratch
                : null,
            WorkingPath = run.WorkingPath,
            Workspace = run.Workspace,
            WorkspaceBranch = run.WorkspaceBranch,
            MaxParallel = run.MaxParallel,
            LeftOutSteps = run.LeftOutSteps,
            PermissionPolicy = PermissionPolicyRegistry.Instance.Resolve(run.PermissionPolicy).Name,
            PermissionRules = run.PermissionRules,
            Skills = run.Skills,
        };

        if (run.CurrentStep is { } state && run.Status != RunStatus.Cancelled)
        {
            view.CurrentStep = StepView.From(run, state);
            view.Next = NextCommands(run.Id, view.CurrentStep, null);
            if (state is { Status: StepStatus.AwaitingPaths, Paths: { } branches })
            {
                view.Paths = branches;
                foreach (var (holder, step) in store?.WaitingSteps(run) ?? [])
                {
                    var branchStep = StepView.From(holder, step);
                    view.PathSteps.Add(branchStep);
                    view.Next.AddRange(NextCommands(run.Id, branchStep, step.StepId));
                }
            }
        }

        if (run.Steps.Any(s => s.HasRun))
        {
            view.Next.Add($"afl run step {run.Id} <step>   (shows a step's output)");
            view.Next.Add($"afl run replay {run.Id} <step> [--feedback \"...\"]");
        }

        return view;
    }

    /// <summary>
    ///     Lists the commands that move a run on from its current step's state.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <param name="step">The current step.</param>
    /// <param name="stepId">The step to name with <c>--step</c>, for a step in one of a fork's branches.</param>
    /// <returns>The commands.</returns>
    private static List<string> NextCommands(string runId, StepView step, string? stepId)
    {
        if (stepId is not null)
        {
            runId = $"{runId} --step {stepId}";
        }

        // Inputs that can be left out (optional, or answered by their default) are shown in brackets.
        var answers = step.AsksInputs
            ? string.Concat(step.Inputs.Select(i => i.Required && string.IsNullOrWhiteSpace(i.Default)
                ? $" --input {i.Id}=\"...\""
                : $" [--input {i.Id}=\"...\"]"))
            : string.Empty;
        return step.Status switch
        {
            StepStatus.AwaitingInput => [$"afl run answer {runId}{answers}"],
            StepStatus.AwaitingPermission =>
            [
                $"afl run permit {runId} {string.Join(" ", step.PermissionRequests.Select(r => $"--once|--always|--deny {r.Index}"))} [--scope run|repo|global]",
                $"afl run permit {runId} --all once|always|deny",
                $"afl run permit {runId} --accept-output   (keep what the AI did without them)",
            ],
            StepStatus.AwaitingApproval when step.OpenQuestions.Count > 0 =>
            [
                $"afl run approve {runId}{answers}",
                $"afl run reject {runId} {string.Join(" ", step.OpenQuestions.Select(q => $"[--answer {q.Number}=\"...\"]"))} [--feedback \"...\"]",
            ],
            StepStatus.AwaitingApproval =>
                [$"afl run approve {runId}{answers}", $"afl run reject {runId} --feedback \"...\""],
            StepStatus.AwaitingUser when step.Interactive is not null =>
                [$"afl run resume {runId}   (opens the interactive session)", $"afl run done {runId} [--note \"...\"]{answers}"],
            StepStatus.AwaitingUser => [$"afl run done {runId} [--note \"...\"]{answers}"],
            StepStatus.AwaitingSession when step.Candidates.Count > 0 => step.Candidates
                .Where(c => c.Status == StepStatus.AwaitingSession)
                .Select(c => $"afl run submit {runId} --agent {c.Agent} --output @<file>")
                .ToList(),
            StepStatus.AwaitingSession => [$"afl run submit {runId} --output @<file>"],
            StepStatus.AwaitingChoice =>
            [
                .. step.Candidates.Where(c => c.Status == StepStatus.Done)
                    .Select(c => $"afl run choose {runId} {c.Agent} [--output @<edited file>]"),
                $"afl run merge {runId} [--agent <agent>] [--note \"...\"]",
                $"afl run reject {runId} --feedback \"...\"   (every agent redoes it)",
            ],
            StepStatus.ChoosingAgent => step.AgentCandidates
                .Select(a => $"afl run use-agent {runId} {a} --by main-agent --reason \"...\"")
                .ToList(),
            StepStatus.AwaitingChildren =>
            [
                .. step.ChildRuns.Where(c => c.Status is not (RunStatus.Done or RunStatus.Cancelled))
                    .Select(c => $"afl run show {c.RunId}   (child run: drive it on; this run carries on by itself)"),
            ],
            StepStatus.Failed => [$"afl run retry {runId}", $"afl run cancel {runId}"],
            _ => [],
        };
    }
}

/// <summary>
///     A step of a run in brief: what it is, where it got to and what it produced.
/// </summary>
/// <param name="Id">The step id.</param>
/// <param name="Name">The step name.</param>
/// <param name="Type">The step type, e.g. <c>mixed/approve</c>.</param>
/// <param name="Advance">What happens once its work is done: <c>auto</c> or <c>approve</c>.</param>
/// <param name="Status">The status.</param>
/// <param name="Output">The step's output so far, if any.</param>
/// <param name="Replays">How many earlier attempts were replaced by a replay (not by a loop or a route back).</param>
/// <param name="Iteration">Which time round the step is on, or was on once done, counting from 1.</param>
/// <param name="MaxRuns">How many times a loop may run the step; null when no loop can bring it back.</param>
/// <param name="NotReached">True when the run is finished and its path never came to the step.</param>
public record StepSummaryView(
    string Id,
    string Name,
    string Type,
    string Advance,
    StepStatus Status,
    string? Output,
    int Replays,
    int Iteration,
    int? MaxRuns,
    bool NotReached)
{
    /// <summary>
    ///     Builds the summary of a step.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The step.</param>
    /// <returns>The summary.</returns>
    public static StepSummaryView From(Run run, RunStep state)
    {
        var definition = run.DefinitionOf(state);
        return new StepSummaryView(state.StepId, state.Name, definition.Type, StepView.AdvanceOf(definition), state.Status,
            state.Output, state.Replays, state.Iteration, FlowRouter.Instance.LimitOf(run.Workflow, definition),
            run.NotReached(state));
    }
}

/// <summary>
///     A step of a run in full: the current step in a run's view, or the step <c>afl run step</c> shows.
/// </summary>
public class StepView
{
    /// <summary>
    ///     Gets or sets the id of the run the step belongs to.
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step's position in the workflow.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    ///     Gets or sets the step id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step type, e.g. <c>mixed/approve</c>.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the id of the agent doing the step's AI work.
    /// </summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the ids of the agents that each do the step, for a step several agents do; otherwise empty.
    /// </summary>
    public List<string> Agents { get; set; } = [];

    /// <summary>
    ///     Gets or sets how the results of a step several agents do become its output (<c>choose</c>, <c>merge</c> or
    ///     <c>all</c>); null for a step one agent does.
    /// </summary>
    public string? Combine { get; set; }

    /// <summary>
    ///     Gets or sets each agent's result, for a step several agents do; otherwise empty.
    /// </summary>
    public List<CandidateView> Candidates { get; set; } = [];

    /// <summary>
    ///     Gets or sets what happens once the step's work is done: <c>auto</c> moves on, <c>approve</c> waits for
    ///     approval.
    /// </summary>
    public string Advance { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the status.
    /// </summary>
    public StepStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets which time round the step is on, or was on once done, counting from 1.
    /// </summary>
    public int Iteration { get; set; } = 1;

    /// <summary>
    ///     Gets or sets how many times a loop may run the step; null when no loop can bring it back.
    /// </summary>
    public int? MaxRuns { get; set; }

    /// <summary>
    ///     Gets or sets what the step is waiting on: the prompt for the calling session, or the user's instructions.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    ///     Gets or sets the output so far.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets the questions the AI left at the end of the output, while the step waits for approval; answered
    ///     with <c>afl run reject --answer n=...</c>.
    /// </summary>
    public List<OpenQuestion> OpenQuestions { get; set; } = [];

    /// <summary>
    ///     Gets or sets the feedback the step was sent back with in this attempt.
    /// </summary>
    public List<string> Feedback { get; set; } = [];

    /// <summary>
    ///     Gets or sets the tool calls the AI was refused that wait for a decision, while the step needs permission;
    ///     decided with <c>afl run permit</c>.
    /// </summary>
    public List<PermissionRequest> PermissionRequests { get; set; } = [];

    /// <summary>
    ///     Gets or sets the permission requests already decided in this attempt.
    /// </summary>
    public List<PermissionRequest> DecidedPermissions { get; set; } = [];

    /// <summary>
    ///     Gets or sets the notes.
    /// </summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>
    ///     Gets or sets why the step failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Gets or sets the command that opens the step's interactive session.
    /// </summary>
    public InteractiveLaunch? Interactive { get; set; }

    /// <summary>
    ///     Gets or sets what the user is asked with the next action: the step's <c>before</c> inputs or the AI's
    ///     questions (<c>afl run answer</c>), or its <c>after</c> inputs (approve, or done). Empty when nothing is
    ///     asked now. Answers go in with <c>--input id=value</c>.
    /// </summary>
    public List<StepInputView> Inputs { get; set; } = [];

    /// <summary>
    ///     Gets or sets whether the next action asks <see cref="Inputs" />.
    /// </summary>
    public bool AsksInputs { get; set; }

    /// <summary>
    ///     Gets or sets whether the AI is the one asking (it stopped part-way to ask questions).
    /// </summary>
    public bool AiAsking { get; set; }

    /// <summary>
    ///     Gets or sets each time the AI stopped to ask questions in this attempt, oldest first.
    /// </summary>
    public List<AskRound> AskRounds { get; set; } = [];

    /// <summary>
    ///     Gets or sets how the step's agent was chosen, or the agents it's choosing among (status
    ///     <c>choosing-agent</c>); null when the step names its agent.
    /// </summary>
    public AgentChoice? AgentChoice { get; set; }

    /// <summary>
    ///     Gets or sets the agents that can do the step, when it's waiting for someone to pick one; otherwise empty.
    /// </summary>
    public List<string> AgentCandidates { get; set; } = [];

    /// <summary>
    ///     Gets or sets the sub-tasks the step's AI handed to other agents in this attempt.
    /// </summary>
    public List<Delegation> Delegations { get; set; } = [];

    /// <summary>
    ///     Gets or sets the tickets the step's AI filed in this attempt.
    /// </summary>
    public List<CreatedTicket> CreatedTickets { get; set; } = [];

    /// <summary>
    ///     Gets or sets the child runs a <c>workflow</c> step started (or reused) in this attempt; status
    ///     <c>awaiting-children</c> means it's waiting for them.
    /// </summary>
    public List<ChildRun> ChildRuns { get; set; } = [];

    /// <summary>
    ///     Gets or sets the file the step's output was saved to, relative to the repo; null when none was.
    /// </summary>
    public string? ArtifactPath { get; set; }

    /// <summary>
    ///     Gets or sets the answers given, by input id.
    /// </summary>
    public Dictionary<string, string> Answers { get; set; } = [];

    /// <summary>
    ///     Gets or sets the step's earlier attempts, replaced by replays, oldest first.
    /// </summary>
    public List<StepAttempt> History { get; set; } = [];

    /// <summary>
    ///     Returns what happens once a step's work is done.
    /// </summary>
    /// <param name="definition">The step.</param>
    /// <returns><c>auto</c> or <c>approve</c>.</returns>
    public static string AdvanceOf(AbstractStepDefinition definition)
    {
        return StepHandlerRegistry.Instance.For(definition).AdvanceOf(definition);
    }

    /// <summary>
    ///     Builds the full view of a step.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The step.</param>
    /// <returns>The view.</returns>
    public static StepView From(Run run, RunStep state)
    {
        var definition = run.DefinitionOf(state);
        var handler = StepHandlerRegistry.Instance.For(definition);
        var asked = handler.InputsAskedNow(run, state);
        return new StepView
        {
            RunId = run.Id,
            Index = run.Steps.IndexOf(state),
            Id = state.StepId,
            Name = state.Name,
            Type = definition.Type,
            Agent = state.AgentChoice?.AgentId ?? definition.Agent ?? run.Executor,
            Agents = definition is AbstractAgentStepDefinition { IsMultiAgent: true } multi ? [.. multi.Agents!] : [],
            Combine = definition is AbstractAgentStepDefinition { IsMultiAgent: true } combined ? combined.Combine ?? StepConstants.CombineChoose : null,
            Candidates = (state.Candidates ?? []).Select(CandidateView.From).ToList(),
            Advance = handler.AdvanceOf(definition),
            Status = state.Status,
            Iteration = state.Iteration,
            MaxRuns = FlowRouter.Instance.LimitOf(run.Workflow, definition),
            Prompt = state.PendingPrompt,
            Output = state.Output,
            OpenQuestions = [.. state.OpenQuestions],
            PermissionRequests = [.. state.OpenPermissionRequests],
            DecidedPermissions = state.PermissionRequests.Where(r => r.Decision is not null).ToList(),
            Feedback = state.Feedback,
            Notes = state.Notes,
            Error = state.Error,
            Interactive = state.Interactive,
            Inputs = asked.Select(StepInputView.From).ToList(),
            AsksInputs = asked.Count > 0,
            AiAsking = state.Status == StepStatus.AwaitingInput && state.OpenAskRound is not null,
            AskRounds = state.AskRounds,
            AgentChoice = state.AgentChoice,
            AgentCandidates = state.Status == StepStatus.ChoosingAgent ? state.AgentChoice?.Candidates ?? [] : [],
            Delegations = state.Delegations,
            CreatedTickets = state.CreatedTickets,
            ChildRuns = state.ChildRuns,
            ArtifactPath = state.ArtifactPath,
            Answers = state.Answers,
            History = state.History,
        };
    }
}

/// <summary>
///     One agent's result for a step several agents do.
/// </summary>
/// <param name="Agent">The agent id.</param>
/// <param name="Status">
///     Where it got to: <c>running</c>, <c>awaiting-session</c> (submit it with <c>--agent</c>), <c>done</c> or
///     <c>failed</c>.
/// </param>
/// <param name="Output">The agent's result, once done.</param>
/// <param name="Error">Why the agent failed, if it did.</param>
/// <param name="Prompt">The prompt for the calling session, when it's awaiting one.</param>
public record CandidateView(string Agent, StepStatus Status, string? Output, string? Error, string? Prompt)
{
    /// <summary>
    ///     Builds the view of a candidate.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The view.</returns>
    public static CandidateView From(StepCandidate candidate)
    {
        return new CandidateView(candidate.Agent, candidate.Status, candidate.Output, candidate.Error,
            candidate.Status == StepStatus.AwaitingSession ? candidate.PendingPrompt : null);
    }
}

/// <summary>
///     A question a step asks the user, and what a valid answer looks like.
/// </summary>
/// <param name="Id">The input id, used in <c>--input id=value</c>.</param>
/// <param name="Label">The question.</param>
/// <param name="Type">The input type, e.g. <c>choice</c>.</param>
/// <param name="Editor">The kind of control to ask with.</param>
/// <param name="Required">Whether an answer is needed.</param>
/// <param name="Options">The options to pick from, for choice types.</param>
/// <param name="Default">The answer used when none is given.</param>
/// <param name="Help">Extra help, if any.</param>
/// <param name="Accepts">What a valid answer looks like, e.g. "yes or no".</param>
/// <param name="When">When the step asks it: <c>before</c>, <c>after</c> or <c>ai</c>.</param>
public record StepInputView(
    string Id,
    string Label,
    string Type,
    StepInputEditor Editor,
    bool Required,
    List<string>? Options,
    string? Default,
    string? Help,
    string Accepts,
    string When)
{
    /// <summary>
    ///     Builds the view of an input.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>The view.</returns>
    public static StepInputView From(StepInput input)
    {
        var type = StepInputTypeRegistry.Instance.For(input);
        return new StepInputView(input.Id, input.Label, input.Type, type.Editor, input.Required, input.Options, input.Default,
            input.Help, type.Describe(input), input.Timing);
    }
}
