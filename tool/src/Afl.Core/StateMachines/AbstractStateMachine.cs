using System.Runtime.CompilerServices;

namespace Afl.Core.StateMachines;

/// <summary>
///     One arrow of a state machine: from a state, when a trigger fires and its guard holds, to its targets. No target
///     ends the machine; several targets enter them all at once, as parallel regions that meet again at a join.
/// </summary>
/// <typeparam name="TState">The type of the machine's states.</typeparam>
/// <typeparam name="TTrigger">The type of what makes the machine move.</typeparam>
/// <typeparam name="TContext">What guards are checked against.</typeparam>
/// <param name="from">The state the transition leaves.</param>
/// <param name="trigger">What fires it.</param>
/// <param name="targets">The states it enters; empty to end the machine.</param>
public class StateTransition<TState, TTrigger, TContext>(TState from, TTrigger trigger, IReadOnlyList<TState> targets)
    where TState : notnull
    where TTrigger : notnull
{
    /// <summary>
    ///     Gets the state the transition leaves.
    /// </summary>
    public TState From { get; } = from;

    /// <summary>
    ///     Gets what fires the transition.
    /// </summary>
    public TTrigger Trigger { get; } = trigger;

    /// <summary>
    ///     Gets the states the transition enters, in order; empty when it ends the machine.
    /// </summary>
    public IReadOnlyList<TState> Targets { get; } = targets;

    /// <summary>
    ///     Gets whether the transition enters several states at once, as parallel regions.
    /// </summary>
    public bool IsParallel => this.Targets.Count > 1;

    /// <summary>
    ///     Gets whether the transition ends the machine.
    /// </summary>
    public bool Ends => this.Targets.Count == 0;

    /// <summary>
    ///     Returns whether the transition may be taken now. By default it always may.
    /// </summary>
    /// <param name="context">What the guard is checked against.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if it may.</returns>
    public virtual ValueTask<bool> PermitsAsync(TContext context, CancellationToken ct)
    {
        return ValueTask.FromResult(true);
    }
}

