using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     Base for steps where the AI produces output from the step's prompt. Runs the executor, records the result, and
///     accepts output submitted by the calling Claude session; subclasses decide what happens once the AI is done.
///     <para>
///         A headless step can be done by several agents side by side (<see cref="AbstractAgentStepDefinition.Agents" />): each
///         works in its own conversation and log, their results are kept in <see cref="RunStep.Candidates" />, and once
///         they have all finished the step's <see cref="AbstractCombineStrategy" /> turns them into its output.
///     </para>
/// </summary>
public abstract class AbstractAiStepHandler : AbstractStepHandler<AbstractAgentStepDefinition>
{
    /// <summary>
    ///     Gets a value indicating whether the step's prompt runs headless (no one to answer permission prompts).
    /// </summary>
    protected virtual bool RunsHeadless => true;

    /// <inheritdoc />
    protected override bool TakesSeveralAgents => this.RunsHeadless;

    /// <inheritdoc />
    public override bool UsesAgent => true;

    /// <inheritdoc />
    protected override bool CanAiAsk(AbstractAgentStepDefinition step)
    {
        return this.RunsHeadless && !step.IsMultiAgent;
    }

    /// <inheritdoc />
    protected override IEnumerable<string> CheckAgent(AbstractAgentStepDefinition step, AbstractStepExecutor executor)
    {
        if (!this.RunsHeadless && !executor.CanRunInteractive)
        {
            yield return $"agent '{executor.Name}' can't open an interactive session (it has no interactiveArgs).";
        }

        if (step.McpServers is { Count: > 0 } && !executor.SupportsMcpServers)
        {
            yield return $"agent '{executor.Name}' can't be given MCP servers ({string.Join(", ", step.McpServers)}).";
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<string> Validate(AbstractAgentStepDefinition step)
    {
        if (string.IsNullOrWhiteSpace(step.Prompt))
        {
            yield return "an AI step needs a 'prompt'.";
        }

        if (this.RunsHeadless && step.PermissionMode == StepConstants.PlanPermissionMode)
        {
            yield return
                $"permissionMode '{StepConstants.PlanPermissionMode}' only works interactively; use '{StepConstants.ReadOnlyPermissionMode}' for a read-only step.";
        }

        if (step.Agents is not null && !this.TakesSeveralAgents)
        {
            yield return $"a {step.Type} step is done by one agent; remove 'agents'.";
        }

        foreach (var problem in ValidateAgents(step).Concat(ValidateCapabilities(step)).Concat(ValidateServers(step)))
        {
            yield return problem;
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(StepContext context, CancellationToken ct)
    {
        return this.RunAiAsync(context, context.Render(DefinitionOf(context).Prompt), false, ct);
    }

    /// <inheritdoc />
    public override Task SubmitAsync(StepContext context, string output, string? agent, CancellationToken ct)
    {
        Require(context, StepTrigger.Submit, "given submitted output");
        var state = context.State;
        var waiting = state.Candidates?.Where(c => c.Status == StepStatus.AwaitingSession).ToList() ?? [];
        if (waiting.Count > 0)
        {
            var candidate = agent is null
                ? waiting[0]
                : waiting.FirstOrDefault(c => string.Equals(c.Agent, agent, StringComparison.OrdinalIgnoreCase)) ??
                  throw new AflException(AflErrorKind.InvalidInput,
                      $"Agent '{agent}' isn't waiting for output on step '{state.StepId}'. Waiting: {string.Join(", ", waiting.Select(c => c.Agent))}.");
            candidate.Output = output;
            candidate.PendingPrompt = null;
            candidate.Status = StepStatus.Done;
            return this.CombineCandidatesAsync(context, ct);
        }

        if (agent is not null && !DefinitionOf(context).IsMultiAgent &&
            !string.Equals(agent, context.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"Step '{state.StepId}' is done by '{context.AgentId}', not '{agent}'.");
        }

        state.Output = output;
        state.PendingPrompt = null;
        return this.OnOutputAsync(context, ct);
    }

    /// <inheritdoc />
    public override Task ChooseAsync(StepContext context, string agent, string? output, CancellationToken ct)
    {
        Require(context, StepTrigger.Choose, "given a chosen result");
        var candidate = (context.State.Candidates ?? [])
            .FirstOrDefault(c => c.Status == StepStatus.Done && string.Equals(c.Agent, agent, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            var results = (context.State.Candidates ?? []).Where(c => c.Status == StepStatus.Done).Select(c => c.Agent);
            throw new AflException(AflErrorKind.InvalidInput,
                $"Agent '{agent}' has no result for step '{context.State.StepId}'. Results: {string.Join(", ", results)}.");
        }

        var edited = !string.IsNullOrWhiteSpace(output) && output != candidate.Output;
        context.State.Notes.Add(edited ? $"Used {candidate.Agent}'s result, edited." : $"Used {candidate.Agent}'s result.");
        return this.UseOutputAsync(context, edited ? output! : candidate.Output ?? string.Empty, ct);
    }

    /// <summary>
    ///     Has an agent merge the results of a step several agents did into its output. If the merge fails the step
    ///     waits for the user to choose a result instead.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="agent">The agent that merges; the step's <see cref="AbstractAgentStepDefinition.MergeAgent" /> (else its first agent) when null.</param>
    /// <param name="note">Extra instructions for the merge, if any.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public override async Task MergeAsync(StepContext context, string? agent, string? note, CancellationToken ct)
    {
        Require(context, StepTrigger.Merge, "merged");
        var state = context.State;
        var definition = DefinitionOf(context);
        if (!definition.IsMultiAgent || state.Candidates is null || state.Candidates.All(c => c.Status != StepStatus.Done))
        {
            throw new AflException(AflErrorKind.InvalidState, $"Step '{state.StepId}' has no results to merge.");
        }

        var mergeAgent = agent ?? definition.MergeAgent ?? definition.Agents![0];
        var executor = context.ExecutorFor(mergeAgent);
        var prompt = MergePrompt(context, note);
        var request = context.CreateRequest(mergeAgent, prompt, Guid.NewGuid().ToString(), false,
            context.LogPathFor(StepConstants.MergeLogName));
        state.Error = null;
        StepLifecycle.Instance.Move(state, StepStatus.Running);
        context.Save();
        context.Report($"Merging the results of step '{state.Name}' with {mergeAgent}.");

        ExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(request, context.ProgressFor(mergeAgent), ct);
        }
        catch (AflException ex)
        {
            result = new ExecutionResult(ExecutionStatus.Failed, null, null, ex.Message);
        }

        switch (result.Status)
        {
            case ExecutionStatus.AwaitingSession:
                state.PendingPrompt = request.Prompt;
                StepLifecycle.Instance.Move(state, StepStatus.AwaitingSession);
                break;
            case ExecutionStatus.Failed:
                state.Error = $"{mergeAgent} couldn't merge the results: {result.Error ?? "it failed."}";
                StepLifecycle.Instance.Move(state, StepStatus.AwaitingChoice);
                break;
            default:
                state.Notes.Add($"Merged by {mergeAgent}.");
                await this.UseOutputAsync(context, result.Output ?? string.Empty, ct);
                break;
        }
    }

    /// <summary>
    ///     Gives the step its output (a chosen, merged or combined result) and carries on as when the AI finishes
    ///     (<see cref="OnAiCompletedAsync" />).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="output">The output.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public Task UseOutputAsync(StepContext context, string output, CancellationToken ct)
    {
        context.State.Error = null;
        context.State.PendingPrompt = null;
        context.State.Output = output;
        return this.OnAiCompletedAsync(context, ct);
    }

    /// <summary>
    ///     Redoes the step with feedback: continues the step's conversation with the feedback when there is one,
    ///     otherwise runs the step's prompt again with the feedback added.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public override Task ReworkAsync(StepContext context, string feedback, CancellationToken ct)
    {
        if (DefinitionOf(context).IsMultiAgent)
        {
            var previous = context.State.History.LastOrDefault()?.Candidates ?? [];
            return this.RunCandidatesAsync(context, candidate =>
            {
                var resume = candidate.SessionId is not null && context.ExecutorFor(candidate.Agent).SupportsResume;
                var earlier = previous.FirstOrDefault(p => p.Agent == candidate.Agent)?.Output;
                return (ReworkPrompt(context, feedback, resume, earlier), resume);
            }, ct);
        }

        var canResume = CanResume(context);
        return this.RunAiAsync(context, ReworkPrompt(context, feedback, canResume), canResume, ct);
    }

    /// <summary>
    ///     Builds the prompt that redoes a step with feedback.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="resume">Whether the step's conversation is continued (so the AI has its previous output).</param>
    /// <returns>The rendered prompt.</returns>
    protected static string ReworkPrompt(StepContext context, string feedback, bool resume)
    {
        return ReworkPrompt(context, feedback, resume, context.State.History.LastOrDefault()?.Output);
    }

    /// <summary>
    ///     Builds the prompt that redoes a step with feedback, given the earlier output to show when the conversation
    ///     isn't continued.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="resume">Whether the step's conversation is continued (so the AI has its previous output).</param>
    /// <param name="previous">The earlier output, if any.</param>
    /// <returns>The rendered prompt.</returns>
    protected static string ReworkPrompt(StepContext context, string feedback, bool resume, string? previous)
    {
        if (resume)
        {
            return context.Render(StepConstants.RejectionPromptTemplate);
        }

        var parts = new[]
        {
            context.Render(DefinitionOf(context).Prompt),
            string.IsNullOrWhiteSpace(previous) ? null : $"{StepConstants.PreviousOutputHeading}\n\n{previous}",
            $"{StepConstants.ReworkHeading}\n\n{feedback}",
        };
        return string.Join("\n\n", parts.Where(p => p is not null));
    }

    /// <summary>
    ///     Returns whether redoing the step can continue its conversation: there is one, and the agent can resume.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>True if it can.</returns>
    protected static bool CanResume(StepContext context)
    {
        return context.State.SessionId is not null && context.Executor.SupportsResume;
    }

    /// <summary>
    ///     Runs the AI on a prompt and records the outcome.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="resume">Whether to continue the step's existing conversation.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="newAttempt">
    ///     Whether this counts as another attempt (<see cref="RunStep.Attempts" />); carrying on after the AI's
    ///     questions are answered doesn't.
    /// </param>
    /// <returns>A task.</returns>
    protected async Task RunAiAsync(StepContext context, string prompt, bool resume, CancellationToken ct, bool newAttempt = true)
    {
        if (DefinitionOf(context).IsMultiAgent)
        {
            await this.RunCandidatesAsync(context, _ => (prompt, resume), ct);
            return;
        }

        if (!resume)
        {
            prompt = this.WithReplyInstructions(context, prompt);
        }

        var state = context.State;
        if (newAttempt || state.Attempts == 0)
        {
            state.Attempts++;
        }

        state.Error = null;
        StepLifecycle.Instance.Move(state, StepStatus.Running);
        var executor = context.Executor;
        var request = context.CreateRequest(prompt, resume);
        context.Save();
        context.Report($"Running step '{state.Name}' with {executor.Name}.");
        NoteIgnoredPermissions(context, [executor]);

        ExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(request, context.Progress, ct);
        }
        catch (AflException ex)
        {
            Fail(context, ex.Message);
            return;
        }

        state.SessionId = result.SessionId ?? state.SessionId;
        if (result.Status == ExecutionStatus.Completed && PauseForPermission(context, executor, result))
        {
            state.Output = result.Output;
            return;
        }

        NoteDeniedTools(context, executor, result);

        switch (result.Status)
        {
            case ExecutionStatus.AwaitingSession:
                state.PendingPrompt = request.Prompt;
                StepLifecycle.Instance.Move(state, StepStatus.AwaitingSession);
                break;
            case ExecutionStatus.Failed:
                Fail(context, result.Error ?? "The AI step failed.");
                break;
            default:
                state.Output = result.Output;
                await this.OnOutputAsync(context, ct);
                break;
        }
    }

    /// <summary>
    ///     Lets the AI carry on once its questions are answered: continues the step's conversation with the answers
    ///     when it can, otherwise runs the step's prompt again with what the AI had written and every answer so far.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="round">The questions and their answers.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected override Task ContinueAfterAnswersAsync(StepContext context, AskRound round, CancellationToken ct)
    {
        return this.ContinueAsync(context, round.Output ?? string.Empty,
            string.Join("\n\n", StepConstants.AnswersHeading, DescribeAnswers([round]), StepConstants.AnswersContinue),
            string.Join("\n\n", StepConstants.AnswersHeading, DescribeAnswers(context.State.AskRounds), StepConstants.AnswersContinue),
            ct);
    }

    /// <inheritdoc />
    public override async Task PermitAsync(
        StepContext context,
        IReadOnlyList<PermissionChoice> choices,
        bool acceptOutput,
        CancellationToken ct)
    {
        Require(context, StepTrigger.Permit, "given permissions");
        var state = context.State;
        var open = state.OpenPermissionRequests;
        foreach (var choice in choices)
        {
            var request = open.FirstOrDefault(r => r.Index == choice.Index) ?? throw new AflException(
                AflErrorKind.InvalidInput,
                $"Step '{state.StepId}' isn't asking about request {choice.Index}. Open: {string.Join(", ", open.Select(r => r.Index))}.");
            Decide(request, choice);
        }

        if (acceptOutput)
        {
            foreach (var request in open.Where(r => r.Decision is null))
            {
                Decide(request, new PermissionChoice(request.Index, PermissionDecision.Deny));
            }

            state.Notes.Add(string.Format(PermissionConstants.AcceptedWithDenialsNote,
                string.Join("; ", open.Select(r => r.Denial.Display))));
            context.Save();
            await this.OnOutputAsync(context, ct);
            return;
        }

        if (open.Any(r => r.Decision is null))
        {
            // Wait for the rest; what's decided so far is kept.
            context.Save();
            return;
        }

        foreach (var request in open.Where(r => r.Decision == PermissionDecision.Always))
        {
            SaveRule(context, request);
        }

        state.OnceRules = open.Where(r => r.Decision == PermissionDecision.Once).Select(r => r.Rule!).Distinct().ToList();
        var message = ResumeMessage(open);
        await this.ContinueAsync(context, state.Output ?? string.Empty, message, message, ct);
    }

    /// <summary>
    ///     Lets the AI carry on after it stopped part-way (a reply block): continues the step's conversation when it
    ///     can, otherwise runs the step's prompt again with what the AI had written. It doesn't count as another attempt.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="previous">What the AI had written before it stopped.</param>
    /// <param name="resumed">What to tell the AI when its conversation is continued.</param>
    /// <param name="restarted">What to tell the AI, after its earlier output, when it starts again.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public Task ContinueAsync(StepContext context, string previous, string resumed, string restarted, CancellationToken ct)
    {
        var resume = CanResume(context);
        var prompt = resume
            ? resumed
            : string.Join("\n\n", context.Render(DefinitionOf(context).Prompt), $"{StepConstants.PreviousOutputHeading}\n\n{previous}", restarted);
        return this.RunAiAsync(context, prompt, resume, ct, newAttempt: false);
    }

    /// <summary>
    ///     Adds the instructions of every reply block the AI may use now (<see cref="ReplyBlockRegistry" />) to a prompt,
    ///     and, for a step that waits for approval, how to leave open questions (<see cref="OpenQuestionsInstructions" />).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <returns>The prompt, with the instructions that apply.</returns>
    protected virtual string WithReplyInstructions(StepContext context, string prompt)
    {
        var instructions = ReplyBlockRegistry.Instance.All.Select(b => b.Instructions(this, context))
            .Append(this.OpenQuestionsInstructions(context))
            .OfType<string>();
        return string.Join("\n\n", new[] { prompt }.Concat(instructions));
    }

    /// <summary>
    ///     Returns how the AI should leave questions for the user (<see cref="StepConstants.OpenQuestionsInstructionsFormat" />)
    ///     when the step waits for approval (<see cref="StepConstants.AdvanceApprove" /> or
    ///     <see cref="StepConstants.AdvanceApproveFinal" />), so the user can answer them; other steps don't stop for the
    ///     user, so they get none.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The instructions, or null when the step doesn't wait for approval.</returns>
    protected string? OpenQuestionsInstructions(StepContext context)
    {
        return this.AdvanceOf(context.Definition) is StepConstants.AdvanceApprove or StepConstants.AdvanceApproveFinal
            ? string.Format(StepConstants.OpenQuestionsInstructionsFormat, StepConstants.OpenQuestionsHeading)
            : null;
    }

    /// <summary>
    ///     Gives the step the AI's output: if it ends with a reply block the AI may use, that block takes over (the step
    ///     waits for answers, or the AI carries on after other agents' work); otherwise it carries on as when the AI is
    ///     done (<see cref="OnAiCompletedAsync" />).
    /// </summary>
    /// <param name="context">The step context, with <see cref="RunStep.Output" /> set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    private async Task OnOutputAsync(StepContext context, CancellationToken ct)
    {
        foreach (var block in ReplyBlockRegistry.Instance.All)
        {
            if (await block.TryHandleAsync(this, context, ct))
            {
                return;
            }
        }

        await this.OnAiCompletedAsync(context, ct);
    }

    /// <summary>
    ///     Describes the answers to the AI's questions, one per line.
    /// </summary>
    /// <param name="rounds">The rounds of questions, answered.</param>
    /// <returns>The answers.</returns>
    private static string DescribeAnswers(IEnumerable<AskRound> rounds)
    {
        return string.Join("\n", rounds.SelectMany(r => r.Questions.Select(q =>
            $"- {q.Label}: {(r.Answers?.GetValueOrDefault(q.Id) is { Length: > 0 } answer ? answer : "(no answer)")}")));
    }

    /// <summary>
    ///     Runs every agent of a multi-agent step at once, each in its own conversation, then combines their results.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="promptFor">Returns the prompt for an agent, and whether to continue its conversation.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected async Task RunCandidatesAsync(
        StepContext context,
        Func<StepCandidate, (string Prompt, bool Resume)> promptFor,
        CancellationToken ct)
    {
        var state = context.State;
        state.Attempts++;
        state.Error = null;
        state.Output = null;
        state.PendingPrompt = null;
        StepLifecycle.Instance.Move(state, StepStatus.Running);
        var earlier = state.Candidates ?? [];
        state.Candidates = context.AgentIds
            .Select(id => new StepCandidate { Agent = id, SessionId = earlier.FirstOrDefault(c => c.Agent == id)?.SessionId })
            .ToList();

        // Executors and requests are made one at a time, before the agents start, since the executor cache isn't
        // thread-safe; only the agents' work runs concurrently.
        var work = state.Candidates.Select(candidate =>
        {
            var (prompt, resume) = promptFor(candidate);
            if (!resume && this.OpenQuestionsInstructions(context) is { } questions)
            {
                prompt = $"{prompt}\n\n{questions}";
            }

            return (Candidate: candidate, Executor: context.ExecutorFor(candidate.Agent),
                Request: context.CreateRequest(candidate, prompt, resume));
        }).ToList();
        context.Save();
        context.Report($"Running step '{state.Name}' with {string.Join(", ", state.Candidates.Select(c => c.Agent))}.");
        NoteIgnoredPermissions(context, work.Select(w => w.Executor));

        var results = await Task.WhenAll(work.Select(w => RunCandidateAsync(context, w.Candidate, w.Executor, w.Request, ct)));
        foreach (var (executor, result) in work.Select(w => w.Executor).Zip(results).Where(r => r.Second is not null))
        {
            NoteDeniedTools(context, executor, result!);
        }

        await this.CombineCandidatesAsync(context, ct);
    }

    /// <summary>
    ///     Moves a multi-agent step on once its agents have reported: it waits while one is waiting for the calling
    ///     Claude session, fails if every agent failed, and otherwise hands the results to the step's combine strategy
    ///     (agents that failed are left out).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected Task CombineCandidatesAsync(StepContext context, CancellationToken ct)
    {
        var state = context.State;
        var candidates = state.Candidates ?? [];
        if (candidates.FirstOrDefault(c => c.Status == StepStatus.AwaitingSession) is { } waiting)
        {
            state.PendingPrompt = waiting.PendingPrompt;
            StepLifecycle.Instance.Move(state, StepStatus.AwaitingSession);
            return Task.CompletedTask;
        }

        if (candidates.All(c => c.Status == StepStatus.Failed))
        {
            Fail(context, "Every agent failed. " + string.Join(" ", candidates.Select(c => $"{c.Agent}: {c.Error}")));
            return Task.CompletedTask;
        }

        return CombineStrategyRegistry.Instance.For(DefinitionOf(context)).CombineAsync(this, context, ct);
    }

    /// <summary>
    ///     Runs one agent of a multi-agent step and records its result on its candidate.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="candidate">The agent's candidate.</param>
    /// <param name="executor">The agent's executor.</param>
    /// <param name="request">The request.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The executor's result, or null if it threw.</returns>
    private static async Task<ExecutionResult?> RunCandidateAsync(
        StepContext context,
        StepCandidate candidate,
        AbstractStepExecutor executor,
        ExecutionRequest request,
        CancellationToken ct)
    {
        candidate.Status = StepStatus.Running;
        ExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(request, context.ProgressFor(candidate.Agent), ct);
        }
        catch (AflException ex)
        {
            candidate.Error = ex.Message;
            candidate.Status = StepStatus.Failed;
            return null;
        }

        candidate.SessionId = result.SessionId ?? candidate.SessionId;
        switch (result.Status)
        {
            case ExecutionStatus.AwaitingSession:
                candidate.PendingPrompt = request.Prompt;
                candidate.Status = StepStatus.AwaitingSession;
                break;
            case ExecutionStatus.Failed:
                candidate.Error = result.Error ?? "The agent failed.";
                candidate.Status = StepStatus.Failed;
                break;
            default:
                candidate.Output = result.Output;
                candidate.Status = StepStatus.Done;
                break;
        }

        return result;
    }

    /// <summary>
    ///     Builds the prompt that merges a multi-agent step's results.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="note">Extra instructions for the merge, if any.</param>
    /// <returns>The rendered prompt.</returns>
    private static string MergePrompt(StepContext context, string? note)
    {
        var definition = DefinitionOf(context);
        var prompt = definition.MergePrompt is null
            ? string.Join("\n\n", context.Render(definition.Prompt), StepConstants.MergeInstructions, context.State.CombineCandidates())
            : context.Render(definition.MergePrompt);
        return string.IsNullOrWhiteSpace(note) ? prompt : $"{prompt}\n\n{StepConstants.MergeNoteHeading}\n\n{note.Trim()}";
    }

    /// <summary>
    ///     Checks how a step's agent is chosen by capability: the capability ids and the selector.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    private static IEnumerable<string> ValidateCapabilities(AbstractAgentStepDefinition step)
    {
        if (step.Capabilities is not null && step.Agents is not null)
        {
            yield return "'capabilities' choose one agent, so they can't be used with 'agents'.";
        }

        if (step.Select is not null && step.Capabilities is null)
        {
            yield return "'select' only applies to a step with 'capabilities'.";
        }
        else if (step.Select is not null && !AgentSelectorRegistry.Instance.Contains(step.Select))
        {
            yield return $"unknown select '{step.Select}'. Use {string.Join(", ", AgentSelectorRegistry.Instance.Keys.Order())}.";
        }

        foreach (var capability in (step.Capabilities ?? []).Where(c => !WorkflowLibrary.IsValidId(c)))
        {
            yield return $"capability '{capability}' isn't a capability id (see afl capability-list).";
        }
    }

    /// <summary>
    ///     Checks the MCP servers a step asks for: that each is a server id, and none is both required and optional.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    private static IEnumerable<string> ValidateServers(AbstractAgentStepDefinition step)
    {
        foreach (var server in (step.McpServers ?? []).Where(s => !WorkflowLibrary.IsValidId(s)))
        {
            yield return $"MCP server '{server}' isn't a server id (see afl mcp-list).";
        }

        foreach (var server in (step.OptionalMcpServers ?? []).Where(s => !WorkflowLibrary.IsValidId(s)))
        {
            yield return $"optional MCP server '{server}' isn't a server id (see afl mcp-list).";
        }

        foreach (var server in (step.OptionalMcpServers ?? []).Intersect(step.McpServers ?? []))
        {
            yield return $"MCP server '{server}' is in both 'mcpServers' and 'optionalMcpServers'.";
        }
    }

    /// <summary>
    ///     Checks a step's agent settings: one agent or several, and how several agents' results are combined.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    private static IEnumerable<string> ValidateAgents(AbstractAgentStepDefinition step)
    {
        if (step.Agents is null)
        {
            if (step.Combine is not null || step.MergeAgent is not null || step.MergePrompt is not null)
            {
                yield return "combine, mergeAgent and mergePrompt only apply to a step with 'agents'.";
            }

            yield break;
        }

        if (step.Agents.Count < 2)
        {
            yield return "'agents' needs two or more agents; use 'agent' for one.";
        }

        if (step.Agent is not null)
        {
            yield return "set 'agent' or 'agents', not both.";
        }

        foreach (var duplicate in step.Agents.GroupBy(a => a, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            yield return $"'agents' lists '{duplicate.Key}' more than once.";
        }

        if (step.Model is not null)
        {
            yield return "'model' applies to a step done by one agent; set each agent's model in its definition instead.";
        }

        if (!string.IsNullOrEmpty(step.Combine) && !CombineStrategyRegistry.Instance.Contains(step.Combine))
        {
            yield return
                $"unknown combine '{step.Combine}'. Known: {string.Join(", ", CombineStrategyRegistry.Instance.Keys.Order())}.";
            yield break;
        }

        foreach (var problem in CombineStrategyRegistry.Instance.For(step).Validate(step))
        {
            yield return problem;
        }
    }

    /// <summary>
    ///     Notes on the step when an agent ignores the step's permission settings.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="executors">The executors doing the step.</param>
    private static void NoteIgnoredPermissions(StepContext context, IEnumerable<AbstractStepExecutor> executors)
    {
        if (DefinitionOf(context).PermissionMode is null && DefinitionOf(context).AllowedTools is not { Count: > 0 })
        {
            return;
        }

        foreach (var executor in executors.Where(e => !e.SupportsPermissions))
        {
            var note = $"Agent '{executor.Name}' ignores the step's permissionMode and allowedTools; it uses its own settings.";
            if (!context.State.Notes.Contains(note))
            {
                context.State.Notes.Add(note);
            }
        }
    }

    /// <summary>
    ///     Pauses the step to ask the user about the tool calls its agent was refused, when the run's policy asks, the
    ///     agent can be resumed, and the step hasn't asked too often already. Calls a deny rule blocks aren't asked
    ///     about, since allowing them can't work: nor are calls refused again after the user allowed them. Those are
    ///     noted instead, and when nothing else was refused the step doesn't pause.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="executor">The agent's executor.</param>
    /// <param name="result">The agent's result.</param>
    /// <returns>True if the step now waits for permission.</returns>
    private static bool PauseForPermission(StepContext context, AbstractStepExecutor executor, ExecutionResult result)
    {
        var state = context.State;
        if (result.Denials is not { Count: > 0 } denials || !context.Policy.PausesOnDenial || !executor.SupportsResume ||
            state.SessionId is null || state.PermissionRounds >= PermissionConstants.MaxPermissionRounds)
        {
            return false;
        }

        foreach (var denial in denials.Where(d => d.BlockedBy is null && WasAllowedBefore(state, d)))
        {
            denial.BlockedBy = PermissionConstants.RefusedAfterAllowed;
        }

        var askable = denials.Where(d => d.BlockedBy is null).ToList();
        if (askable.Count == 0)
        {
            return false;
        }

        NoteDeniedTools(context, executor, denials.Where(d => d.BlockedBy is not null).ToList());
        state.PermissionRounds++;
        foreach (var denial in askable)
        {
            state.PermissionRequests.Add(new PermissionRequest
            {
                Index = state.PermissionRequests.Count + 1, Denial = denial, Round = state.PermissionRounds,
            });
        }

        StepLifecycle.Instance.Move(state, StepStatus.AwaitingPermission);
        context.Report($"Step '{state.Name}' needs permission: {string.Join("; ", askable.Select(d => d.Display))}.");
        return true;
    }

    /// <summary>
    ///     Returns whether the user already allowed a call on this step, so being refused it again means something
    ///     else blocks it.
    /// </summary>
    /// <param name="state">The step.</param>
    /// <param name="denial">The refused call.</param>
    /// <returns>True if it was allowed before.</returns>
    private static bool WasAllowedBefore(RunStep state, PermissionDenial denial)
    {
        return state.PermissionRequests.Any(r =>
            r.Decision is PermissionDecision.Once or PermissionDecision.Always && r.Denial.Display == denial.Display);
    }

    /// <summary>
    ///     Records a decision on one request, checking the rule it allows.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="choice">The decision.</param>
    private static void Decide(PermissionRequest request, PermissionChoice choice)
    {
        string? rule = null;
        if (choice.Decision != PermissionDecision.Deny)
        {
            rule = string.IsNullOrWhiteSpace(choice.Rule)
                ? request.Denial.SuggestedRules.FirstOrDefault() ?? request.Denial.Tool
                : choice.Rule.Trim();
            if (PermissionRule.Problem(rule) is { } problem)
            {
                throw new AflException(AflErrorKind.InvalidInput, problem);
            }
        }

        request.Decision = choice.Decision;
        request.Rule = rule;
        request.Scope = choice.Decision == PermissionDecision.Always ? choice.Scope : null;
        request.DecidedAt = DateTimeOffset.Now;
    }

    /// <summary>
    ///     Saves an always-allowed rule to the scope the user chose. Without the saved rules (some tests), it goes on
    ///     the run.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="request">The decided request.</param>
    private static void SaveRule(StepContext context, PermissionRequest request)
    {
        var note = request.Denial.Display;
        if (request.Scope is PermissionScope.Run or null || context.PermissionRules is null)
        {
            PermissionRule.AddTo(context.Run.PermissionRules, request.Rule!, PermissionRuleKind.Allow, note);
            request.Scope = PermissionScope.Run;
            return;
        }

        context.PermissionRules.Add(request.Scope.Value, context.Run.RepoId, request.Rule!, PermissionRuleKind.Allow, note);
    }

    /// <summary>
    ///     Writes what to tell the AI when it carries on after the user decided.
    /// </summary>
    /// <param name="requests">The decided requests.</param>
    /// <returns>The message.</returns>
    private static string ResumeMessage(IReadOnlyList<PermissionRequest> requests)
    {
        var allowed = requests.Where(r => r.Decision != PermissionDecision.Deny).ToList();
        var refused = requests.Where(r => r.Decision == PermissionDecision.Deny).ToList();
        var allowedText = allowed.Count == 0
            ? string.Empty
            : "You may now use:\n" + string.Join("\n", allowed.Select(r => $"- {r.Rule} (for {r.Denial.Display})")) + "\n\n";
        var refusedText = refused.Count == 0
            ? string.Empty
            : "Still refused, don't retry these:\n" + string.Join("\n", refused.Select(r => $"- {r.Denial.Display}")) + "\n";
        return string.Format(PermissionConstants.ResumeMessageFormat, allowedText, refusedText);
    }

    /// <summary>
    ///     Notes on the step, and reports, any tools the agent was denied.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="executor">The agent's executor.</param>
    /// <param name="result">The agent's result.</param>
    private static void NoteDeniedTools(StepContext context, AbstractStepExecutor executor, ExecutionResult result)
    {
        NoteDeniedTools(context, executor, result.Denials ?? []);
    }

    /// <summary>
    ///     Notes on the step, and reports, tools the agent was denied: those a deny rule blocks apart from the rest,
    ///     since only changing the deny rules helps them.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="executor">The agent's executor.</param>
    /// <param name="denied">The refused calls.</param>
    private static void NoteDeniedTools(StepContext context, AbstractStepExecutor executor, IReadOnlyList<PermissionDenial> denied)
    {
        var blocked = denied.Where(d => d.BlockedBy is not null).ToList();
        var rest = denied.Where(d => d.BlockedBy is null).ToList();
        if (rest.Count > 0)
        {
            Warn(context,
                $"{executor.Name} was denied permission to use {string.Join("; ", rest.Select(d => d.Display))}, so it may not have finished everything. " +
                context.Policy.DenialAdvice);
        }

        if (blocked.Count > 0)
        {
            Warn(context, string.Format(PermissionConstants.BlockedNoteFormat, executor.Name,
                string.Join("; ", blocked.Select(d => $"{d.Display} (blocked by {d.BlockedBy})"))));
        }
    }

    /// <summary>
    ///     Notes a warning on the step and reports it.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="warning">The warning.</param>
    private static void Warn(StepContext context, string warning)
    {
        context.State.Notes.Add(warning);
        context.Progress?.Report(new ExecutorEvent(ExecutorEventKind.Error, warning));
    }

    /// <summary>
    ///     Called once the AI has produced <see cref="RunStep.Output" />. By default the step's work is done
    ///     (<see cref="AbstractStepHandler.FinishAsync" />).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual Task OnAiCompletedAsync(StepContext context, CancellationToken ct)
    {
        return this.FinishAsync(context, context.State.Output, ct);
    }
}
