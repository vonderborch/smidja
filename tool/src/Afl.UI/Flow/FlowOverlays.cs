using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.UI.DataContexts;

namespace Afl.UI.Flow;

/// <summary>
///     How a step went, as an overlay on the flow graph shows it.
/// </summary>
public enum FlowNodeMark
{
    /// <summary>
    ///     Nothing to show: there's no overlay.
    /// </summary>
    None,

    /// <summary>
    ///     The step ran and finished.
    /// </summary>
    Reached,

    /// <summary>
    ///     The step is running or waiting on someone.
    /// </summary>
    Active,

    /// <summary>
    ///     The step failed.
    /// </summary>
    Failed,

    /// <summary>
    ///     The step was left out, so it passed straight through.
    /// </summary>
    Skipped,

    /// <summary>
    ///     The step's path was cancelled because another path got where it led first.
    /// </summary>
    Cancelled,

    /// <summary>
    ///     Nothing reached the step, or hasn't yet.
    /// </summary>
    NotReached,
}

/// <summary>
///     What is drawn over the flow graph to show how a run goes: which arrows fired or died, and how each step went.
///     <see cref="SimulationOverlay" /> shows a <see cref="FlowSimulator" />'s run and <see cref="RunOverlay" /> a real
///     one, so the editor and the Runs page draw both the same way.
/// </summary>
public abstract class AbstractFlowOverlay
{
    /// <summary>
    ///     Gets a line saying how the run went, as a whole.
    /// </summary>
    public abstract string Summary { get; }

    /// <summary>
    ///     Returns the key an arrow's state is kept under (<see cref="FlowMachine.KeyOf(string, string, string)" />).
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>The key, or null for an arrow that isn't a step's.</returns>
    public static string? KeyOf(FlowEdge edge)
    {
        return edge is { Arrow: { } arrow, From.Step: { } step } ? FlowMachine.KeyOf(step.Id.Trim(), arrow.To.Trim(), arrow.On) : null;
    }

    /// <summary>
    ///     Returns how an arrow ended up.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>Its state; <see cref="ArrowState.Pending" /> when it hasn't fired or died.</returns>
    public abstract ArrowState StateOf(FlowEdge edge);

    /// <summary>
    ///     Returns how a step went.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The mark.</returns>
    public abstract FlowNodeMark MarkOf(string stepId);

    /// <summary>
    ///     Returns how many times a step ran.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The count.</returns>
    public abstract int RunsOf(string stepId);

    /// <summary>
    ///     Returns the few words shown under a step, e.g. <c>✓ ran ×3</c> or <c>not reached</c>.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The words, or null for none.</returns>
    public virtual string? StatusOf(string stepId)
    {
        var runs = this.RunsOf(stepId);
        var times = runs > 1 ? $" ×{runs}" : string.Empty;
        return this.MarkOf(stepId) switch
        {
            FlowNodeMark.Reached => $"✓ ran{times}",
            FlowNodeMark.Active => $"● running{times}",
            FlowNodeMark.Failed => $"✗ failed{times}",
            FlowNodeMark.Skipped => "left out",
            FlowNodeMark.Cancelled => "cancelled · overtaken",
            FlowNodeMark.NotReached => "not reached",
            _ => null,
        };
    }

    /// <summary>
    ///     Returns the label drawn beside an arrow.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>The label; the arrow's own by default.</returns>
    public virtual string? LabelOf(FlowEdge edge)
    {
        return edge.Label;
    }
}

/// <summary>
///     Shows a simulated run (<see cref="FlowSimulation" />), and whether each arrow that might or might not be taken was.
/// </summary>
public class SimulationOverlay : AbstractFlowOverlay
{
    /// <summary>
    ///     Whether each arrow the simulation could take or not was taken, by key.
    /// </summary>
    private readonly IReadOnlyDictionary<string, bool> _choices;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SimulationOverlay" /> class.
    /// </summary>
    /// <param name="simulation">The simulated run.</param>
    /// <param name="choices">Whether each conditional arrow or arrow back was taken, by key.</param>
    public SimulationOverlay(FlowSimulation simulation, IReadOnlyDictionary<string, bool> choices)
    {
        this.Simulation = simulation;
        this._choices = choices;
    }

    /// <summary>
    ///     Gets the simulated run.
    /// </summary>
    public FlowSimulation Simulation { get; }

    /// <inheritdoc />
    public override string Summary
    {
        get
        {
            var ran = this.Simulation.Visits.Count;
            var steps = ran == 1 ? "1 step run" : $"{ran} step runs";
            return this.Simulation switch
            {
                { Failed: true, FailedAt: { } at } => $"Simulated: {steps}, then the run fails at '{at}'.",
                { Failed: true, Error: { } error } => $"Simulated: {steps}, then the run stops: {error}",
                { Failed: true } => $"Simulated: {steps}; the run ends failed.",
                _ => $"Simulated: {steps}; the run ends done.",
            };
        }
    }

