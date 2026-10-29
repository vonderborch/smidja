using System.Runtime.CompilerServices;
using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.StateMachines;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Flow;

/// <summary>
///     One arrow of a workflow's flow for one way its step can end, with the definition it came from. Its guard is the
///     arrow's condition: <c>when</c> must hold, <c>until</c> must not yet.
/// </summary>
/// <param name="step">The step it leaves.</param>
/// <param name="trigger">How the step ended.</param>
/// <param name="arrow">The arrow.</param>
/// <param name="index">Where the arrow is in the step's list.</param>
public class FlowTransition(AbstractStepDefinition step, FlowTrigger trigger, ArrowDefinition arrow, int index)
    : StateTransition<string, FlowTrigger, StepContext>(step.Id, trigger, arrow.Ends ? [] : [arrow.To])
{
    /// <summary>
    ///     Gets the step the transition leaves.
    /// </summary>
    public AbstractStepDefinition Step { get; } = step;

    /// <summary>
    ///     Gets the arrow.
    /// </summary>
    public ArrowDefinition Arrow { get; } = arrow;

    /// <summary>
    ///     Gets where the arrow is in the step's list.
    /// </summary>
    public int Index { get; } = index;

    /// <summary>
    ///     Gets the key of the arrow's state in <c>Run.Arrows</c>.
    /// </summary>
    public string Key => FlowMachine.KeyOf(this.Step.Id, this.Arrow);

    /// <inheritdoc />
    public override async ValueTask<bool> PermitsAsync(StepContext context, CancellationToken ct)
    {
        if (this.Arrow.When is { } when)
        {
            return await ConditionRegistry.Instance.EvaluateAsync(when, context, ct);
        }

        return this.Arrow.Until is not { } until || !await ConditionRegistry.Instance.EvaluateAsync(until, context, ct);
    }
}

/// <summary>
///     A workflow's flow as a state machine whose states are its steps and whose transitions are its arrows, one per way
///     a step can end that the arrow fires on (<see cref="AbstractOutcome" />). Built once per expanded workflow
///     (<see cref="Of" />); it answers where each step can go, what a step waits for, which arrows go back, what always
///     runs before a step, and what is wrong with the flow.
/// </summary>
public class FlowMachine : AbstractStateMachine<string, FlowTrigger, StepContext, FlowTransition>
{
    /// <summary>
    ///     The machines already built for workflows, so a run builds its workflow's only once.
    /// </summary>
    private static readonly ConditionalWeakTable<WorkflowDefinition, FlowMachine> Cache = new();

    /// <summary>
    ///     The steps by id.
    /// </summary>
    private readonly Dictionary<string, AbstractStepDefinition> _steps = new();

    /// <summary>
    ///     What <see cref="OnlyLeadsTo" /> has worked out so far.
    /// </summary>
    private readonly Dictionary<(string, string), bool> _leadsTo = new();

    /// <summary>
    ///     The steps that have always run by the time each step starts, worked out on first use.
    /// </summary>
    private Dictionary<string, HashSet<string>>? _guaranteed;

    /// <summary>
    ///     The steps every run reaches, worked out with <see cref="_guaranteed" />.
    /// </summary>
    private HashSet<string>? _alwaysRun;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FlowMachine" /> class for a workflow.
    /// </summary>
    /// <param name="workflow">The workflow, expanded.</param>
    public FlowMachine(WorkflowDefinition workflow)
    {
        this.Workflow = workflow;
        foreach (var step in workflow.Steps.Where(s => !string.IsNullOrEmpty(s.Id)))
        {
            this.AddState(step.Id);
            this._steps.TryAdd(step.Id, step);
        }

        foreach (var step in this._steps.Values)
        {
            var arrows = step.Arrows ?? [];
            foreach (var trigger in Enum.GetValues<FlowTrigger>())
            {
                for (var i = 0; i < arrows.Count; i++)
                {
                    var arrow = arrows[i];
                    if (!OutcomeRegistry.Instance.Contains(arrow.Outcome) ||
                        !OutcomeRegistry.Instance.Of(arrow).Matches(trigger, step) ||
                        (trigger == FlowTrigger.LeftOut && arrow.IsConditional))
                    {
                        continue;
                    }

                    this.AddTransition(new FlowTransition(step, trigger, arrow, i));
                }
            }
        }
    }

