using System.Text.Json.Serialization;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.GitHosts;
using Afl.Core.Permissions;
using Afl.Core.Storage;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Runs;

/// <summary>
///     The state of a run as a whole.
/// </summary>
public enum RunStatus
{
    /// <summary>
    ///     A step is executing.
    /// </summary>
    Running,

    /// <summary>
    ///     The current step is waiting on the user or the calling Claude session.
    /// </summary>
    Waiting,

    /// <summary>
    ///     Every step is done.
    /// </summary>
    Done,

    /// <summary>
    ///     The current step failed; it can be retried.
    /// </summary>
    Failed,

    /// <summary>
    ///     The run was cancelled.
    /// </summary>
    Cancelled,
}

/// <summary>
///     Why a step's attempt was set aside to redo the step.
/// </summary>
public enum AttemptCause
{
    /// <summary>
    ///     The user replayed the step, sent it back, or replayed an earlier step.
    /// </summary>
    Replay,

    /// <summary>
    ///     An arrow went back round a loop the step is on.
    /// </summary>
    Loop,

    /// <summary>
    ///     Arrows reached the step again by another way in.
    /// </summary>
    Route,

    /// <summary>
    ///     The step failed and its <c>retry</c> ran it again.
    /// </summary>
    Retry,
}

/// <summary>
///     The state of one step in a run.
/// </summary>
public enum StepStatus
{
    /// <summary>
    ///     Not started.
    /// </summary>
    Pending,

    /// <summary>
    ///     The AI is working on it.
    /// </summary>
    Running,

    /// <summary>
    ///     Waiting for the user to do something and mark it done.
    /// </summary>
    AwaitingUser,

    /// <summary>
    ///     Waiting for the user to approve or reject the AI's output.
    /// </summary>
    AwaitingApproval,

    /// <summary>
    ///     Waiting for the calling Claude session to do the work and submit the output.
    /// </summary>
    AwaitingSession,

    /// <summary>
    ///     Several agents did the step; waiting for the user to choose one's result or merge them.
    /// </summary>
    AwaitingChoice,

    /// <summary>
    ///     Waiting for the user to answer <see cref="RunStep.Questions" />: the step's <c>before</c> inputs, or what the
    ///     AI asked while it worked.
    /// </summary>
    AwaitingInput,

    /// <summary>
    ///     The AI was refused tool calls; waiting for the user to allow or deny each
    ///     (<see cref="RunStep.PermissionRequests" />) before it carries on.
    /// </summary>
    AwaitingPermission,

    /// <summary>
    ///     Waiting for someone to choose which agent does the step (see <see cref="RunStep.AgentChoice" />): the run's
    ///     agent is the calling Claude session, or a person is to pick.
    /// </summary>
    ChoosingAgent,

    /// <summary>
    ///     Waiting for the child runs it started (<see cref="RunStep.ChildRuns" />) to finish.
    /// </summary>
    AwaitingChildren,

    /// <summary>
    ///     Where the arrows split into parallel paths: waiting for its path runs (<see cref="RunStep.Paths" />).
    /// </summary>
    AwaitingPaths,

    /// <summary>
    ///     Finished.
    /// </summary>
    Done,

    /// <summary>
    ///     Failed; see <see cref="RunStep.Error" />.
    /// </summary>
    Failed,

    /// <summary>
    ///     Passed over: it is optional and was left out when the run started. A step the run never reached stays
    ///     <see cref="Pending" />.
    /// </summary>
    Skipped,
}

/// <summary>
///     What a run is to the user.
/// </summary>
public enum RunKind
{
    /// <summary>
    ///     A run the user (or a <c>workflow</c> step) started.
    /// </summary>
    Main,

    /// <summary>
    ///     One parallel path of its parent run (<see cref="Run.ParentRunId" />): it runs one step of the parent's
    ///     workflow (<see cref="Run.StopAfterStep" />), which the parent then routes, and is folded under the parent.
    /// </summary>
    Path,

    /// <summary>
    ///     A run of the AI authoring helper, drafting a skill, common step or workflow (see
    ///     <see cref="Authoring.AuthoringService" />). It is left out of run lists and the Runs page.
    /// </summary>
    Authoring,
}

