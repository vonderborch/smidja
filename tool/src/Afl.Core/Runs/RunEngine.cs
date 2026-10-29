using System.Text.Json;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.GitHosts;
using Afl.Core.Helpers;
using Afl.Core.Jira;
using Afl.Core.Notifications;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Settings;
using Afl.Core.Steps;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using Afl.Core.Workspaces;

namespace Afl.Core.Runs;

/// <summary>
///     Moves runs through their workflow. Every front-end (CLI, plugin, UI) drives runs through this class: it creates
///     runs, starts steps until one needs someone, and applies the user's actions (approve, reject, done, ...). The run
///     is locked while it's being worked on and saved after every transition, so any front-end can pick it up.
/// </summary>
/// <param name="store">The run store.</param>
/// <param name="executors">
///     Creates the executor for an agent id; defaults to the agents in the store's afl home (<see cref="AgentStore" />).
/// </param>
public class RunEngine(RunStore store, Func<string, AbstractStepExecutor>? executors = null)
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RunEngine" /> class that uses one executor for every agent (for
    ///     tests).
    /// </summary>
    /// <param name="store">The run store.</param>
    /// <param name="executorOverride">The executor for every agent.</param>
    public RunEngine(RunStore store, AbstractStepExecutor executorOverride)
        : this(store, _ => executorOverride)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RunEngine" /> class.
    /// </summary>
    /// <param name="paths">The afl paths.</param>
    public RunEngine(AflPaths paths)
        : this(new RunStore(paths))
    {
    }

    /// <summary>
    ///     Gets the run store.
    /// </summary>
    public RunStore Store { get; } = store;

    /// <summary>
    ///     Gets or sets the agent store: the agents a step chosen by capability can go to and, unless executors were
    ///     injected, where each agent's executor comes from.
    /// </summary>
    public AgentStore Agents { get; init; } = new(store.Paths);

    /// <summary>
    ///     Gets or sets the global settings store (the agent preference order and default selector).
    /// </summary>
    public SettingsStore Settings { get; init; } = new(store.Paths);

    /// <summary>
    ///     Gets or sets the ticket sources tickets the AI files go to.
    /// </summary>
    public TicketSourceRegistry Tickets { get; init; } = new() { JiraInstances = new JiraInstanceStore(store.Paths), GitHosts = new GitHostStore(store.Paths) };

    /// <summary>
    ///     Gets or sets the repo registry, which keeps the folders created for each repo's runs.
    /// </summary>
    public RepoRegistry Repos { get; init; } = new(store.Paths);

    /// <summary>
    ///     Gets or sets what tells the user, by desktop notification, when a run newly needs them or finishes; null (the
    ///     default, and what tests use) for no notifications.
    /// </summary>
    public RunNotifier? Notifier { get; init; }

    /// <summary>
    ///     Creates and saves a new run, without starting it.
    /// </summary>
    /// <param name="repo">The repo to work in.</param>
    /// <param name="workflow">The workflow to run.</param>
    /// <param name="ticket">The ticket to work on.</param>
    /// <param name="agent">The id of the agent doing AI work for steps that don't pick their own.</param>
    /// <param name="mode">How agents' commands run (a <see cref="RunModeRegistry" /> name); null for the settings' default.</param>
    /// <param name="parameters">The values for the workflow's parameters, by id.</param>
    /// <param name="maxParallel">How many paths or child runs may run at once; null for no limit.</param>
    /// <param name="optionalSteps">
    ///     Whether to include each optional step (<see cref="AbstractStepDefinition.Optional" />), by step id; the ones not
    ///     listed follow their defaults.
    /// </param>
    /// <param name="permissions">
    ///     The permission policy (a <see cref="PermissionPolicyRegistry" /> name); null for the settings' default.
    /// </param>
    /// <param name="skills">The skills the run's agent steps use in place of the workflow's own; null for the workflow's.</param>
    /// <returns>The run.</returns>
    /// <exception cref="AflException">
    ///     The workflow is invalid or can't be expanded, an agent is unknown or can't do its steps, the mode or
    ///     permission policy is unknown, a skill is unknown, or a choice names a step that isn't optional.
    /// </exception>
    public Run Create(
        RepoRecord repo,
        WorkflowDefinition workflow,
        Ticket ticket,
        string agent = ExecutorRegistry.DefaultExecutor,
        string? mode = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        int? maxParallel = null,
        IReadOnlyDictionary<string, bool>? optionalSteps = null,
        string? permissions = null,
        IReadOnlyList<string>? skills = null)
    {
        var policy = permissions is null
            ? PermissionPolicyRegistry.Instance.Resolve(this.Settings.Load().DefaultPermissionPolicy)
            : PermissionPolicyRegistry.Instance.Require(permissions);
        return this.Create(repo, workflow, ticket, agent, mode, parameters, maxParallel, optionalSteps, null,
            new RunChoices(policy.Name, [], skills?.ToList()));
    }

    /// <summary>
    ///     Creates and saves an authoring run for the AI authoring helper, without starting it. It is
    ///     <see cref="RunKind.Authoring" />, so run lists leave it out, and it asks the user before any tool call its
    ///     workflow doesn't allow (the <c>ask</c> permission policy), whatever the settings' default.
    /// </summary>
    /// <param name="repo">Where the run works: the authoring session's folder, as a repo.</param>
    /// <param name="workflow">The workflow drafting the document.</param>
    /// <param name="ticket">What to draft, as a ticket.</param>
    /// <param name="agent">The id of the agent drafting it.</param>
    /// <returns>The run.</returns>
    /// <exception cref="AflException">The workflow is invalid, or the agent is unknown or can't do its steps.</exception>
    public Run CreateAuthoring(RepoRecord repo, WorkflowDefinition workflow, Ticket ticket, string agent)
    {
        return this.Create(repo, workflow, ticket, agent, AgentConstants.RunModeBackground, null, null, null, null,
            new RunChoices(PermissionConstants.PolicyAsk, [], null, RunKind.Authoring));
    }

    /// <summary>
    ///     Creates and saves a child run for a <c>workflow</c> step, without starting it. It works in the parent's repo,
    ///     working copy and scratch directory, in the parent's mode, and is single-threaded if the parent is.
    /// </summary>
    /// <param name="parent">The parent run.</param>
    /// <param name="stepId">The parent's step starting it.</param>
    /// <param name="workflow">The workflow to run.</param>
    /// <param name="ticket">The ticket to work on.</param>
    /// <param name="agent">The id of the child's main agent.</param>
    /// <param name="parameters">The values for the workflow's parameters, by id.</param>
    /// <returns>The child run.</returns>
    /// <exception cref="AflException">The child would be nested too deep, or can't be created.</exception>
    public Run CreateChild(Run parent, string stepId, WorkflowDefinition workflow, Ticket ticket, string agent,
        IReadOnlyDictionary<string, string>? parameters)
    {
        if (parent.Depth + 1 > StepConstants.MaxRunDepth)
        {
            throw new AflException(AflErrorKind.InvalidWorkflow,
                $"Run {parent.Id} is already {parent.Depth} child runs deep; child runs can be nested at most {StepConstants.MaxRunDepth} deep.");
        }

        var repo = new RepoRecord { Id = parent.RepoId, Name = parent.RepoName, Path = parent.RepoPath, Host = parent.RepoHost };
        var choices = new RunChoices(PermissionPolicyRegistry.Instance.Resolve(parent.PermissionPolicy).Name, parent.PermissionRules.Select(r => new PermissionRule
        {
            Rule = r.Rule, Kind = r.Kind, AddedAt = r.AddedAt, Note = r.Note,
        }).ToList(), parent.Skills?.ToList());
        return this.Create(repo, workflow, ticket, agent, parent.Mode, parameters, parent.MaxParallel, null, (parent, stepId), choices);
    }

    /// <summary>
    ///     Drives a child run on for the <c>workflow</c> step that started it, like <see cref="DriveAsync" /> but without
    ///     waking the parent afterwards: the step driving it holds the parent's lock and looks at the result itself.
    /// </summary>
    /// <param name="runId">The child run id.</param>
    /// <param name="retry">Whether to retry its failed step first.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="prepare">Work to do on the locked child before it is driven (e.g. giving it its workspace), if any.</param>
    /// <returns>The updated child run.</returns>
    public Task<Run> DriveChildAsync(
        string runId,
        bool retry,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct,
        Func<Run, Task>? prepare = null)
    {
        return this.WorkAsync(runId, false, async (run, executors) =>
        {
            if (prepare is not null)
            {
                await prepare(run);
            }

            if (retry && run.CurrentStep is { Status: StepStatus.Failed } failed)
            {
                await this.RetryStepAsync(this.CreateContext(run, failed, executors, progress), ct);
            }
        }, progress, ct, false);
    }

    /// <summary>
    ///     Creates and saves a path run for one of a run's paths: a copy of the run (so the steps before can be read)
    ///     that does just the path's step (<see cref="Run.StopAfterStep" />), which the run then merges back and routes.
    ///     It works where the run does, doesn't count as a level of nesting, and is folded under the run rather than
    ///     listed.
    /// </summary>
    /// <param name="parent">The run whose path it is.</param>
    /// <param name="hostStepId">The step waiting on the paths.</param>
    /// <param name="path">The path.</param>
    /// <returns>The path run.</returns>
    public Run CreatePathRun(Run parent, string hostStepId, ActivePath path)
    {
        var pathRun = JsonSerializer.Deserialize<Run>(JsonSerializer.Serialize(parent, JsonConstants.Options), JsonConstants.Options)!;
        pathRun.Id = this.Store.NewId();
        pathRun.ParentRunId = parent.Id;
        pathRun.ParentStepId = hostStepId;
        pathRun.Kind = RunKind.Path;
        pathRun.StopAfterStep = true;
        pathRun.CurrentStepId = path.StepId;
        pathRun.Status = RunStatus.Running;
        pathRun.Paths = [];
        pathRun.Arrows = [];
        pathRun.Workspace = null;
        pathRun.WorkspaceBranch = null;
        pathRun.Path = [];
        pathRun.CreatedAt = DateTimeOffset.Now;

        // From inside the path run the host is simply done: its paths belong to the run.
        if (pathRun.StepOf(hostStepId) is { } host)
        {
            host.Status = StepStatus.Done;
            host.Paths = null;
        }

        this.Store.Save(pathRun);
        return pathRun;
    }

    /// <summary>
    ///     Creates the context for another step of the run a context is for, with the same executors and progress.
    /// </summary>
    /// <param name="from">The context.</param>
    /// <param name="state">The other step.</param>
    /// <returns>Its context.</returns>
    public StepContext ContextFor(StepContext from, RunStep state)
    {
        return this.CreateContext(from.Run, state, from.ExecutorFor, from.Progress);
    }

    /// <summary>
    ///     Finds the run to act on for a step: the run itself, or, while it waits on a fork, the path run (or the
    ///     path run's path run) that holds the step.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="stepId">The step to act on, or null for the one step waiting.</param>
    /// <returns>The id of the run holding the step.</returns>
    /// <exception cref="AflException">No step is given and several are waiting, or the named one isn't waiting.</exception>
    public string ResolveTarget(string runId, string? stepId = null)
    {
        var run = this.Store.GetRequired(runId);
        var waiting = this.Store.WaitingSteps(run);
        if (stepId is not null)
        {
            if (waiting.FirstOrDefault(w => w.Step.StepId == stepId) is { Run: { } holder })
            {
                return holder.Id;
            }

            if (run.CurrentStep is { } current && current.StepId != stepId && waiting.Count > 0)
            {
                throw new AflException(AflErrorKind.InvalidState,
                    $"Step '{stepId}' isn't waiting in run {run.Id}. Waiting: {DescribeWaiting(waiting)}.");
            }

            return run.Id;
        }

        if (waiting.Count > 1)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"Run {run.Id} has several steps waiting: {DescribeWaiting(waiting)}. Say which with --step.");
        }

        return waiting.Count == 1 ? waiting[0].Run.Id : run.Id;
    }

    /// <summary>
    ///     Lists waiting steps for a message.
    /// </summary>
    /// <param name="waiting">The steps.</param>
    /// <returns>The list.</returns>
    private static string DescribeWaiting(IEnumerable<(Run Run, RunStep Step)> waiting)
    {
        return string.Join(", ", waiting.Select(w => $"'{w.Step.StepId}' ({JsonNamingPolicy.KebabCaseLower.ConvertName(w.Step.Status.ToString())}{(w.Run.IsPathRun ? $", path run {w.Run.Id}" : string.Empty)})"));
    }

    /// <summary>
    ///     Creates and saves a new run, without starting it.
    /// </summary>
    /// <param name="repo">The repo to work in.</param>
    /// <param name="workflow">The workflow to run.</param>
    /// <param name="ticket">The ticket to work on.</param>
    /// <param name="agent">The id of the agent doing AI work for steps that don't pick their own.</param>
    /// <param name="mode">How agents' commands run; null for the settings' default.</param>
    /// <param name="parameters">The values for the workflow's parameters, by id.</param>
    /// <param name="maxParallel">How many paths or child runs may run at once; null for no limit.</param>
    /// <param name="optionalSteps">Whether to include each optional step, by step id; null for their defaults.</param>
    /// <param name="parent">The parent run and its step starting this one, for a child run.</param>
    /// <param name="choices">The permission policy, rules and skills the run starts with.</param>
    /// <returns>The run.</returns>
    private Run Create(
        RepoRecord repo,
        WorkflowDefinition workflow,
        Ticket ticket,
        string agent,
        string? mode,
        IReadOnlyDictionary<string, string>? parameters,
        int? maxParallel,
        IReadOnlyDictionary<string, bool>? optionalSteps,
        (Run Run, string StepId)? parent,
        RunChoices choices)
    {
        var settings = this.Settings.Load();
        mode ??= settings.DefaultRunMode;
        if (!RunModeRegistry.Instance.Contains(mode))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"There's no run mode '{mode}'. Use one of {string.Join(", ", RunModeRegistry.Instance.Keys.Order())}.");
        }

        // Only afl's own authoring workflows may use internal step types.
        List<string> internalSteps = choices.Kind == RunKind.Authoring ? [] : WorkflowLibrary.InternalStepProblems(workflow).ToList();
        if (internalSteps.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidWorkflow,
                $"Workflow '{workflow.Id}' is invalid:\n  - {string.Join("\n  - ", internalSteps)}");
        }

        var composer = new WorkflowComposer(new WorkflowLibrary(this.Store.Paths, repo.Path));
        composer.ThrowIfInvalid(workflow);
        var definition = workflow;
        if (choices.Skills is { } picked)
        {
            ThrowIfUnknownSkills(new SkillLibrary(this.Store.Paths, repo.Path), picked);
        }

        workflow = composer.Expand(workflow, parameters, choices.Skills);
        this.ThrowIfAgentsUnusable(workflow, agent);

        var now = DateTimeOffset.Now;
        var run = new Run
        {
            Id = this.Store.NewId(),
            Kind = choices.Kind,
            RepoId = repo.Id,
            RepoName = repo.Name,
            RepoPath = repo.Path,
            RepoHost = repo.Host,
            WorkflowId = definition.Id,
            Workflow = workflow,
            ParentRunId = parent?.Run.Id,
            ParentStepId = parent?.StepId,
            Depth = parent is null ? 0 : parent.Value.Run.Depth + 1,
            Ticket = ticket,
            Executor = agent,
            Mode = mode,
            ScratchDirectory = parent?.Run.ScratchDirectory ?? ScratchArea.DirectoryFor(settings.ScratchDirectory, ticket),
            WorkingPath = parent?.Run.WorkingPath,
            MaxParallel = maxParallel,
            PermissionPolicy = choices.Policy,
            PermissionRules = choices.Rules,
            Skills = choices.Skills,
            LeftOutSteps = workflow.LeftOutSteps(optionalSteps),
            CurrentStepId = workflow.StartStepId,
            CreatedAt = now,
            Steps = workflow.Steps.Select(s => new RunStep { StepId = s.Id, Name = s.Name }).ToList(),
        };
        if (UsesScratch(workflow))
        {
            ScratchArea.Ensure(run.RepoPath, run.ScratchDirectory);
        }

        this.Store.Save(run);
        return run;
    }

    /// <summary>
    ///     Insists that every skill picked for a run exists.
    /// </summary>
    /// <param name="library">The skills.</param>
    /// <param name="skills">The picked ids.</param>
    /// <exception cref="AflException">One is unknown.</exception>
    private static void ThrowIfUnknownSkills(SkillLibrary library, IEnumerable<string> skills)
    {
        var known = library.List().Select(s => s.Skill.Id).ToHashSet(StringComparer.Ordinal);
        // A template such as {{params.x}} is filled in and checked when the workflow is expanded.
        var unknown = skills.Where(s => !known.Contains(s) && !s.Contains("{{", StringComparison.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"There's no skill {string.Join(", ", unknown.Select(s => $"'{s}'"))}. Use one of {string.Join(", ", known.Order())}.");
        }
    }

    /// <summary>
    ///     Returns whether an expanded workflow refers to any scratch variable, so its scratch directory should exist.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>True if it does.</returns>
    private static bool UsesScratch(WorkflowDefinition workflow)
    {
        return PromptTemplate.References(JsonSerializer.Serialize(workflow, JsonConstants.Options))
            .Any(n => n.StartsWith(ScratchArea.VariablePrefix, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Starts pending steps until the run finishes or a step needs someone.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> DriveAsync(string runId, IProgress<ExecutorEvent>? progress = null, CancellationToken ct = default)
    {
        return this.ActAsync(runId, null, progress, ct);
    }

    /// <summary>
    ///     Approves the current step's AI output and continues.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="answers">The answers to the step's inputs, by input id, if any.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> ApproveAsync(
        string runId,
        IReadOnlyDictionary<string, string>? answers = null,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        return this.ActAsync(runId, (h, c) => h.ApproveAsync(c, answers, ct), progress, ct);
    }

    /// <summary>
    ///     Rejects the current step, which is waiting for approval (or for a choice between agents' results): it is redone with the feedback (the AI continues its
    ///     conversation, a user step reopens with the feedback, an interactive session reopens). The rejected attempt is
    ///     kept in the step's <see cref="RunStep.History" />.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> RejectAsync(string runId, string feedback, IProgress<ExecutorEvent>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedback))
        {
            throw new AflException(AflErrorKind.InvalidInput, "Rejecting needs feedback saying what to change.");
        }

        return this.SendBackAsync(runId, run =>
        {
            if (run.IsFinished || run.CurrentStep is not { Status: StepStatus.AwaitingApproval or StepStatus.AwaitingChoice })
            {
                throw new AflException(AflErrorKind.InvalidState,
                    $"Run {run.Id} has no step waiting for approval or a choice; use replay to redo a step.");
            }

            return run.CurrentStepId!;
        }, feedback.Trim(), progress, ct, true);
    }

    /// <summary>
    ///     Answers the open questions the AI left at the end of the current step's output (<see cref="RunStep.OpenQuestions" />)
    ///     by sending the step back with the answers as feedback; questions left unanswered keep the AI's assumption.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="answers">The answers by question number.</param>
    /// <param name="note">Anything else to change, if anything.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> AnswerQuestionsAsync(string runId, IReadOnlyDictionary<int, string> answers, string? note,
        IProgress<ExecutorEvent>? progress = null, CancellationToken ct = default)
    {
        var run = this.Store.GetRequired(runId);
        var questions = run is { IsFinished: false, CurrentStep: { } state } ? state.OpenQuestions : [];
        if (questions.Count == 0)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"Run {run.Id} has no step waiting for approval with open questions; send it back with feedback instead.");
        }

        return this.RejectAsync(run.Id, OpenQuestion.FormatAnswers(questions, answers, note), progress, ct);
    }

    /// <summary>
    ///     Replays a step that has run, whatever its kind and whatever state the run is in: the step and every later
    ///     step that ran are reset (their attempts kept in <see cref="RunStep.History" />) and the run continues from
    ///     the step. With feedback the step is redone with it (an AI step continues its conversation); without, it
    ///     starts again from scratch.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="stepId">The id of the step to replay.</param>
    /// <param name="feedback">What to change, if anything.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> ReplayAsync(
        string runId,
        string stepId,
        string? feedback = null,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        feedback = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
        var parent = this.Store.GetRequired(runId);

        // While the run is split, a step after the split is replayed on its path; the other paths keep going.
        if (parent is { IsFinished: false, Paths.Count: > 0, CurrentStep: { Status: StepStatus.AwaitingPaths } host } &&
            FlowMachine.Of(parent.Workflow).Descendants(host.StepId).Contains(stepId) && parent.StepOf(stepId) is { HasRun: true } or { Status: StepStatus.Failed })
        {
            return this.ReplayInPathRunAsync(parent.Id, stepId, feedback, progress, ct);
        }

        return this.SendBackAsync(runId, run =>
        {
            if (run.StepOf(stepId) is not { } target)
            {
                throw new AflException(AflErrorKind.NotFound,
                    $"Run {run.Id} has no step '{stepId}'. Steps: {string.Join(", ", run.Steps.Select(s => s.StepId))}.");
            }

            if (!target.HasRun)
            {
                throw new AflException(AflErrorKind.InvalidState,
                    $"Step '{stepId}' hasn't run (or was skipped); there's nothing to replay.");
            }

            var later = FlowMachine.Of(run.Workflow).Descendants(stepId);
            foreach (var step in run.Steps.Where(s => s.StepId == stepId || later.Contains(s.StepId)))
            {
                step.Runs = 0;
                step.Rounds = 0;
            }

            return stepId;
        }, feedback, progress, ct);
    }

    /// <summary>
    ///     Replays a step on one of a split run's paths. If a path run is still on the step, the step is replayed there;
    ///     otherwise the step and the steps after it are reset (the paths on them cancelled) and it starts again as a
    ///     path of its own. Either way the other paths keep what they've done.
    /// </summary>
    /// <param name="runId">The split run.</param>
    /// <param name="stepId">The step to replay.</param>
    /// <param name="feedback">What to change, if anything.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    private async Task<Run> ReplayInPathRunAsync(
        string runId,
        string stepId,
        string? feedback,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct)
    {
        string? pathRunId = null;
        using (var runLock = this.Store.Lock(this.Store.GetRequired(runId)))
        {
            var run = this.Store.GetRequired(runId);
            var host = run.CurrentStep!;
            var path = run.Paths.FirstOrDefault(p => p.StepId == stepId);
            var record = path?.RunId is { } id ? host.Paths?.LastOrDefault(c => c.RunId == id) : null;
            if (record is { Merged: not true } && this.Store.Get(record.RunId) is { Status: not RunStatus.Cancelled } existing &&
                existing.StepOf(stepId) is { HasRun: true } or { Status: StepStatus.Failed })
            {
                pathRunId = existing.Id;
            }
            else
            {
                var machine = FlowMachine.Of(run.Workflow);
                var arrivedFrom = run.StepOf(stepId)?.ArrivedFrom;
                var reset = machine.Descendants(stepId).Append(stepId).Where(s => s != host.StepId).ToHashSet();
                foreach (var other in run.Paths.Where(p => reset.Contains(p.StepId)).ToList())
                {
                    if (other.RunId is { } otherId && host.Paths?.LastOrDefault(c => c.RunId == otherId) is { } child)
                    {
                        if (this.Store.Get(child.RunId) is { IsFinished: false })
                        {
                            this.Cancel(child.RunId);
                        }

                        child.Status = RunStatus.Cancelled;
                    }

                    run.Paths.Remove(other);
                }

                foreach (var step in run.Steps.Where(s => reset.Contains(s.StepId)))
                {
                    if (step.Status != StepStatus.Pending)
                    {
                        step.Archive(AttemptCause.Replay, null, false);
                    }

                    foreach (var arrow in machine.ArrowsOf(step.StepId))
                    {
                        run.Arrows.Remove(FlowMachine.KeyOf(step.StepId, arrow));
                    }
                }

                // It goes back into the lane it ran in, so a step in a worktree is replayed in one.
                var last = host.Paths?.LastOrDefault(c => c.Item == stepId);
                run.Paths.Add(new ActivePath
                {
                    StepId = stepId, Origin = last?.Branch ?? stepId, Lane = last?.Lane, OuterLane = last?.OuterLane,
                    ArrivedFrom = arrivedFrom,
                });
                if (feedback is not null)
                {
                    run.StepOf(stepId)!.Feedback.Add(feedback);
                }
            }

            host.Error = null;
            host.EndedAt = null;
            if (host.Status != StepStatus.AwaitingPaths)
            {
                StepLifecycle.Instance.Move(host, StepStatus.AwaitingPaths);
            }

            run.Status = RunStatus.Waiting;
            this.Store.Save(run);
        }

        if (pathRunId is not null)
        {
            await this.ReplayAsync(pathRunId, stepId, feedback, progress, ct);
        }

        var parent = this.Store.GetRequired(runId);
        return parent.IsFinished ? parent : await this.DriveAsync(runId, progress, ct);
    }

    /// <summary>
    ///     Marks the current step's user or interactive work as done and continues.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="note">An optional note on what was done.</param>
    /// <param name="answers">The answers to the step's inputs, by input id, if any.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> CompleteAsync(
        string runId,
        string? note = null,
        IReadOnlyDictionary<string, string>? answers = null,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        return this.ActAsync(runId, (h, c) => h.CompleteAsync(c, note, answers, ct), progress, ct);
    }

    /// <summary>
    ///     Fails the current step, which waits for the calling Claude session or a person, because they can't do it,
    ///     and continues: an arrow on failure handles it, or the run fails and the step can be retried.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="reason">Why it failed; becomes the step's error.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    /// <exception cref="AflException">No reason is given.</exception>
    public Task<Run> GiveUpAsync(string runId, string reason, IProgress<ExecutorEvent>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new AflException(AflErrorKind.InvalidInput, "Failing a step needs a reason saying why.");
        }

        return this.ActAsync(runId, (h, c) => h.GiveUpAsync(c, reason.Trim(), ct), progress, ct);
    }

    /// <summary>
    ///     Gives the current step the output the calling Claude session produced, and continues.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="output">The output.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="agent">For a step several agents do, the agent whose result this is; null for the first one waiting.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> SubmitAsync(
        string runId,
        string output,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default,
        string? agent = null)
    {
        return this.ActAsync(runId, (h, c) => h.SubmitAsync(c, output, agent, ct), progress, ct);
    }

    /// <summary>
    ///     Uses one agent's result as the output of the current step, which several agents did and is waiting for a
    ///     choice, and continues.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="agent">The agent whose result to use.</param>
    /// <param name="output">The result as edited by the user, or null to use it as it is.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> ChooseAsync(
        string runId,
        string agent,
        string? output = null,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        return this.ActAsync(runId, (h, c) => h.ChooseAsync(c, agent, output, ct), progress, ct);
    }

    /// <summary>
    ///     Has an agent merge the results of the current step, which several agents did and is waiting for a choice,
    ///     and continues.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="agent">The agent that merges; the step's merge agent when null.</param>
    /// <param name="note">Extra instructions for the merge, if any.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> MergeAsync(
        string runId,
        string? agent = null,
        string? note = null,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        return this.ActAsync(runId, (h, c) => h.MergeAsync(c, agent, note, ct), progress, ct);
    }

    /// <summary>
    ///     Answers what the current step is asking (<see cref="StepStatus.AwaitingInput" />): its <c>before</c> inputs,
    ///     or the AI's questions. The step then starts, or the AI carries on.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="answers">The answers, by question id.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> AnswerAsync(
        string runId,
        IReadOnlyDictionary<string, string>? answers,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        return this.ActAsync(runId, (h, c) => h.AnswerAsync(c, answers, ct), progress, ct);
    }

    /// <summary>
    ///     Decides on the tool calls a step's agent was refused (<see cref="StepStatus.AwaitingPermission" />). Once every
    ///     open request is decided the agent carries on with what was allowed; <paramref name="acceptOutput" /> takes its
    ///     output as it is instead.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="choices">The decisions, by request number.</param>
    /// <param name="acceptOutput">Whether to accept the output as it is, denying what's still undecided.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> PermitAsync(
        string runId,
        IReadOnlyList<PermissionChoice> choices,
        bool acceptOutput = false,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        return this.ActAsync(runId, (h, c) => h.PermitAsync(c, choices, acceptOutput, ct), progress, ct);
    }

    /// <summary>
    ///     Sets which agent does a step that hasn't started, or that waits at <see cref="StepStatus.ChoosingAgent" />
    ///     (which then starts it). For a step chosen by capability the agent must qualify; for any other it must be one
    ///     the step can use.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="agentId">The agent's id.</param>
    /// <param name="stepId">The step; the current one when null.</param>
    /// <param name="reason">Why, if given.</param>
    /// <param name="by">Who chose: one of <see cref="AgentConstants.PickedBy" />.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    /// <exception cref="AflException">The step or agent is unknown, or the agent can't do the step.</exception>
    public Task<Run> UseAgentAsync(
        string runId,
        string agentId,
        string? stepId = null,
        string? reason = null,
        string by = AgentConstants.ChosenByUser,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        if (!AgentConstants.PickedBy.Contains(by))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"'by' must be one of {string.Join(", ", AgentConstants.PickedBy)}.");
        }

        return this.WorkAsync(runId, false, async (run, executors) =>
        {
            var state = stepId is null ? run.CurrentStep : run.Steps.Find(s => s.StepId == stepId);
            if (state is null)
            {
                throw new AflException(AflErrorKind.NotFound,
                    $"Run {run.Id} has no step '{stepId}'. Steps: {string.Join(", ", run.Steps.Select(s => s.StepId))}.");
            }

            var definition = run.DefinitionOf(state);
            var handler = StepHandlerRegistry.Instance.For(definition);
            var agent = this.Agents.GetRequired(agentId);
            var pool = new AgentPool(this.Agents, this.Settings, executors);
            var problems = definition is AbstractAgentStepDefinition { ChoosesAgent: true }
                ? pool.WhyNot(handler, definition, run.Executor, agent)
                : handler.CheckAgent(definition, executors(agent.Id)).ToList();
            if (problems.Count > 0)
            {
                throw new AflException(AflErrorKind.InvalidInput,
                    $"Agent '{agent.Id}' can't do step '{state.StepId}': {string.Join("; ", problems)}.");
            }

            var choice = new AgentChoice { AgentId = agent.Id, Reason = reason, By = by };
            var context = this.CreateContext(run, state, executors, progress);
            await handler.UseAgentAsync(context, choice, ct);
            this.Store.Save(run);
        }, progress, ct);
    }

    /// <summary>
    ///     Restarts the current step after a failure.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    public Task<Run> RetryAsync(string runId, IProgress<ExecutorEvent>? progress = null, CancellationToken ct = default)
    {
        return this.ActAsync(runId, (_, c) =>
        {
            if (c.State.Status != StepStatus.Failed)
            {
                throw new AflException(AflErrorKind.InvalidState, $"Step '{c.State.StepId}' hasn't failed; nothing to retry.");
            }

            return this.RetryStepAsync(c, ct);
        }, progress, ct);
    }

    /// <summary>
    ///     Adds a note to the current step (or the last step, once the run is done).
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="note">The note.</param>
    /// <returns>The updated run.</returns>
    public Run AddNote(string runId, string note)
    {
        return this.Edit(runId, run => (run.CurrentStep ?? run.Steps.Last()).Notes.Add(note));
    }

    /// <summary>
    ///     Gives a run a name of its own, shown instead of its ticket's title.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="title">The name; blank to show the ticket's title again.</param>
    /// <returns>The updated run.</returns>
    public Run Rename(string runId, string? title)
    {
        var normalized = RunLabels.NormalizeTitle(title);
        return this.Edit(runId, run => run.Title = normalized);
    }

    /// <summary>
    ///     Adds tags to a run and removes others (<see cref="RunLabels.NormalizeTags" />). Tags are matched ignoring case.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="add">The tags to add.</param>
    /// <param name="remove">The tags to remove.</param>
    /// <returns>The updated run.</returns>
    /// <exception cref="AflException">A tag has a comma in it.</exception>
    public Run Tag(string runId, IEnumerable<string> add, IEnumerable<string>? remove = null)
    {
        var adding = RunLabels.NormalizeTags(add);
        var removing = RunLabels.NormalizeTags(remove ?? []);
        return this.Edit(runId, run =>
            run.Tags = RunLabels.NormalizeTags(run.Tags.Where(t => !removing.Contains(t, RunLabels.Comparer)).Concat(adding)));
    }

    /// <summary>
    ///     Replaces a run's tags.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="tags">The tags.</param>
    /// <returns>The updated run.</returns>
    /// <exception cref="AflException">A tag has a comma in it.</exception>
    public Run SetTags(string runId, IEnumerable<string> tags)
    {
        var normalized = RunLabels.NormalizeTags(tags);
        return this.Edit(runId, run => run.Tags = normalized);
    }

    /// <summary>
    ///     Lists the folders of a repo's runs: those created for it and those runs are filed in, with the folders they're
    ///     in, sorted.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <returns>The folder paths.</returns>
    public IReadOnlyList<string> ListFolders(string repoId)
    {
        var created = this.Repos.Find(repoId)?.RunFolders ?? [];
        return RunLabels.WithParents(created.Concat(this.Store.List(repoId).Select(r => r.Folder)));
    }

    /// <summary>
    ///     Creates a folder for a repo's runs, with the folders it's in; it's listed while it's empty.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="folder">The folder path, e.g. <c>billing/q4</c>.</param>
    /// <returns>The folder's path, spelled as it already was if it existed.</returns>
    /// <exception cref="AflException">The path is blank or has <c>.</c> or <c>..</c> in it, or no such repo.</exception>
    public string CreateFolder(string repoId, string folder)
    {
        var path = this.SpellFolder(repoId, RunLabels.RequireFolder(folder));
        this.Repos.Modify(repoId, repo => AddFolder(repo, path));
        return path;
    }

    /// <summary>
    ///     Files a run in a folder, creating the folder if it's new. Child and path runs are shown under the run that
    ///     started them, so only a run started directly can be filed.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="folder">The folder path; blank for the top level.</param>
    /// <returns>The updated run.</returns>
    /// <exception cref="AflException">The run was started by another, or the path has <c>.</c> or <c>..</c> in it.</exception>
    public Run MoveToFolder(string runId, string? folder)
    {
        var run = this.Store.GetRequired(runId);
        if (run.ParentRunId is { } parentId)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"Run {run.Id} was started by run {parentId} and is shown under it; move that run instead.");
        }

        var path = RunLabels.NormalizeFolder(folder) is { } normalized ? this.SpellFolder(run.RepoId, normalized) : null;
        if (path is not null)
        {
            this.Repos.Modify(run.RepoId, repo => AddFolder(repo, path));
        }

        return this.Edit(run.Id, r => r.Folder = path);
    }

    /// <summary>
    ///     Renames or moves a folder of a repo's runs, with the runs and folders in it.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="folder">The folder's path.</param>
    /// <param name="newFolder">Its new path, e.g. <c>billing/2026</c> to rename <c>billing/q4</c>.</param>
    /// <returns>The ids of the runs moved with it.</returns>
    /// <exception cref="AflException">No such folder, a blank new path, or a move into itself.</exception>
    public IReadOnlyList<string> RenameFolder(string repoId, string folder, string newFolder)
    {
        var from = this.RequireExistingFolder(repoId, folder);
        var to = RunLabels.RequireFolder(newFolder);
        if (RunLabels.IsWithin(to, from) && !RunLabels.Comparer.Equals(to, from))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"Folder '{from}' can't be moved into itself.");
        }

        return this.MoveFolder(repoId, from, RunLabels.Comparer.Equals(to, from) ? to : this.SpellFolder(repoId, to));
    }

    /// <summary>
    ///     Removes a folder of a repo's runs, moving the runs and folders in it up into the folder it's in, so no run is
    ///     lost.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="folder">The folder's path.</param>
    /// <returns>The ids of the runs moved up.</returns>
    /// <exception cref="AflException">No such folder.</exception>
    public IReadOnlyList<string> RemoveFolder(string repoId, string folder)
    {
        var path = this.RequireExistingFolder(repoId, folder);
        return this.MoveFolder(repoId, path, RunLabels.ParentOf(path));
    }

    /// <summary>
    ///     Cancels a run.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <returns>The updated run.</returns>
    public Run Cancel(string runId)
    {
        var run = this.Store.GetRequired(runId);
        using var runLock = this.Store.Lock(run);
        run = this.Store.GetRequired(run.Id);
        if (run.IsFinished)
        {
            throw new AflException(AflErrorKind.InvalidState, $"Run {run.Id} is already {run.Status}.");
        }

        run.Status = RunStatus.Cancelled;
        this.Store.Save(run);
        runLock.Dispose();

        // Cancel the children it started (not ones it reused, which belong to whoever started them).
        foreach (var child in run.Steps.SelectMany(s => s.ChildRuns.Concat(s.Paths ?? [])).Where(c => c.Reused != true))
        {
            if (this.Store.Get(child.RunId) is { IsFinished: false })
            {
                this.Cancel(child.RunId);
            }
        }

        this.DiscardWorkspace(run);
        return run;
    }

    /// <summary>
    ///     Deletes a finished run with its step logs, and the child runs it started (not ones it reused, which belong to
    ///     whoever started them), tidying away their workspaces. A child run goes with its parent, so it can't be deleted
    ///     on its own while the parent is kept. The ticket's scratch documents are shared between runs and are kept.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <returns>The ids of the deleted runs, children first.</returns>
    /// <exception cref="AflException">The run isn't finished, or belongs to another run.</exception>
    public IReadOnlyList<string> Delete(string runId)
    {
        var run = this.Store.GetRequired(runId);
        if (run.ParentRunId is { } parentId && this.Store.Get(parentId) is not null)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"Run {run.Id} was started by run {parentId}; delete that run and this one goes with it.");
        }

        var deleted = new List<string>();
        this.DeleteTree(run, deleted);
        return deleted;
    }

    /// <summary>
    ///     Deletes a run after the child runs it started, refusing if any of them is still going.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="deleted">Receives the ids of the deleted runs.</param>
    /// <exception cref="AflException">The run or one of its children isn't finished.</exception>
    private void DeleteTree(Run run, List<string> deleted)
    {
        if (!run.IsFinished)
        {
            throw new AflException(AflErrorKind.InvalidState, $"Run {run.Id} is {run.Status}; cancel it before deleting it.");
        }

        var children = run.Steps.SelectMany(s => s.ChildRuns.Concat(s.Paths ?? [])).Where(c => c.Reused != true).Select(c => c.RunId)
            .Concat(run.Paths.Select(p => p.RunId).OfType<string>()).Distinct();
        foreach (var childId in children)
        {
            if (this.Store.Get(childId) is { } childRun)
            {
                this.DeleteTree(childRun, deleted);
            }
        }

        using (this.Store.Lock(run))
        {
            this.DiscardWorkspace(run);
            this.Store.Delete(run.Id);
            var logs = Path.Combine(this.Store.Paths.RunsDirectoryFor(run.RepoId), run.Id);
            if (Directory.Exists(logs))
            {
                Directory.Delete(logs, true);
            }
        }

        File.Delete(this.Store.LockPath(run));
        deleted.Add(run.Id);
    }

    /// <summary>
    ///     Changes a run under its lock, reading it afresh first, and saves it.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="change">The change.</param>
    /// <returns>The updated run.</returns>
    private Run Edit(string runId, Action<Run> change)
    {
        var run = this.Store.GetRequired(runId);
        using var runLock = this.Store.Lock(run);
        run = this.Store.GetRequired(run.Id);
        change(run);
        this.Store.Save(run);
        return run;
    }

    /// <summary>
    ///     Moves a folder of a repo's runs, with what's in it, to another path.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="from">The folder's path.</param>
    /// <param name="to">Its new path, or null to empty it into the top level.</param>
    /// <returns>The ids of the runs moved.</returns>
    private List<string> MoveFolder(string repoId, string from, string? to)
    {
        this.Repos.Modify(repoId, repo =>
        {
            repo.RunFolders = [.. repo.RunFolders.Select(f => RunLabels.Rebase(f, from, to)).OfType<string>().Distinct(RunLabels.Comparer)];
            if (to is not null)
            {
                AddFolder(repo, to);
            }
        });

        var moved = new List<string>();
        foreach (var run in this.Store.List(repoId).Where(r => RunLabels.IsWithin(r.Folder, from)))
        {
            this.Edit(run.Id, r => r.Folder = RunLabels.Rebase(r.Folder, from, to));
            moved.Add(run.Id);
        }

        return moved;
    }

    /// <summary>
    ///     Returns a folder's path as the repo already spells it: the deepest folder it has on the path (ignoring case)
    ///     keeps its spelling, and the new names after it are kept as given.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="folder">The folder's path.</param>
    /// <returns>The path.</returns>
    private string SpellFolder(string repoId, string folder)
    {
        var existing = this.ListFolders(repoId).Where(f => RunLabels.IsWithin(folder, f)).MaxBy(f => f.Length);
        return existing is null ? folder : existing + folder[existing.Length..];
    }

    /// <summary>
    ///     Returns a folder's path as the repo spells it.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="folder">The folder's path.</param>
    /// <returns>The path.</returns>
    /// <exception cref="AflException">The repo has no such folder.</exception>
    private string RequireExistingFolder(string repoId, string folder)
    {
        var path = RunLabels.RequireFolder(folder);
        return this.ListFolders(repoId).FirstOrDefault(f => RunLabels.Comparer.Equals(f, path))
               ?? throw new AflException(AflErrorKind.NotFound, $"There's no folder '{path}' for this repo's runs.");
    }

    /// <summary>
    ///     Adds a folder to a repo's created folders, if it isn't there.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="folder">The folder's path.</param>
    private static void AddFolder(RepoRecord repo, string folder)
    {
        if (!repo.RunFolders.Contains(folder, RunLabels.Comparer))
        {
            repo.RunFolders.Add(folder);
        }
    }

    /// <summary>
    ///     Tidies away a cancelled or deleted run's own workspace (a worktree its parent's step gave it), keeping its branch. Best
    ///     effort: the parent's step tries again when it next looks at the run.
    /// </summary>
    /// <param name="run">The cancelled or deleted run.</param>
    private void DiscardWorkspace(Run run)
    {
        if (run.Workspace is null || run.ParentRunId is null || this.Store.Get(run.ParentRunId) is not { } parent ||
            !WorkspaceRegistry.Instance.Contains(run.Workspace))
        {
            return;
        }

        try
        {
            // Safe to wait on: workspaces use ConfigureAwait(false) throughout, so this doesn't deadlock on the UI thread.
            var context = new WorkspaceContext(this.Store.Paths, parent, run, run.Ticket.Key, false);
            WorkspaceRegistry.Instance.Create(run.Workspace).DiscardAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (AflException)
        {
            // Left for the parent's step to settle.
        }
    }

    /// <summary>
    ///     Creates the executor for an agent.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The executor.</returns>
    /// <exception cref="AflException">No agent has that id.</exception>
    protected virtual AbstractStepExecutor CreateExecutor(string agentId)
    {
        return executors is null ? this.Agents.CreateExecutor(agentId) : executors(agentId);
    }

    /// <summary>
    ///     Checks that the run's agent and every step's agent exist and can do their steps.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="agent">The run's agent.</param>
    /// <exception cref="AflException">An agent is unknown or can't do a step.</exception>
    private void ThrowIfAgentsUnusable(WorkflowDefinition workflow, string agent)
    {
        var resolve = this.CreateExecutorCache();
        resolve(agent);
        var pool = new AgentPool(this.Agents, this.Settings, resolve);
        var problems = workflow.Steps
            .SelectMany(step =>
            {
                var handler = StepHandlerRegistry.Instance.For(step);
                foreach (var other in step.FlowConditions().Select(c => c.Agent).Append((step as AbstractAgentStepDefinition)?.MergeAgent).OfType<string>())
                {
                    resolve(other);
                }

                var unknown = ((step as AbstractAgentStepDefinition)?.McpServers ?? []).Where(id => this.Agents.Servers.Find(id) is null).ToList();
                if (unknown.Count > 0)
                {
                    return [$"Step '{step.Id}': there's no MCP server {string.Join(", ", unknown.Select(id => $"'{id}'"))}. Add it with afl mcp-add or afl mcp-import."];
                }

                if (step is AbstractAgentStepDefinition { ChoosesAgent: true } && handler.UsesAgent)
                {
                    // The preferred agent is only a preference: any agent that qualifies will do.
                    return pool.Qualifying(handler, step, agent).Count > 0
                        ? []
                        : [$"Step '{step.Id}': {pool.DescribeShortfall(handler, step, agent)}"];
                }

                return (step is AbstractAgentStepDefinition agentStep ? agentStep.AgentIds(agent) : [step.Agent ?? agent])
                    .SelectMany(id => handler.CheckAgent(step, resolve(id)))
                    .Select(problem => $"Step '{step.Id}': {problem}");
            })
            .ToList();
        if (problems.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, string.Join(" ", problems));
        }
    }

    /// <summary>
    ///     Returns a function that creates each agent's executor once.
    /// </summary>
    /// <returns>The function.</returns>
    private Func<string, AbstractStepExecutor> CreateExecutorCache()
    {
        var cache = new Dictionary<string, AbstractStepExecutor>(StringComparer.OrdinalIgnoreCase);
        return id =>
        {
            if (!cache.TryGetValue(id, out var executor))
            {
                executor = this.CreateExecutor(id);
                cache[id] = executor;
            }

            return executor;
        };
    }

    /// <summary>
    ///     Locks and reloads a run, applies an action to its current step, then drives it on.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="action">The action, or null to just drive.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    private Task<Run> ActAsync(
        string runId,
        Func<AbstractStepHandler, StepContext, Task>? action,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct)
    {
        if (action is not null)
        {
            runId = this.ResolveTarget(runId);
        }

        return this.WorkAsync(runId, false, async (run, executors) =>
        {
            if (action is not null && run.CurrentStep is { } state)
            {
                this.RecoverInterrupted(state);
                await action(StepHandlerRegistry.Instance.For(run.DefinitionOf(state)), this.CreateContext(run, state, executors, progress));
                this.Store.Save(run);
            }
        }, progress, ct);
    }

    /// <summary>
    ///     Sends a run back to a step: archives that step's attempt and every later one that ran, resets them, then
    ///     redoes the step with the feedback (or starts it afresh without) and drives the run on.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="pick">Picks the id of the step to send back to, throwing if that isn't allowed.</param>
    /// <param name="feedback">What to change, if anything.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="resolve">Whether to act on the path run holding the waiting step, while the run waits on a fork.</param>
    /// <returns>The updated run.</returns>
    private Task<Run> SendBackAsync(
        string runId,
        Func<Run, string> pick,
        string? feedback,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct,
        bool resolve = false)
    {
        if (resolve)
        {
            runId = this.ResolveTarget(runId);
        }

        return this.WorkAsync(runId, true, async (run, executors) =>
        {
            if (run.CurrentStep is { } current)
            {
                this.RecoverInterrupted(current);
            }

            var id = pick(run);
            var target = run.StepOf(id)!;
            var machine = FlowMachine.Of(run.Workflow);
            var later = machine.Descendants(id);
            foreach (var step in run.Steps.Where(s => s.StepId == id || later.Contains(s.StepId)))
            {
                this.CancelPaths(step);
                foreach (var arrow in machine.ArrowsOf(step.StepId))
                {
                    run.Arrows.Remove(FlowMachine.KeyOf(step.StepId, arrow));
                }
            }

            run.Paths.Clear();
            run.UnhandledFailure = false;

            var answers = new Dictionary<string, string>(target.Answers);
            target.Archive(AttemptCause.Replay, feedback, feedback is not null);
            if (feedback is not null)
            {
                KeepBeforeAnswers(run.DefinitionOf(target), target, answers);
            }

            foreach (var step in run.Steps.Where(s => later.Contains(s.StepId) && s.Status != StepStatus.Pending))
            {
                step.Archive(AttemptCause.Replay, null, false);
            }

            run.CurrentStepId = id;
            run.Status = RunStatus.Running;
            progress?.Report(new ExecutorEvent(ExecutorEventKind.Status, $"Replaying step '{target.StepId}'."));
            if (feedback is null)
            {
                this.Store.Save(run);
                return;
            }

            target.Feedback.Add(feedback);
            target.StartedAt = DateTimeOffset.Now;
            this.Store.Save(run);
            await StepHandlerRegistry.Instance.For(run.DefinitionOf(target))
                .ReworkAsync(this.CreateContext(run, target, executors, progress), feedback, ct);
            this.Store.Save(run);
        }, progress, ct);
    }

    /// <summary>
    ///     Locks and reloads a run, does some work on it, then drives it on. A step left running by a cancellation is
    ///     marked failed so it can be retried.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="allowFinished">Whether the work may be done on a done or cancelled run.</param>
    /// <param name="work">The work, given the locked run and its agents' executors.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="wakeParents">
    ///     Whether to drive on the runs waiting on this one (<see cref="StepStatus.AwaitingChildren" />) once it finishes
    ///     or fails; false when the parent's step is driving it.
    /// </param>
    /// <returns>The updated run.</returns>
    private async Task<Run> WorkAsync(
        string runId,
        bool allowFinished,
        Func<Run, Func<string, AbstractStepExecutor>, Task> work,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct,
        bool wakeParents = true)
    {
        RunAttention? before = null;
        var run = await this.WorkLockedAsync(runId, allowFinished, async (locked, executors) =>
        {
            before = this.Notifier is null ? null : RunAttention.Of(locked);
            await work(locked, executors);
        }, progress, ct);
        if (this.Notifier is { } notifier && before is not null)
        {
            await notifier.NoticeAsync(before, run, progress, ct);
        }

        if (wakeParents && (run.IsFinished || run.Status == RunStatus.Failed))
        {
            await this.WakeParentsAsync(run, progress, ct);
        }

        return run;
    }

    /// <summary>
    ///     Drives on the runs whose current step waits on a run that has just finished or failed, once that run's lock
    ///     is released.
    /// </summary>
    /// <param name="child">The run that finished or failed.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    private async Task WakeParentsAsync(Run child, IProgress<ExecutorEvent>? progress, CancellationToken ct)
    {
        var waiting = this.Store.List(child.RepoId)
            .Where(r => !r.IsFinished && r.CurrentStep is { } step &&
                        ((step.Status == StepStatus.AwaitingChildren && step.ChildRuns.Any(c => c.RunId == child.Id)) ||
                         (step.Status == StepStatus.AwaitingPaths && step.Paths?.Any(c => c.RunId == child.Id) == true)))
            .ToList();
        foreach (var parent in waiting)
        {
            progress?.Report(new ExecutorEvent(ExecutorEventKind.Status, $"Run {child.Id} is {child.Status}; carrying on with run {parent.Id}."));
            await this.DriveAsync(parent.Id, progress, ct);
        }
    }

    /// <summary>
    ///     Locks and reloads a run, does some work on it, then drives it on; see <see cref="WorkAsync" />.
    /// </summary>
    /// <param name="runId">The run id or prefix.</param>
    /// <param name="allowFinished">Whether the work may be done on a done or cancelled run.</param>
    /// <param name="work">The work, given the locked run and its agents' executors.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The updated run.</returns>
    private async Task<Run> WorkLockedAsync(
        string runId,
        bool allowFinished,
        Func<Run, Func<string, AbstractStepExecutor>, Task> work,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct)
    {
        var run = this.Store.GetRequired(runId);
        using var runLock = this.Store.Lock(run);
        run = this.Store.GetRequired(run.Id);
        if (run.IsFinished && !allowFinished)
        {
            throw new AflException(AflErrorKind.InvalidState, $"Run {run.Id} is {run.Status}.");
        }

        var executors = this.CreateExecutorCache();
        try
        {
            await work(run, executors);
            await this.AdvanceAsync(run, executors, progress, ct);
        }
        catch (OperationCanceledException)
        {
            if (run.CurrentStep is { Status: StepStatus.Running } running)
            {
                StepLifecycle.Instance.Move(running, StepStatus.Failed);
                running.Error = "Interrupted.";
                running.FailureKind = StepConstants.FailureInterrupted;
                run.Status = RunStatus.Failed;
                this.Store.Save(run);
            }

            throw;
        }

        return run;
    }

    /// <summary>
    ///     Starts pending steps and follows the arrows of ended ones until a step needs someone or the run finishes.
    ///     While the run is split, its paths are looked after by <see cref="FlowCoordinator" />.
    /// </summary>
    /// <param name="run">The run (locked).</param>
    /// <param name="executors">Returns the executor for an agent id.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    private async Task AdvanceAsync(Run run, Func<string, AbstractStepExecutor> executors, IProgress<ExecutorEvent>? progress, CancellationToken ct)
    {
        while (true)
        {
            var state = run.CurrentStep;
            if (state is null)
            {
                this.End(run, run.UnhandledFailure ? RunStatus.Failed : RunStatus.Done, progress);
                return;
            }

            this.RecoverInterrupted(state);
            var context = this.CreateContext(run, state, executors, progress);
            if (state.Status == StepStatus.AwaitingPaths)
            {
                await FlowCoordinator.Instance.RefreshAsync(this, context, ct);
                this.Store.Save(run);
                if (state.Status == StepStatus.AwaitingPaths && run.CurrentStepId == state.StepId)
                {
                    run.Status = RunStatus.Waiting;
                    this.Store.Save(run);
                    return;
                }

                if (state.Status == StepStatus.Failed && run.CurrentStepId == state.StepId)
                {
                    this.FailRun(run, state, progress);
                    return;
                }

                continue;
            }

            switch (state.Status)
            {
                case StepStatus.Done:
                case StepStatus.Skipped:
                    if (run.StopAfterStep)
                    {
                        this.End(run, RunStatus.Done, progress);
                        return;
                    }

                    if (!await this.RouteAsync(context, state.Status == StepStatus.Done ? FlowTrigger.Succeeded : FlowTrigger.LeftOut, ct))
                    {
                        this.FailRun(run, state, progress);
                        return;
                    }

                    continue;
                case StepStatus.Pending:
                    if (!await this.StartAsync(context, ct))
                    {
                        this.FailRun(run, state, progress);
                        return;
                    }

                    continue;
                case StepStatus.AwaitingChildren:
                    await StepHandlerRegistry.Instance.For(run.DefinitionOf(state)).RefreshAsync(context, ct);
                    this.Store.Save(run);
                    if (state.Status == StepStatus.AwaitingChildren)
                    {
                        run.Status = RunStatus.Waiting;
                        this.Store.Save(run);
                        return;
                    }

                    continue;
                case StepStatus.Failed:
                    // An interrupted step waits for a retry, and a split run's failed host for its paths' retry.
                    if (state.FailureKind == StepConstants.FailureInterrupted || run.Paths.Count > 0)
                    {
                        this.FailRun(run, state, progress);
                        return;
                    }

                    if (await this.TryRetryAsync(context, ct))
                    {
                        continue;
                    }

                    if (run.StopAfterStep)
                    {
                        this.FailRun(run, state, progress);
                        return;
                    }

                    var trigger = state.FailureKind == StepConstants.FailureTimeout ? FlowTrigger.TimedOut : FlowTrigger.Failed;
                    if (!await this.RouteAsync(context, trigger, ct))
                    {
                        this.FailRun(run, state, progress);
                        return;
                    }

                    continue;
                default:
                    run.Status = RunStatus.Waiting;
                    this.Store.Save(run);
                    return;
            }
        }
    }

    /// <summary>
    ///     Follows the arrows of a step the run ran itself that has ended: back round a loop, on to the one step they
    ///     make ready, splitting the run when they make several ready, or ending the run's path when none.
    /// </summary>
    /// <param name="context">The step's context.</param>
    /// <param name="trigger">How it ended.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>False when the run fails: routing failed, or nothing handled the step's failure.</returns>
    private async Task<bool> RouteAsync(StepContext context, FlowTrigger trigger, CancellationToken ct)
    {
        var run = context.Run;
        var route = await FlowRouter.Instance.RouteAsync(context, trigger, ct);
        if (route is null || route.Fails)
        {
            this.Store.Save(run);
            return false;
        }

        if (route.Back is { } back)
        {
            FlowRouter.Instance.GoBack(context, back.Arrow.To);
        }
        else
        {
            var ready = route.Ready.Count == 0 ? FlowRouter.Instance.KillPending(run, FlowMachine.Of(run.Workflow)) : route.Ready;
            switch (ready.Count)
            {
                case 0:
                    run.CurrentStepId = null;
                    break;
                case 1:
                    FlowRouter.Instance.GoOn(context, ready[0]);
                    break;
                default:
                    await FlowCoordinator.Instance.SplitAsync(this, context, ready, ct);
                    break;
            }
        }

        this.Store.Save(run);
        return true;
    }

    /// <summary>
    ///     Starts a pending step, unless it was left out or the run has done its <c>maxSteps</c>; a step with a
    ///     <c>timeout</c> that runs over is cancelled and fails with <see cref="StepConstants.FailureTimeout" />.
    /// </summary>
    /// <param name="context">The step's context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>False when the run fails before the step can start.</returns>
    private async Task<bool> StartAsync(StepContext context, CancellationToken ct)
    {
        var run = context.Run;
        var state = context.State;
        run.Status = RunStatus.Running;
        if (FlowRouter.Instance.TrySkip(context))
        {
            this.Store.Save(run);
            return true;
        }

        var maxSteps = run.Workflow.MaxSteps ?? StepConstants.DefaultMaxSteps;
        if (!run.StopAfterStep && run.StepRuns >= maxSteps)
        {
            StepLifecycle.Instance.Move(state, StepStatus.Failed);
            state.Error = string.Format(StepConstants.MaxStepsReachedFormat, maxSteps, state.StepId);
            state.FailureKind = StepConstants.FailureInterrupted;
            return false;
        }

        state.StartedAt ??= DateTimeOffset.Now;
        RecordVisit(run, state);
        if (!run.StopAfterStep)
        {
            run.StepRuns++;
        }

        this.Store.Save(run);
        var handler = StepHandlerRegistry.Instance.For(context.Definition);
        if (!Durations.TryParse(context.Definition.Timeout, out var timeout) || timeout <= TimeSpan.Zero)
        {
            await handler.BeginAsync(context, ct);
            this.Store.Save(run);
            return true;
        }

        using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limited.CancelAfter(timeout);
        try
        {
            await handler.BeginAsync(context, limited.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (state.Status != StepStatus.Failed)
            {
                StepLifecycle.Instance.Move(state, StepStatus.Failed);
            }

            state.Error = $"Step '{state.StepId}' ran over its timeout of {Durations.Format(timeout)}.";
            state.FailureKind = StepConstants.FailureTimeout;
            state.EndedAt = DateTimeOffset.Now;
        }

        this.Store.Save(run);
        return true;
    }

    /// <summary>
    ///     Runs a failed step again under its <c>retry</c> policy, before any of its arrows on failure fire.
    /// </summary>
    /// <param name="context">The failed step's context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if it is going to run again.</returns>
    private async Task<bool> TryRetryAsync(StepContext context, CancellationToken ct)
    {
        var state = context.State;
        if (context.Definition.Retry is not { } retry || state.FailureKind == StepConstants.FailureGivenUp)
        {
            return false;
        }

        var retries = state.History.AsEnumerable().Reverse().TakeWhile(a => a.Cause == AttemptCause.Retry).Count();
        if (retries >= retry.Max)
        {
            return false;
        }

        if (Durations.TryParse(retry.Delay, out var delay) && delay > TimeSpan.Zero)
        {
            context.Report($"Step '{state.StepId}' failed; retrying in {Durations.Format(delay)} ({retries + 1}/{retry.Max}).");
            await Task.Delay(delay, ct);
        }
        else
        {
            context.Report($"Step '{state.StepId}' failed; retrying ({retries + 1}/{retry.Max}).");
        }

        state.Archive(AttemptCause.Retry, state.Error, false);
        this.Store.Save(context.Run);
        return true;
    }

    /// <summary>
    ///     Ends a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="status">How it ended.</param>
    /// <param name="progress">Receives live events, if set.</param>
    private void End(Run run, RunStatus status, IProgress<ExecutorEvent>? progress)
    {
        run.Status = status;
        this.Store.Save(run);
        progress?.Report(status == RunStatus.Done
            ? new ExecutorEvent(ExecutorEventKind.Status, $"Run {run.Id} is done.")
            : new ExecutorEvent(ExecutorEventKind.Error, $"Run {run.Id} failed: a step's failure wasn't handled."));
    }

    /// <summary>
    ///     Fails a run at a step that failed, leaving it to be retried.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The failed step.</param>
    /// <param name="progress">Receives live events, if set.</param>
    private void FailRun(Run run, RunStep state, IProgress<ExecutorEvent>? progress)
    {
        run.Status = RunStatus.Failed;
        this.Store.Save(run);
        progress?.Report(new ExecutorEvent(ExecutorEventKind.Error, $"Step '{state.StepId}' failed: {state.Error}"));
    }

    /// <summary>
    ///     Retries a failed step: a step whose paths failed goes back to waiting on them and gets them going again
    ///     (keeping the ones that are done); any other step goes back to pending (<see cref="ResetForRetry" />).
    /// </summary>
    /// <param name="context">The failed step's context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    private async Task RetryStepAsync(StepContext context, CancellationToken ct)
    {
        var state = context.State;
        StepLifecycle.Instance.Require(state, StepTrigger.Retry, "retried");
        context.Run.UnhandledFailure = false;
        if (context.Run.Paths.Count > 0)
        {
            StepLifecycle.Instance.Move(state, StepStatus.AwaitingPaths);
            state.Error = null;
            state.EndedAt = null;
            await FlowCoordinator.Instance.RetryFailedAsync(this, context, ct);
            return;
        }

        ResetForRetry(context.Definition, state);
    }

    /// <summary>
    ///     Cancels the path runs of a step that haven't finished, before the step is reset.
    /// </summary>
    /// <param name="state">The step.</param>
    private void CancelPaths(RunStep state)
    {
        foreach (var branch in state.Paths ?? [])
        {
            if (this.Store.Get(branch.RunId) is { IsFinished: false })
            {
                this.Cancel(branch.RunId);
            }
        }
    }

    /// <summary>
    ///     Puts a failed step back to pending so it starts again, keeping its <c>before</c> answers and the child runs it
    ///     started (which it retries rather than starting new ones).
    /// </summary>
    /// <param name="definition">The step's definition.</param>
    /// <param name="state">The step.</param>
    private static void ResetForRetry(AbstractStepDefinition definition, RunStep state)
    {
        StepLifecycle.Instance.Move(state, StepStatus.Pending);
        KeepBeforeAnswers(definition, state, new Dictionary<string, string>(state.Answers));
        state.Error = null;
        state.FailureKind = null;
        state.EndedAt = null;
    }

    /// <summary>
    ///     Leaves a step with only the answers to its <c>before</c> inputs, so redoing the same attempt doesn't ask them
    ///     again.
    /// </summary>
    /// <param name="definition">The step's definition.</param>
    /// <param name="state">The step.</param>
    /// <param name="answers">The answers the step had.</param>
    private static void KeepBeforeAnswers(AbstractStepDefinition definition, RunStep state, Dictionary<string, string> answers)
    {
        state.Answers = definition.InputsAsked(StepConstants.WhenBefore)
            .Where(i => answers.ContainsKey(i.Id))
            .ToDictionary(i => i.Id, i => answers[i.Id]);
    }

    /// <summary>
    ///     Adds a step that is starting to the run's <see cref="Run.Path" />, unless it's a retry of the same visit.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The step.</param>
    private static void RecordVisit(Run run, RunStep state)
    {
        var iteration = state.Runs + 1;
        if (run.Path.LastOrDefault() is { } last && last.StepId == state.StepId && last.Iteration == iteration)
        {
            return;
        }

        run.Path.Add(new StepVisit { StepId = state.StepId, Iteration = iteration, At = DateTimeOffset.Now });
    }

    /// <summary>
    ///     Marks a step left <see cref="StepStatus.Running" /> by a process that died as failed, so it can be retried.
    ///     Safe because a running step is only ever seen here by the process holding the run lock.
    /// </summary>
    /// <param name="state">The step.</param>
    private void RecoverInterrupted(RunStep state)
    {
        if (state.Status == StepStatus.Running)
        {
            StepLifecycle.Instance.Move(state, StepStatus.Failed);
            state.Error = "The afl process running this step stopped before it finished.";
            state.FailureKind = StepConstants.FailureInterrupted;
        }
    }

    /// <summary>
    ///     Creates the context for a step.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The step.</param>
    /// <param name="executors">Returns the executor for an agent id.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <returns>The context.</returns>
    private StepContext CreateContext(
        Run run,
        RunStep state,
        Func<string, AbstractStepExecutor> executors,
        IProgress<ExecutorEvent>? progress)
    {
        return new StepContext(run, state, executors, this.Store.LogPath(run, state.StepId), () => this.Store.Save(run), progress)
        {
            Pool = new AgentPool(this.Agents, this.Settings, executors),
            Tickets = this.Tickets,
            Repos = new RepoRegistry(this.Store.Paths),
            Skills = new SkillLibrary(this.Store.Paths, run.RepoPath),
            PermissionRules = new PermissionRuleStore(this.Store.Paths),
            Engine = this,
        };
    }
}

/// <summary>
///     What a run starts with besides its workflow: its permission policy and rules, and the skills picked for it.
/// </summary>
/// <param name="Policy">The permission policy name.</param>
/// <param name="Rules">The run's own permission rules.</param>
/// <param name="Skills">The skills in place of the workflow's own, or null for the workflow's.</param>
/// <param name="Kind">What the run is to the user.</param>
internal record RunChoices(string Policy, List<PermissionRule> Rules, List<string>? Skills, RunKind Kind = RunKind.Main);
