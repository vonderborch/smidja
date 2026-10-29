using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     Registers a reply block with the <see cref="ReplyBlockRegistry" />.
/// </summary>
/// <param name="tag">The fence language the AI marks the block with, such as <c>afl-ask</c>.</param>
/// <param name="description">What the block lets the AI do, for front-ends.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class ReplyBlockRegistrationAttribute(string tag, string description) : AbstractRegistrationAttribute(tag)
{
    /// <summary>
    ///     Gets what the block lets the AI do, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     Finds reply blocks by tag.
/// </summary>
public class ReplyBlockRegistry() : TypeRegistry<AbstractReplyBlock, ReplyBlockRegistrationAttribute>("reply block")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static ReplyBlockRegistry Instance { get; } = new();

    /// <summary>
    ///     Holds the blocks once made; they keep no state, so one of each serves every step.
    /// </summary>
    private IReadOnlyList<AbstractReplyBlock>? _all;

    /// <summary>
    ///     Gets every block, in tag order (the order replies are checked in).
    /// </summary>
    public IReadOnlyList<AbstractReplyBlock> All => this._all ??= this.CreateAll();
}

/// <summary>
///     Base for something the AI can do part-way through a step by ending its reply with a fenced JSON block: the step
///     tells the AI about it (<see cref="Instructions" />), and when a reply ends with the block afl acts on it, for
///     example pausing for the user's answers or running work on other agents, then lets the AI carry on. A new one is
///     one subclass with a <see cref="ReplyBlockRegistrationAttribute" />; <see cref="AbstractAiStepHandler" /> checks
///     every registered block, so no step handler changes.
/// </summary>
public abstract class AbstractReplyBlock
{
    /// <summary>
    ///     Gets the fence language the AI marks the block with.
    /// </summary>
    public string Tag => this.GetType().GetCustomAttribute<ReplyBlockRegistrationAttribute>()!.Key;

    /// <summary>
    ///     Gets what the AI tried to do, for the note left when a block can't be acted on ("ask questions").
    /// </summary>
    protected abstract string Attempt { get; }

    /// <summary>
    ///     Returns whether the step lets the AI use the block at all.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <returns>True if it may.</returns>
    public abstract bool Applies(AbstractAiStepHandler handler, StepContext context);

    /// <summary>
    ///     Returns the instructions added to the step's prompt, or null when the AI can't use the block now (for
    ///     example it has used up its rounds).
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <returns>The instructions, or null.</returns>
    public abstract string? Instructions(AbstractAiStepHandler handler, StepContext context);

    /// <summary>
    ///     Checks a step's settings for the block.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(AbstractStepHandler handler, AbstractStepDefinition step)
    {
        return [];
    }

    /// <summary>
    ///     Acts on the block if the step's output ends with one: takes it out of the output, and either takes over the
    ///     step (it pauses or carries on itself) or, when it can't be acted on, notes why on the step.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context, with <see cref="RunStep.Output" /> set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if the block took over the step; false if the step should finish as usual.</returns>
    public abstract Task<bool> TryHandleAsync(AbstractAiStepHandler handler, StepContext context, CancellationToken ct);

    /// <summary>
    ///     Notes on the step that the AI used a block that couldn't be acted on.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="problem">Why.</param>
    protected void NoteUnusable(StepContext context, string problem)
    {
        context.State.Notes.Add($"The AI tried to {this.Attempt}, but {problem}; its output was kept as it was.");
    }
}