/// <summary>
///     One execution of a workflow against a ticket, saved after every transition so any front-end can pick it up.
/// </summary>
[DocumentVersion(3, 0)]
public class Run : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the run id, e.g. <c>260924-3fa2</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the id of the repo the run works in.
    /// </summary>
    public string RepoId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the repo's display name.
    /// </summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the repo's root directory.
    /// </summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the GitHub repository or GitLab project the repo's issues come from, if it's linked to one.
    /// </summary>
    public RepoHostLink? RepoHost { get; set; }

    /// <summary>
    ///     Gets or sets the name the user gave the run, shown instead of the ticket's title; null to show the ticket's.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    ///     Gets or sets the run's tags, for finding and filtering runs (see <see cref="RunLabels.NormalizeTags" />).
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    ///     Gets or sets the folder the run is filed in on its repo's run list, as a path such as <c>billing/q4</c>; null
    ///     for the top level. Only a run started directly is filed; child and path runs are shown under their parent.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    ///     Gets the name the run is shown with: <see cref="Title" />, or the ticket's title.
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle => this.Title ?? this.Ticket.Title;

    /// <summary>
    ///     Gets or sets the id of the workflow being run.
    /// </summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the run that started this one from a <c>workflow</c> step, if any.
    /// </summary>
    public string? ParentRunId { get; set; }

    /// <summary>
    ///     Gets or sets the step of <see cref="ParentRunId" /> that started this run.
    /// </summary>
    public string? ParentStepId { get; set; }

    /// <summary>
    ///     Gets or sets what the run is: a run of its own, or one path of its parent's.
    /// </summary>
    public RunKind Kind { get; set; } = RunKind.Main;

    /// <summary>
    ///     Gets or sets whether the run stops once its current step ends, without following its arrows: a path run
    ///     runs one step, and the run it belongs to routes it.
    /// </summary>
    public bool StopAfterStep { get; set; }

    /// <summary>
    ///     Gets or sets how many parent runs this run has: 0 for a run started directly.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    ///     Gets or sets a snapshot of the workflow taken when the run started, so later edits don't change a run
    ///     in flight.
    /// </summary>
    public WorkflowDefinition Workflow { get; set; } = new();

    /// <summary>
    ///     Gets or sets a snapshot of the ticket taken when the run started.
    /// </summary>
    public Ticket Ticket { get; set; } = new();

    /// <summary>
    ///     Gets or sets the id of the run's default agent (see <c>afl agent list</c>); steps can pick their own.
    /// </summary>
    public string Executor { get; set; } = ExecutorRegistry.DefaultExecutor;

    /// <summary>
    ///     Gets or sets how agents that run a command run it (see <see cref="RunModeRegistry" />): <c>background</c> or
    ///     <c>terminal</c>. Null (runs saved before modes existed) means background.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    ///     Gets or sets the run's scratch directory, relative to the repo (e.g. <c>.scratch/dark-mode</c>): where its
    ///     working documents go, never tracked by git. Fixed when the run starts; child runs share their parent's.
    /// </summary>
    public string? ScratchDirectory { get; set; }

    /// <summary>
    ///     Gets or sets the checkout the run's agents work in when it isn't <see cref="RepoPath" /> (a git worktree its
    ///     parent's step gave it); null to work in the repo itself. Child runs inherit it.
    /// </summary>
    public string? WorkingPath { get; set; }

    /// <summary>
    ///     Gets or sets the workspace this run was given by its parent's step (see <c>Workspaces.WorkspaceRegistry</c>),
    ///     or null when it shares its parent's.
    /// </summary>
    public string? Workspace { get; set; }

    /// <summary>
    ///     Gets or sets the branch of the run's own workspace, if it has one.
    /// </summary>
    public string? WorkspaceBranch { get; set; }

    /// <summary>
    ///     Gets or sets the most child or path runs the run drives at once; null for no limit. 1 does one thing at a
    ///     time. Child runs inherit it.
    /// </summary>
    public int? MaxParallel { get; set; }

    /// <summary>
    ///     Gets whether the run does one thing at a time (<see cref="MaxParallel" /> is 1).
    /// </summary>
    [JsonIgnore]
    public bool Sequential => this.MaxParallel == 1;

    /// <summary>
    ///     Gets or sets the ids of the optional steps (<see cref="AbstractStepDefinition.Optional" />) chosen to be left out when
    ///     the run started; they are skipped. Child runs start with their own workflow's defaults.
    /// </summary>
    public List<string> LeftOutSteps { get; set; } = [];

    /// <summary>
    ///     Gets or sets the permission policy the run's agents work under (<see cref="AbstractPermissionPolicy" />:
    ///     <c>ask</c>, <c>strict</c>, <c>auto</c>); null for the default. Child and path runs inherit it.
    /// </summary>
    public string? PermissionPolicy { get; set; }

    /// <summary>
    ///     Gets or sets the permission rules saved for this run only. Child and path runs start with a copy.
    /// </summary>
    public List<PermissionRule> PermissionRules { get; set; } = [];

    /// <summary>
    ///     Gets or sets the skills chosen for the run when it started, in place of its workflow's own; null for the
    ///     workflow's. Steps' own skills and <c>skipSkills</c> still apply. Child runs inherit them.
    /// </summary>
    public List<string>? Skills { get; set; }

    /// <summary>
    ///     Gets or sets the run status.
    /// </summary>
    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>
    ///     Gets or sets the id of the current step: the one the run is on itself, or, while it has several paths
    ///     (<see cref="Paths" />), the step whose arrows split it, waiting at <see cref="StepStatus.AwaitingPaths" />.
    ///     Null once the run has nowhere further to go.
    /// </summary>
    public string? CurrentStepId { get; set; }

    /// <summary>
    ///     Gets or sets the paths the run has while it has two or more: the step each is at, run by its own path run.
    ///     Empty while the run is on one step (<see cref="CurrentStepId" />).
    /// </summary>
    public List<ActivePath> Paths { get; set; } = [];

    /// <summary>
    ///     Gets or sets the state of each forward arrow that has fired or died in this pass through the flow, by its key
    ///     (<c>Flow.FlowMachine.KeyOf</c>); an arrow that isn't here is still pending. Going back round a loop clears the
    ///     arrows leaving the steps on it.
    /// </summary>
    public Dictionary<string, ArrowState> Arrows { get; set; } = [];

    /// <summary>
    ///     Gets or sets how many step runs the run has done in all, counting every loop and path, which
    ///     <see cref="WorkflowDefinition.MaxSteps" /> bounds.
    /// </summary>
    public int StepRuns { get; set; }

    /// <summary>
    ///     Gets or sets whether a step failed and no arrow on failure handled it (an arrow on <c>always</c> or the
    ///     workflow's <c>onFailure</c> step took the run on instead), so the run ends failed.
    /// </summary>
    public bool UnhandledFailure { get; set; }

    /// <summary>
    ///     Gets or sets the state of each step, in the order the workflow lists them.
    /// </summary>
    public List<RunStep> Steps { get; set; } = [];

    /// <summary>
    ///     Gets or sets every step the run has started, in order, including each time a loop or route went back.
    /// </summary>
    public List<StepVisit> Path { get; set; } = [];

    /// <summary>
    ///     Gets or sets when the run was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Gets or sets when the run last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    ///     Gets the current step's state, or null when the run has nowhere further to go.
    /// </summary>
    [JsonIgnore]
    public RunStep? CurrentStep => this.CurrentStepId is null ? null : this.StepOf(this.CurrentStepId);

    /// <summary>
    ///     Gets whether this is a path run, running one of its parent run's parallel paths.
    /// </summary>
    [JsonIgnore]
    public bool IsPathRun => this.Kind == RunKind.Path;

    /// <summary>
    ///     Gets whether this is an authoring run, drafting a document for the AI authoring helper.
    /// </summary>
    [JsonIgnore]
    public bool IsAuthoring => this.Kind == RunKind.Authoring;

    /// <summary>
    ///     Gets, for a path run, the step it runs: the one it visited, or the one it's on before it starts.
    /// </summary>
    [JsonIgnore]
    public string? PathStepId => this.IsPathRun ? this.Path.FirstOrDefault()?.StepId ?? this.CurrentStepId : null;

    /// <summary>
    ///     Gets whether the run is finished and its path never came to a step, for front-ends to show as "not reached".
    /// </summary>
    /// <param name="step">One of the run's steps.</param>
    /// <returns>True if the step was never reached.</returns>
    public bool NotReached(RunStep step)
    {
        return this.IsFinished && step.Status == StepStatus.Pending;
    }

    /// <summary>
    ///     Returns a step's state by id.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The state, or null when the run has no such step.</returns>
    public RunStep? StepOf(string stepId)
    {
        return this.Steps.Find(s => s.StepId == stepId);
    }

    /// <summary>
    ///     Gets whether the run has finished, one way or another.
    /// </summary>
    public bool IsFinished => this.Status is RunStatus.Done or RunStatus.Cancelled;

    /// <summary>
    ///     Gets whether the run is stopped at a step that waits on a person (<see cref="RunStep.WaitsForUser" />).
    /// </summary>
    [JsonIgnore]
    public bool WaitsForUser => !this.IsFinished && this.CurrentStep?.WaitsForUser == true;

    /// <summary>
    ///     Gets what the run is doing, as lists show it (<see cref="RunStates" />).
    /// </summary>
    [JsonIgnore]
    public RunState State => RunStates.Of(this);

    /// <summary>
    ///     Returns the definition of a step in the workflow snapshot.
    /// </summary>
    /// <param name="state">The step's state.</param>
    /// <returns>The definition.</returns>
    public AbstractStepDefinition DefinitionOf(RunStep state)
    {
        return this.Workflow.Steps.First(s => s.Id == state.StepId);
    }
}

