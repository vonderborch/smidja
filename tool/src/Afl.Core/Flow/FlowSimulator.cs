using Afl.Core.Constants;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Flow;

/// <summary>
///     How a simulated run went (<see cref="FlowSimulator.SimulateAsync(WorkflowDefinition, CancellationToken)" />): the steps in the order they ran, how each
///     arrow ended up, and how the run ended.
/// </summary>
public class FlowSimulation
{
    /// <summary>
    ///     Gets the steps in the order they ran, with how each ended; a step a loop brings back is in it once per round.
    /// </summary>
    public List<(string StepId, FlowTrigger Outcome)> Visits { get; } = [];

    /// <summary>
    ///     Gets the arrows that fired at least once, by key (<see cref="FlowMachine.KeyOf(string, string, string)" />).
    /// </summary>
    public HashSet<string> Fired { get; } = [];

    /// <summary>
    ///     Gets the arrows that died and never fired, by key.
    /// </summary>
    public HashSet<string> Dead { get; } = [];

    /// <summary>
    ///     Gets the steps whose paths were cancelled because another path got to where they led first.
    /// </summary>
    public HashSet<string> Overtaken { get; } = [];

    /// <summary>
    ///     Gets or sets the step a failure stopped the run at, if one did.
    /// </summary>
    public string? FailedAt { get; set; }

    /// <summary>
    ///     Gets or sets why the run failed or stopped, if it did.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Gets or sets whether the run ended failed: a step failed and nothing handled it, or a loop hit its limit.
    /// </summary>
    public bool Failed { get; set; }

    /// <summary>
    ///     Returns how many times a step ran.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The count.</returns>
    public int RunsOf(string stepId)
    {
        return this.Visits.Count(v => v.StepId == stepId);
    }
}

/// <summary>
///     Runs a workflow's flow without running any step: each step ends the way it's told to (success by default), and
///     each conditional arrow is taken or not as it's told to. It routes with the run's own <see cref="FlowRouter" />
///     code (arrows back, <c>follow</c>, wait rules, dead paths, loop limits, the catch step) and starts and cancels
///     paths as <see cref="FlowCoordinator" /> does, one step at a time, so what it shows is what a run does.
/// </summary>
public class FlowSimulator : FlowRouter
{
    /// <summary>
    ///     How each step ends, by id.
    /// </summary>
    private readonly IReadOnlyDictionary<string, FlowTrigger> _outcomes;

    /// <summary>
    ///     Whether each conditional arrow is taken, by key.
    /// </summary>
    private readonly IReadOnlyDictionary<string, bool> _taken;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FlowSimulator" /> class.
    /// </summary>
    /// <param name="outcomes">How each step ends, by id; a step not in it succeeds.</param>
    /// <param name="taken">Whether each conditional arrow is taken, by key; see <see cref="TakenByDefault" />.</param>
    public FlowSimulator(IReadOnlyDictionary<string, FlowTrigger>? outcomes = null, IReadOnlyDictionary<string, bool>? taken = null)
    {
        this._outcomes = outcomes ?? new Dictionary<string, FlowTrigger>();
        this._taken = taken ?? new Dictionary<string, bool>();
    }

    /// <summary>
    ///     Returns whether a conditional arrow is taken when nothing says otherwise: a forward one is, an arrow back
    ///     isn't (so a loop doesn't go round until its limit).
    /// </summary>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="stepId">The step the arrow leaves.</param>
    /// <param name="arrow">The arrow.</param>
    /// <returns>True if taken.</returns>
    public static bool TakenByDefault(FlowMachine machine, string stepId, ArrowDefinition arrow)
    {
        return !machine.IsBack(stepId, arrow);
    }