/// <summary>
///     Base for reply blocks that read their JSON into a <typeparamref name="TRequest" />, check it, then act on it.
/// </summary>
/// <typeparam name="TRequest">What the block asks for.</typeparam>
public abstract class AbstractReplyBlock<TRequest> : AbstractReplyBlock
{
    /// <inheritdoc />
    public override async Task<bool> TryHandleAsync(AbstractAiStepHandler handler, StepContext context, CancellationToken ct)
    {
        if (!this.Applies(handler, context) || FencedBlock.FindLast(context.State.Output, this.Tag) is not { } block)
        {
            return false;
        }

        context.State.Output = block.Rest;
        string? problem = block.Error;
        var request = default(TRequest);
        if (problem is null)
        {
            try
            {
                request = this.Read(block.Json!);
                problem = this.Check(handler, context, request);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                problem = $"its {this.Tag} block can't be read ({ex.Message})";
            }
        }

        if (problem is not null)
        {
            this.NoteUnusable(context, problem);
            return false;
        }

        return await this.HandleAsync(handler, context, block.Rest, request!, ct);
    }

    /// <summary>
    ///     Reads the block's JSON.
    /// </summary>
    /// <param name="json">The block's JSON object.</param>
    /// <returns>The request.</returns>
    /// <exception cref="FormatException">The JSON doesn't have the block's shape.</exception>
    protected abstract TRequest Read(JsonObject json);

    /// <summary>
    ///     Checks a request against what the step lets the AI do.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <param name="request">The request.</param>
    /// <returns>Why it can't be acted on, or null when it can.</returns>
    protected abstract string? Check(AbstractAiStepHandler handler, StepContext context, TRequest request);

    /// <summary>
    ///     Acts on a request that passed <see cref="Check" />.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <param name="output">The AI's reply without the block.</param>
    /// <param name="request">The request.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if the block took over the step.</returns>
    protected abstract Task<bool> HandleAsync(
        AbstractAiStepHandler handler,
        StepContext context,
        string output,
        TRequest request,
        CancellationToken ct);
}

/// <summary>
///     What the AI asked in an <c>afl-ask</c> block: which of the step's <c>ai</c> inputs it wants answered, and its
///     own questions.
/// </summary>
/// <param name="Inputs">The ids of the step inputs the AI asked for.</param>
/// <param name="Questions">The AI's own questions, without ids.</param>
public record AskRequest(IReadOnlyList<string> Inputs, IReadOnlyList<StepInput> Questions);

/// <summary>
///     Lets the AI stop to ask the user questions: the step's inputs asked <c>ai</c>, and with
///     <see cref="AbstractAgentStepDefinition.Ask" /> its own. The step waits at <see cref="StepStatus.AwaitingInput" />, recording an
///     <see cref="AskRound" />, and the AI carries on once they're answered.
/// </summary>
[ReplyBlockRegistration(StepConstants.AskBlockTag, "The AI asks the user questions and carries on with the answers.")]
public class AskReplyBlock : AbstractReplyBlock<AskRequest>
{
    /// <inheritdoc />
    protected override string Attempt => "ask questions";

    /// <inheritdoc />
    public override bool Applies(AbstractAiStepHandler handler, StepContext context)
    {
        var definition = context.Definition;
        return handler.CanAiAsk(definition) &&
               (definition is AbstractAgentStepDefinition { Ask: true } || definition.InputsAsked(StepConstants.WhenAi).Count > 0);
    }

    /// <inheritdoc />
    public override string? Instructions(AbstractAiStepHandler handler, StepContext context)
    {
        if (!this.Applies(handler, context) || context.State.AskRounds.Count >= StepConstants.MaxAskRounds)
        {
            return null;
        }

        var inputs = context.Definition.InputsAsked(StepConstants.WhenAi);
        var listed = inputs.Count == 0
            ? StepConstants.AskNoInputs
            : string.Format(StepConstants.AskInputsFormat, string.Join("\n", inputs.Select(DescribeInput)));
        return string.Format(StepConstants.AskInstructionsFormat, listed,
            context.AgentDefinition is { Ask: true } ? StepConstants.AskOwnQuestions : StepConstants.AskNoOwnQuestions);
    }

