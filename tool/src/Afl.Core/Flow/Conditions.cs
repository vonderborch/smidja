using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Registration;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Flow;

/// <summary>
///     A condition an arrow checks once its step has ended (<see cref="ArrowDefinition.When" /> or
///     <see cref="ArrowDefinition.Until" />). Which fields apply depends on <see cref="Kind" />; see the matching <see cref="AbstractCondition" />.
/// </summary>
public class ConditionDefinition
{
    /// <summary>
    ///     Gets or sets the kind: <c>input</c>, <c>output</c>, <c>ai</c>, <c>all</c>, <c>any</c>, <c>not</c> or
    ///     <c>always</c>.
    /// </summary>
    public string Kind { get; set; } = StepConstants.ConditionAlways;

    /// <summary>
    ///     Gets or sets the id of the step the condition looks at: the step just done when unset, else an earlier one.
    /// </summary>
    public string? Step { get; set; }

    /// <summary>
    ///     Gets or sets the id of the input whose answer an <c>input</c> condition checks.
    /// </summary>
    public string? Input { get; set; }

    /// <summary>
    ///     Gets or sets the answer an <c>input</c> condition wants, ignoring case.
    /// </summary>
    [JsonPropertyName("equals")]
    public string? EqualTo { get; set; }

    /// <summary>
    ///     Gets or sets text an <c>output</c> condition wants in the step's output, ignoring case.
    /// </summary>
    public string? Contains { get; set; }

    /// <summary>
    ///     Gets or sets a regular expression an <c>output</c> condition wants to match the step's output.
    /// </summary>
    public string? Matches { get; set; }

    /// <summary>
    ///     Gets or sets the yes/no question an <c>ai</c> condition asks about the step's output (template).
    /// </summary>
    public string? Question { get; set; }

    /// <summary>
    ///     Gets or sets the agent that answers an <c>ai</c> condition; the step's agent when unset.
    /// </summary>
    public string? Agent { get; set; }

    /// <summary>
    ///     Gets or sets the nested conditions of an <c>all</c>, <c>any</c> or <c>not</c> condition.
    /// </summary>
    public List<ConditionDefinition>? Conditions { get; set; }

    /// <summary>
    ///     Returns this condition and every condition nested in it.
    /// </summary>
    /// <returns>The conditions, this one first.</returns>
    public IEnumerable<ConditionDefinition> Flatten()
    {
        return new[] { this }.Concat((this.Conditions ?? []).SelectMany(c => c.Flatten()));
    }
}

/// <summary>
///     The fields of a <see cref="ConditionDefinition" /> a kind of condition uses, so front-ends know what to ask for
///     without knowing the kind.
/// </summary>
[Flags]
public enum ConditionFields
{
    /// <summary>
    ///     No fields.
    /// </summary>
    None = 0,

    /// <summary>
    ///     <see cref="ConditionDefinition.Step" />.
    /// </summary>
    Step = 1,

    /// <summary>
    ///     <see cref="ConditionDefinition.Input" /> and <see cref="ConditionDefinition.EqualTo" />.
    /// </summary>
    Input = 2,

    /// <summary>
    ///     <see cref="ConditionDefinition.Contains" /> and <see cref="ConditionDefinition.Matches" />.
    /// </summary>
    Text = 4,

    /// <summary>
    ///     <see cref="ConditionDefinition.Question" /> and <see cref="ConditionDefinition.Agent" />.
    /// </summary>
    Question = 8,

    /// <summary>
    ///     <see cref="ConditionDefinition.Conditions" />.
    /// </summary>
    Nested = 16,
}

/// <summary>
///     Registers a condition kind with the <see cref="ConditionRegistry" />.
/// </summary>
/// <param name="kind">The value of <see cref="ConditionDefinition.Kind" /> that picks the condition.</param>
/// <param name="description">What the condition checks, for front-ends.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class ConditionRegistrationAttribute(string kind, string description) : AbstractRegistrationAttribute(kind)
{
    /// <summary>
    ///     Gets what the condition checks, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     Finds conditions by kind, and checks and evaluates condition definitions with them.
/// </summary>
public class ConditionRegistry() : TypeRegistry<AbstractCondition, ConditionRegistrationAttribute>("condition")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static ConditionRegistry Instance { get; } = new();

    /// <summary>
    ///     Checks a condition (and the ones nested in it) on a step for problems.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step the condition is on.</param>
    /// <returns>The problems; empty when valid.</returns>
    public IEnumerable<string> Validate(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        if (!this.Contains(condition.Kind))
        {
            return [$"unknown condition kind '{condition.Kind}'. Known: {string.Join(", ", this.Keys.Order())}."];
        }

        return this.Create(condition.Kind).Validate(condition, workflow, step);
    }

    /// <summary>
    ///     Evaluates a condition on the step just done.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="context">The context of the step just done.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Whether the condition holds.</returns>
    public Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        return this.Create(condition.Kind).EvaluateAsync(condition, context, ct);
    }
}