    /// <summary>
    ///     Gets the workflow.
    /// </summary>
    public WorkflowDefinition Workflow { get; }

    /// <inheritdoc />
    public override string Initial => this.Workflow.StartStepId ?? string.Empty;

    /// <summary>
    ///     Returns the machine for a workflow, building it the first time. Only for workflows that no longer change, such
    ///     as a run's; build a new one (<see cref="FlowMachine(WorkflowDefinition)" />) for a workflow being edited.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The machine.</returns>
    public static FlowMachine Of(WorkflowDefinition workflow)
    {
        return Cache.GetValue(workflow, w => new FlowMachine(w));
    }

    /// <summary>
    ///     Returns the key of an arrow's state in <c>Run.Arrows</c>.
    /// </summary>
    /// <param name="from">The step it leaves.</param>
    /// <param name="arrow">The arrow.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string from, ArrowDefinition arrow)
    {
        return KeyOf(from, arrow.To, arrow.Outcome);
    }

    /// <summary>
    ///     Returns the key of an arrow's state in <c>Run.Arrows</c>.
    /// </summary>
    /// <param name="from">The step it leaves.</param>
    /// <param name="to">Where it goes.</param>
    /// <param name="outcome">The outcome it fires on.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string from, string to, string outcome)
    {
        return string.Format(ArrowConstants.ArrowKeyFormat, from, to, outcome);
    }

    /// <summary>
    ///     Returns a step's definition.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The definition, or null when there's no such step.</returns>
    public AbstractStepDefinition? StepOf(string stepId)
    {
        return this._steps.GetValueOrDefault(stepId);
    }

    /// <summary>
    ///     Returns a step's arrows, in order.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The arrows; empty when there's no such step.</returns>
    public IReadOnlyList<ArrowDefinition> ArrowsOf(string stepId)
    {
        return this.StepOf(stepId)?.Arrows ?? [];
    }

    /// <summary>
    ///     Returns whether an arrow goes back: to its own step, or to a step before it.
    /// </summary>
    /// <param name="from">The step it leaves.</param>
    /// <param name="arrow">The arrow.</param>
    /// <returns>True if it goes back.</returns>
    public bool IsBack(string from, ArrowDefinition arrow)
    {
        return !arrow.Ends && this.IsBackEdge(from, arrow.To);
    }

    /// <summary>
    ///     Returns whether a transition goes back.
    /// </summary>
    /// <param name="transition">The transition.</param>
    /// <returns>True if it goes back.</returns>
    public bool IsBack(FlowTransition transition)
    {
        return this.IsBack(transition.From, transition.Arrow);
    }

    /// <summary>
    ///     Returns the forward arrows into a step, with the steps they leave, in step order.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The arrows.</returns>
    public IReadOnlyList<(string From, ArrowDefinition Arrow)> ArrowsInto(string stepId)
    {
        return this._steps.Values
            .SelectMany(s => (s.Arrows ?? []).Where(a => a.To == stepId && !this.IsBack(s.Id, a)).Select(a => (s.Id, a)))
            .ToList();
    }

    /// <summary>
    ///     Returns the steps whose forward arrows come into a step: what its <c>waitFor</c> is over.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The step ids, in step order.</returns>
    public IReadOnlyList<string> IncomingForward(string stepId)
    {
        return this.ArrowsInto(stepId).Select(a => a.From).Distinct().ToList();
    }

    /// <summary>
    ///     Returns what a step waits for before it starts.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>Its <c>waitFor</c>, or the default (every arrow in).</returns>
    public AbstractWaitRule WaitRuleOf(string stepId)
    {
        return this.StepOf(stepId)?.WaitFor ?? WaitRuleRegistry.Default;
    }