    /// <inheritdoc />
    protected override AskRequest Read(JsonObject json)
    {
        var inputs = (json["inputs"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? string.Empty).ToList();
        var questions = (json["questions"] as JsonArray ?? []).Select(n => n as JsonObject).Select(q => new StepInput
        {
            Label = q?["label"]?.GetValue<string>() ?? string.Empty,
            Type = q?["type"]?.GetValue<string>() ?? StepConstants.InputText,
            Help = q?["help"]?.GetValue<string>(),
            Options = (q?["options"] as JsonArray)?.Select(o => o?.ToString() ?? string.Empty).ToList(),
            Required = q?["required"]?.GetValue<bool>() ?? true,
        }).ToList();
        return new AskRequest(inputs, questions);
    }

    /// <inheritdoc />
    protected override string? Check(AbstractAiStepHandler handler, StepContext context, AskRequest request)
    {
        var definition = context.Definition;
        if (request.Inputs.Count == 0 && request.Questions.Count == 0)
        {
            return $"its {this.Tag} block asks nothing";
        }

        if (context.State.AskRounds.Count >= StepConstants.MaxAskRounds)
        {
            return $"it had already asked {StepConstants.MaxAskRounds} times";
        }

        var allowed = definition.InputsAsked(StepConstants.WhenAi).Select(i => i.Id).ToList();
        var unknown = request.Inputs.Where(id => !allowed.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            return $"the step has no input it may ask called {string.Join(", ", unknown.Select(id => $"'{id}'"))}";
        }

        if (request.Questions.Count > 0 && definition is not AbstractAgentStepDefinition { Ask: true })
        {
            return "the step doesn't let it ask its own questions";
        }

        foreach (var question in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Label))
            {
                return "a question has no label";
            }

            if (!StepInputTypeRegistry.Instance.Contains(question.Type))
            {
                return $"question '{question.Label}' has unknown type '{question.Type}'";
            }

            if (StepInputTypeRegistry.Instance.For(question).Validate(question).FirstOrDefault() is { } invalid)
            {
                return $"question '{question.Label}' is invalid ({invalid})";
            }
        }

        return null;
    }

    /// <inheritdoc />
    protected override Task<bool> HandleAsync(
        AbstractAiStepHandler handler,
        StepContext context,
        string output,
        AskRequest request,
        CancellationToken ct)
    {
        var state = context.State;
        var prefix = string.Format(StepConstants.AskQuestionIdFormat, string.Empty);
        var asked = state.AskRounds.Sum(r => r.Questions.Count(q => q.Id.StartsWith(prefix, StringComparison.Ordinal)));
        var questions = context.Definition.InputsAsked(StepConstants.WhenAi)
            .Where(i => request.Inputs.Contains(i.Id))
            .Select(i => i.Clone())
            .Concat(request.Questions.Select(q =>
            {
                q.Id = string.Format(StepConstants.AskQuestionIdFormat, ++asked);
                return q;
            }))
            .ToList();
        state.AskRounds.Add(new AskRound
        {
            Round = state.AskRounds.Count + 1, Output = output, Questions = questions, AskedAt = DateTimeOffset.Now,
        });
        state.Questions = questions.Select(q => q.Clone()).ToList();
        state.PendingPrompt = null;
        StepLifecycle.Instance.Move(state, StepStatus.AwaitingInput);
        return Task.FromResult(true);
    }

    /// <summary>
    ///     Describes a step input the AI may ask, for the instructions.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>One line.</returns>
    private static string DescribeInput(StepInput input)
    {
        var options = input.Options is { Count: > 0 } ? $": {string.Join(", ", input.Options)}" : string.Empty;
        var help = string.IsNullOrWhiteSpace(input.Help) ? string.Empty : $" ({input.Help})";
        return $"- {input.Id}: {input.Label}{help} [{input.Type}{options}]";
    }
}

