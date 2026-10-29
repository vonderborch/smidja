using Afl.Core.Agents;
using Afl.Core.Execution;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     Registers a step handler with the <see cref="StepHandlerRegistry" />.
/// </summary>
/// <param name="type">The step type it handles, e.g. <c>ai</c>.</param>
public class StepHandlerRegistrationAttribute(string type) : AbstractRegistrationAttribute(type);

/// <summary>
///     The step handlers afl knows about, keyed by <see cref="AbstractStepDefinition.Type" />. Each handler names the
///     definition subclass its kind of step is read as, so this is also where a step's type and its subclass meet.
/// </summary>
public class StepHandlerRegistry() : TypeRegistry<AbstractStepHandler, StepHandlerRegistrationAttribute>("step type")
{
    /// <summary>
    ///     The definition subclass of each step type, built on first use.
    /// </summary>
    private IReadOnlyDictionary<string, Type>? _definitionTypes;

    /// <summary>
    ///     The step type of each definition subclass, built on first use.
    /// </summary>
    private IReadOnlyDictionary<Type, string>? _types;

    /// <summary>
    ///     The step types people can use, built on first use.
    /// </summary>
    private IReadOnlyList<string>? _publicKeys;

    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static StepHandlerRegistry Instance { get; } = new();

    /// <summary>
    ///     Gets the definition subclass of each step type.
    /// </summary>
    private IReadOnlyDictionary<string, Type> DefinitionTypes =>
        this._definitionTypes ??= this.Keys.ToDictionary(k => k, k => this.Create(k).DefinitionType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the step type of each definition subclass.
    /// </summary>
    private IReadOnlyDictionary<Type, string> Types => this._types ??= this.DefinitionTypes.ToDictionary(p => p.Value, p => p.Key);

    /// <summary>
    ///     Gets the step types people can use in their own workflows and common steps: every type but the internal ones
    ///     (<see cref="AbstractStepHandler.IsInternal" />), in order.
    /// </summary>
    public IReadOnlyList<string> PublicKeys => this._publicKeys ??= this.Keys.Where(k => !this.Create(k).IsInternal).Order().ToList();

    /// <summary>
    ///     Returns whether a step type is internal to afl, so people can't use it in their own workflows.
    /// </summary>
    /// <param name="type">The step type.</param>
    /// <returns>Whether it is a known, internal type.</returns>
    public bool IsInternal(string type)
    {
        return this.Contains(type) && !this.PublicKeys.Contains(type, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Creates the handler for a step.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The handler.</returns>
    public AbstractStepHandler For(AbstractStepDefinition step)
    {
        return this.Create(step.Type);
    }

    /// <summary>
    ///     Returns the definition subclass a step type is read as.
    /// </summary>
    /// <param name="type">The step type, e.g. <c>ai</c>.</param>
    /// <returns>The subclass.</returns>
    /// <exception cref="AflException">No handler is registered for the type.</exception>
    public Type DefinitionTypeOf(string type)
    {
        return this.DefinitionTypes.TryGetValue(type, out var definition) ? definition : this.Create(type).DefinitionType;
    }

    /// <summary>
    ///     Creates an empty step of a type.
    /// </summary>
    /// <param name="type">The step type.</param>
    /// <returns>The step, as the type's definition subclass.</returns>
    public AbstractStepDefinition CreateDefinition(string type)
    {
        return (AbstractStepDefinition)Activator.CreateInstance(this.DefinitionTypeOf(type))!;
    }

    /// <summary>
    ///     Returns the step type of a definition, from its subclass.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The step type, e.g. <c>ai</c>.</returns>
    /// <exception cref="AflException">No handler takes the definition's subclass.</exception>
    public string TypeOf(AbstractStepDefinition step)
    {
        return this.Types.TryGetValue(step.GetType(), out var type)
            ? type
            : throw new AflException(AflErrorKind.InvalidState, $"No step handler takes a {step.GetType().Name}.");
    }
}

/// <summary>
///     Base for the behaviour of one kind of step (who does the work and how it finishes). A new kind of step is one
///     subclass with a <see cref="StepHandlerRegistrationAttribute" />; the engine, validation and front-ends pick it up.
///     <para>
///         Handlers move <see cref="RunStep.Status" /> along; the engine saves the run and advances past done steps.
///         Whatever the kind of step, once its work is done it calls <see cref="FinishAsync" />, which either completes it or
///         (for a step whose <see cref="AdvanceOf" /> is <c>approve</c>) waits for approval; approving and sending a
///         step back (<see cref="ReworkAsync" />) work the same way for every kind.
///         Actions a handler doesn't support throw <see cref="AflErrorKind.InvalidState" />.
///     </para>
/// </summary>
public abstract class AbstractStepHandler
{
    /// <summary>
    ///     Gets what happens once this kind of step's work is done when the step doesn't set
    ///     <see cref="AbstractStepDefinition.Advance" />: <see cref="StepConstants.AdvanceAuto" /> or
    ///     <see cref="StepConstants.AdvanceApprove" />.
    /// </summary>
    public virtual string DefaultAdvance => StepConstants.AdvanceAuto;

    /// <summary>
    ///     Gets the definition subclass this kind of step is read as.
    /// </summary>
    public abstract Type DefinitionType { get; }

    /// <summary>
    ///     Gets what this kind of step is called where one is picked, e.g. in the builder's Add step menu.
    /// </summary>
    public abstract string DisplayName { get; }

    /// <summary>
    ///     Gets a sentence on who does this kind of step and how, shown beside <see cref="DisplayName" />.
    /// </summary>
    public abstract string Summary { get; }

    /// <summary>
    ///     Gets whether a person finishes this kind of step (marks it done), so it can ask its inputs then.
    /// </summary>
    protected virtual bool PersonFinishes => false;

    /// <summary>
    ///     Gets whether several agents can do this kind of step side by side (<see cref="AbstractAgentStepDefinition.Agents" />).
    /// </summary>
    protected virtual bool TakesSeveralAgents => false;

    /// <summary>
    ///     Gets whether the step's work is done by an agent, so its agent can be chosen by capability
    ///     (<see cref="AbstractAgentStepDefinition.Capabilities" />) or picked with <c>afl run use-agent</c>.
    /// </summary>
    public virtual bool UsesAgent => false;

    /// <summary>
    ///     Gets whether this kind of step starts child runs of another workflow, so it takes <c>workflow</c>,
    ///     <c>forEach</c>, <c>wait</c>, <c>parallel</c>, <c>reuse</c> and <c>childOutput</c>.
    /// </summary>
    public virtual bool StartsChildRuns => false;

    /// <summary>
    ///     Gets whether this kind of step is internal to afl: workflows afl builds itself use it (the AI authoring
    ///     helper's <c>author-draft</c>, for example), but people can't add it to their own workflows or common steps,
    ///     and the builder doesn't offer it.
    /// </summary>
    public virtual bool IsInternal => false;

    /// <summary>
    ///     Returns what happens once a step's work is done: its own <see cref="AbstractStepDefinition.Advance" />, or this kind
    ///     of step's default.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns><see cref="StepConstants.AdvanceAuto" /> or <see cref="StepConstants.AdvanceApprove" />.</returns>
    public string AdvanceOf(AbstractStepDefinition step)
    {
        return string.IsNullOrEmpty(step.Advance) ? this.DefaultAdvance : step.Advance;
    }

    /// <summary>
    ///     Returns whether a step waits for approval once its work is done.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>True if the step is approval-gated.</returns>
    public bool IsGated(AbstractStepDefinition step)
    {
        return this.AdvanceOf(step) == StepConstants.AdvanceApprove;
    }

    /// <summary>
    ///     Returns whether a step can ask <c>after</c> inputs (<see cref="StepConstants.WhenAfter" />): it waits for
    ///     approval, or a person finishes it. <c>before</c> inputs suit every step.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>True if the step can have <c>after</c> inputs.</returns>
    public bool TakesInputs(AbstractStepDefinition step)
    {
        return this.PersonFinishes || this.IsGated(step);
    }

    /// <summary>
    ///     Returns whether the AI may stop part-way through a step to ask questions: its <c>ai</c> inputs
    ///     (<see cref="StepConstants.WhenAi" />) and, with <see cref="AbstractAgentStepDefinition.Ask" />, its own.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>True if it may.</returns>
    public virtual bool CanAiAsk(AbstractStepDefinition step)
    {
        return false;
    }

    /// <summary>
    ///     Returns what a step in its current state asks the user with the next action: the questions it's waiting on
    ///     (<see cref="StepStatus.AwaitingInput" />), or its <c>after</c> inputs when it's approved (if it waits for
    ///     approval) or marked done. Front-ends ask exactly these, so they needn't know when each kind of input is
    ///     asked. Defaults that use template variables come rendered.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The step's state.</param>
    /// <returns>The inputs; empty when nothing is asked now.</returns>
    public IReadOnlyList<StepInput> InputsAskedNow(Run run, RunStep state)
    {
        return WithDefaultsRendered(run, state, this.InputsAskedNowUnrendered(run, state));
    }

    /// <summary>
    ///     Renders the defaults of inputs about to be asked, for <see cref="InputsAskedNow" /> and for checking the
    ///     answers.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The step's state.</param>
    /// <param name="inputs">The inputs, or null.</param>
    /// <returns>The inputs, with any template defaults rendered.</returns>
    protected static IReadOnlyList<StepInput> WithDefaultsRendered(Run run, RunStep state, IReadOnlyList<StepInput>? inputs)
    {
        IReadOnlyDictionary<string, string>? variables = null;
        return (inputs ?? []).Select(i => i.WithDefaultRendered(() => variables ??= TemplateVariables.Build(run, state))).ToList();
    }

    /// <summary>
    ///     Returns what <see cref="InputsAskedNow" /> asks, with defaults as written.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="state">The step's state.</param>
    /// <returns>The inputs.</returns>
    private IReadOnlyList<StepInput> InputsAskedNowUnrendered(Run run, RunStep state)
    {
        if (state.Status == StepStatus.AwaitingInput)
        {
            return state.Questions ?? [];
        }

        var step = run.DefinitionOf(state);
        // A step that only waits for approval at the end of its loop has had its inputs answered when marked done.
        var asked = (state.Status == StepStatus.AwaitingApproval && (this.IsGated(step) || !this.PersonFinishes)) ||
                    (state.Status is StepStatus.AwaitingUser or StepStatus.AwaitingSession && this.PersonFinishes && !this.IsGated(step));
        return asked ? step.InputsAsked(StepConstants.WhenAfter) : [];
    }

    /// <summary>
    ///     Checks that an agent can do this kind of step, when a run is created.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="executor">The executor of the step's agent.</param>
    /// <returns>The problems; empty when it can.</returns>
    public virtual IEnumerable<string> CheckAgent(AbstractStepDefinition step, AbstractStepExecutor executor)
    {
        return [];
    }

    /// <summary>
    ///     Checks the parts of a step definition every kind of step shares (its <see cref="AbstractStepDefinition.Advance" />
    ///     and inputs), then this kind of step's own rules.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public IEnumerable<string> ValidateAll(AbstractStepDefinition step)
    {
        if (!string.IsNullOrEmpty(step.Advance) &&
            step.Advance is not (StepConstants.AdvanceAuto or StepConstants.AdvanceApprove or StepConstants.AdvanceApproveFinal))
        {
            yield return
                $"unknown advance '{step.Advance}'; use '{StepConstants.AdvanceAuto}', '{StepConstants.AdvanceApprove}' or '{StepConstants.AdvanceApproveFinal}'.";
        }
        else if (step.Advance == StepConstants.AdvanceApproveFinal && step.Arrows is not { Count: > 0 })
        {
            yield return $"advance '{StepConstants.AdvanceApproveFinal}' waits for approval when the step's loop ends, so the step needs an arrow back round a loop.";
        }

        if (!this.StartsChildRuns && step.Arrows is not { Count: > 1 })
        {
            // A step whose arrows split the run gives its paths a workspace too.
            foreach (var name in new[] { ("workspace", step.Workspace), ("mergeBack", (object?)step.MergeBack) }.Where(f => f.Item2 is not null).Select(f => f.Item1))
            {
                yield return $"only a {StepConstants.TypeWorkflow} step or a step with several arrows out has a workspace; remove '{name}'.";
            }
        }

        if (step.Use is null)
        {
            foreach (var name in (step.OtherFields ?? []).Select(f => f.Key))
            {
                yield return $"a {step.Type} step doesn't take '{name}'.";
            }
        }

        var blocks = ReplyBlockRegistry.Instance.All.SelectMany(b => b.Validate(this, step));
        foreach (var problem in this.Validate(step).Concat(this.ValidateInputs(step)).Concat(blocks))
        {
            yield return problem;
        }
    }

    /// <summary>
    ///     Checks a step definition for problems specific to this kind of step.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(AbstractStepDefinition step)
    {
        return [];
    }

    /// <summary>
    ///     Names inputs for a validation message ("input 'a'", "inputs 'a', 'b'").
    /// </summary>
    /// <param name="inputs">The inputs.</param>
    /// <returns>The names.</returns>
    private static string Names(IReadOnlyList<StepInput> inputs)
    {
        return (inputs.Count == 1 ? "input " : "inputs ") + string.Join(", ", inputs.Select(i => $"'{i.Id}'"));
    }

    /// <summary>
    ///     Checks a step's inputs: that this kind of step takes them, and that each has a unique id, a label and a
    ///     known type it is valid for.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> ValidateInputs(AbstractStepDefinition step)
    {
        if (step is AbstractAgentStepDefinition { Ask: true } && !this.CanAiAsk(step))
        {
            yield return $"'ask' only works on an {StepConstants.TypeAi} step done by one agent.";
        }

        var inputs = step.Inputs ?? [];
        if (inputs.Count == 0)
        {
            yield break;
        }

        string[] timings = [StepConstants.WhenBefore, StepConstants.WhenAfter, StepConstants.WhenAi];
        foreach (var input in inputs.Where(i => !timings.Contains(i.Timing)))
        {
            yield return $"input '{input.Id}': unknown when '{input.When}'. Use {string.Join(", ", timings)}.";
        }

        var after = step.InputsAsked(StepConstants.WhenAfter);
        if (after.Count > 0 && !this.TakesInputs(step))
        {
            yield return
                $"{Names(after)}: a {step.Type} step only asks 'inputs' after its work if it waits for approval (advance '{StepConstants.AdvanceApprove}'); otherwise ask them '{StepConstants.WhenBefore}'.";
        }

        var asked = step.InputsAsked(StepConstants.WhenAi);
        if (asked.Count > 0 && !this.CanAiAsk(step))
        {
            yield return $"{Names(asked)}: only an {StepConstants.TypeAi} step done by one agent can have inputs the AI asks (when '{StepConstants.WhenAi}').";
        }

        var seen = new HashSet<string>();
        foreach (var input in inputs)
        {
            var label = string.IsNullOrWhiteSpace(input.Id) ? "an input" : $"input '{input.Id}'";
            if (!WorkflowLibrary.IsValidId(input.Id))
            {
                yield return $"{label}: id must be lowercase letters, digits and dashes.";
            }
            else if (!seen.Add(input.Id))
            {
                yield return $"{label}: id is used twice.";
            }

            if (string.IsNullOrWhiteSpace(input.Label))
            {
                yield return $"{label} needs a 'label'.";
            }

            if (!StepInputTypeRegistry.Instance.Contains(input.Type))
            {
                yield return $"{label}: unknown type '{input.Type}'. Types: {string.Join(", ", StepInputTypeRegistry.Instance.Keys)}.";
                continue;
            }

            foreach (var problem in StepInputTypeRegistry.Instance.For(input).Validate(input))
            {
                yield return problem;
            }
        }
    }

    /// <summary>
    ///     Starts a pending step.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when the step is done or waiting.</returns>
    public abstract Task StartAsync(StepContext context, CancellationToken ct);

    /// <summary>
    ///     Re-checks work a step is waiting on elsewhere (e.g. the child runs of a step at
    ///     <see cref="StepStatus.AwaitingChildren" />), moving the step on when it's done. By default there is none.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual Task RefreshAsync(StepContext context, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Checks a step for problems that depend on the rest of its workflow. By default there are none.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> ValidateInWorkflow(WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        return [];
    }

    /// <summary>
    ///     Begins a pending step: asks its <c>before</c> inputs first if they haven't been answered, otherwise starts it
    ///     (<see cref="StartAsync" />).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when the step is done or waiting.</returns>
    public Task BeginAsync(StepContext context, CancellationToken ct)
    {
        var before = context.Definition.InputsAsked(StepConstants.WhenBefore);
        if (before.Any(i => !context.State.Answers.ContainsKey(i.Id)))
        {
            context.State.Questions = before.Select(i => i.Clone()).ToList();
            context.State.PendingPrompt = null;
            context.State.Error = null;
            Move(context, StepStatus.AwaitingInput);
            return Task.CompletedTask;
        }

        return this.ChooseAgentThenStartAsync(context, ct);
    }

    /// <summary>
    ///     Chooses the step's agent, if it's chosen by capability and hasn't been yet, then starts the step's work. When
    ///     someone has to pick, the step waits at <see cref="StepStatus.ChoosingAgent" />; when no agent qualifies, it
    ///     fails saying what's missing.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    private async Task ChooseAgentThenStartAsync(StepContext context, CancellationToken ct)
    {
        if (this.UsesAgent && context.Definition is AbstractAgentStepDefinition { ChoosesAgent: true } && context.State.AgentChoice?.AgentId is null &&
            context.Pool is { } pool)
        {
            try
            {
                context.State.AgentChoice = await pool.ChooseAsync(this, context, ct);
            }
            catch (AflException ex) when (ex.Kind == AflErrorKind.InvalidInput)
            {
                Fail(context, ex.Message);
                return;
            }

            if (context.State.AgentChoice.AgentId is null)
            {
                context.State.Error = null;
                Move(context, StepStatus.ChoosingAgent);
                return;
            }

            context.Report($"'{context.State.StepId}' goes to {context.State.AgentChoice.Describe()}");
        }

        await this.StartAsync(context, ct);
    }

    /// <summary>
    ///     Sets which agent does the step, before it starts or while it waits at <see cref="StepStatus.ChoosingAgent" />
    ///     (which then starts it).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="choice">The choice.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="AflException">The step has started, or its agent can't be chosen.</exception>
    public Task UseAgentAsync(StepContext context, AgentChoice choice, CancellationToken ct)
    {
        Require(context, StepTrigger.UseAgent, "given an agent");
        if (!this.UsesAgent || context.Definition is AbstractAgentStepDefinition { IsMultiAgent: true })
        {
            throw Unsupported(context, "given an agent");
        }

        choice.Candidates = context.State.AgentChoice?.Candidates ?? [];
        choice.At = DateTimeOffset.Now;
        context.State.AgentChoice = choice;
        if (context.State.Status == StepStatus.Pending)
        {
            return Task.CompletedTask;
        }

        context.State.PendingPrompt = null;
        context.Report($"'{context.State.StepId}' goes to {choice.Describe()}");
        return this.StartAsync(context, ct);
    }

    /// <summary>
    ///     Takes the answers to the questions a step is waiting on (<see cref="RunStep.Questions" />), then starts the
    ///     step if they were its <c>before</c> inputs, or lets the AI carry on if it asked them.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="answers">The answers, by question id.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when the step is done or waiting.</returns>
    public Task AnswerAsync(StepContext context, IReadOnlyDictionary<string, string>? answers, CancellationToken ct)
    {
        Require(context, StepTrigger.Answer, "answered");
        var resolved = AcceptAnswers(context, context.State.Questions, answers);
        context.State.Questions = null;
        if (context.State.OpenAskRound is { } round)
        {
            round.Answers = resolved;
            return this.ContinueAfterAnswersAsync(context, round, ct);
        }

        return this.ChooseAgentThenStartAsync(context, ct);
    }

    /// <summary>
    ///     Lets the AI carry on once the questions it asked (<paramref name="round" />) are answered.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="round">The questions and their answers.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when the step is done or waiting.</returns>
    protected virtual Task ContinueAfterAnswersAsync(StepContext context, AskRound round, CancellationToken ct)
    {
        throw Unsupported(context, "given answers to the AI's questions");
    }

    /// <summary>
    ///     Decides on tool calls the AI was refused (<see cref="RunStep.OpenPermissionRequests" />). Once every request
    ///     is decided the AI carries on with what was allowed; with <paramref name="acceptOutput" /> its output is taken
    ///     as it is instead.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="choices">The decisions, by request number.</param>
    /// <param name="acceptOutput">Whether to accept the AI's output as it is, denying whatever is still undecided.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when the step is done or waiting.</returns>
    public virtual Task PermitAsync(StepContext context, IReadOnlyList<PermissionChoice> choices, bool acceptOutput, CancellationToken ct)
    {
        throw Unsupported(context, "given permissions");
    }

    /// <summary>
    ///     Approves a step waiting for approval, keeping the answers to its inputs, so the run moves on.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="answers">The answers to the step's inputs, by input id, if any.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual Task ApproveAsync(StepContext context, IReadOnlyDictionary<string, string>? answers, CancellationToken ct)
    {
        Require(context, StepTrigger.Approve, "approved");
        AcceptAnswers(context, context.Definition.InputsAsked(StepConstants.WhenAfter), answers);
        Complete(context, this.FinalOutput(context, context.State.Output));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Redoes a step that was sent back with feedback (rejected, or replayed with feedback). The engine has already
    ///     archived the earlier attempt and added the feedback to <see cref="RunStep.Feedback" />. By default the step
    ///     starts again from scratch.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes when the step is done or waiting.</returns>
    public virtual Task ReworkAsync(StepContext context, string feedback, CancellationToken ct)
    {
        return this.StartAsync(context, ct);
    }

    /// <summary>
    ///     Marks work the user was doing as done.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="note">An optional note on what was done.</param>
    /// <param name="answers">The answers to the step's inputs, by input id, if any.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual Task CompleteAsync(StepContext context, string? note, IReadOnlyDictionary<string, string>? answers, CancellationToken ct)
    {
        throw Unsupported(context, "marked done");
    }

    /// <summary>
    ///     Accepts output produced by the calling Claude session.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="output">The output.</param>
    /// <param name="agent">For a step several agents do, the agent whose result this is; null for the only one waiting.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual Task SubmitAsync(StepContext context, string output, string? agent, CancellationToken ct)
    {
        throw Unsupported(context, "given submitted output");
    }

    /// <summary>
    ///     Fails a step its session or person can't do, saying why. It is routed like any failure (an arrow on failure
    ///     handles it), but not retried. On a step several agents do, the whole step fails.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="reason">Why it failed.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual Task GiveUpAsync(StepContext context, string reason, CancellationToken ct)
    {
        Require(context, StepTrigger.GiveUp, "failed");
        context.State.PendingPrompt = null;
        context.State.FailureKind = StepConstants.FailureGivenUp;
        Fail(context, reason);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Uses one agent's result as the output of a step several agents did.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="agent">The agent whose result to use.</param>
    /// <param name="output">The result as edited by the user, or null to use it as it is.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual Task ChooseAsync(StepContext context, string agent, string? output, CancellationToken ct)
    {
        throw Unsupported(context, "given a chosen result");
    }

    /// <summary>
    ///     Has an agent merge the results of a step several agents did into its output.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="agent">The agent that merges; the step's merge agent when null.</param>
    /// <param name="note">Extra instructions for the merge, if any.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual Task MergeAsync(StepContext context, string? agent, string? note, CancellationToken ct)
    {
        throw Unsupported(context, "merged");
    }

    /// <summary>
    ///     Builds the step's final output from what its work produced. By default the output is kept as is.
    /// </summary>
    /// <param name="context">The step context, with its answers accepted.</param>
    /// <param name="output">What the work produced.</param>
    /// <returns>The output.</returns>
    protected virtual string? ComposeOutput(StepContext context, string? output)
    {
        return output;
    }

    /// <summary>
    ///     Builds the output a step finishes with: <see cref="ComposeOutput" />, then rendered through the step's
    ///     <see cref="AbstractStepDefinition.OutputTemplate" /> if it has one.
    /// </summary>
    /// <param name="context">The step context, with its answers accepted.</param>
    /// <param name="output">What the work produced.</param>
    /// <returns>The output.</returns>
    protected string? FinalOutput(StepContext context, string? output)
    {
        var composed = this.ComposeOutput(context, output);
        if (string.IsNullOrWhiteSpace(context.Definition.OutputTemplate))
        {
            return composed;
        }

        var variables = new Dictionary<string, string>(TemplateVariables.Build(context.Run, context.State))
        {
            [TemplateVariables.Output] = output ?? string.Empty,
        };
        return PromptTemplate.Render(context.Definition.OutputTemplate, variables).Trim();
    }

    /// <summary>
    ///     Called when the step's work is done: waits for approval if the step is gated (or, for
    ///     <see cref="StepConstants.AdvanceApproveFinal" />, if this pass ends its loop), otherwise finishes it.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="output">What the work produced.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected async Task FinishAsync(StepContext context, string? output, CancellationToken ct)
    {
        if (this.IsGated(context.Definition) || await this.EndsLoopAsync(context, output, ct))
        {
            context.State.Output = output;
            context.State.PendingPrompt = null;
            context.State.Error = null;
            Move(context, StepStatus.AwaitingApproval);
            context.SaveArtifact(output);
            return;
        }

        Complete(context, this.FinalOutput(context, output));
    }

    /// <summary>
    ///     Returns whether a step that waits for approval when its loop ends (<see cref="StepConstants.AdvanceApproveFinal" />)
    ///     is on that pass: its loop won't go round again with this output.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="output">What the work produced.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if it should wait for approval now.</returns>
    private async Task<bool> EndsLoopAsync(StepContext context, string? output, CancellationToken ct)
    {
        if (this.AdvanceOf(context.Definition) != StepConstants.AdvanceApproveFinal)
        {
            return false;
        }

        context.State.Output = output;
        return !await FlowRouter.Instance.WillRepeatAsync(context, ct);
    }

    /// <summary>
    ///     Accepts the answers given when a person marks the step done. A gated step asks its inputs when it's approved
    ///     instead, so answers given now are refused.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="answers">The answers given, by input id, if any.</param>
    /// <exception cref="AflException">Answers are invalid, or given to a gated step.</exception>
    protected void AcceptAnswersOnDone(StepContext context, IReadOnlyDictionary<string, string>? answers)
    {
        if (!this.IsGated(context.Definition))
        {
            AcceptAnswers(context, context.Definition.InputsAsked(StepConstants.WhenAfter), answers);
        }
        else if (answers is { Count: > 0 })
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"Step '{context.State.StepId}' waits for approval; answer its inputs when you approve it.");
        }
    }

    /// <summary>
    ///     Checks answers against the inputs being asked and keeps them on the step, alongside answers given earlier
    ///     to its other inputs.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="inputs">The inputs being asked.</param>
    /// <param name="answers">The answers given, by input id, if any.</param>
    /// <returns>The answers kept, by input id.</returns>
    /// <exception cref="AflException">Answers are missing, unknown or invalid.</exception>
    protected static Dictionary<string, string> AcceptAnswers(
        StepContext context,
        IReadOnlyList<StepInput>? inputs,
        IReadOnlyDictionary<string, string>? answers)
    {
        var resolved = StepInputTypeRegistry.Instance.Resolve(WithDefaultsRendered(context.Run, context.State, inputs), answers);
        foreach (var input in inputs ?? [])
        {
            context.State.Answers.Remove(input.Id);
        }

        foreach (var (id, answer) in resolved)
        {
            context.State.Answers[id] = answer;
        }

        return resolved;
    }

    /// <summary>
    ///     Finishes the step.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="output">The step output.</param>
    protected static void Complete(StepContext context, string? output)
    {
        context.State.Output = output;
        context.State.PendingPrompt = null;
        context.State.Error = null;
        Move(context, StepStatus.Done);
        context.State.EndedAt = DateTimeOffset.Now;
        context.SaveArtifact(output);
    }

    /// <summary>
    ///     Fails the step.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="error">Why.</param>
    protected static void Fail(StepContext context, string error)
    {
        context.State.Error = error;
        Move(context, StepStatus.Failed);
        context.State.EndedAt = DateTimeOffset.Now;
    }

    /// <summary>
    ///     Throws unless a trigger can fire from where the step is in its lifecycle (<see cref="StepLifecycle" />).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="trigger">The trigger the action fires.</param>
    /// <param name="action">The action being attempted, for the message.</param>
    protected static void Require(StepContext context, StepTrigger trigger, string action)
    {
        StepLifecycle.Instance.Require(context.State, trigger, action);
    }

    /// <summary>
    ///     Moves the step to a status along its lifecycle (<see cref="StepLifecycle" />).
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="to">Where it goes.</param>
    protected static void Move(StepContext context, StepStatus to)
    {
        StepLifecycle.Instance.Move(context.State, to);
    }

    /// <summary>
    ///     Creates the error for an action this kind of step doesn't support.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="action">The action, e.g. "approved".</param>
    /// <returns>The exception.</returns>
    private static AflException Unsupported(StepContext context, string action)
    {
        return new AflException(AflErrorKind.InvalidState,
            $"Step '{context.State.StepId}' ({context.Definition.Type}) can't be {action}.");
    }
}

/// <summary>
///     A step handler for one definition subclass: it reads its steps as <typeparamref name="TDefinition" />, so its
///     checks and actions see only the fields its kind of step has.
/// </summary>
/// <typeparam name="TDefinition">The definition subclass its steps are read as.</typeparam>
public abstract class AbstractStepHandler<TDefinition> : AbstractStepHandler
    where TDefinition : AbstractStepDefinition
{
    /// <inheritdoc />
    /// <remarks>
    ///     <typeparamref name="TDefinition" /> by default; a handler whose steps share a base with another kind of step
    ///     (e.g. <see cref="AbstractAiStepHandler" />) names its own subclass of it.
    /// </remarks>
    public override Type DefinitionType => typeof(TDefinition);

    /// <inheritdoc />
    public sealed override bool CanAiAsk(AbstractStepDefinition step)
    {
        return this.CanAiAsk(Cast(step));
    }

    /// <inheritdoc />
    public sealed override IEnumerable<string> CheckAgent(AbstractStepDefinition step, AbstractStepExecutor executor)
    {
        return this.CheckAgent(Cast(step), executor);
    }

    /// <inheritdoc />
    public sealed override IEnumerable<string> Validate(AbstractStepDefinition step)
    {
        return this.Validate(Cast(step));
    }

    /// <inheritdoc />
    public sealed override IEnumerable<string> ValidateInWorkflow(WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        return this.ValidateInWorkflow(workflow, Cast(step));
    }

    /// <summary>
    ///     Returns whether the AI may stop part-way through one of these steps to ask questions.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>True if it may; false by default.</returns>
    protected virtual bool CanAiAsk(TDefinition step)
    {
        return false;
    }

    /// <summary>
    ///     Checks that an agent can do one of these steps, when a run is created.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="executor">The executor of the step's agent.</param>
    /// <returns>The problems; none by default.</returns>
    protected virtual IEnumerable<string> CheckAgent(TDefinition step, AbstractStepExecutor executor)
    {
        return [];
    }

    /// <summary>
    ///     Checks one of these steps for problems specific to its kind.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; none by default.</returns>
    protected virtual IEnumerable<string> Validate(TDefinition step)
    {
        return [];
    }

    /// <summary>
    ///     Checks one of these steps for problems that depend on the rest of its workflow.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step.</param>
    /// <returns>The problems; none by default.</returns>
    protected virtual IEnumerable<string> ValidateInWorkflow(WorkflowDefinition workflow, TDefinition step)
    {
        return [];
    }

    /// <summary>
    ///     Returns the definition of the step a context is for, as this handler's subclass.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The definition.</returns>
    protected static TDefinition DefinitionOf(StepContext context)
    {
        return Cast(context.Definition);
    }

    /// <summary>
    ///     Casts a step definition to this handler's subclass.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The step as <typeparamref name="TDefinition" />.</returns>
    private static TDefinition Cast(AbstractStepDefinition step)
    {
        return step as TDefinition ?? throw new AflException(AflErrorKind.InvalidState,
            $"Step '{step.Id}' is a {step.GetType().Name}, not a {typeof(TDefinition).Name}.");
    }
}