    /// <summary>
    ///     Works out whether a step can start, from the states of the arrows into it. The arrows from one step count as
    ///     fired once one has, and dead once all are.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <param name="stateOf">The state of an arrow, by its key (<see cref="KeyOf(string, string, string)" />); missing means pending.</param>
    /// <returns>Whether it's ready, still waiting or dead.</returns>
    public WaitResult WaitOf(string stepId, Func<string, ArrowState?> stateOf)
    {
        var arrows = this.ArrowsInto(stepId);
        var incoming = arrows.Select(a => a.From).Distinct().ToList();
        return this.WaitRuleOf(stepId).Evaluate(incoming, source =>
        {
            var states = arrows.Where(a => a.From == source).Select(a => stateOf(KeyOf(a.From, a.Arrow)) ?? ArrowState.Pending).ToList();
            if (states.Contains(ArrowState.Fired))
            {
                return ArrowState.Fired;
            }

            return states.All(s => s == ArrowState.Dead) ? ArrowState.Dead : ArrowState.Pending;
        });
    }

    /// <summary>
    ///     Returns whether every way on from one step leads to another without ending, so a path at the first is only
    ///     heading for the second (and <c>others: cancel</c> can cancel it once the second starts).
    /// </summary>
    /// <param name="stepId">The step a path is at.</param>
    /// <param name="target">The step it may be heading for.</param>
    /// <returns>True if it only leads there.</returns>
    public bool OnlyLeadsTo(string stepId, string target)
    {
        if (stepId == target)
        {
            return true;
        }

        if (this._leadsTo.TryGetValue((stepId, target), out var known))
        {
            return known;
        }

        // Until worked out, assume not, which also ends any walk round a cycle.
        this._leadsTo[(stepId, target)] = false;
        var forward = this.ArrowsOf(stepId).Where(a => !this.IsBack(stepId, a)).ToList();
        var leads = forward.Count > 0 && forward.All(a => !a.Ends && this.Contains(a.To) && this.OnlyLeadsTo(a.To, target));
        this._leadsTo[(stepId, target)] = leads;
        return leads;
    }

    /// <summary>
    ///     Returns how many times a step can run, for a step a loop can bring back: the limit of an arrow back to it, or
    ///     of an arrow back round a loop it sits inside (the first that applies). Front-ends show it as
    ///     "iteration n/max".
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The limit, or null when no loop can bring the step back.</returns>
    public int? LimitOf(string stepId)
    {
        if (!this.Contains(stepId))
        {
            return null;
        }

        var back = this._steps.Values.SelectMany(s => (s.Arrows ?? []).Where(a => this.IsBack(s.Id, a)).Select(a => (From: s.Id, Arrow: a))).ToList();
        foreach (var (_, arrow) in back.Where(b => b.Arrow.To == stepId))
        {
            return arrow.Max ?? StepConstants.DefaultMaxRuns;
        }

        foreach (var (_, arrow) in back.Where(b => this.CycleOf(b.From, b.Arrow.To).Contains(stepId)))
        {
            return arrow.Max ?? StepConstants.DefaultMaxRuns;
        }

        return null;
    }

    /// <summary>
    ///     Returns the steps that have always run (or been left out) by the time a step starts, whichever way the run
    ///     got there: what its <c>waitFor</c> brings however it comes to hold.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The step ids, not including the step itself.</returns>
    public IReadOnlySet<string> Guaranteed(string stepId)
    {
        this.WorkOutGuaranteed();
        return this._guaranteed!.TryGetValue(stepId, out var steps) ? steps : new HashSet<string>();
    }

    /// <summary>
    ///     Returns whether every run reaches a step, whichever way its arrows go (as long as its steps succeed).
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>True if every run reaches it.</returns>
    public bool AlwaysRuns(string stepId)
    {
        this.WorkOutGuaranteed();
        return this._alwaysRun!.Contains(stepId);
    }