/// <summary>
///     One sub-task in an <c>afl-delegate</c> block.
/// </summary>
/// <param name="Agent">The agent the AI named, if any.</param>
/// <param name="Capabilities">The capabilities the task needs.</param>
/// <param name="Prompt">What the agent is asked to do.</param>
public record DelegateTask(string? Agent, IReadOnlyList<string> Capabilities, string Prompt);

/// <summary>
///     Lets the AI hand sub-tasks to other agents (<see cref="AbstractAgentStepDefinition.Delegate" />): afl runs them at the same
///     time on agents that have what each needs, records them as <see cref="Delegation" />s, and the AI carries on with
///     their results, up to <see cref="StepConstants.MaxDelegationRounds" /> times an attempt.
/// </summary>
[ReplyBlockRegistration(StepConstants.DelegateBlockTag, "The AI hands sub-tasks to other agents and carries on with their results.")]
public class DelegateReplyBlock : AbstractReplyBlock<IReadOnlyList<DelegateTask>>
{
    /// <inheritdoc />
    protected override string Attempt => "hand off work";

    /// <inheritdoc />
    public override bool Applies(AbstractAiStepHandler handler, StepContext context)
    {
        return context.AgentDefinition is { Delegate: true } && handler.CanAiAsk(context.Definition) && context.Pool is not null;
    }

    /// <inheritdoc />
    public override string? Instructions(AbstractAiStepHandler handler, StepContext context)
    {
        if (!this.Applies(handler, context) || Rounds(context) >= StepConstants.MaxDelegationRounds)
        {
            return null;
        }

        var helpers = Helpers(handler, context, []);
        return helpers.Count == 0
            ? null
            : string.Format(StepConstants.DelegateInstructionsFormat, string.Join("\n", helpers.Select(context.Pool!.Describe)));
    }