/// <summary>
///     Base for a kind of condition. A new kind is one subclass with a <see cref="ConditionRegistrationAttribute" />.
/// </summary>
public abstract class AbstractCondition
{
    /// <summary>
    ///     Gets the fields of a <see cref="ConditionDefinition" /> this kind uses.
    /// </summary>
    public virtual ConditionFields Fields => ConditionFields.None;

    /// <summary>
    ///     Checks a condition's settings for problems.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step the condition is on.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        return [];
    }

    /// <summary>
    ///     Evaluates the condition once a step is done.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="context">The context of the step just done.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Whether the condition holds.</returns>
    public abstract Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct);

    /// <summary>
    ///     Finds the step a condition looks at, checking it is the step the condition is on or one that leads to it.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step the condition is on.</param>
    /// <param name="problems">Receives a problem when the step can't be used.</param>
    /// <returns>The step, or null when it can't be used.</returns>
    protected static AbstractStepDefinition? ReferencedStep(
        ConditionDefinition condition,
        WorkflowDefinition workflow,
        AbstractStepDefinition step,
        List<string> problems)
    {
        if (condition.Step is null || condition.Step == step.Id)
        {
            return step;
        }

        var machine = new FlowMachine(workflow);
        if (!machine.Contains(step.Id) || !machine.Ancestors(step.Id).Contains(condition.Step))
        {
            problems.Add($"condition step '{condition.Step}' must be this step or one that leads to it.");
            return null;
        }

        return machine.StepOf(condition.Step);
    }

    /// <summary>
    ///     Returns the state of the step a condition looks at.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="context">The context of the step just done.</param>
    /// <returns>The step's state.</returns>
    protected static RunStep StateOf(ConditionDefinition condition, StepContext context)
    {
        return condition.Step is null
            ? context.State
            : context.Run.Steps.First(s => s.StepId == condition.Step);
    }

    /// <summary>
    ///     Checks the nested conditions of a compound condition.
    /// </summary>
    /// <param name="condition">The compound condition.</param>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step the condition is on.</param>
    /// <returns>The problems; empty when valid.</returns>
    protected static IEnumerable<string> ValidateNested(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        return (condition.Conditions ?? []).SelectMany(c => ConditionRegistry.Instance.Validate(c, workflow, step));
    }
}