    /// <summary>
    ///     Simulates a run of a workflow from its start.
    /// </summary>
    /// <param name="workflow">The workflow; one that doesn't validate may get stuck or stop early.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>How the run went.</returns>
    public virtual async Task<FlowSimulation> SimulateAsync(WorkflowDefinition workflow, CancellationToken ct = default)
    {
        var result = new FlowSimulation();
        var machine = FlowMachine.Of(workflow);
        var run = new Run
        {
            Workflow = workflow,
            Steps = workflow.Steps.Select(s => new RunStep { StepId = s.Id, Name = s.Name }).ToList(),
            LeftOutSteps = [.. this._outcomes.Where(o => o.Value == FlowTrigger.LeftOut).Select(o => o.Key)],
        };
        if (workflow.StartStepId is { } start && machine.Contains(start))
        {
            run.Paths.Add(new ActivePath { StepId = start, Origin = start });
        }

        var maxSteps = workflow.MaxSteps ?? StepConstants.DefaultMaxSteps;
        while (run.Paths.Count > 0)
        {
            var path = run.Paths[0];
            if (result.Visits.Count >= maxSteps)
            {
                result.Failed = true;
                result.Error = string.Format(StepConstants.MaxStepsReachedFormat, maxSteps, path.StepId);
                break;
            }

            run.Paths.RemoveAt(0);
            if (run.StepOf(path.StepId) is not { } state)
            {
                continue;
            }

            this.Arrive(run, path.StepId, path.ArrivedFrom);
            var trigger = run.LeftOutSteps.Contains(state.StepId) ? FlowTrigger.LeftOut : this._outcomes.GetValueOrDefault(state.StepId);
            state.Status = trigger switch
            {
                FlowTrigger.LeftOut => StepStatus.Skipped,
                FlowTrigger.Failed or FlowTrigger.TimedOut => StepStatus.Failed,
                _ => StepStatus.Done,
            };
            result.Visits.Add((state.StepId, trigger));

            var handled = !run.UnhandledFailure;
            var route = await this.RouteAsync(new StepContext(run, state, _ => throw new InvalidOperationException(), string.Empty, () => { }, null), trigger, ct);
            Note(run, result);
            if (handled && run.UnhandledFailure)
            {
                result.FailedAt ??= state.StepId;
            }

            if (route is null || route.Fails)
            {
                result.Failed = true;
                result.FailedAt ??= state.StepId;
                result.Error ??= state.Error;
                continue;
            }

            if (route.Back is { } back)
            {
                result.Fired.Add(back.Key);
                this.Rewind(run, machine, state.StepId, back.Arrow.To);
                if (!this.IsActive(run, back.Arrow.To))
                {
                    run.Paths.Add(new ActivePath { StepId = back.Arrow.To, ArrivedFrom = state.StepId });
                }

                continue;
            }

            this.Activate(run, machine, route.Ready, state.StepId, result);
            if (run.Paths.Count == 0)
            {
                this.Activate(run, machine, this.KillPending(run, machine), null, result);
                Note(run, result);
            }
        }

        result.Failed |= run.UnhandledFailure;
        foreach (var key in run.Arrows.Where(a => a.Value == ArrowState.Dead).Select(a => a.Key).Where(k => !result.Fired.Contains(k)))
        {
            result.Dead.Add(key);
        }

        return result;
    }