    /// <inheritdoc />
    public override IEnumerable<string> Validate(AbstractStepHandler handler, AbstractStepDefinition step)
    {
        if (step is not AbstractAgentStepDefinition agentStep)
        {
            yield break;
        }

        if (agentStep.Delegate == true && !handler.CanAiAsk(step))
        {
            yield return $"'delegate' only works on an {StepConstants.TypeAi} step done by one agent.";
        }

        if (agentStep.DelegateCapabilities is not null && agentStep.Delegate != true)
        {
            yield return "'delegateCapabilities' only applies with 'delegate': true.";
        }

        foreach (var capability in (agentStep.DelegateCapabilities ?? []).Where(c => !WorkflowLibrary.IsValidId(c)))
        {
            yield return $"delegate capability '{capability}' must be lowercase letters, digits and dashes.";
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<DelegateTask> Read(JsonObject json)
    {
        return (json["tasks"] as JsonArray ?? throw new FormatException("it has no \"tasks\" list"))
            .Select(n => n as JsonObject ?? throw new FormatException("a task isn't an object"))
            .Select(t => new DelegateTask(
                t["agent"]?.GetValue<string>(),
                (t["capabilities"] as JsonArray ?? []).Select(c => c?.GetValue<string>() ?? string.Empty).ToList(),
                t["prompt"]?.GetValue<string>() ?? string.Empty))
            .ToList();
    }

    /// <inheritdoc />
    protected override string? Check(AbstractAiStepHandler handler, StepContext context, IReadOnlyList<DelegateTask> request)
    {
        if (request.Count == 0)
        {
            return $"its {this.Tag} block has no tasks";
        }

        if (Rounds(context) >= StepConstants.MaxDelegationRounds)
        {
            return $"it had already handed off work {StepConstants.MaxDelegationRounds} times";
        }

        for (var i = 0; i < request.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(request[i].Prompt))
            {
                return $"task {i + 1} has no prompt";
            }

            if (PickAgent(handler, context, request[i], out var why) is null)
            {
                return $"task {i + 1} {why}";
            }
        }

        return null;
    }

    /// <inheritdoc />
    protected override async Task<bool> HandleAsync(
        AbstractAiStepHandler handler,
        StepContext context,
        string output,
        IReadOnlyList<DelegateTask> request,
        CancellationToken ct)
    {
        var state = context.State;
        var round = Rounds(context) + 1;

        // Executors and requests are made one at a time, before the agents start, since the executor cache isn't
        // thread-safe; only the agents' work runs concurrently.
        var work = request.Select(task =>
        {
            var agent = PickAgent(handler, context, task, out _)!;
            var delegation = new Delegation
            {
                Round = round, Agent = agent, Capabilities = task.Capabilities.Count > 0 ? [..task.Capabilities] : null,
                Prompt = task.Prompt, Status = StepStatus.Running, StartedAt = DateTimeOffset.Now,
            };
            state.Delegations.Add(delegation);
            var executor = context.ExecutorFor(agent);
            var logName = string.Format(StepConstants.DelegateLogNameFormat, state.Delegations.Count);
            var executionRequest = context.CreateRequest(agent, task.Prompt, Guid.NewGuid().ToString(), false, context.LogPathFor(logName));
            executionRequest.Model = executor.Agent.Model;
            return (Delegation: delegation, Executor: executor, Request: executionRequest);
        }).ToList();
        context.Save();
        context.Report($"Step '{state.Name}' handed work to {string.Join(", ", work.Select(w => w.Delegation.Agent))}.");

        await Task.WhenAll(work.Select(w => RunAsync(context, w.Delegation, w.Executor, w.Request, ct)));
        var thisRound = state.Delegations.Where(d => d.Round == round).ToList();
        await handler.ContinueAsync(context, output,
            string.Join("\n\n", StepConstants.DelegationResultsHeading, DescribeResults(state.Delegations, thisRound), StepConstants.DelegationContinue),
            string.Join("\n\n", StepConstants.DelegationResultsHeading, DescribeResults(state.Delegations, state.Delegations), StepConstants.DelegationContinue),
            ct);
        return true;
    }

    /// <summary>
    ///     Counts the times the AI handed off work in this attempt.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The count.</returns>
    private static int Rounds(StepContext context)
    {
        return context.State.Delegations.Select(d => d.Round).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    ///     Returns the agents the AI may hand work to: available, with the step's <see cref="AbstractAgentStepDefinition.DelegateCapabilities" />
    ///     and the task's own, and not the agent doing the step or one that only works as a run's agent.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <param name="capabilities">The task's capabilities.</param>
    /// <returns>The agents, in preference order.</returns>
    private static IReadOnlyList<AgentDefinition> Helpers(AbstractAiStepHandler handler, StepContext context, IEnumerable<string> capabilities)
    {
        var needed = (context.AgentDefinition?.DelegateCapabilities ?? []).Concat(capabilities).Distinct().ToList();
        return context.Pool!.Qualifying(handler, context.Definition, context.Run.Executor, needed)
            .Where(a => !string.Equals(a.Id, context.AgentId, StringComparison.OrdinalIgnoreCase))
            .Where(a => !context.ExecutorFor(a.Id).OnlyAsMainAgent)
            .ToList();
    }

    /// <summary>
    ///     Picks the agent for a task: the one the AI named, if it may use it, else the first that has what the task needs.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <param name="task">The task.</param>
    /// <param name="why">Why no agent can do it, when none can.</param>
    /// <returns>The agent id, or null.</returns>
    private static string? PickAgent(AbstractAiStepHandler handler, StepContext context, DelegateTask task, out string why)
    {
        var helpers = Helpers(handler, context, task.Capabilities);
        why = string.Empty;
        if (task.Agent is null)
        {
            why = task.Capabilities.Count == 0
                ? "has no agent to go to"
                : $"needs {string.Join(", ", task.Capabilities)}, which no agent it may use has";
            return helpers.FirstOrDefault()?.Id;
        }

        var named = helpers.FirstOrDefault(a => string.Equals(a.Id, task.Agent, StringComparison.OrdinalIgnoreCase));
        why = $"names agent '{task.Agent}', which it may not use for it";
        return named?.Id;
    }

    /// <summary>
    ///     Runs one task and records how it went.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="delegation">The task's record.</param>
    /// <param name="executor">The agent's executor.</param>
    /// <param name="request">The request.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    private static async Task RunAsync(
        StepContext context,
        Delegation delegation,
        AbstractStepExecutor executor,
        ExecutionRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await executor.ExecuteAsync(request, context.ProgressFor(delegation.Agent), ct);
            (delegation.Status, delegation.Output, delegation.Error) = result.Status switch
            {
                ExecutionStatus.Completed => (StepStatus.Done, result.Output, null),
                ExecutionStatus.AwaitingSession => (StepStatus.Failed, null, "it needs a Claude session, so it can't take delegated work"),
                _ => (StepStatus.Failed, result.Output, result.Error ?? "it failed"),
            };
        }
        catch (AflException ex)
        {
            (delegation.Status, delegation.Error) = (StepStatus.Failed, ex.Message);
        }

        delegation.EndedAt = DateTimeOffset.Now;
    }