/// <summary>
///     The state of one step in a run.
/// </summary>
public class RunStep
{
    /// <summary>
    ///     Gets or sets the step id.
    /// </summary>
    public string StepId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step status.
    /// </summary>
    public StepStatus Status { get; set; } = StepStatus.Pending;

    /// <summary>
    ///     Gets or sets the step's result, available to later steps as <c>{{steps.&lt;id&gt;.output}}</c>.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets the feedback given each time the user sent the step back (rejected or replayed it), oldest
    ///     first.
    /// </summary>
    public List<string> Feedback { get; set; } = [];

    /// <summary>
    ///     Gets or sets free-form notes added to the step.
    /// </summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>
    ///     Gets or sets the answers to the step's inputs, by input id, available to later steps as
    ///     <c>{{steps.&lt;id&gt;.inputs.&lt;input&gt;}}</c>.
    /// </summary>
    public Dictionary<string, string> Answers { get; set; } = [];

    /// <summary>
    ///     Gets or sets what the user is being asked, when <see cref="Status" /> is <see cref="StepStatus.AwaitingInput" />.
    /// </summary>
    public List<StepInput>? Questions { get; set; }

    /// <summary>
    ///     Gets or sets each time the AI stopped to ask questions in this attempt, oldest first.
    /// </summary>
    public List<AskRound> AskRounds { get; set; } = [];

    /// <summary>
    ///     Gets or sets the sub-tasks the AI handed to other agents in this attempt, oldest first.
    /// </summary>
    public List<Delegation> Delegations { get; set; } = [];

