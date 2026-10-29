using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Flow;

/// <summary>
///     Which of a step's arrows fired once it ended (<see cref="FlowRouter.RouteAsync" />), and what that means for the
///     run.
/// </summary>
public class FlowRoute
{
    /// <summary>
    ///     Gets or sets the arrow back that fired, if one did: the run goes round again from its target, and no forward
    ///     arrow is followed.
    /// </summary>
    public FlowTransition? Back { get; set; }

    /// <summary>
    ///     Gets or sets the forward arrows that fired (including ones to <see cref="ArrowConstants.End" />).
    /// </summary>
    public List<FlowTransition> Fired { get; set; } = [];

    /// <summary>
    ///     Gets or sets the steps the fired arrows made ready to start, in the order they became ready (including the
    ///     workflow's catch step, when a failure goes there).
    /// </summary>
    public List<string> Ready { get; set; } = [];

    /// <summary>
    ///     Gets or sets whether the step failed and nothing deals with it: no arrow fired and there's no catch step to go
    ///     to, so the run (or path) fails and can be retried.
    /// </summary>
    public bool Fails { get; set; }
}

/// <summary>
///     Decides where a run goes once a step ends, by firing the workflow's <see cref="FlowMachine" />: arrows back are
///     checked first and the first that holds wins (bounded by its <c>max</c>); otherwise the step's forward arrows fire
///     as its <c>follow</c> says, the arrows it didn't take die, and every step whose <c>waitFor</c> that settles becomes
///     ready (or dies in turn, so paths that never come don't keep a step waiting). Moving the run is left to the caller:
///     <see cref="RunEngine" /> for a run on one path, <see cref="FlowCoordinator" /> for one on several.
/// </summary>
public class FlowRouter
{
    /// <summary>
    ///     Gets the shared router.
    /// </summary>
    public static FlowRouter Instance { get; } = new();

    /// <summary>
    ///     Fires a step's arrows for how it ended, records them in <see cref="Run.Arrows" />, and works out which steps
    ///     that makes ready. If a condition can't be checked or a loop reaches its limit with <c>onLimit: fail</c>, the
    ///     step fails instead (with the reason as its error) and null is returned.
    /// </summary>
    /// <param name="context">The context of the step that ended.</param>
    /// <param name="trigger">How it ended.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The route, or null when the step failed while routing.</returns>
    public virtual async Task<FlowRoute?> RouteAsync(StepContext context, FlowTrigger trigger, CancellationToken ct)
    {
        var state = context.State;
        var run = context.Run;
        var machine = FlowMachine.Of(run.Workflow);
        if (trigger != FlowTrigger.LeftOut)
        {
            state.Runs++;
        }

        FlowRoute route;
        try
        {
            route = await this.FireArrowsAsync(context, machine, trigger, ct);
        }
        catch (AflException ex)
        {
            if (trigger != FlowTrigger.LeftOut)
            {
                state.Runs--;
            }

            StepLifecycle.Instance.Move(state, StepStatus.Failed);
            state.Error = ex.Message;
            return null;
        }

        var failed = trigger is FlowTrigger.Failed or FlowTrigger.TimedOut;
        var fired = route.Fired.Select(t => t.Arrow).Concat(route.Back is null ? [] : [route.Back.Arrow]).ToList();
        if (route.Back is not null)
        {
            run.UnhandledFailure |= failed && !fired.Any(a => OutcomeRegistry.Instance.Of(a).HandlesFailure);
            return route;
        }

        if (failed && fired.Count == 0)
        {
            if (this.CatchStepFor(run, machine, state.StepId) is not { } catchStep)
            {
                state.Runs--;
                route.Fails = true;
                return route;
            }

            context.Report($"Step '{state.StepId}' failed; going to the workflow's catch step '{catchStep}'.");
            route.Ready.AddRange(this.Fire(run, machine, state.StepId, []));
            route.Ready.Add(catchStep);
            run.UnhandledFailure = true;
            return route;
        }

        run.UnhandledFailure |= failed && !fired.Any(a => OutcomeRegistry.Instance.Of(a).HandlesFailure);
        route.Ready.AddRange(this.Fire(run, machine, state.StepId, route.Fired));
        return route;
    }