    /// <summary>
    ///     Describes tasks' results for the prompt that lets the AI carry on.
    /// </summary>
    /// <param name="all">Every task in the attempt, to number them.</param>
    /// <param name="shown">The tasks to describe.</param>
    /// <returns>The results.</returns>
    private static string DescribeResults(List<Delegation> all, IEnumerable<Delegation> shown)
    {
        return string.Join("\n\n", shown.Select(d => string.Format(StepConstants.DelegationResultFormat, all.IndexOf(d) + 1, d.Agent,
            d.Status == StepStatus.Done ? "done" : "failed",
            d.Status == StepStatus.Done ? d.Output ?? "(no output)" : d.Error)));
    }
}

/// <summary>
///     Lets the AI file follow-up tickets while it works (<see cref="AbstractAgentStepDefinition.CreateTickets" />): afl files them
///     in the run's ticket source (or the repo's, when the run's can't take them), records each as a
///     <see cref="CreatedTicket" />, and the AI carries on with their keys. A ticket afl couldn't file is recorded with
///     why and, where the source has one, an address to file it by hand. At most
///     <see cref="StepConstants.MaxTicketsPerStep" /> are filed an attempt.
/// </summary>
[ReplyBlockRegistration(StepConstants.TicketBlockTag, "The AI files follow-up tickets and carries on with their keys.")]
public class TicketReplyBlock : AbstractReplyBlock<IReadOnlyList<NewTicket>>
{
    /// <inheritdoc />
    protected override string Attempt => "file tickets";

    /// <inheritdoc />
    public override bool Applies(AbstractAiStepHandler handler, StepContext context)
    {
        return context.AgentDefinition is { CreateTickets: true } && handler.CanAiAsk(context.Definition) &&
               context.Tickets is not null && context.Repos is not null;
    }

    /// <inheritdoc />
    public override string? Instructions(AbstractAiStepHandler handler, StepContext context)
    {
        var remaining = StepConstants.MaxTicketsPerStep - context.State.CreatedTickets.Count;
        if (!this.Applies(handler, context) || remaining <= 0 || Destination(context) is not { } destination)
        {
            return null;
        }

        return string.Format(StepConstants.TicketInstructionsFormat,
            destination.Source.DescribeDestination(destination.Repo, context.Run.Ticket), remaining);
    }