    /// <summary>
    ///     Gets or sets the tickets the AI filed in this attempt, oldest first.
    /// </summary>
    public List<CreatedTicket> CreatedTickets { get; set; } = [];

    /// <summary>
    ///     Gets or sets the child runs a <c>workflow</c> step started or reused, in order.
    /// </summary>
    public List<ChildRun> ChildRuns { get; set; } = [];

    /// <summary>
    ///     Gets or sets, for the step whose arrows split the run into paths, the path runs started since, one per step a
    ///     path ran; null for a step that didn't split the run.
    /// </summary>
    public List<ChildRun>? Paths { get; set; }

    /// <summary>
    ///     Gets or sets the step whose arrow started this attempt, available as <c>{{arrived.from}}</c>; null for the
    ///     step a run starts at.
    /// </summary>
    public string? ArrivedFrom { get; set; }

    /// <summary>
    ///     Gets or sets how the step failed when it wasn't its own doing: <c>timeout</c> (it ran over its
    ///     <c>timeout</c>) or <c>interrupted</c> (the afl process stopped); null otherwise.
    /// </summary>
    public string? FailureKind { get; set; }

    /// <summary>
    ///     Gets or sets, for the step waiting on the paths its arrows split the run into, how it had ended before, which
    ///     it goes back to once they're done.
    /// </summary>
    public StepStatus? EndedAs { get; set; }

    /// <summary>
    ///     Gets or sets the file the step's output was saved to (<see cref="AbstractStepDefinition.Artifact" />), relative to the
    ///     repo; null when it has none or it hasn't been written.
    /// </summary>
    public string? ArtifactPath { get; set; }

    /// <summary>
    ///     Gets the AI's questions waiting for answers, if it is asking now.
    /// </summary>
    [JsonIgnore]
    public AskRound? OpenAskRound => this.AskRounds.LastOrDefault(r => r.Answers is null);

    /// <summary>
    ///     Gets whether the step is waiting on a person: to answer, approve, choose, pick an agent, finish it or deal
    ///     with its failure.
    /// </summary>
    [JsonIgnore]
    public bool WaitsForUser => this.Status is StepStatus.AwaitingInput or StepStatus.AwaitingPermission or StepStatus.AwaitingApproval
        or StepStatus.AwaitingChoice or StepStatus.AwaitingUser or StepStatus.ChoosingAgent or StepStatus.Failed;

    /// <summary>
    ///     Gets or sets the Claude conversation id used for the step.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    ///     Gets or sets what the step is waiting on someone to do: the rendered prompt for the calling Claude session,
    ///     or the rendered instructions for the user.
    /// </summary>
    public string? PendingPrompt { get; set; }

    /// <summary>
    ///     Gets or sets the command that opens the step's interactive session, for interactive steps.
    /// </summary>
    public InteractiveLaunch? Interactive { get; set; }

    /// <summary>
    ///     Gets or sets why the step failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Gets or sets the step's earlier attempts, oldest first, kept each time the step (or one before it) was
    ///     replayed.
    /// </summary>
    public List<StepAttempt> History { get; set; } = [];

    /// <summary>
    ///     Gets or sets each agent's result, for a step several agents do; null otherwise.
    /// </summary>
    public List<StepCandidate>? Candidates { get; set; }

    /// <summary>
    ///     Gets or sets which agent does the step and why, for a step whose agent is chosen by capability
    ///     (<see cref="AbstractAgentStepDefinition.Capabilities" />) or was picked with <c>afl run use-agent</c>; null otherwise.
    /// </summary>
    public AgentChoice? AgentChoice { get; set; }

    /// <summary>
    ///     Gets or sets how many times the AI has been run for the step.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    ///     Gets or sets how many times the step has been done; loops and routes back to it count against its limit.
    /// </summary>
    public int Runs { get; set; }

    /// <summary>
    ///     Gets or sets how many times in a row an arrow from the step back to itself has brought it round, since it was
    ///     last reached another way; that arrow's <c>max</c> bounds this, so a self-loop's limit starts again each time an
    ///     outer loop comes round.
    /// </summary>
    public int Rounds { get; set; }

    /// <summary>
    ///     Gets or sets the tool calls the AI was refused and asked the user about in this attempt, with what they
    ///     decided, oldest first.
    /// </summary>
    public List<PermissionRequest> PermissionRequests { get; set; } = [];

    /// <summary>
    ///     Gets or sets how many times the step has paused for permission in this attempt.
    /// </summary>
    public int PermissionRounds { get; set; }

    /// <summary>
    ///     Gets or sets how many times, in this attempt, the step's output was checked, found wanting and sent back to
    ///     the AI to fix (an <c>author-draft</c> step's draft, for example).
    /// </summary>
    public int CheckRounds { get; set; }

    /// <summary>
    ///     Gets or sets the rules the user allowed once, for the AI's next turn only.
    /// </summary>
    public List<string> OnceRules { get; set; } = [];

    /// <summary>
    ///     Gets the requests waiting for a decision in the current round.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<PermissionRequest> OpenPermissionRequests => this.Status == StepStatus.AwaitingPermission
        ? this.PermissionRequests.Where(r => r.Round == this.PermissionRounds).ToList()
        : [];