    /// <summary>
    ///     Returns the steps that can come before a step but may not have run by the time it starts (they're on only
    ///     some ways in, or on a path the step doesn't wait for). Templates name them with the optional form,
    ///     <c>{{steps.x.output?}}</c>.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The step ids.</returns>
    public IReadOnlySet<string> Possible(string stepId)
    {
        if (!this.Contains(stepId))
        {
            return new HashSet<string>();
        }

        var possible = this.Ancestors(stepId).ToHashSet();
        possible.ExceptWith(this.Guaranteed(stepId));
        return possible;
    }

    /// <summary>
    ///     Checks the flow as a whole for problems: where it starts, each step's arrows and what it waits for, the
    ///     workflow's catch step, and that every loop has a way out.
    /// </summary>
    /// <returns>The problems; empty when valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (this.Workflow.Start is { } start && !this.Contains(start))
        {
            problems.Add($"start '{start}' isn't a step.");
        }

        if (this.Workflow.MaxSteps is < 1)
        {
            problems.Add("maxSteps must be at least 1.");
        }

        if (this.Workflow.OnFailure is { } catchStep)
        {
            if (!this.Contains(catchStep))
            {
                problems.Add($"onFailure '{catchStep}' isn't a step.");
            }
            else if (this._steps.Values.Any(s => (s.Arrows ?? []).Any(a => a.To == catchStep)))
            {
                problems.Add($"onFailure '{catchStep}' is where failures go, so no arrow can lead to it.");
            }
        }

        foreach (var step in this._steps.Values)
        {
            problems.AddRange(this.ValidateStep(step).Select(p => $"Step '{step.Id}': {p}"));
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        foreach (var step in this._steps.Values)
        {
            var incoming = this.IncomingForward(step.Id);
            if (step.WaitFor is { } rule)
            {
                problems.AddRange(rule.Validate(incoming).Select(p => $"Step '{step.Id}': {p}"));
            }
        }

        problems.AddRange(this.ValidateLoops());
        return problems;
    }

    /// <summary>
    ///     Lists things about the flow that are allowed but probably not meant, such as steps the run can never reach.
    /// </summary>
    /// <returns>The warnings.</returns>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();
        foreach (var step in this._steps.Values)
        {
            var incoming = this.IncomingForward(step.Id);
            if (step.WaitFor is { } rule)
            {
                var used = rule.Sources(incoming).ToHashSet();
                warnings.AddRange(incoming.Where(s => !used.Contains(s))
                    .Select(s => $"Step '{step.Id}': its waitFor leaves out the arrow from '{s}', so that arrow can never start it."));
            }

            if (step.Timeout is null && (step.Arrows ?? []).Any(a => a.Outcome == ArrowConstants.OnTimeout))
            {
                warnings.Add($"Step '{step.Id}': it has an arrow on timeout but no timeout, so the arrow never fires.");
            }
        }

        var reachable = this.Reachable.ToHashSet();
        if (this.Workflow.OnFailure is { } catchStep && this.Contains(catchStep))
        {
            reachable.Add(catchStep);
            reachable.UnionWith(this.Descendants(catchStep));
        }

