using Afl.Core.Constants;
using Afl.Core.Registration;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     Registers a combine strategy with the <see cref="CombineStrategyRegistry" />.
/// </summary>
/// <param name="key">The value of <see cref="AbstractAgentStepDefinition.Combine" /> that picks the strategy.</param>
/// <param name="description">What the strategy does, for front-ends.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class CombineStrategyRegistrationAttribute(string key, string description) : AbstractRegistrationAttribute(key)
{
    /// <summary>
    ///     Gets what the strategy does, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     Finds combine strategies by key.
/// </summary>
public class CombineStrategyRegistry()
    : TypeRegistry<AbstractCombineStrategy, CombineStrategyRegistrationAttribute>("combine strategy")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static CombineStrategyRegistry Instance { get; } = new();

    /// <summary>
    ///     Creates the strategy a step uses: its <see cref="AbstractAgentStepDefinition.Combine" />, else
    ///     <see cref="StepConstants.CombineChoose" />.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The strategy.</returns>
    public AbstractCombineStrategy For(AbstractAgentStepDefinition step)
    {
        return this.Create(string.IsNullOrEmpty(step.Combine) ? StepConstants.CombineChoose : step.Combine);
    }
}

/// <summary>
///     Base for how the results of a step that several agents do (<see cref="AbstractAgentStepDefinition.Agents" />) become its
///     output. It runs once every agent has finished and at least one succeeded; it ends by giving the step its output
///     (through <see cref="AbstractAiStepHandler.UseOutputAsync" /> or <see cref="AbstractAiStepHandler.MergeAsync" />)
///     or by leaving the step waiting on someone. A new strategy is one subclass with a
///     <see cref="CombineStrategyRegistrationAttribute" />.
/// </summary>
public abstract class AbstractCombineStrategy
{
    /// <summary>
    ///     Checks a step's combine settings for problems specific to this strategy.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(AbstractAgentStepDefinition step)
    {
        return [];
    }

    /// <summary>
    ///     Turns the step's candidates (<see cref="RunStep.Candidates" />) into its output.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public abstract Task CombineAsync(AbstractAiStepHandler handler, StepContext context, CancellationToken ct);
}

/// <summary>
///     Waits for the user to choose one agent's result (editing it if they like), have an agent merge them, or send the
///     step back.
/// </summary>
[CombineStrategyRegistration(StepConstants.CombineChoose, "Wait for the user to choose a result or merge them")]
public class ChooseCombineStrategy : AbstractCombineStrategy
{
    /// <inheritdoc />
    public override Task CombineAsync(AbstractAiStepHandler handler, StepContext context, CancellationToken ct)
    {
        context.State.PendingPrompt = null;
        StepLifecycle.Instance.Move(context.State, StepStatus.AwaitingChoice);
        return Task.CompletedTask;
    }
}

/// <summary>
///     Has the step's merge agent (its first agent by default) merge every result into one.
/// </summary>
[CombineStrategyRegistration(StepConstants.CombineMerge, "Have an agent merge the results")]
public class MergeCombineStrategy : AbstractCombineStrategy
{
    /// <inheritdoc />
    public override Task CombineAsync(AbstractAiStepHandler handler, StepContext context, CancellationToken ct)
    {
        return handler.MergeAsync(context, null, null, ct);
    }
}

/// <summary>
///     Keeps every result, one after another under each agent's heading.
/// </summary>
[CombineStrategyRegistration(StepConstants.CombineAll, "Keep every result, one after another")]
public class AllCombineStrategy : AbstractCombineStrategy
{
    /// <inheritdoc />
    public override IEnumerable<string> Validate(AbstractAgentStepDefinition step)
    {
        if (step.MergeAgent is not null || step.MergePrompt is not null)
        {
            yield return $"mergeAgent and mergePrompt aren't used with combine '{StepConstants.CombineAll}'.";
        }
    }

    /// <inheritdoc />
    public override Task CombineAsync(AbstractAiStepHandler handler, StepContext context, CancellationToken ct)
    {
        return handler.UseOutputAsync(context, context.State.CombineCandidates(), ct);
    }
}