    /// <summary>
    ///     Records which of a step's forward arrows fired: those are fired, the rest dead. Every step one of them leads
    ///     to whose <c>waitFor</c> that makes true is returned as ready; one it makes dead (and that isn't running) dies
    ///     too, and so on down.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="stepId">The step whose arrows these are.</param>
    /// <param name="fired">The arrows that fired.</param>
    /// <returns>The steps that became ready.</returns>
    public virtual IReadOnlyList<string> Fire(Run run, FlowMachine machine, string stepId, IReadOnlyCollection<FlowTransition> fired)
    {
        var keys = fired.Select(t => t.Key).ToHashSet();
        var changes = Forward(machine, stepId)
            .Select(a => (Key: FlowMachine.KeyOf(stepId, a), a.To))
            .Select(a => (a.Key, a.To, keys.Contains(a.Key) ? ArrowState.Fired : ArrowState.Dead))
            .ToList();
        var ready = new List<string>();
        this.Set(run, machine, changes, ready);
        return ready;
    }

    /// <summary>
    ///     Kills every arrow that hasn't fired or died, once nothing is left running that could fire it, and returns the
    ///     steps that makes ready (a step waiting on paths that will now never come).
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <returns>The steps that became ready.</returns>
    public virtual IReadOnlyList<string> KillPending(Run run, FlowMachine machine)
    {
        var changes = machine.States.SelectMany(s => Unset(run, machine, s)).ToList();
        var ready = new List<string>();
        this.Set(run, machine, changes, ready);
        return ready;
    }

    /// <summary>
    ///     Returns whether a step is where the run (or one of its paths) is now.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="stepId">The step id.</param>
    /// <returns>True if active.</returns>
    public virtual bool IsActive(Run run, string stepId)
    {
        return run.CurrentStepId == stepId || run.Paths.Any(p => p.StepId == stepId);
    }

    /// <summary>
    ///     Readies a step an arrow reached: one that has run before in this pass (reached again another way) is archived
    ///     so it runs afresh, and it records which step it came from.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="target">The step reached.</param>
    /// <param name="from">The step whose arrow reached it.</param>
    public virtual void Arrive(Run run, string target, string? from)
    {
        if (run.StepOf(target) is not { } next)
        {
            return;
        }

        if (next.Status != StepStatus.Pending)
        {
            next.Archive(AttemptCause.Route, from is null ? null : string.Format(StepConstants.ArrivedReasonFormat, from), false);
        }

        next.ArrivedFrom = from;
    }

    /// <summary>
    ///     Goes on to a step further along, which the run then runs itself.
    /// </summary>
    /// <param name="context">The context of the step just ended.</param>
    /// <param name="target">The step to go to.</param>
    public virtual void GoOn(StepContext context, string target)
    {
        this.Arrive(context.Run, target, context.State.StepId);
        context.Report($"Going on to step '{target}'.");
        context.Run.CurrentStepId = target;
    }

    /// <summary>
    ///     Goes back to a step that comes before (or is) the one just done (<see cref="Rewind" />), which the run then
    ///     runs itself.
    /// </summary>
    /// <param name="context">The context of the step whose arrow went back.</param>
    /// <param name="target">The step to go back to.</param>
    public virtual void GoBack(StepContext context, string target)
    {
        var reason = this.Rewind(context.Run, FlowMachine.Of(context.Run.Workflow), context.State.StepId, target);
        context.Report($"Going back to step '{target}' ({reason.ToLowerInvariant()}).");
        context.Run.CurrentStepId = target;
    }