    /// <summary>
    ///     Gets how many earlier attempts were replaced by a replay (not by a loop or a route back).
    /// </summary>
    [JsonIgnore]
    public int Replays => this.History.Count(a => a.Cause == AttemptCause.Replay);

    /// <summary>
    ///     Gets which time round the step is on (or, once done, was on), counting from 1.
    /// </summary>
    [JsonIgnore]
    public int Iteration => this.Status == StepStatus.Done ? Math.Max(this.Runs, 1) : this.Runs + 1;

    /// <summary>
    ///     Gets or sets when the step started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    ///     Gets or sets when the step finished.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    ///     Gets whether the step has been started, so it has something to show and can be replayed.
    /// </summary>
    [JsonIgnore]
    public bool HasRun => this.Status is not (StepStatus.Pending or StepStatus.Skipped);

    /// <summary>
    ///     Gets the output of the step's latest earlier attempt, available as <c>{{steps.&lt;id&gt;.previous}}</c>.
    /// </summary>
    [JsonIgnore]
    public string? PreviousOutput => this.History.LastOrDefault(a => a.Output is not null)?.Output;

    /// <summary>
    ///     Gets the questions the AI left at the end of the output, while the step waits for approval; otherwise empty.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<OpenQuestion> OpenQuestions =>
        this.Status == StepStatus.AwaitingApproval ? OpenQuestion.Find(this.Output) : [];

    /// <summary>
    ///     Returns the results of the agents that finished, each under its agent's heading, for showing them together.
    /// </summary>
    /// <returns>The results, or an empty string.</returns>
    public string CombineCandidates()
    {
        return string.Join("\n\n", (this.Candidates ?? [])
            .Where(c => c.Status == StepStatus.Done)
            .Select(c => string.Format(StepConstants.CandidateHeadingFormat, c.Agent) + "\n\n" + c.Output?.Trim()));
    }

    /// <summary>
    ///     Moves the step's current attempt into <see cref="History" /> and resets it to <see cref="StepStatus.Pending" />.
    ///     Notes and the attempt count are kept; the conversation id is kept only when asked, so a replay with feedback
    ///     can continue it.
    /// </summary>
    /// <param name="cause">Why the step is being redone.</param>
    /// <param name="reason">Why, in words, if given.</param>
    /// <param name="keepSession">Whether to keep <see cref="SessionId" />.</param>
    public void Archive(AttemptCause cause, string? reason, bool keepSession)
    {
        if (this.HasRun)
        {
            this.History.Add(new StepAttempt
            {
                Status = this.Status,
                Output = this.Output,
                Answers = new Dictionary<string, string>(this.Answers),
                Feedback = [..this.Feedback],
                Error = this.Error,
                Candidates = this.Candidates?.Select(c => c.Clone()).ToList(),
                AskRounds = this.AskRounds.Count > 0 ? [..this.AskRounds] : null,
                Delegations = this.Delegations.Count > 0 ? [..this.Delegations] : null,
                PermissionRequests = this.PermissionRequests.Count > 0 ? [..this.PermissionRequests] : null,
                CreatedTickets = this.CreatedTickets.Count > 0 ? [..this.CreatedTickets] : null,
                ChildRuns = this.ChildRuns.Count > 0 ? [..this.ChildRuns] : null,
                Paths = this.Paths is { Count: > 0 } ? [..this.Paths] : null,
                ArtifactPath = this.ArtifactPath,
                AgentChoice = this.AgentChoice,
                StartedAt = this.StartedAt,
                EndedAt = this.EndedAt,
                ReplacedAt = DateTimeOffset.Now,
                Cause = cause,
                Reason = reason,
            });
        }

        this.Status = StepStatus.Pending;
        this.Output = null;
        this.Answers = [];
        this.Feedback = [];
        this.PendingPrompt = null;
        this.Questions = null;
        this.AskRounds = [];
        this.Delegations = [];
        this.PermissionRequests = [];
        this.PermissionRounds = 0;
        this.CheckRounds = 0;
        this.OnceRules = [];
        this.CreatedTickets = [];
        this.ChildRuns = [];
        this.Paths = null;
        this.ArtifactPath = null;
        this.Interactive = null;
        this.Error = null;
        this.ArrivedFrom = null;
        this.FailureKind = null;
        this.EndedAs = null;
        this.StartedAt = null;
        this.EndedAt = null;
        if (!keepSession)
        {
            // A new conversation may go to a different agent; one that's continued stays with the agent it was with.
            this.SessionId = null;
            this.AgentChoice = null;
        }

        this.Candidates = keepSession
            ? this.Candidates?.Select(c => new StepCandidate { Agent = c.Agent, SessionId = c.SessionId }).ToList()
            : null;
    }
}

/// <summary>
///     An earlier attempt at a step, kept when the step was replayed.
/// </summary>
public class StepAttempt
{
    /// <summary>
    ///     Gets or sets the status the attempt had reached.
    /// </summary>
    public StepStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets the attempt's output.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets the answers given in the attempt, by input id.
    /// </summary>
    public Dictionary<string, string> Answers { get; set; } = [];

    /// <summary>
    ///     Gets or sets the feedback given during the attempt, oldest first.
    /// </summary>
    public List<string> Feedback { get; set; } = [];