    /// <inheritdoc />
    public override ArrowState StateOf(FlowEdge edge)
    {
        return KeyOf(edge) is not { } key ? ArrowState.Pending
            : this.Simulation.Fired.Contains(key) ? ArrowState.Fired
            : this.Simulation.Dead.Contains(key) ? ArrowState.Dead
            : ArrowState.Pending;
    }

    /// <inheritdoc />
    public override FlowNodeMark MarkOf(string stepId)
    {
        var visits = this.Simulation.Visits.Where(v => v.StepId == stepId).ToList();
        if (visits.Count == 0)
        {
            return this.Simulation.Overtaken.Contains(stepId) ? FlowNodeMark.Cancelled : FlowNodeMark.NotReached;
        }

        return visits[^1].Outcome switch
        {
            FlowTrigger.LeftOut => FlowNodeMark.Skipped,
            FlowTrigger.Failed or FlowTrigger.TimedOut => FlowNodeMark.Failed,
            _ => FlowNodeMark.Reached,
        };
    }

    /// <inheritdoc />
    public override int RunsOf(string stepId)
    {
        return this.Simulation.RunsOf(stepId);
    }

    /// <inheritdoc />
    public override string? LabelOf(FlowEdge edge)
    {
        if (KeyOf(edge) is not { } key || !this._choices.TryGetValue(key, out var taken))
        {
            return edge.Label;
        }

        var choice = taken ? "✓ taken" : "✗ not taken";
        return edge.Label is { } label ? $"{label} · {choice}" : choice;
    }
}

/// <summary>
///     Shows a real run: its steps' statuses and how many times each ran, and the arrows that fired or died in the
///     current pass (<see cref="Run.Arrows" />).
/// </summary>
public class RunOverlay : AbstractFlowOverlay
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RunOverlay" /> class.
    /// </summary>
    /// <param name="run">The run.</param>
    public RunOverlay(Run run)
    {
        this.Run = run;
    }

    /// <summary>
    ///     Gets the run.
    /// </summary>
    public Run Run { get; }

    /// <inheritdoc />
    public override string Summary
    {
        get
        {
            var active = this.ActiveSteps().ToList();
            return active.Count == 0 ? $"The run is {UiConverters.ToText(this.Run.Status)}."
                : $"The run is {UiConverters.ToText(this.Run.Status)}, at {string.Join(", ", active.Select(s => $"'{s}'"))}.";
        }
    }

    /// <inheritdoc />
    public override ArrowState StateOf(FlowEdge edge)
    {
        return KeyOf(edge) is { } key && this.Run.Arrows.TryGetValue(key, out var state) ? state : ArrowState.Pending;
    }

    /// <inheritdoc />
    public override FlowNodeMark MarkOf(string stepId)
    {
        if (this.Run.StepOf(stepId) is not { } step)
        {
            return FlowNodeMark.None;
        }

        return step.Status switch
        {
            StepStatus.Done => FlowNodeMark.Reached,
            StepStatus.Failed => FlowNodeMark.Failed,
            StepStatus.Skipped => FlowNodeMark.Skipped,
            StepStatus.Pending when this.ActiveSteps().Contains(stepId) => FlowNodeMark.Active,
            StepStatus.Pending => FlowNodeMark.NotReached,
            _ => FlowNodeMark.Active,
        };
    }

    /// <inheritdoc />
    public override int RunsOf(string stepId)
    {
        return this.Run.StepOf(stepId) is { } step ? Math.Max(step.Runs, step.History.Count + (step.Status == StepStatus.Pending ? 0 : 1)) : 0;
    }

    /// <inheritdoc />
    public override string? StatusOf(string stepId)
    {
        if (this.MarkOf(stepId) == FlowNodeMark.NotReached)
        {
            return this.Run.IsFinished ? "not reached" : null;
        }

        if (this.MarkOf(stepId) == FlowNodeMark.Active && this.Run.StepOf(stepId) is { Status: not StepStatus.Running and not StepStatus.Pending } step)
        {
            var runs = this.RunsOf(stepId);
            return $"● {UiConverters.ToText(step.Status)}{(runs > 1 ? $" ×{runs}" : string.Empty)}";
        }

        return base.StatusOf(stepId);
    }

    /// <summary>
    ///     Returns the steps the run is at: its own step and those of its paths.
    /// </summary>
    /// <returns>The step ids.</returns>
    private IEnumerable<string> ActiveSteps()
    {
        if (this.Run.IsFinished)
        {
            return [];
        }

        return this.Run.Paths.Select(p => p.StepId).Prepend(this.Run.CurrentStepId).OfType<string>().Distinct();
    }
}
