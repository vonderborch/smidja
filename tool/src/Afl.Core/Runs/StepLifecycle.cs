using Afl.Core.Exceptions;
using Afl.Core.StateMachines;

namespace Afl.Core.Runs;

/// <summary>
///     What moves a step along its lifecycle (<see cref="StepLifecycle" />).
/// </summary>
public enum StepTrigger
{
    /// <summary>
    ///     The engine reaches a pending step and begins it.
    /// </summary>
    Begin,

    /// <summary>
    ///     Someone answers what the step is asking.
    /// </summary>
    Answer,

    /// <summary>
    ///     Allow or deny the tool calls the AI was refused, or accept its output as it is.
    /// </summary>
    Permit,

    /// <summary>
    ///     Someone picks which agent does the step.
    /// </summary>
    UseAgent,

    /// <summary>
    ///     The user approves the step's output.
    /// </summary>
    Approve,

    /// <summary>
    ///     The user sends the step back with feedback.
    /// </summary>
    Reject,

    /// <summary>
    ///     A person marks their work on the step done.
    /// </summary>
    Complete,

    /// <summary>
    ///     The calling Claude session gives the step its output.
    /// </summary>
    Submit,

    /// <summary>
    ///     The calling Claude session or the person doing the step says it failed.
    /// </summary>
    GiveUp,

    /// <summary>
    ///     The user picks one agent's result.
    /// </summary>
    Choose,

    /// <summary>
    ///     An agent merges several agents' results.
    /// </summary>
    Merge,

    /// <summary>
    ///     The step is restarted after it failed.
    /// </summary>
    Retry,

    /// <summary>
    ///     The step (or one before it) is replayed, or a loop or route goes back round it.
    /// </summary>
    Replay,

    /// <summary>
    ///     The step is passed over before it starts: it was left out when the run started.
    /// </summary>
    Skip,

    /// <summary>
    ///     The engine looks again at the child or path runs the step is waiting for.
    /// </summary>
    Refresh,

    /// <summary>
    ///     The process working on the step stopped part-way.
    /// </summary>
    Interrupt,

    /// <summary>
    ///     The agent working on the step finishes a pass.
    /// </summary>
    Work,

    /// <summary>
    ///     A step's arrows are followed once it ends: they start several paths, or a condition can't be checked.
    /// </summary>
    Route,
}