    /// <summary>
    ///     Gets or sets why the attempt failed, if it did.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Gets or sets each agent's result in the attempt, for a step several agents do.
    /// </summary>
    public List<StepCandidate>? Candidates { get; set; }

    /// <summary>
    ///     Gets or sets each time the AI stopped to ask questions in the attempt, if it did.
    /// </summary>
    public List<AskRound>? AskRounds { get; set; }

    /// <summary>
    ///     Gets or sets the sub-tasks the AI handed to other agents in the attempt, if it did.
    /// </summary>
    public List<Delegation>? Delegations { get; set; }

    /// <summary>
    ///     Gets or sets the tool calls the AI was refused and asked the user about in the attempt, if any.
    /// </summary>
    public List<PermissionRequest>? PermissionRequests { get; set; }

    /// <summary>
    ///     Gets or sets the tickets the AI filed, if any.
    /// </summary>
    public List<CreatedTicket>? CreatedTickets { get; set; }

    /// <summary>
    ///     Gets or sets the child runs the attempt started or reused, if any.
    /// </summary>
    public List<ChildRun>? ChildRuns { get; set; }

    /// <summary>
    ///     Gets or sets the path runs started after the attempt, if its arrows split the run.
    /// </summary>
    public List<ChildRun>? Paths { get; set; }

    /// <summary>
    ///     Gets or sets the file the attempt's output was saved to, relative to the repo, if any.
    /// </summary>
    public string? ArtifactPath { get; set; }

    /// <summary>
    ///     Gets or sets which agent did the attempt and why, when it was chosen.
    /// </summary>
    public AgentChoice? AgentChoice { get; set; }

    /// <summary>
    ///     Gets or sets when the attempt started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    ///     Gets or sets when the attempt finished.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    ///     Gets or sets when the attempt was replaced.
    /// </summary>
    public DateTimeOffset ReplacedAt { get; set; }

    /// <summary>
    ///     Gets or sets why the attempt was replaced: a replay, a loop, a route back or a retry.
    /// </summary>
    public AttemptCause Cause { get; set; }

    /// <summary>
    ///     Gets or sets why, in words: the feedback a replay was asked with, or the loop or route, if any.
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>
///     Which agent does a step and who chose it, for a step whose agent is chosen by capability.
/// </summary>
public class AgentChoice
{
    /// <summary>
    ///     Gets or sets the chosen agent's id; null while the choice is waiting (<see cref="StepStatus.ChoosingAgent" />).
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    ///     Gets or sets why it was chosen.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    ///     Gets or sets who chose it: one of the <c>AgentConstants.ChosenBy*</c> values.
    /// </summary>
    public string? By { get; set; }

    /// <summary>
    ///     Gets or sets the ids of the agents that qualified, in preference order.
    /// </summary>
    public List<string> Candidates { get; set; } = [];

    /// <summary>
    ///     Gets or sets when the choice was made (or, while waiting, asked for).
    /// </summary>
    public DateTimeOffset At { get; set; }

    /// <summary>
    ///     Describes the choice in one line, for front-ends to show.
    /// </summary>
    /// <returns>The line.</returns>
    public string Describe()
    {
        if (this.AgentId is null)
        {
            return $"choosing among {string.Join(", ", this.Candidates)}";
        }

        var reason = string.IsNullOrWhiteSpace(this.Reason) ? string.Empty : $": {this.Reason}";
        return $"{this.AgentId} (chosen by {this.By}{reason})";
    }
}

/// <summary>
///     One time the AI stopped part-way through a step to ask the user questions.
/// </summary>
public class AskRound
{
    /// <summary>
    ///     Gets or sets which time the AI asked in the attempt, from 1.
    /// </summary>
    public int Round { get; set; }

    /// <summary>
    ///     Gets or sets what the AI had written before it asked.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets the questions asked: the step's <c>ai</c> inputs the AI picked, then its own questions.
    /// </summary>
    public List<StepInput> Questions { get; set; } = [];

    /// <summary>
    ///     Gets or sets the answers, by question id; null while the questions are waiting.
    /// </summary>
    public Dictionary<string, string>? Answers { get; set; }

    /// <summary>
    ///     Gets or sets when the AI asked.
    /// </summary>
    public DateTimeOffset AskedAt { get; set; }

    /// <summary>
    ///     Describes the round as one line per question, with its answer, for front-ends to show.
    /// </summary>
    /// <returns>The lines.</returns>
    public IEnumerable<string> Describe()
    {
        foreach (var question in this.Questions)
        {
            var answer = this.Answers is null ? "(waiting)"
                : this.Answers.GetValueOrDefault(question.Id) is { Length: > 0 } given ? given : "(no answer)";
            yield return $"{question.Label} → {answer}";
        }
    }

    /// <summary>
    ///     Describes rounds, one line per question, for front-ends to show.
    /// </summary>
    /// <param name="rounds">The rounds, or null.</param>
    /// <returns>The lines, joined with newlines; empty when there are none.</returns>
    public static string Describe(IEnumerable<AskRound>? rounds)
    {
        return string.Join("\n", (rounds ?? []).SelectMany(r => r.Describe()));
    }
}

/// <summary>
///     A step the run started, for showing the path a run took through loops and routes.
/// </summary>
public class StepVisit
{
    /// <summary>
    ///     Gets or sets the step id.
    /// </summary>
    public string StepId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets which time this is that the step runs, from 1.
    /// </summary>
    public int Iteration { get; set; }