/// <summary>
///     Base for a state machine (a statechart): its states, the state it starts in, and an ordered table of guarded
///     transitions. Firing a trigger takes the first transition from the state whose guard holds; a transition with
///     several targets runs them as parallel regions that meet at their join. A subclass declares its states and
///     transitions with <see cref="AddState" /> and <see cref="AddTransition" />; this base answers what can happen next
///     and analyses the shape of the machine: which states are reachable or final, which arrows go back round a cycle,
///     what comes before a state, and where parallel regions join.
/// </summary>
/// <typeparam name="TState">The type of the machine's states.</typeparam>
/// <typeparam name="TTrigger">The type of what makes the machine move.</typeparam>
/// <typeparam name="TContext">What guards are checked against.</typeparam>
/// <typeparam name="TTransition">The type of the machine's transitions.</typeparam>
public abstract class AbstractStateMachine<TState, TTrigger, TContext, TTransition>
    where TState : notnull
    where TTrigger : notnull
    where TTransition : StateTransition<TState, TTrigger, TContext>
{
    /// <summary>
    ///     The states, in the order they were added.
    /// </summary>
    private readonly List<TState> _states = [];

    /// <summary>
    ///     The transitions, in the order they were added, which is the order they're tried in.
    /// </summary>
    private readonly List<TTransition> _transitions = [];

    /// <summary>
    ///     The shape of the machine, worked out on first use once it's declared.
    /// </summary>
    private Shape? _shape;

    /// <summary>
    ///     Gets the state the machine starts in.
    /// </summary>
    public abstract TState Initial { get; }

    /// <summary>
    ///     Gets the states, in the order they were declared.
    /// </summary>
    public IReadOnlyList<TState> States => this._states;

    /// <summary>
    ///     Gets the transitions, in the order they're tried.
    /// </summary>
    public IReadOnlyList<TTransition> Transitions => this._transitions;

    /// <summary>
    ///     Gets the shape of the machine, worked out on first use.
    /// </summary>
    private Shape Analysis => this._shape ??= new Shape(this);

    /// <summary>
    ///     Declares a state.
    /// </summary>
    /// <param name="state">The state.</param>
    protected void AddState(TState state)
    {
        if (!this._states.Contains(state))
        {
            this._states.Add(state);
            this._shape = null;
        }
    }

    /// <summary>
    ///     Declares a transition, tried after the ones already declared from the same state.
    /// </summary>
    /// <param name="transition">The transition.</param>
    protected void AddTransition(TTransition transition)
    {
        this._transitions.Add(transition);
        this._shape = null;
    }

    /// <summary>
    ///     Returns whether a state is one of the machine's.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>True if it is.</returns>
    public bool Contains(TState state)
    {
        return this._states.Contains(state);
    }

    /// <summary>
    ///     Returns the transitions leaving a state, in the order they're tried.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The transitions.</returns>
    public IReadOnlyList<TTransition> TransitionsFrom(TState state)
    {
        return this._transitions.Where(t => EqualityComparer<TState>.Default.Equals(t.From, state)).ToList();
    }

    /// <summary>
    ///     Returns the transitions a trigger can fire from a state, in the order they're tried.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="trigger">The trigger.</param>
    /// <returns>The transitions.</returns>
    public IReadOnlyList<TTransition> TransitionsFrom(TState state, TTrigger trigger)
    {
        return this.TransitionsFrom(state).Where(t => EqualityComparer<TTrigger>.Default.Equals(t.Trigger, trigger)).ToList();
    }

    /// <summary>
    ///     Returns whether a trigger can fire from a state at all, whatever the guards say.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="trigger">The trigger.</param>
    /// <returns>True if some transition answers it.</returns>
    public bool CanFire(TState state, TTrigger trigger)
    {
        return this.TransitionsFrom(state, trigger).Count > 0;
    }

    /// <summary>
    ///     Returns the triggers that can fire from a state.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The triggers, in the order their first transitions were declared.</returns>
    public IReadOnlyList<TTrigger> TriggersFrom(TState state)
    {
        return this.TransitionsFrom(state).Select(t => t.Trigger).Distinct().ToList();
    }

    /// <summary>
    ///     Returns whether some transition goes straight from one state to another.
    /// </summary>
    /// <param name="from">The state left.</param>
    /// <param name="to">The state entered.</param>
    /// <returns>True if one does.</returns>
    public bool Permits(TState from, TState to)
    {
        return this.TransitionsFrom(from).Any(t => t.Targets.Contains(to));
    }

    /// <summary>
    ///     Lists the transitions a trigger may take from a state now, in the order they're tried, checking each guard
    ///     only when the one before it was passed over.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="trigger">The trigger.</param>
    /// <param name="context">What guards are checked against.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The transitions whose guards hold.</returns>
    public async IAsyncEnumerable<TTransition> PermittedAsync(
        TState state,
        TTrigger trigger,
        TContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var transition in this.TransitionsFrom(state, trigger))
        {
            if (await transition.PermitsAsync(context, ct))
            {
                yield return transition;
            }
        }
    }

    /// <summary>
    ///     Returns the first transition a trigger may take from a state now.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="trigger">The trigger.</param>
    /// <param name="context">What guards are checked against.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The transition, or null when none may be taken.</returns>
    public async Task<TTransition?> FireAsync(TState state, TTrigger trigger, TContext context, CancellationToken ct = default)
    {
        await foreach (var transition in this.PermittedAsync(state, trigger, context, ct))
        {
            return transition;
        }

        return null;
    }

    /// <summary>
    ///     Returns the states a state leads straight to, by any transition, in order.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The states.</returns>
    public IReadOnlyList<TState> Successors(TState state)
    {
        return this.Analysis.Successors[state];
    }

    /// <summary>
    ///     Returns the states that lead straight to a state, by any transition, in declaration order.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The states.</returns>
    public IReadOnlyList<TState> Predecessors(TState state)
    {
        return this._states.Where(s => this.Analysis.Successors[s].Contains(state)).ToList();
    }

    /// <summary>
    ///     Gets the states the machine can reach from <see cref="Initial" />.
    /// </summary>
    public IReadOnlySet<TState> Reachable => this.Analysis.Reachable;

    /// <summary>
    ///     Returns whether a state ends the machine: no transition leads anywhere from it.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>True if it's final.</returns>
    public bool IsFinal(TState state)
    {
        return this.Analysis.Successors[state].Count == 0;
    }

    /// <summary>
    ///     Returns whether an arrow goes back round a cycle: it leads to a state on the way to its own source, as found
    ///     by a depth-first walk from <see cref="Initial" /> (then from any state it didn't reach, in order).
    /// </summary>
    /// <param name="from">The arrow's source.</param>
    /// <param name="to">The arrow's target.</param>
    /// <returns>True if it goes back.</returns>
    public bool IsBackEdge(TState from, TState to)
    {
        return this.Analysis.BackEdges.Contains((from, to));
    }

    /// <summary>
    ///     Gets every arrow that goes back round a cycle.
    /// </summary>
    public IReadOnlySet<(TState From, TState To)> BackEdges => this.Analysis.BackEdges;

    /// <summary>
    ///     Gets the states in an order where every state comes after each state with an arrow to it, leaving out the
    ///     arrows that go back round cycles.
    /// </summary>
    public IReadOnlyList<TState> TopologicalOrder => this.Analysis.Order;

    /// <summary>
    ///     Returns the states that come before a state: those with a way to it, leaving out arrows back round cycles.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The states, not including the state itself.</returns>
    public IReadOnlySet<TState> Ancestors(TState state)
    {
        return this.Analysis.Ancestors[state];
    }

    /// <summary>
    ///     Returns the states that come after a state: those it has a way to, leaving out arrows back round cycles.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The states, not including the state itself.</returns>
    public IReadOnlySet<TState> Descendants(TState state)
    {
        return this.Analysis.Descendants[state];
    }

    /// <summary>
    ///     Returns the states an arrow back round a cycle brings round again: its target, its source, and every state
    ///     on a way from the one to the other.
    /// </summary>
    /// <param name="from">The arrow's source.</param>
    /// <param name="to">The arrow's target.</param>
    /// <returns>The states.</returns>
    public IReadOnlySet<TState> CycleOf(TState from, TState to)
    {
        var cycle = this.Analysis.Descendants[to].Where(s => this.Analysis.Ancestors[from].Contains(s)).ToHashSet();
        cycle.Add(from);
        cycle.Add(to);
        return cycle;
    }

    /// <summary>
    ///     Returns the states every way on from a state passes through (it post-dominates), leaving out arrows back round
    ///     cycles, nearest first; the state itself is first.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The states.</returns>
    public IReadOnlyList<TState> PostDominators(TState state)
    {
        return this.Analysis.PostDominators[state];
    }

    /// <summary>
    ///     The shape of a machine: its arrows as a graph, and what follows from them.
    /// </summary>
    private sealed class Shape
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="Shape" /> class, analysing a machine.
        /// </summary>
        /// <param name="machine">The machine.</param>
        public Shape(AbstractStateMachine<TState, TTrigger, TContext, TTransition> machine)
        {
            this.Successors = machine._states.ToDictionary(s => s, s => (IReadOnlyList<TState>)machine.TransitionsFrom(s)
                .SelectMany(t => t.Targets)
                .Where(machine._states.Contains)
                .Distinct()
                .ToList());
            this.Reachable = machine._states.Contains(machine.Initial) ? Walk(machine.Initial, this.Successors) : [];
            this.BackEdges = this.FindBackEdges(machine);
            var forward = machine._states.ToDictionary(s => s, s => (IReadOnlyList<TState>)this.Successors[s]
                .Where(t => !this.BackEdges.Contains((s, t)))
                .ToList());
            this.Order = Sort(machine._states, forward);
            this.Descendants = machine._states.ToDictionary(s => s, s =>
            {
                var below = Walk(s, forward);
                below.Remove(s);
                return below;
            });
            this.Ancestors = machine._states.ToDictionary(s => s,
                s => (IReadOnlySet<TState>)machine._states.Where(a => this.Descendants[a].Contains(s)).ToHashSet());
            this.PostDominators = new Dictionary<TState, IReadOnlyList<TState>>();
            foreach (var state in this.Order.Reverse())
            {
                var after = forward[state];
                var common = after.Count == 0
                    ? new HashSet<TState>()
                    : after.Select(n => this.PostDominators[n].ToHashSet()).Aggregate((a, b) =>
                    {
                        a.IntersectWith(b);
                        return a;
                    });
                this.PostDominators[state] = new[] { state }.Concat(this.Order.Where(common.Contains)).ToList();
            }
        }

        /// <summary>
        ///     Gets the states each state leads straight to.
        /// </summary>
        public Dictionary<TState, IReadOnlyList<TState>> Successors { get; }

        /// <summary>
        ///     Gets the states reachable from the initial one.
        /// </summary>
        public HashSet<TState> Reachable { get; }

        /// <summary>
        ///     Gets the arrows that go back round cycles.
        /// </summary>
        public HashSet<(TState, TState)> BackEdges { get; }

        /// <summary>
        ///     Gets the states in topological order of the forward arrows.
        /// </summary>
        public IReadOnlyList<TState> Order { get; }

        /// <summary>
        ///     Gets the states each state leads to by forward arrows.
        /// </summary>
        public Dictionary<TState, HashSet<TState>> Descendants { get; }

        /// <summary>
        ///     Gets the states that lead to each state by forward arrows.
        /// </summary>
        public Dictionary<TState, IReadOnlySet<TState>> Ancestors { get; }

        /// <summary>
        ///     Gets the states every way on from each state passes through, nearest first.
        /// </summary>
        public Dictionary<TState, IReadOnlyList<TState>> PostDominators { get; }

        /// <summary>
        ///     Returns the states reachable from a state, it included.
        /// </summary>
        /// <param name="start">The state.</param>
        /// <param name="edges">The arrows to follow.</param>
        /// <returns>The states.</returns>
        private static HashSet<TState> Walk(TState start, IReadOnlyDictionary<TState, IReadOnlyList<TState>> edges)
        {
            var seen = new HashSet<TState>();
            var pending = new Stack<TState>([start]);
            while (pending.TryPop(out var state))
            {
                if (seen.Add(state) && edges.TryGetValue(state, out var next))
                {
                    foreach (var target in next)
                    {
                        pending.Push(target);
                    }
                }
            }

            return seen;
        }

        /// <summary>
        ///     Finds the arrows that go back round cycles with a depth-first walk from the initial state, then from each
        ///     state it didn't reach.
        /// </summary>
        /// <param name="machine">The machine.</param>
        /// <returns>The arrows.</returns>
        private HashSet<(TState, TState)> FindBackEdges(AbstractStateMachine<TState, TTrigger, TContext, TTransition> machine)
        {
            var back = new HashSet<(TState, TState)>();
            var done = new HashSet<TState>();
            var onPath = new HashSet<TState>();
            IEnumerable<TState> roots = machine._states.Contains(machine.Initial)
                ? machine._states.Prepend(machine.Initial)
                : machine._states;
            foreach (var root in roots.Where(r => !done.Contains(r)))
            {
                // An explicit stack of (state, next successor to look at) keeps deep workflows off the call stack.
                var stack = new Stack<(TState State, int Next)>([(root, 0)]);
                onPath.Add(root);
                while (stack.TryPop(out var frame))
                {
                    var successors = this.Successors[frame.State];
                    if (frame.Next >= successors.Count)
                    {
                        onPath.Remove(frame.State);
                        done.Add(frame.State);
                        continue;
                    }

                    stack.Push((frame.State, frame.Next + 1));
                    var target = successors[frame.Next];
                    if (onPath.Contains(target))
                    {
                        back.Add((frame.State, target));
                    }
                    else if (!done.Contains(target))
                    {
                        onPath.Add(target);
                        stack.Push((target, 0));
                    }
                }
            }

            return back;
        }

        /// <summary>
        ///     Sorts states so each comes after every state with a forward arrow to it, keeping declaration order where
        ///     the arrows allow.
        /// </summary>
        /// <param name="states">The states, in declaration order.</param>
        /// <param name="forward">The forward arrows.</param>
        /// <returns>The sorted states.</returns>
        private static IReadOnlyList<TState> Sort(IReadOnlyList<TState> states, IReadOnlyDictionary<TState, IReadOnlyList<TState>> forward)
        {
            var incoming = states.ToDictionary(s => s, _ => 0);
            foreach (var target in states.SelectMany(s => forward[s]))
            {
                incoming[target]++;
            }

            var order = new List<TState>();
            var ready = states.Where(s => incoming[s] == 0).ToList();
            while (ready.Count > 0)
            {
                var state = ready[0];
                ready.RemoveAt(0);
                order.Add(state);
                foreach (var target in forward[state])
                {
                    if (--incoming[target] == 0)
                    {
                        ready.Add(target);
                        ready.Sort((a, b) => IndexIn(states, a).CompareTo(IndexIn(states, b)));
                    }
                }
            }

            return order;
        }

        /// <summary>
        ///     Returns where a state was declared.
        /// </summary>
        /// <param name="states">The states, in declaration order.</param>
        /// <param name="state">The state.</param>
        /// <returns>Its index.</returns>
        private static int IndexIn(IReadOnlyList<TState> states, TState state)
        {
            for (var i = 0; i < states.Count; i++)
            {
                if (EqualityComparer<TState>.Default.Equals(states[i], state))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