    /// <summary>
    ///     Simulates a run of a workflow that uses common steps or includes other workflows, as a run would follow it:
    ///     expanded first (each parameter set to its default, or its id), with the result told in the workflow's own
    ///     step ids. An include step stands for the steps it brings in: it runs when the run enters them, its outcome is
    ///     given to the ones that leave them, and a failure inside them is its failure.
    /// </summary>
    /// <param name="workflow">The workflow, unexpanded.</param>
    /// <param name="composer">Expands it.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>How the run went.</returns>
    /// <exception cref="Exceptions.AflException">Something it uses is missing.</exception>
    public virtual async Task<FlowSimulation> SimulateAsync(WorkflowDefinition workflow, WorkflowComposer composer, CancellationToken ct = default)
    {
        if (!workflow.IsComposed)
        {
            return await this.SimulateAsync(workflow, ct);
        }

        var expanded = composer.Expand(workflow, (workflow.Parameters ?? []).ToDictionary(p => p.Id, p => p.Default ?? p.Id));
        var includes = workflow.Steps.Where(s => s.Include is not null).Select(s => s.Id).ToList();
        string Own(string id) => includes.FirstOrDefault(i => id.StartsWith(i + "-", StringComparison.Ordinal)) ?? id;
        string? OwnKey(string from, ArrowDefinition arrow) =>
            Own(from) is var source && Own(arrow.To) is var target && source == target && source != from ? null
                : FlowMachine.KeyOf(source, target, arrow.Outcome);

        var arrows = expanded.Steps.SelectMany(step => (step.Arrows ?? []).Select(arrow => (From: step.Id, Arrow: arrow))).ToList();
        var entries = arrows.Where(a => Own(a.From) != Own(a.Arrow.To) && Own(a.Arrow.To) != a.Arrow.To).Select(a => a.Arrow.To)
            .Append(expanded.StartStepId).OfType<string>().ToHashSet();
        var taken = new Dictionary<string, bool>();
        var outcomes = new Dictionary<string, FlowTrigger>();
        foreach (var (from, arrow) in arrows)
        {
            if (OwnKey(from, arrow) is { } key && this._taken.TryGetValue(key, out var choice))
            {
                taken[FlowMachine.KeyOf(from, arrow)] = choice;
            }

            if (this._outcomes.TryGetValue(Own(from), out var outcome) && (from == Own(from) || Own(arrow.To) != Own(from)))
            {
                outcomes[from] = outcome;
            }
        }

        foreach (var (stepId, outcome) in this._outcomes.Where(o => expanded.Steps.Any(s => s.Id == o.Key)))
        {
            outcomes[stepId] = outcome;
        }

        var inner = await this.With(outcomes, taken).SimulateAsync(expanded, ct);
        var result = new FlowSimulation { Failed = inner.Failed, Error = inner.Error, FailedAt = inner.FailedAt is { } at ? Own(at) : null };
        foreach (var (stepId, outcome) in inner.Visits)
        {
            if (Own(stepId) == stepId || entries.Contains(stepId))
            {
                result.Visits.Add((Own(stepId), outcome));
            }
            else if (outcome is FlowTrigger.Failed or FlowTrigger.TimedOut && result.Visits.FindLastIndex(v => v.StepId == Own(stepId)) is >= 0 and var last)
            {
                result.Visits[last] = (Own(stepId), outcome);
            }
        }

        foreach (var (from, arrow) in arrows)
        {
            var key = FlowMachine.KeyOf(from, arrow);
            if (OwnKey(from, arrow) is { } own)
            {
                (inner.Fired.Contains(key) ? result.Fired : inner.Dead.Contains(key) ? result.Dead : null)?.Add(own);
            }
        }

        result.Dead.ExceptWith(result.Fired);
        result.Overtaken.UnionWith(inner.Overtaken.Select(Own));
        return result;
    }

    /// <summary>
    ///     Returns a simulator like this one that ends steps and takes arrows as given, for simulating an expanded
    ///     workflow.
    /// </summary>
    /// <param name="outcomes">How each step ends, by id.</param>
    /// <param name="taken">Whether each conditional arrow is taken, by key.</param>
    /// <returns>The simulator.</returns>
    protected virtual FlowSimulator With(IReadOnlyDictionary<string, FlowTrigger> outcomes, IReadOnlyDictionary<string, bool> taken)
    {
        return new FlowSimulator(outcomes, taken);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> PermitsAsync(StepContext context, FlowTransition transition, CancellationToken ct)
    {
        return ValueTask.FromResult(this._taken.TryGetValue(transition.Key, out var taken)
            ? taken
            : TakenByDefault(FlowMachine.Of(context.Run.Workflow), transition.Step.Id, transition.Arrow));
    }

    /// <summary>
    ///     Starts a path at each step made ready that isn't running already, cancelling, for one with
    ///     <c>others: cancel</c>, the paths that can now only lead to it.
    /// </summary>
    /// <param name="run">The simulated run.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="ready">The steps made ready.</param>
    /// <param name="from">The step whose arrows made them ready, if any.</param>
    /// <param name="result">The simulation, which records the cancelled paths.</param>
    protected virtual void Activate(Run run, FlowMachine machine, IReadOnlyList<string> ready, string? from, FlowSimulation result)
    {
        foreach (var target in ready.Where(t => !this.IsActive(run, t)))
        {
            run.Paths.Add(new ActivePath { StepId = target, Origin = target, ArrivedFrom = from });
            if (machine.StepOf(target) is not { LetsOthersFinish: false })
            {
                continue;
            }

            foreach (var overtaken in run.Paths.Where(p => p.StepId != target && machine.OnlyLeadsTo(p.StepId, target)).ToList())
            {
                run.Paths.Remove(overtaken);
                result.Overtaken.Add(overtaken.StepId);
                this.Fire(run, machine, overtaken.StepId, []);
            }
        }
    }

    /// <summary>
    ///     Adds the arrows that have fired so far to the simulation, since a loop going round forgets them.
    /// </summary>
    /// <param name="run">The simulated run.</param>
    /// <param name="result">The simulation.</param>
    private static void Note(Run run, FlowSimulation result)
    {
        foreach (var key in run.Arrows.Where(a => a.Value == ArrowState.Fired).Select(a => a.Key))
        {
            result.Fired.Add(key);
        }
    }
}