    /// <summary>
    ///     Gets or sets, for a visit made by one of the run's parallel paths, the step that path began at; null for the
    ///     run's own.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    ///     Gets or sets when the step started.
    /// </summary>
    public DateTimeOffset At { get; set; }

    /// <summary>
    ///     Describes a run's path: <c>a &gt; b</c>, with parallel paths' visits as <c>split ⑂ [a &gt; b | c]</c>, for
    ///     front-ends to show.
    /// </summary>
    /// <param name="path">The visits, the paths' merged in after the step that split the run.</param>
    /// <param name="separator">What goes between visits.</param>
    /// <returns>The path.</returns>
    public static string Describe(IReadOnlyList<StepVisit> path, string separator = " > ")
    {
        var parts = new List<string>();
        for (var i = 0; i < path.Count; i++)
        {
            if (path[i].Branch is null)
            {
                parts.Add(Visit(path[i]));
                continue;
            }

            var group = new List<StepVisit>();
            while (i < path.Count && path[i].Branch is not null)
            {
                group.Add(path[i++]);
            }

            i--;
            var branches = group.GroupBy(v => v.Branch).Select(g => string.Join(separator, g.Select(Visit)));
            var fork = parts.Count > 0 ? parts[^1] : string.Empty;
            var text = $"⑂ [{string.Join(" | ", branches)}]";
            if (parts.Count > 0)
            {
                parts[^1] = $"{fork} {text}";
            }
            else
            {
                parts.Add(text);
            }
        }

        return string.Join(separator, parts);

        static string Visit(StepVisit visit)
        {
            return visit.Iteration > 1 ? $"{visit.StepId} #{visit.Iteration}" : visit.StepId;
        }
    }
}

/// <summary>
///     One agent's result for a step that several agents do.
/// </summary>
public class StepCandidate
{
    /// <summary>
    ///     Gets or sets the agent's id.
    /// </summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets how far the agent has got: <see cref="StepStatus.Pending" />, <see cref="StepStatus.Running" />,
    ///     <see cref="StepStatus.AwaitingSession" /> (the calling Claude session is to submit it),
    ///     <see cref="StepStatus.Done" /> or <see cref="StepStatus.Failed" />.
    /// </summary>
    public StepStatus Status { get; set; } = StepStatus.Pending;

    /// <summary>
    ///     Gets or sets the agent's result.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets the agent's conversation id for the step.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    ///     Gets or sets the prompt waiting for the calling Claude session, when <see cref="Status" /> is
    ///     <see cref="StepStatus.AwaitingSession" />.
    /// </summary>
    public string? PendingPrompt { get; set; }

    /// <summary>
    ///     Gets or sets why the agent failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Returns a copy of the candidate.
    /// </summary>
    /// <returns>The copy.</returns>
    public StepCandidate Clone()
    {
        return (StepCandidate)this.MemberwiseClone();
    }
}

/// <summary>
///     A sub-task the AI handed to another agent part-way through a step (an <c>afl-delegate</c> block).
/// </summary>
public class Delegation
{
    /// <summary>
    ///     Gets or sets which time the AI handed off work in the attempt, from 1.
    /// </summary>
    public int Round { get; set; }

    /// <summary>
    ///     Gets or sets the agent that did the task.
    /// </summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the capabilities the AI asked for, if any.
    /// </summary>
    public List<string>? Capabilities { get; set; }

    /// <summary>
    ///     Gets or sets what the agent was asked to do.
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets how far the task got: running, done or failed.
    /// </summary>
    public StepStatus Status { get; set; } = StepStatus.Pending;

    /// <summary>
    ///     Gets or sets the agent's reply.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets why the task failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Gets or sets when the task started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    ///     Gets or sets when the task ended.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    ///     Describes the task in one line for front-ends to show.
    /// </summary>
    /// <returns>The line.</returns>
    public string Describe()
    {
        var how = this.Status == StepStatus.Failed ? $"failed: {this.Error}" : this.Status.ToString().ToLowerInvariant();
        return $"{this.Agent} ({how}): {this.Prompt.ReplaceLineEndings(" ")}";
    }
}

/// <summary>
///     A ticket the AI filed part-way through a step (an <c>afl-ticket</c> block).
/// </summary>
public class CreatedTicket
{
    /// <summary>
    ///     Gets or sets which time the AI filed tickets in the attempt, from 1.
    /// </summary>
    public int Round { get; set; }

    /// <summary>
    ///     Gets or sets the ticket's title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the new ticket's key (e.g. <c>owner/repo#12</c> or <c>PROJ-34</c>); null when it wasn't created.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    ///     Gets or sets the new ticket's web address, or where to file it by hand when it couldn't be created.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    ///     Gets or sets why the ticket couldn't be created.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Gets or sets when it was filed.
    /// </summary>
    public DateTimeOffset At { get; set; }