/// <summary>
///     Holds when the answer to a step's input is a given value, ignoring case.
/// </summary>
[ConditionRegistration(StepConstants.ConditionInput, "An input's answer is a given value")]
public class InputCondition : AbstractCondition
{
    /// <inheritdoc />
    public override ConditionFields Fields => ConditionFields.Step | ConditionFields.Input;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(condition.Input) || condition.EqualTo is null)
        {
            problems.Add("an input condition needs 'input' and 'equals'.");
            return problems;
        }

        var referenced = ReferencedStep(condition, workflow, step, problems);
        if (referenced is null)
        {
            return problems;
        }

        var input = referenced.Inputs?.FirstOrDefault(i => i.Id == condition.Input);
        if (input is null)
        {
            problems.Add($"step '{referenced.Id}' has no input '{condition.Input}'.");
        }
        else if (StepInputTypeRegistry.Instance.Contains(input.Type))
        {
            try
            {
                StepInputTypeRegistry.Instance.For(input).Normalize(input, condition.EqualTo);
            }
            catch (AflException ex)
            {
                problems.Add($"'equals' can't be an answer to input '{input.Id}': {ex.Message}");
            }
        }

        return problems;
    }

    /// <inheritdoc />
    public override Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        var state = StateOf(condition, context);
        var answer = state.Answers.GetValueOrDefault(condition.Input!) ?? string.Empty;
        var wanted = condition.EqualTo ?? string.Empty;
        if (context.Run.DefinitionOf(state).Inputs?.FirstOrDefault(i => i.Id == condition.Input) is { } input)
        {
            wanted = StepInputTypeRegistry.Instance.For(input).Normalize(input, wanted);
        }

        return Task.FromResult(string.Equals(answer.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     Holds when a step's output contains some text (ignoring case) or matches a regular expression.
/// </summary>
[ConditionRegistration(StepConstants.ConditionOutput, "The output contains some text or matches a pattern")]
public class OutputCondition : AbstractCondition
{
    /// <inheritdoc />
    public override ConditionFields Fields => ConditionFields.Step | ConditionFields.Text;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        var problems = new List<string>();
        ReferencedStep(condition, workflow, step, problems);
        if (string.IsNullOrEmpty(condition.Contains) == string.IsNullOrEmpty(condition.Matches))
        {
            problems.Add("an output condition needs one of 'contains' or 'matches'.");
        }
        else if (condition.Matches is not null)
        {
            try
            {
                _ = new Regex(condition.Matches);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"'matches' isn't a valid regular expression: {ex.Message}");
            }
        }

        return problems;
    }

    /// <inheritdoc />
    public override Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        var output = StateOf(condition, context).Output ?? string.Empty;
        return Task.FromResult(string.IsNullOrEmpty(condition.Contains)
            ? Regex.IsMatch(output, condition.Matches!, RegexOptions.Multiline, TimeSpan.FromSeconds(5))
            : output.Contains(condition.Contains, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     Asks an agent a yes/no question about a step's output. The agent works read-only in a fresh conversation, its
///     answer is the first word of its reply, and the exchange is logged next to the step's log.
/// </summary>
[ConditionRegistration(StepConstants.ConditionAi, "An agent answers yes to a question about the output")]
public class AiCondition : AbstractCondition
{
    /// <inheritdoc />
    public override ConditionFields Fields => ConditionFields.Step | ConditionFields.Question;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        var problems = new List<string>();
        ReferencedStep(condition, workflow, step, problems);
        if (string.IsNullOrWhiteSpace(condition.Question))
        {
            problems.Add("an ai condition needs a 'question'.");
        }

        if (condition.Agent is not null && !WorkflowLibrary.IsValidId(condition.Agent))
        {
            problems.Add($"agent '{condition.Agent}' isn't an agent id (see afl agent list).");
        }

        return problems;
    }

    /// <inheritdoc />
    public override async Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        var agent = condition.Agent ?? context.AgentId;
        var question = context.Render(condition.Question);
        var prompt = string.Format(StepConstants.ConditionPromptFormat, question, StateOf(condition, context).Output ?? string.Empty);
        var request = context.CreateRequest(agent, prompt, Guid.NewGuid().ToString(), false,
            context.LogPathFor(StepConstants.ConditionLogName), StepConstants.ReadOnlyPermissionMode);
        context.Report($"Asking {agent}: {question}");
        var result = await context.ExecutorFor(agent).ExecuteAsync(request, context.ProgressFor(agent), ct);
        if (result.Status == ExecutionStatus.AwaitingSession)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"Agent '{agent}' can't answer an ai condition, since afl doesn't run it; give the condition an agent such as claude.");
        }

        if (result.Status == ExecutionStatus.Failed)
        {
            throw new AflException(AflErrorKind.ExternalTool, $"Agent '{agent}' couldn't answer '{question}': {result.Error}");
        }

        var word = new string((result.Output ?? string.Empty).TrimStart().TakeWhile(char.IsLetter).ToArray());
        bool answer;
        if (word.Equals(StepConstants.Yes, StringComparison.OrdinalIgnoreCase))
        {
            answer = true;
        }
        else if (word.Equals(StepConstants.No, StringComparison.OrdinalIgnoreCase))
        {
            answer = false;
        }
        else
        {
            throw new AflException(AflErrorKind.ExternalTool,
                $"Agent '{agent}' didn't answer yes or no to '{question}': {result.Output?.Trim()}");
        }

        context.State.Notes.Add($"{agent} answered {(answer ? "yes" : "no")} to: {question}");
        return answer;
    }
}

/// <summary>
///     Holds when every nested condition does. Stops at the first that doesn't.
/// </summary>
[ConditionRegistration(StepConstants.ConditionAll, "All of several conditions hold")]
public class AllCondition : AbstractCondition
{
    /// <inheritdoc />
    public override ConditionFields Fields => ConditionFields.Nested;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        return condition.Conditions is { Count: > 0 }
            ? ValidateNested(condition, workflow, step)
            : [$"an {condition.Kind} condition needs 'conditions'."];
    }

    /// <inheritdoc />
    public override async Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        foreach (var nested in condition.Conditions ?? [])
        {
            if (!await ConditionRegistry.Instance.EvaluateAsync(nested, context, ct))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
///     Holds when any nested condition does. Stops at the first that does.
/// </summary>
[ConditionRegistration(StepConstants.ConditionAny, "Any of several conditions holds")]
public class AnyCondition : AllCondition
{
    /// <inheritdoc />
    public override async Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        foreach (var nested in condition.Conditions ?? [])
        {
            if (await ConditionRegistry.Instance.EvaluateAsync(nested, context, ct))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     Holds when its one nested condition doesn't.
/// </summary>
[ConditionRegistration(StepConstants.ConditionNot, "A condition doesn't hold")]
public class NotCondition : AbstractCondition
{
    /// <inheritdoc />
    public override ConditionFields Fields => ConditionFields.Nested;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ConditionDefinition condition, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        return condition.Conditions is { Count: 1 }
            ? ValidateNested(condition, workflow, step)
            : ["a not condition needs exactly one condition in 'conditions'."];
    }

    /// <inheritdoc />
    public override async Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        return !await ConditionRegistry.Instance.EvaluateAsync(condition.Conditions![0], context, ct);
    }
}

/// <summary>
///     Always holds.
/// </summary>
[ConditionRegistration(StepConstants.ConditionAlways, "Always")]
public class AlwaysCondition : AbstractCondition
{
    /// <inheritdoc />
    public override Task<bool> EvaluateAsync(ConditionDefinition condition, StepContext context, CancellationToken ct)
    {
        return Task.FromResult(true);
    }
}