    /// <inheritdoc />
    public override IEnumerable<string> Validate(AbstractStepHandler handler, AbstractStepDefinition step)
    {
        if (step is AbstractAgentStepDefinition { CreateTickets: true } && !handler.CanAiAsk(step))
        {
            yield return $"'createTickets' only works on an {StepConstants.TypeAi} step done by one agent.";
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<NewTicket> Read(JsonObject json)
    {
        return (json["tickets"] as JsonArray ?? throw new FormatException("it has no \"tickets\" list"))
            .Select(n => n as JsonObject ?? throw new FormatException("a ticket isn't an object"))
            .Select(t => new NewTicket
            {
                Title = t["title"]?.GetValue<string>().Trim() ?? string.Empty,
                Body = t["body"]?.GetValue<string>(),
                Labels = (t["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>().Trim() ?? string.Empty)
                    .Where(l => l.Length > 0).Distinct().ToList(),
                Type = t["type"]?.GetValue<string>().Trim().ToLowerInvariant() is { Length: > 0 } type ? type : NewTicket.DefaultType,
            })
            .ToList();
    }

    /// <inheritdoc />
    protected override string? Check(AbstractAiStepHandler handler, StepContext context, IReadOnlyList<NewTicket> request)
    {
        var remaining = StepConstants.MaxTicketsPerStep - context.State.CreatedTickets.Count;
        if (request.Count == 0)
        {
            return $"its {this.Tag} block has no tickets";
        }

        if (request.Count > remaining)
        {
            return $"only {Math.Max(remaining, 0)} more may be filed in a step (it asked for {request.Count})";
        }

        if (request.Select((t, i) => (t, i)).FirstOrDefault(p => p.t.Title.Length == 0) is { t: not null } untitled)
        {
            return $"ticket {untitled.i + 1} has no title";
        }

        return Destination(context) is null ? "there's nowhere to file tickets for this repo" : null;
    }

    /// <inheritdoc />
    protected override async Task<bool> HandleAsync(
        AbstractAiStepHandler handler,
        StepContext context,
        string output,
        IReadOnlyList<NewTicket> request,
        CancellationToken ct)
    {
        var state = context.State;
        var (source, repo) = Destination(context)!.Value;
        var round = state.CreatedTickets.Select(t => t.Round).DefaultIfEmpty(0).Max() + 1;

        // One at a time, so the tickets are numbered in the order the AI listed them.
        foreach (var ticket in request)
        {
            var created = new CreatedTicket { Round = round, Title = ticket.Title, At = DateTimeOffset.Now };
            try
            {
                var filed = await source.CreateAsync(repo, context.Run.Ticket, ticket, ct);
                (created.Key, created.Url) = (filed.Key, filed.Url);
            }
            catch (AflException ex)
            {
                (created.Error, created.Url) = (ex.Message, source.ManualCreateUrl(repo, context.Run.Ticket, ticket));
            }

            state.CreatedTickets.Add(created);
        }

        context.Save();
        var thisRound = state.CreatedTickets.Where(t => t.Round == round).ToList();
        context.Report($"Step '{state.Name}' filed {thisRound.Count(t => t.Key is not null)} of {thisRound.Count} tickets.");
        await handler.ContinueAsync(context, output,
            string.Join("\n\n", StepConstants.TicketResultsHeading, Describe(thisRound), StepConstants.TicketContinue),
            string.Join("\n\n", StepConstants.TicketResultsHeading, Describe(state.CreatedTickets), StepConstants.TicketContinue),
            ct);
        return true;
    }

    /// <summary>
    ///     Finds where the run's tickets go: the run's repo and the source that can file them there.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The source and repo, or null when there's nowhere.</returns>
    private static (AbstractTicketSource Source, RepoRecord Repo)? Destination(StepContext context)
    {
        if (context.Repos?.Find(context.Run.RepoId) is not { } repo ||
            context.Tickets?.CreatorFor(repo, context.Run.Ticket) is not { } source)
        {
            return null;
        }

        return (source, repo);
    }

    /// <summary>
    ///     Describes filed tickets for the prompt that lets the AI carry on.
    /// </summary>
    /// <param name="tickets">The tickets.</param>
    /// <returns>One line each.</returns>
    private static string Describe(IEnumerable<CreatedTicket> tickets)
    {
        return string.Join("\n", tickets.Select(t => $"- {t.Describe()}"));
    }
}