    /// <summary>
    ///     Winds the run back round a loop: every step on the cycle from the target to the step going back is archived
    ///     (into <see cref="RunStep.History" />) and reset, and the states of the arrows leaving them are forgotten, so
    ///     they run and fire again. A step further on that died because the cycle didn't lead to it last round gets its
    ///     dead arrows back too, so what waits on them waits for this round.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="from">The step whose arrow goes back.</param>
    /// <param name="target">The step it goes back to.</param>
    /// <returns>Why, as recorded on each archived attempt.</returns>
    public virtual string Rewind(Run run, FlowMachine machine, string from, string target)
    {
        var reason = string.Format(StepConstants.LoopReasonFormat, (run.StepOf(target)?.Runs ?? 0) + 1);
        var cycle = machine.CycleOf(from, target);
        foreach (var step in run.Steps.Where(s => cycle.Contains(s.StepId)))
        {
            step.Rounds = from == target ? step.Rounds + 1 : 0;
            step.Archive(AttemptCause.Loop, reason, false);
            foreach (var arrow in machine.ArrowsOf(step.StepId))
            {
                run.Arrows.Remove(FlowMachine.KeyOf(step.StepId, arrow));
            }
        }

        var unreached = machine.Descendants(target).Where(s => !cycle.Contains(s) && !this.IsActive(run, s) && run.StepOf(s) is { Status: StepStatus.Pending });
        foreach (var stepId in unreached)
        {
            foreach (var key in machine.ArrowsOf(stepId).Select(a => FlowMachine.KeyOf(stepId, a)))
            {
                if (run.Arrows.GetValueOrDefault(key) == ArrowState.Dead)
                {
                    run.Arrows.Remove(key);
                }
            }
        }

        if (run.StepOf(target) is { } state)
        {
            state.ArrivedFrom = from;
        }

        return reason;
    }

    /// <summary>
    ///     Returns how many times a step can run, for a step a loop can bring back (<see cref="FlowMachine.LimitOf" />).
    ///     Front-ends show it as "iteration n/max".
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step.</param>
    /// <returns>The limit, or null when no loop can bring the step back.</returns>
    public virtual int? LimitOf(WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        return FlowMachine.Of(workflow).LimitOf(step.Id);
    }

    /// <summary>
    ///     Checks the flow of a whole workflow for problems (<see cref="FlowMachine.Validate" />).
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IReadOnlyList<string> ValidateFlow(WorkflowDefinition workflow)
    {
        return new FlowMachine(workflow).Validate();
    }

    /// <summary>
    ///     Lists things about a workflow's flow that are allowed but probably not meant (<see cref="FlowMachine.Warnings" />).
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The warnings.</returns>
    public virtual IReadOnlyList<string> Warnings(WorkflowDefinition workflow)
    {
        return new FlowMachine(workflow).Warnings();
    }

    /// <summary>
    ///     Checks the conditions on a step's arrows, and that what they ask about is there when they're checked.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        var problems = new List<string>();
        foreach (var arrow in step.Arrows ?? [])
        {
            if (arrow.Condition is { } condition)
            {
                var to = arrow.Ends ? ArrowConstants.End : $"'{arrow.To}'";
                problems.AddRange(ConditionRegistry.Instance.Validate(condition, workflow, step).Select(p => $"the arrow to {to}: {p}"));
            }
        }

        // Conditions are checked once the step has ended, so its own output is there too.
        var machine = new FlowMachine(workflow);
        var earlier = TemplateVariables.StepsBefore(machine, step).Append(step).ToList();
        var possible = TemplateVariables.StepsMaybeBefore(machine, step);
        foreach (var condition in step.FlowConditions())
        {
            foreach (var reference in PromptTemplate.References(condition.Question))
            {
                if (!TemplateVariables.IsAvailable(reference, earlier, workflow.Steps, null, possible))
                {
                    problems.Add($"'{{{{{reference}}}}}' is not available in a condition. {TemplateVariables.Describe(earlier, workflow.Steps, null, possible)}");
                }
            }
        }