/// <summary>
///     The lifecycle every step follows, whatever its kind, as a state machine over <see cref="StepStatus" />. Actions
///     check they can fire from where the step is (<see cref="Require" />) and handlers change a step's status only
///     with <see cref="Move" />, which refuses a change no transition allows, so a step can't end up somewhere it has no
///     way out of.
/// </summary>
public class StepLifecycle : AbstractStateMachine<StepStatus, StepTrigger, RunStep, StateTransition<StepStatus, StepTrigger, RunStep>>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="StepLifecycle" /> class.
    /// </summary>
    public StepLifecycle()
    {
        foreach (var status in Enum.GetValues<StepStatus>())
        {
            this.AddState(status);
        }

        // Anywhere a step can go once it's under way.
        StepStatus[] working = Enum.GetValues<StepStatus>().Where(s => s is not (StepStatus.Pending or StepStatus.Skipped)).ToArray();
        this.Add(StepStatus.Pending, StepTrigger.Begin, working);
        this.Add(StepStatus.Pending, StepTrigger.Skip, StepStatus.Skipped, StepStatus.Failed);
        this.Add(StepStatus.Pending, StepTrigger.UseAgent, [StepStatus.Pending, ..working]);
        this.Add(StepStatus.ChoosingAgent, StepTrigger.UseAgent, working);
        this.Add(StepStatus.AwaitingInput, StepTrigger.Answer, working);
        this.Add(StepStatus.AwaitingPermission, StepTrigger.Permit, working);
        this.Add(StepStatus.Running, StepTrigger.Work, working);
        this.Add(StepStatus.Running, StepTrigger.Merge, working);
        this.Add(StepStatus.Running, StepTrigger.Interrupt, StepStatus.Failed);
        this.Add(StepStatus.AwaitingSession, StepTrigger.Submit, working);
        this.Add(StepStatus.AwaitingSession, StepTrigger.Complete, working);
        this.Add(StepStatus.AwaitingUser, StepTrigger.Complete, working);
        this.Add(StepStatus.AwaitingSession, StepTrigger.GiveUp, StepStatus.Failed);
        this.Add(StepStatus.AwaitingUser, StepTrigger.GiveUp, StepStatus.Failed);
        this.Add(StepStatus.AwaitingApproval, StepTrigger.Approve, StepStatus.Done, StepStatus.Failed);
        this.Add(StepStatus.AwaitingApproval, StepTrigger.Reject, StepStatus.Pending);
        this.Add(StepStatus.AwaitingChoice, StepTrigger.Choose, working);
        this.Add(StepStatus.AwaitingChoice, StepTrigger.Merge, working);
        this.Add(StepStatus.AwaitingChoice, StepTrigger.Reject, StepStatus.Pending);
        this.Add(StepStatus.AwaitingChildren, StepTrigger.Refresh, working);
        this.Add(StepStatus.AwaitingPaths, StepTrigger.Refresh, [..working, StepStatus.Skipped]);
        this.Add(StepStatus.Done, StepTrigger.Route, StepStatus.AwaitingPaths, StepStatus.Failed);
        this.Add(StepStatus.Failed, StepTrigger.Route, StepStatus.AwaitingPaths);
        this.Add(StepStatus.Skipped, StepTrigger.Route, StepStatus.AwaitingPaths);
        this.Add(StepStatus.Failed, StepTrigger.Retry, StepStatus.Pending, StepStatus.AwaitingChildren, StepStatus.AwaitingPaths);
        foreach (var status in working)
        {
            this.Add(status, StepTrigger.Replay, StepStatus.Pending);
        }
    }

    /// <summary>
    ///     Gets the shared lifecycle.
    /// </summary>
    public static StepLifecycle Instance { get; } = new();

    /// <inheritdoc />
    public override StepStatus Initial => StepStatus.Pending;

    /// <summary>
    ///     Throws unless a trigger can fire from where the step is.
    /// </summary>
    /// <param name="state">The step.</param>
    /// <param name="trigger">The trigger.</param>
    /// <param name="action">The action being attempted, for the message, e.g. "approved".</param>
    /// <exception cref="AflException">The step can't be acted on that way now.</exception>
    public void Require(RunStep state, StepTrigger trigger, string action)
    {
        if (!this.CanFire(state.Status, trigger))
        {
            throw new AflException(AflErrorKind.InvalidState, $"Step '{state.StepId}' is {state.Status}; it can't be {action} right now.");
        }
    }

    /// <summary>
    ///     Moves a step to a status, if some transition goes there from where it is; staying put is always allowed.
    /// </summary>
    /// <param name="state">The step.</param>
    /// <param name="to">Where it goes.</param>
    /// <exception cref="InvalidOperationException">No transition goes there: a bug in the handler moving it.</exception>
    public void Move(RunStep state, StepStatus to)
    {
        if (state.Status != to && !this.Permits(state.Status, to))
        {
            throw new InvalidOperationException($"Step '{state.StepId}' can't go from {state.Status} to {to}.");
        }

        state.Status = to;
    }

    /// <summary>
    ///     Declares the transitions a trigger may take from a status, one per status it may go to.
    /// </summary>
    /// <param name="from">Where they leave.</param>
    /// <param name="trigger">What fires them.</param>
    /// <param name="targets">Where the step may go.</param>
    private void Add(StepStatus from, StepTrigger trigger, params StepStatus[] targets)
    {
        foreach (var target in targets)
        {
            this.AddTransition(new StateTransition<StepStatus, StepTrigger, RunStep>(from, trigger, [target]));
        }
    }
}