    /// <summary>
    ///     Describes the ticket in one line for front-ends to show.
    /// </summary>
    /// <returns>The line.</returns>
    public string Describe()
    {
        return this.Key is null
            ? $"not created ({this.Error}): {this.Title}{(this.Url is null ? string.Empty : $" — file it at {this.Url}")}"
            : $"{this.Key}: {this.Title}{(this.Url is null ? string.Empty : $" ({this.Url})")}";
    }
}

/// <summary>
///     A run a <c>workflow</c> step started (or reused), as last seen by the step.
/// </summary>
public class ChildRun
{
    /// <summary>
    ///     Gets or sets the child run's id.
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the workflow it runs.
    /// </summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the key of the ticket it works on.
    /// </summary>
    public string TicketKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets, for a path run, the step its path began at after the run split (<see cref="ActivePath.Origin" />).
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    ///     Gets or sets, for a path run, the lane whose workspace it works in (<see cref="ActivePath.Lane" />), if any.
    /// </summary>
    public string? Lane { get; set; }

    /// <summary>
    ///     Gets or sets the lane its <see cref="Lane" /> was split off inside (<see cref="ActivePath.OuterLane" />). For
    ///     the lane's first path run, its workspace starts from the outer lane's checkout and goes back into it.
    /// </summary>
    public string? OuterLane { get; set; }

    /// <summary>
    ///     Gets or sets, for the first path run of a lane, the step whose arrows split the run into it: its
    ///     <c>workspace</c> and <c>mergeBack</c> are the lane's.
    /// </summary>
    public string? SplitFrom { get; set; }

    /// <summary>
    ///     Gets or sets, for a path run, whether its step has been merged back into the parent run and routed there.
    /// </summary>
    public bool? Merged { get; set; }

    /// <summary>
    ///     Gets or sets the item it was started for (<c>{{item}}</c>): the ticket key, or a line of output.
    /// </summary>
    public string? Item { get; set; }

    /// <summary>
    ///     Gets or sets whether it is an existing run the step reused rather than one it started.
    /// </summary>
    public bool? Reused { get; set; }

    /// <summary>
    ///     Gets or sets its status when the step last looked.
    /// </summary>
    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>
    ///     Gets or sets a note on how it ended, e.g. that another path got there first.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    ///     Gets or sets its output: that of its <c>childOutput</c> step, or of its last step with output.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets why it failed, if it did.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Gets or sets the workspace it was given (see <c>Workspaces.WorkspaceRegistry</c>), once it has one of its own;
    ///     null while it hasn't, or when it shares the parent's checkout.
    /// </summary>
    public string? Workspace { get; set; }

    /// <summary>
    ///     Gets or sets whether its workspace has been settled (merged, kept or discarded) since it finished.
    /// </summary>
    public bool? WorkspaceSettled { get; set; }

    /// <summary>
    ///     Gets or sets what happened when its workspace was last settled, e.g. the branch merged or why it conflicted.
    /// </summary>
    public string? WorkspaceNote { get; set; }

    /// <summary>
    ///     Gets whether its workspace still needs settling: it has one of its own that hasn't been.
    /// </summary>
    [JsonIgnore]
    public bool HasUnsettledWorkspace => this.Workspace is not null && this.WorkspaceSettled != true;

    /// <summary>
    ///     Describes the child in one line for front-ends to show.
    /// </summary>
    /// <returns>The line.</returns>
    public string Describe()
    {
        var reused = this.Reused == true ? ", reused" : string.Empty;
        var item = this.Item is null || this.Item == this.TicketKey ? string.Empty : $" — {this.Item}";
        var workspace = this.WorkspaceNote is null ? string.Empty : $" — {this.WorkspaceNote}";
        var note = this.Note is null ? string.Empty : $" — {this.Note}";
        if (this.Branch is not null)
        {
            return $"path {this.Item}: {this.RunId} ({this.Status.ToString().ToLowerInvariant()}){note}{workspace}";
        }

        return $"{this.RunId} {this.WorkflowId} on {this.TicketKey}{item} ({this.Status.ToString().ToLowerInvariant()}{reused}){note}{workspace}";
    }
}

/// <summary>
///     One of a run's parallel paths (<see cref="Run.Paths" />): the step it is at, and the path run doing it.
/// </summary>
public class ActivePath
{
    /// <summary>
    ///     Gets or sets the step the path is at.
    /// </summary>
    public string StepId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the path run doing the step, once it has one.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    ///     Gets or sets the step the path began at after the run split; a step that paths from several places meet at
    ///     begins its own.
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the lane the path works in: the <see cref="Origin" /> whose path run was given its own workspace
    ///     (a step splitting the run has <c>workspace: worktree</c>), which the steps after it on that path share; null
    ///     to work in the run's own checkout.
    /// </summary>
    public string? Lane { get; set; }

    /// <summary>
    ///     Gets or sets the lane the path's <see cref="Lane" /> was split off inside, when a step in one lane split the
    ///     run into lanes of their own; null for a lane split off from the run's own checkout.
    /// </summary>
    public string? OuterLane { get; set; }

    /// <summary>
    ///     Gets or sets the step whose arrow brought the path here.
    /// </summary>
    public string? ArrivedFrom { get; set; }
}