        return problems;
    }

    /// <summary>
    ///     Skips a step that is about to start if it is an optional step the run left out
    ///     (<see cref="Run.LeftOutSteps" />): it is marked <see cref="StepStatus.Skipped" /> with
    ///     <see cref="StepConstants.LeftOutNote" />, and passes the run through (<see cref="FlowTrigger.LeftOut" />).
    /// </summary>
    /// <param name="context">The context of the step about to start.</param>
    /// <returns>True if the step was skipped.</returns>
    public virtual bool TrySkip(StepContext context)
    {
        var state = context.State;
        if (!context.Run.LeftOutSteps.Contains(state.StepId))
        {
            return false;
        }

        context.Report($"Skipping step '{state.StepId}': it was left out when the run started.");
        StepLifecycle.Instance.Move(state, StepStatus.Skipped);
        state.Notes.Add(StepConstants.LeftOutNote);
        state.EndedAt = DateTimeOffset.Now;
        return true;
    }

    /// <summary>
    ///     Returns whether the step will go round again once it is done with its current output: an arrow back on success
    ///     holds and hasn't reached its limit.
    /// </summary>
    /// <param name="context">The context of the step, with its output set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if it will.</returns>
    public virtual async Task<bool> WillRepeatAsync(StepContext context, CancellationToken ct)
    {
        var machine = FlowMachine.Of(context.Run.Workflow);
        foreach (var transition in machine.TransitionsFrom(context.State.StepId, FlowTrigger.Succeeded).Where(machine.IsBack))
        {
            if (this.RunsAgainst(context, transition) < (transition.Arrow.Max ?? StepConstants.DefaultMaxRuns) &&
                await this.PermitsAsync(context, transition, ct))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Works out which of a step's arrows fire: the first arrow back that holds and is under its limit, else the
    ///     forward arrows its <c>follow</c> takes. A left-out step takes no arrow back.
    /// </summary>
    /// <param name="context">The context of the step.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="trigger">How the step ended.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The route, with nothing ready yet.</returns>
    /// <exception cref="AflException">A loop reached its limit and fails, or a condition couldn't be checked.</exception>
    protected virtual async Task<FlowRoute> FireArrowsAsync(StepContext context, FlowMachine machine, FlowTrigger trigger, CancellationToken ct)
    {
        var state = context.State;
        var transitions = machine.TransitionsFrom(state.StepId, trigger);
        if (trigger != FlowTrigger.LeftOut)
        {
            foreach (var transition in transitions.Where(machine.IsBack))
            {
                if (!await this.PermitsAsync(context, transition, ct))
                {
                    continue;
                }

                var max = transition.Arrow.Max ?? StepConstants.DefaultMaxRuns;
                if (this.RunsAgainst(context, transition) < max)
                {
                    return new FlowRoute { Back = transition };
                }

                if ((transition.Arrow.OnLimit ?? StepConstants.OnLimitFail) == StepConstants.OnLimitFail)
                {
                    throw new AflException(AflErrorKind.InvalidState,
                        $"Loop limit reached ({max}): step '{transition.Arrow.To}' ran {max} times without its repeat condition holding.");
                }

                context.Report($"Loop limit reached ({max}) going back to '{transition.Arrow.To}'; moving on.");
                state.Notes.Add($"Loop limit reached ({max}); moved on.");
            }
        }

        var forward = transitions.Where(t => !machine.IsBack(t)).ToList();
        var fired = new List<FlowTransition>();
        if (context.Definition.FollowsFirst)
        {
            foreach (var transition in forward.Where(t => t.Arrow.IsConditional))
            {
                if (await this.PermitsAsync(context, transition, ct))
                {
                    fired.Add(transition);
                    break;
                }
            }

            if (fired.Count == 0)
            {
                fired.AddRange(forward.Where(t => !t.Arrow.IsConditional));
            }
        }
        else
        {
            foreach (var transition in forward)
            {
                if (!transition.Arrow.IsConditional || await this.PermitsAsync(context, transition, ct))
                {
                    fired.Add(transition);
                }
            }
        }

        return new FlowRoute { Fired = fired };
    }

    /// <summary>
    ///     Returns whether an arrow's condition lets it be taken (<see cref="FlowTransition.PermitsAsync" />).
    /// </summary>
    /// <param name="context">The context of the step the arrow leaves.</param>
    /// <param name="transition">The arrow.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if it can be taken.</returns>
    protected virtual ValueTask<bool> PermitsAsync(StepContext context, FlowTransition transition, CancellationToken ct)
    {
        return transition.PermitsAsync(context, ct);
    }

    /// <summary>
    ///     Returns how many times the step an arrow back goes to has run, which its <c>max</c> bounds: for an arrow back
    ///     to itself, the times round in a row (<see cref="RunStep.Rounds" />, counting this one), so it starts again
    ///     whenever an outer loop comes round; else the target's runs.
    /// </summary>
    /// <param name="context">The context of the step the arrow leaves.</param>
    /// <param name="transition">The arrow back.</param>
    /// <returns>The count.</returns>
    protected virtual int RunsAgainst(StepContext context, FlowTransition transition)
    {
        return transition.Arrow.To == context.State.StepId
            ? context.State.Rounds + 1
            : context.Run.StepOf(transition.Arrow.To)?.Runs ?? 0;
    }

    /// <summary>
    ///     Returns the workflow's catch step (<see cref="WorkflowDefinition.OnFailure" />) when a failure of a step can
    ///     go there: it isn't the catch step failing, and the catch step hasn't run yet.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="stepId">The step that failed.</param>
    /// <returns>The catch step, or null.</returns>
    protected virtual string? CatchStepFor(Run run, FlowMachine machine, string stepId)
    {
        return run.Workflow.OnFailure is { } catchStep && catchStep != stepId && machine.Contains(catchStep) &&
               run.StepOf(catchStep) is { Status: StepStatus.Pending, History.Count: 0 } && !this.IsActive(run, catchStep)
            ? catchStep
            : null;
    }

    /// <summary>
    ///     Sets the states of some arrows, then looks again at the steps they lead to: one now ready is added to the
    ///     list, and one now dead that isn't running kills its own arrows that haven't fired, and so on.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="changes">The arrows, by key, with the steps they lead to and their new states.</param>
    /// <param name="ready">Receives the steps that became ready.</param>
    protected virtual void Set(Run run, FlowMachine machine, IReadOnlyList<(string Key, string To, ArrowState State)> changes, List<string> ready)
    {
        var targets = changes.Select(c => c.To).Where(machine.Contains).Distinct().ToList();
        var before = targets.ToDictionary(t => t, t => machine.WaitOf(t, StateOf(run)));
        foreach (var (key, _, arrowState) in changes)
        {
            run.Arrows[key] = arrowState;
        }

        foreach (var target in targets)
        {
            var after = machine.WaitOf(target, StateOf(run));
            if (after == before[target])
            {
                continue;
            }

            if (after == WaitResult.Ready && !ready.Contains(target))
            {
                ready.Add(target);
            }
            else if (after == WaitResult.Dead && !this.IsActive(run, target) && run.StepOf(target) is { Status: StepStatus.Pending })
            {
                this.Set(run, machine, Unset(run, machine, target).ToList(), ready);
            }
        }
    }

    /// <summary>
    ///     Returns a step's forward arrows that lead to a step (not ones to <see cref="ArrowConstants.End" />).
    /// </summary>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="stepId">The step id.</param>
    /// <returns>The arrows.</returns>
    private static IEnumerable<ArrowDefinition> Forward(FlowMachine machine, string stepId)
    {
        return machine.ArrowsOf(stepId).Where(a => !a.Ends && !machine.IsBack(stepId, a));
    }

    /// <summary>
    ///     Returns a step's forward arrows that have neither fired nor died, as changes that kill them.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="stepId">The step id.</param>
    /// <returns>The changes.</returns>
    private static IEnumerable<(string Key, string To, ArrowState State)> Unset(Run run, FlowMachine machine, string stepId)
    {
        return Forward(machine, stepId)
            .Select(a => (Key: FlowMachine.KeyOf(stepId, a), a.To, State: ArrowState.Dead))
            .Where(c => !run.Arrows.ContainsKey(c.Key));
    }

    /// <summary>
    ///     Returns a lookup of the run's arrow states, for <see cref="FlowMachine.WaitOf" />.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The lookup; null for an arrow with no state yet.</returns>
    private static Func<string, ArrowState?> StateOf(Run run)
    {
        return key => run.Arrows.TryGetValue(key, out var state) ? state : null;
    }
}