        warnings.AddRange(this.States.Where(s => !reachable.Contains(s))
            .Select(s => $"Step '{s}' can never run: no arrow leads to it from the start ('{this.Initial}')."));
        return warnings;
    }

    /// <summary>
    ///     Checks one step's flow settings and arrows.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems, without the step's name.</returns>
    private IEnumerable<string> ValidateStep(AbstractStepDefinition step)
    {
        if (step.Follow is not null && !ArrowConstants.FollowModes.Contains(step.Follow))
        {
            yield return $"follow must be {Quoted(ArrowConstants.FollowModes)}.";
        }

        if (step.Others is not null && !ArrowConstants.OthersModes.Contains(step.Others))
        {
            yield return $"others must be {Quoted(ArrowConstants.OthersModes)}.";
        }

        if (step.Retry is { } retry)
        {
            if (retry.Max < 1)
            {
                yield return "retry.max must be at least 1.";
            }

            if (retry.Delay is not null && !Durations.TryParse(retry.Delay, out _))
            {
                yield return $"retry.delay '{retry.Delay}' isn't a duration (e.g. 30s, 5m, 1h).";
            }
        }

        if (step.Timeout is not null && (!Durations.TryParse(step.Timeout, out var timeout) || timeout <= TimeSpan.Zero))
        {
            yield return $"timeout '{step.Timeout}' isn't a duration (e.g. 30s, 5m, 1h).";
        }

        var arrows = step.Arrows ?? [];
        foreach (var arrow in arrows)
        {
            var to = arrow.Ends ? ArrowConstants.End : $"'{arrow.To}'";
            if (string.IsNullOrEmpty(arrow.To))
            {
                yield return "an arrow has no 'to'.";
                continue;
            }

            if (!arrow.Ends && !this.Contains(arrow.To))
            {
                yield return $"an arrow goes to '{arrow.To}', which isn't a step.";
                continue;
            }

            if (!OutcomeRegistry.Instance.Contains(arrow.Outcome))
            {
                yield return $"the arrow to {to}: on must be {Quoted(OutcomeRegistry.Instance.Keys)}.";
            }

            if (arrow.When is not null && arrow.Until is not null)
            {
                yield return $"the arrow to {to} has both when and until; use one.";
            }

            if (arrow.OnLimit is not null && arrow.OnLimit != StepConstants.OnLimitFail && arrow.OnLimit != StepConstants.OnLimitContinue)
            {
                yield return $"the arrow to {to}: onLimit must be '{StepConstants.OnLimitFail}' or '{StepConstants.OnLimitContinue}'.";
            }

            var back = this.IsBack(step.Id, arrow);
            if (arrow.Max is < 1)
            {
                yield return $"the arrow to {to}: max must be at least 1.";
            }
            else if ((arrow.Max is not null || arrow.OnLimit is not null) && !back)
            {
                yield return $"the arrow to {to}: max and onLimit only apply to an arrow that goes back.";
            }
        }

        foreach (var duplicate in arrows.GroupBy(a => (a.To, a.Outcome)).Where(g => g.Count() > 1))
        {
            yield return $"it has two arrows to '{duplicate.Key.To}' on {duplicate.Key.Outcome}; merge their conditions instead.";
        }

        // Its loop ends when none of its own arrows back on success fires, so it needs one (an arrow back from a later
        // step doesn't count: this step has finished by then). With no arrows at all the step's own checks say so.
        if (step.Advance == StepConstants.AdvanceApproveFinal && arrows.Count > 0 &&
            !arrows.Any(a => this.IsBack(step.Id, a) && a.Outcome is ArrowConstants.OnSuccess or ArrowConstants.OnAlways))
        {
            yield return $"advance '{StepConstants.AdvanceApproveFinal}' waits for approval when the step's loop ends, but none of its " +
                         "arrows goes back on success; add one, or use 'approve'.";
        }

        // Only arrows going forward that can fire together start paths: one forward arrow and an arrow back, or one on
        // success and one on failure, is still a single path.
        var forward = arrows.Where(a => !a.Ends && !this.IsBack(step.Id, a)).ToList();
        if (step is not WorkflowStepDefinition && (step.Workspace is not null || step.MergeBack is not null) && arrows.Count > 1 &&
            !forward.Any(a => forward.Count(b => b.Outcome == a.Outcome || b.Outcome == ArrowConstants.OnAlways) > 1))
        {
            yield return "its arrows don't split the run (that takes two or more going forward), so it has no paths to give a " +
                         "workspace; remove 'workspace' and 'mergeBack'.";
        }
    }

    /// <summary>
    ///     Checks that every loop has a way out, so a run can't go round it until its limit every time.
    /// </summary>
    /// <returns>The problems.</returns>
    private IEnumerable<string> ValidateLoops()
    {
        foreach (var (from, to) in this.BackEdges)
        {
            var cycle = this.CycleOf(from, to);
            var exits = cycle.Any(s =>
            {
                var arrows = this.ArrowsOf(s);
                var back = arrows.Where(a => this.IsBack(s, a)).ToList();
                if (back.Any(a => a.IsConditional || a.OnLimit == StepConstants.OnLimitContinue))
                {
                    return true;
                }

                // An unconditional arrow back on success always wins, so the step's other success arrows never fire.
                var blocked = back.Any(a => a.Outcome == ArrowConstants.OnSuccess);
                return arrows.Where(a => !this.IsBack(s, a) && !(blocked && a.Outcome == ArrowConstants.OnSuccess))
                    .Any(a => a.Ends || !cycle.Contains(a.To));
            });
            if (!exits)
            {
                yield return $"Step '{from}': going back to '{to}' makes a loop with no way out (give an arrow a condition, " +
                             $"or onLimit '{StepConstants.OnLimitContinue}').";
            }
        }
    }

    /// <summary>
    ///     Works out, in flow order, the steps every run reaches and the steps that have always run by the time each
    ///     step starts.
    /// </summary>
    private void WorkOutGuaranteed()
    {
        if (this._guaranteed is not null)
        {
            return;
        }

        var guaranteed = new Dictionary<string, HashSet<string>>();
        var alwaysRun = new HashSet<string>();
        foreach (var step in this.TopologicalOrder)
        {
            var arrows = this.ArrowsInto(step);
            var incoming = arrows.Select(a => a.From).Distinct().ToList();
            var definition = this.StepOf(step)!;
            if (incoming.Count == 0)
            {
                guaranteed[step] = [];
                if (step == this.Initial && definition.Optional is null)
                {
                    alwaysRun.Add(step);
                }

                continue;
            }

            bool CanDie(string source)
            {
                return !alwaysRun.Contains(source) || !arrows.Any(a => a.From == source && this.AlwaysFires(source, a.Arrow));
            }

            var rule = this.WaitRuleOf(step);
            guaranteed[step] = rule.Guaranteed(incoming, s => With(guaranteed[s], s), CanDie);
            if (definition.Optional is null && !rule.CanDie(incoming, CanDie))
            {
                alwaysRun.Add(step);
            }
        }

        this._alwaysRun = alwaysRun;
        this._guaranteed = guaranteed;
    }

    /// <summary>
    ///     Returns whether an arrow fires whenever its step (which always runs) ends well: it fires on success, has no
    ///     condition, and isn't passed over for a conditional one or an arrow back.
    /// </summary>
    /// <param name="from">The step it leaves.</param>
    /// <param name="arrow">The arrow.</param>
    /// <returns>True if it always fires.</returns>
    private bool AlwaysFires(string from, ArrowDefinition arrow)
    {
        var step = this.StepOf(from)!;
        var arrows = step.Arrows ?? [];
        return arrow is { IsConditional: false } &&
               (arrow.Outcome == ArrowConstants.OnSuccess || arrow.Outcome == ArrowConstants.OnAlways) &&
               !(step.FollowsFirst && arrows.Any(a => a.IsConditional && !this.IsBack(from, a))) &&
               !arrows.Any(a => this.IsBack(from, a) && !a.IsConditional && (a.OnLimit ?? StepConstants.OnLimitFail) == StepConstants.OnLimitFail);
    }

    /// <summary>
    ///     Returns a copy of a set with a step added.
    /// </summary>
    /// <param name="steps">The set.</param>
    /// <param name="step">The step.</param>
    /// <returns>The copy.</returns>
    private static HashSet<string> With(HashSet<string> steps, string step)
    {
        return [..steps, step];
    }

    /// <summary>
    ///     Lists allowed values for a message, e.g. <c>'a' or 'b'</c>.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <returns>The text.</returns>
    private static string Quoted(IEnumerable<string> values)
    {
        return string.Join(" or ", values.Select(v => $"'{v}'"));
    }
}
