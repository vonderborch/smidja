using Afl.Core.Constants;
using Afl.Core.Registration;
using Afl.Core.Workflows;

namespace Afl.Core.Flow;

/// <summary>
///     How a step ended, which decides which of its arrows can fire (<see cref="FlowMachine" />).
/// </summary>
public enum FlowTrigger
{
    /// <summary>
    ///     The step is done.
    /// </summary>
    Succeeded,

    /// <summary>
    ///     The step failed.
    /// </summary>
    Failed,

    /// <summary>
    ///     The step ran over its <c>timeout</c> (and so failed).
    /// </summary>
    TimedOut,

    /// <summary>
    ///     The step is optional and was left out when the run started, so it passes the run through.
    /// </summary>
    LeftOut,
}

/// <summary>
///     Registers an <see cref="AbstractOutcome" />: its key is the value of an arrow's <c>on</c>.
/// </summary>
/// <param name="key">The key (<c>success</c>, <c>failure</c>, <c>timeout</c>, <c>always</c>).</param>
/// <param name="description">When it fires, for front-ends.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class OutcomeRegistrationAttribute(string key, string description) : AbstractRegistrationAttribute(key)
{
    /// <summary>
    ///     Gets when the outcome fires, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     The outcomes an arrow can fire on.
/// </summary>
public class OutcomeRegistry() : TypeRegistry<AbstractOutcome, OutcomeRegistrationAttribute>("outcome")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static OutcomeRegistry Instance { get; } = new();

    /// <summary>
    ///     Returns the outcome an arrow fires on.
    /// </summary>
    /// <param name="arrow">The arrow.</param>
    /// <returns>The outcome.</returns>
    public AbstractOutcome Of(ArrowDefinition arrow)
    {
        return this.Create(arrow.Outcome);
    }
}

/// <summary>
///     When an arrow fires (its <c>on</c>), from how its step ended. A new outcome is one subclass with
///     <see cref="OutcomeRegistrationAttribute" />.
/// </summary>
public abstract class AbstractOutcome
{
    /// <summary>
    ///     Gets whether an arrow on this outcome deals with its step's failure, so the run can still end done. An arrow
    ///     that fires however the step ended runs the next step without dealing with it, like <c>finally</c>.
    /// </summary>
    public virtual bool HandlesFailure => true;

    /// <summary>
    ///     Returns whether an arrow on this outcome can fire when its step ended a given way.
    /// </summary>
    /// <param name="trigger">How the step ended.</param>
    /// <param name="step">The step, for outcomes that depend on its other arrows.</param>
    /// <returns>True if it can fire.</returns>
    public abstract bool Matches(FlowTrigger trigger, AbstractStepDefinition step);
}

/// <summary>
///     Fires when the step succeeds, or passes the run through because it was left out.
/// </summary>
[OutcomeRegistration(ArrowConstants.OnSuccess, "When the step succeeds")]
public class SuccessOutcome : AbstractOutcome
{
    /// <inheritdoc />
    public override bool HandlesFailure => false;

    /// <inheritdoc />
    public override bool Matches(FlowTrigger trigger, AbstractStepDefinition step)
    {
        return trigger is FlowTrigger.Succeeded or FlowTrigger.LeftOut;
    }
}

/// <summary>
///     Fires when the step fails, including running over its timeout when it has no arrow on timeout.
/// </summary>
[OutcomeRegistration(ArrowConstants.OnFailure, "When the step fails")]
public class FailureOutcome : AbstractOutcome
{
    /// <inheritdoc />
    public override bool Matches(FlowTrigger trigger, AbstractStepDefinition step)
    {
        return trigger == FlowTrigger.Failed ||
               (trigger == FlowTrigger.TimedOut && (step.Arrows ?? []).All(a => a.Outcome != ArrowConstants.OnTimeout));
    }
}

/// <summary>
///     Fires when the step runs over its timeout.
/// </summary>
[OutcomeRegistration(ArrowConstants.OnTimeout, "When the step runs over its timeout")]
public class TimeoutOutcome : AbstractOutcome
{
    /// <inheritdoc />
    public override bool Matches(FlowTrigger trigger, AbstractStepDefinition step)
    {
        return trigger == FlowTrigger.TimedOut;
    }
}

/// <summary>
///     Fires however the step ends, without dealing with a failure: the run carries on, and ends failed.
/// </summary>
[OutcomeRegistration(ArrowConstants.OnAlways, "However the step ends")]
public class AlwaysOutcome : AbstractOutcome
{
    /// <inheritdoc />
    public override bool HandlesFailure => false;

    /// <inheritdoc />
    public override bool Matches(FlowTrigger trigger, AbstractStepDefinition step)
    {
        return true;
    }
}
