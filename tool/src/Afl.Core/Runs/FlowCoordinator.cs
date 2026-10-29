using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Steps;
using Afl.Core.Workspaces;

namespace Afl.Core.Runs;

/// <summary>
///     Runs a run's parallel paths (<see cref="Run.Paths" />) once a step's arrows split it. The step that split it
///     (the host) stays the run's current step, waiting at <see cref="StepStatus.AwaitingPaths" />, while each path's
///     step runs as a hidden path run (<see cref="RunKind.Path" />) that does just that one step
///     (<see cref="Run.StopAfterStep" />). As each finishes, its step is merged back into the run and routed here, so
///     the run is the only one that follows arrows: what they make ready becomes a path of its own, a step whose
///     <c>waitFor</c> holds starts (cancelling, with <c>others: cancel</c>, the paths that could only have led to it),
///     and an arrow back starts its loop again. Once one path is left and nothing is running, the run takes it on
///     itself; once none is left, the run ends. A step splitting the run with <c>workspace: worktree</c> gives each of
///     its paths a lane: a checkout of its own that the steps after it on that path share, brought back once no path
///     is left in it.
/// </summary>
public class FlowCoordinator : AbstractChildRunCoordinator
{
    /// <summary>
    ///     What looking at the paths left the host.
    /// </summary>
    protected enum PathsOutcome
    {
        /// <summary>
        ///     Paths are waiting on someone, or on runs being driven elsewhere.
        /// </summary>
        Waiting,

        /// <summary>
        ///     New paths are ready to start.
        /// </summary>
        Continue,

        /// <summary>
        ///     The host is no longer waiting: the run took the last path on itself, went back, ended, or failed.
        /// </summary>
        Decided,
    }

    /// <summary>
    ///     Gets the shared coordinator.
    /// </summary>
    public static FlowCoordinator Instance { get; } = new();

    /// <inheritdoc />
    public override List<ChildRun> ChildrenOf(StepContext context)
    {
        return context.State.Paths ??= [];
    }

    /// <summary>
    ///     Splits the run at a step whose arrows made several steps ready: the step waits for its paths, one starting
    ///     at each of them.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The context of the step that split the run.</param>
    /// <param name="ready">The steps its arrows made ready.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual async Task SplitAsync(RunEngine engine, StepContext context, IReadOnlyList<string> ready, CancellationToken ct)
    {
        var state = context.State;
        state.EndedAs = state.Status;
        StepLifecycle.Instance.Move(state, StepStatus.AwaitingPaths);
        context.Report($"Step '{state.StepId}' splits the run: {string.Join(", ", ready.Select(r => $"'{r}'"))}.");
        await this.ActivateAsync(engine, context, ready, state.StepId, null, ct);
        context.Save();
    }

    /// <summary>
    ///     Starts the paths that have no path run yet, drives them on, merges and routes the ones that have finished,
    ///     and goes round again until nothing changes; then the run takes on its last path, ends, fails, or keeps
    ///     waiting.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual async Task RefreshAsync(RunEngine engine, StepContext context, CancellationToken ct)
    {
        while (true)
        {
            if (!await this.StartPathsAsync(engine, context, ct))
            {
                break;
            }

            await this.DriveReadyAsync(engine, context, ct);
            var decided = false;
            foreach (var path in context.Run.Paths.ToList())
            {
                if (context.Run.Paths.Contains(path) && this.HasFinished(engine, context, path) &&
                    !await this.MergeAndRouteAsync(engine, context, path, ct))
                {
                    decided = true;
                    break;
                }
            }

            if (decided || !await this.SettleLanesAsync(engine, context, this.IsLaneOpen(context), ct) ||
                await this.ConcludeAsync(engine, context, ct) != PathsOutcome.Continue)
            {
                break;
            }
        }

        context.Save();
    }

    /// <summary>
    ///     Gets the paths going again after the host failed: a path whose step failed in the run runs it afresh, and a
    ///     path run that stopped part-way (or was cancelled) is retried or started again.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public override async Task RetryFailedAsync(RunEngine engine, StepContext context, CancellationToken ct)
    {
        foreach (var path in context.Run.Paths)
        {
            var state = context.Run.StepOf(path.StepId)!;
            var child = this.ChildOf(context, path);
            if (state.Status == StepStatus.Failed)
            {
                context.Report($"Retrying step '{path.StepId}'");
                state.Archive(AttemptCause.Retry, null, false);
                path.RunId = null;
            }
            else if (child is { Status: RunStatus.Failed, Merged: not true })
            {
                context.Report($"Retrying run {child.RunId}");
                await this.DriveAsync(engine, context, child, true, ct);
            }
            else if (child is { Status: RunStatus.Cancelled, Merged: not true })
            {
                path.RunId = null;
            }
        }

        context.Save();
    }

    /// <inheritdoc />
    protected override bool DrivesTogether(StepContext context)
    {
        return !context.Run.Sequential;
    }

    /// <inheritdoc />
    protected override string ItemOf(ChildRun child)
    {
        return child.Branch ?? base.ItemOf(child);
    }

    /// <summary>
    ///     Returns the workspace of a lane's first path run, which the lane's later runs share: the one the step that
    ///     split the run into it asked for. Every other path run works where the run does.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="child">The record of the path run.</param>
    /// <returns>The workspace name, or null.</returns>
    protected override string? WorkspaceOf(StepContext context, ChildRun child)
    {
        return this.SplitterOf(context, child)?.Workspace;
    }

    /// <inheritdoc />
    protected override bool MergeBackOf(StepContext context, ChildRun child)
    {
        return this.SplitterOf(context, child)?.MergeBack ?? true;
    }

    /// <summary>
    ///     Builds what a workspace needs for a path run; a lane split off inside another lane starts from that lane's
    ///     checkout and goes back into it, rather than the run's own.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="run">The path run.</param>
    /// <param name="child">The record of it.</param>
    /// <returns>The workspace context.</returns>
    protected override WorkspaceContext WorkspaceContext(RunEngine engine, StepContext context, Run run, ChildRun child)
    {
        var workspace = base.WorkspaceContext(engine, context, run, child);
        if (child.OuterLane is not { } outer ||
            this.ChildrenOf(context).LastOrDefault(c => c.Lane == outer && c.SplitFrom is not null) is not { } holder ||
            engine.Store.Get(holder.RunId) is not { WorkingPath: { } checkout })
        {
            return workspace;
        }

        return workspace with { Base = checkout };
    }

    /// <summary>
    ///     Settles a path run's workspace once no path is left in its lane; until then the lane's later steps still
    ///     work in it, so it counts as settled for now.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="child">The record of the path run.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True when settled or put off; false when its work couldn't be brought back.</returns>
    protected override Task<bool> SettleAsync(RunEngine engine, StepContext context, ChildRun child, CancellationToken ct)
    {
        return child.Lane is { } lane && this.IsLaneOpen(context)(lane) ? Task.FromResult(true) : base.SettleAsync(engine, context, child, ct);
    }

    /// <summary>
    ///     Starts a path run for each path that hasn't one; a step left out when the run started is skipped and routed
    ///     in the run instead.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>False when the host stopped waiting (it failed, or the run went back past it).</returns>
    protected virtual async Task<bool> StartPathsAsync(RunEngine engine, StepContext context, CancellationToken ct)
    {
        var run = context.Run;
        var maxSteps = run.Workflow.MaxSteps ?? StepConstants.DefaultMaxSteps;
        foreach (var path in run.Paths.Where(p => p.RunId is null).ToList())
        {
            var state = run.StepOf(path.StepId)!;
            if (!run.Paths.Contains(path) || state.Status != StepStatus.Pending)
            {
                continue;
            }

            if (FlowRouter.Instance.TrySkip(engine.ContextFor(context, state)))
            {
                run.Paths.Remove(path);
                if (!await this.RouteAsync(engine, context, path, state, FlowTrigger.LeftOut, ct))
                {
                    return false;
                }

                continue;
            }

            if (run.StepRuns >= maxSteps)
            {
                this.Fail(context, string.Format(StepConstants.MaxStepsReachedFormat, maxSteps, path.StepId));
                return false;
            }

            var pathRun = engine.CreatePathRun(run, context.State.StepId, path);
            run.StepRuns++;
            path.RunId = pathRun.Id;
            var children = this.ChildrenOf(context);
            var child = new ChildRun
            {
                RunId = pathRun.Id, WorkflowId = run.WorkflowId, TicketKey = run.Ticket.Key, Item = path.StepId, Branch = path.Origin, Lane = path.Lane,
                OuterLane = path.OuterLane,
            };
            if (path.Lane is { } lane)
            {
                if (children.FirstOrDefault(c => c.Lane == lane && c.SplitFrom is not null && c.WorkspaceSettled != true) is { } first)
                {
                    this.ShareLane(engine, first, pathRun);
                }
                else
                {
                    child.SplitFrom = path.ArrivedFrom;
                }
            }

            children.Add(child);
            context.Report($"Started step '{path.StepId}' on path '{path.Origin}' as run {pathRun.Id}");
        }

        context.Save();
        return true;
    }

    /// <summary>
    ///     Has a lane's later path run work in the checkout its first one was given.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="first">The record of the lane's first path run.</param>
    /// <param name="pathRun">The later path run.</param>
    protected virtual void ShareLane(RunEngine engine, ChildRun first, Run pathRun)
    {
        if (engine.Store.Get(first.RunId) is not { WorkingPath: { } path } holder)
        {
            return;
        }

        // Not its Workspace: the checkout is the first run's to settle, so cancelling this one mustn't discard it.
        pathRun.WorkingPath = path;
        pathRun.WorkspaceBranch = holder.WorkspaceBranch;
        engine.Store.Save(pathRun);
    }

    /// <summary>
    ///     Returns whether a path has a path run that has finished its step and hasn't been merged: it is done, or
    ///     failed other than by being interrupted (which is retried instead).
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="path">The path.</param>
    /// <returns>True if it is ready to merge.</returns>
    protected virtual bool HasFinished(RunEngine engine, StepContext context, ActivePath path)
    {
        if (this.ChildOf(context, path) is not { Merged: not true } child || engine.Store.Get(child.RunId) is not { } pathRun)
        {
            return false;
        }

        return child.Status == RunStatus.Done ||
               (child.Status == RunStatus.Failed && pathRun.StepOf(path.StepId)?.FailureKind != StepConstants.FailureInterrupted);
    }

    /// <summary>
    ///     Merges a finished path's step back into the run (its record and its visits) and routes it.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="path">The path.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>False when the host stopped waiting.</returns>
    protected virtual async Task<bool> MergeAndRouteAsync(RunEngine engine, StepContext context, ActivePath path, CancellationToken ct)
    {
        var run = context.Run;
        var child = this.ChildOf(context, path)!;
        var pathRun = engine.Store.Get(child.RunId)!;
        var merged = Copy(pathRun.StepOf(path.StepId)!);
        var index = run.Steps.FindIndex(s => s.StepId == path.StepId);
        run.Steps[index] = merged;
        foreach (var visit in pathRun.Path)
        {
            run.Path.Add(new StepVisit { StepId = visit.StepId, Iteration = visit.Iteration, Branch = visit.Branch ?? path.Origin, At = visit.At });
        }

        child.Merged = true;
        run.Paths.Remove(path);
        var trigger = merged.Status switch
        {
            StepStatus.Done => FlowTrigger.Succeeded,
            StepStatus.Skipped => FlowTrigger.LeftOut,
            _ when merged.FailureKind == StepConstants.FailureTimeout => FlowTrigger.TimedOut,
            _ => FlowTrigger.Failed,
        };
        return await this.RouteAsync(engine, context, path, merged, trigger, ct);
    }

    /// <summary>
    ///     Routes a path's step that ended. A failure nothing handles leaves the path in place, failed, for a retry; an
    ///     arrow back starts its loop again on the same path (or, when the loop goes back past the host, collapses the
    ///     paths and goes back in the run); otherwise each step the arrows made ready becomes a path.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="path">The path, already taken off <see cref="Run.Paths" />.</param>
    /// <param name="state">The run's record of the step.</param>
    /// <param name="trigger">How the step ended.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>False when the host stopped waiting.</returns>
    protected virtual async Task<bool> RouteAsync(
        RunEngine engine,
        StepContext context,
        ActivePath path,
        RunStep state,
        FlowTrigger trigger,
        CancellationToken ct)
    {
        var run = context.Run;
        var machine = FlowMachine.Of(run.Workflow);
        var stepContext = engine.ContextFor(context, state);
        var route = await FlowRouter.Instance.RouteAsync(stepContext, trigger, ct);
        if (route is null || route.Fails)
        {
            context.Report($"Step '{state.StepId}' failed on path '{path.Origin}': {state.Error}");
            run.Paths.Add(path);
            return true;
        }

        if (route.Back is { } back)
        {
            var target = back.Arrow.To;
            if (machine.CycleOf(state.StepId, target).Contains(context.State.StepId))
            {
                await this.GoBackPastAsync(engine, context, stepContext, target, ct);
                return false;
            }

            var reason = FlowRouter.Instance.Rewind(run, machine, state.StepId, target);
            context.Report($"Path '{path.Origin}' goes back to step '{target}' ({reason.ToLowerInvariant()}).");
            if (!FlowRouter.Instance.IsActive(run, target))
            {
                run.Paths.Add(new ActivePath { StepId = target, Origin = path.Origin, Lane = path.Lane, OuterLane = path.OuterLane, ArrivedFrom = state.StepId });
            }

            return true;
        }

        if (route.Ready.Count == 0)
        {
            context.Report($"Path '{path.Origin}' ends at step '{state.StepId}'.");
        }

        await this.ActivateAsync(engine, context, route.Ready, state.StepId, path, ct);
        return true;
    }

    /// <summary>
    ///     Makes each step an arrow made ready a path of its own. A path a step splits off begins there (in a lane of
    ///     its own when the step asks for its own workspace); a step that paths meet at begins one too; otherwise the
    ///     path carries on. With <c>others: cancel</c>, a step that starts cancels the paths still on their way that can
    ///     only lead to it.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="targets">The steps made ready.</param>
    /// <param name="from">The step whose arrows made them ready, if any.</param>
    /// <param name="source">The path that step was on; null for the host or when no path is.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task ActivateAsync(
        RunEngine engine,
        StepContext context,
        IReadOnlyList<string> targets,
        string? from,
        ActivePath? source,
        CancellationToken ct)
    {
        var run = context.Run;
        var machine = FlowMachine.Of(run.Workflow);
        var splits = targets.Count > 1;
        var isolated = from is not null && WorkspaceRegistry.Instance.CreateOrShared(machine.StepOf(from)?.Workspace).IsIsolated;
        foreach (var target in targets)
        {
            if (FlowRouter.Instance.IsActive(run, target))
            {
                continue;
            }

            FlowRouter.Instance.Arrive(run, target, from);
            var arrived = machine.ArrowsInto(target).Where(a => run.Arrows.GetValueOrDefault(FlowMachine.KeyOf(a.From, a.Arrow)) == ArrowState.Fired).ToList();
            var path = new ActivePath { StepId = target, ArrivedFrom = from };
            if (arrived.Count > 1)
            {
                path.Origin = target;
                path.Lane = this.LaneOfJoin(context, arrived.Select(a => a.From), from, source);
                path.OuterLane = path.Lane is { } lane ? this.OuterOf(context, lane) : null;
            }
            else if (splits && isolated)
            {
                path.Origin = target;
                path.Lane = target;
                path.OuterLane = source?.Lane;
            }
            else
            {
                path.Origin = splits || source is null ? target : source.Origin;
                path.Lane = source?.Lane;
                path.OuterLane = source?.OuterLane;
            }

            run.Paths.Add(path);
            if (machine.StepOf(target) is { LetsOthersFinish: false })
            {
                await this.CancelOvertakenAsync(engine, context, path, ct);
            }
        }
    }

    /// <summary>
    ///     Returns the lane a step that paths meet at runs in: the innermost lane every path that got there was in (a
    ///     split inside a lane joining up again), else none, so it runs once the lanes inside it are merged.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="sources">The steps whose arrows into it fired.</param>
    /// <param name="from">The step whose arrow made it ready.</param>
    /// <param name="source">The path that step was on, if any.</param>
    /// <returns>The lane, or null.</returns>
    protected virtual string? LaneOfJoin(StepContext context, IEnumerable<string> sources, string? from, ActivePath? source)
    {
        var children = this.ChildrenOf(context);
        var chains = sources.Select(s => s == from && source is not null ? this.LanesOf(context, source).ToList()
            : this.LanesUp(context, children.LastOrDefault(c => c.Item == s)?.Lane).ToList()).ToList();
        return chains[0].FirstOrDefault(lane => chains.All(c => c.Contains(lane)));
    }

    /// <summary>
    ///     Cancels the paths that can now only lead to a step that has started: their path runs are cancelled (noted
    ///     <see cref="StepConstants.OvertakenNote" />), their arrows die, and their lanes are settled once empty.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="started">The path of the step that started.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task CancelOvertakenAsync(RunEngine engine, StepContext context, ActivePath started, CancellationToken ct)
    {
        var run = context.Run;
        var machine = FlowMachine.Of(run.Workflow);
        var overtaken = run.Paths.Where(p => p != started && p.StepId != started.StepId && machine.OnlyLeadsTo(p.StepId, started.StepId)).ToList();
        foreach (var path in overtaken)
        {
            run.Paths.Remove(path);
            if (this.ChildOf(context, path) is { } child)
            {
                if (engine.Store.Get(child.RunId) is { IsFinished: false })
                {
                    engine.Cancel(child.RunId);
                }

                child.Status = RunStatus.Cancelled;
                child.Note = StepConstants.OvertakenNote;
                child.Error = null;
            }

            FlowRouter.Instance.Fire(run, machine, path.StepId, []);
            context.Report($"Step '{path.StepId}' on path '{path.Origin}': {StepConstants.OvertakenNote}");
        }

        if (overtaken.Count > 0)
        {
            await this.SettleLanesAsync(engine, context, this.IsLaneOpen(context), ct);
        }
    }

    /// <summary>
    ///     Settles the workspaces of the lanes no path is left in. One that can't be brought back fails the host.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="isOpen">Returns whether a lane still has a path in it.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>False when one couldn't be settled.</returns>
    protected virtual async Task<bool> SettleLanesAsync(RunEngine engine, StepContext context, Func<string, bool> isOpen, CancellationToken ct)
    {
        var closed = this.ChildrenOf(context).Where(c => c is { HasUnsettledWorkspace: true, Lane: { } lane } && !isOpen(lane));
        foreach (var child in closed.OrderByDescending(c => this.LanesUp(context, c.Lane).Count()).ToList())
        {
            if (!await base.SettleAsync(engine, context, child, ct))
            {
                this.Fail(context, $"Path '{child.Branch}' is done, but: {child.WorkspaceNote}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Decides what the host does once every finished path is routed: with no path left, the paths that will now
    ///     never come die and the run ends (or goes on to what that makes ready); with one path left that hasn't
    ///     started and isn't in a lane with a checkout of its own, the run takes it on; with a failed path and nothing else that can move, the host fails.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>What happened.</returns>
    protected virtual async Task<PathsOutcome> ConcludeAsync(RunEngine engine, StepContext context, CancellationToken ct)
    {
        var run = context.Run;
        var host = context.State;
        if (run.Paths.Count == 0)
        {
            await this.ActivateAsync(engine, context, FlowRouter.Instance.KillPending(run, FlowMachine.Of(run.Workflow)), null, null, ct);
            if (run.Paths.Count == 0)
            {
                StepLifecycle.Instance.Move(host, host.EndedAs ?? StepStatus.Done);
                context.Report("Every path has ended.");
                run.CurrentStepId = null;
                return PathsOutcome.Decided;
            }
        }

        var startable = run.Paths.Where(p => p.RunId is null && run.StepOf(p.StepId) is { Status: StepStatus.Pending }).ToList();
        if (run.Paths.Count == 1 && startable.Count == 1 && !this.HasCheckout(context, startable[0]))
        {
            if (!await this.SettleLanesAsync(engine, context, _ => false, ct))
            {
                return PathsOutcome.Decided;
            }

            var last = run.Paths[0];
            StepLifecycle.Instance.Move(host, host.EndedAs ?? StepStatus.Done);
            run.Paths.Clear();
            context.Report($"One path is left; the run goes on to step '{last.StepId}'.");
            run.CurrentStepId = last.StepId;
            return PathsOutcome.Decided;
        }

        if (startable.Count > 0)
        {
            return PathsOutcome.Continue;
        }

        var moving = run.Paths.Any(p => this.ChildOf(context, p) is { Merged: not true, Status: RunStatus.Running or RunStatus.Waiting });
        if (!moving && run.Paths.FirstOrDefault(p => this.IsFailed(context, p)) is { } failed)
        {
            var error = run.StepOf(failed.StepId) is { Status: StepStatus.Failed } state ? state.Error : this.ChildOf(context, failed)?.Error;
            this.Fail(context, $"Step '{failed.StepId}' failed on path '{failed.Origin}': {error}");
            return PathsOutcome.Decided;
        }

        return PathsOutcome.Waiting;
    }

    /// <summary>
    ///     Returns whether a path is in a lane whose checkout hasn't been settled yet, so its step has to run there.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="path">The path.</param>
    /// <returns>True if it is.</returns>
    protected virtual bool HasCheckout(StepContext context, ActivePath path)
    {
        return path.Lane is { } lane && this.ChildrenOf(context).Any(c => c is { HasUnsettledWorkspace: true } && c.Lane == lane);
    }

    /// <summary>
    ///     Returns whether a path has failed: its step failed in the run (and nothing handled it), or its path run was
    ///     interrupted or cancelled.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="path">The path.</param>
    /// <returns>True if failed.</returns>
    protected virtual bool IsFailed(StepContext context, ActivePath path)
    {
        return context.Run.StepOf(path.StepId) is { Status: StepStatus.Failed } ||
               this.ChildOf(context, path) is { Merged: not true, Status: RunStatus.Failed or RunStatus.Cancelled };
    }

    /// <summary>
    ///     Goes back to a step before the host: every other path is cancelled, every lane settled, and the run goes back
    ///     round the loop itself, forgetting the arrows fired after the host so the paths start afresh.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The host's context.</param>
    /// <param name="from">The context of the step whose arrow goes back.</param>
    /// <param name="target">The step it goes back to.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task GoBackPastAsync(RunEngine engine, StepContext context, StepContext from, string target, CancellationToken ct)
    {
        var run = context.Run;
        var machine = FlowMachine.Of(run.Workflow);
        foreach (var child in run.Paths.Select(p => this.ChildOf(context, p)).OfType<ChildRun>())
        {
            if (engine.Store.Get(child.RunId) is { IsFinished: false })
            {
                engine.Cancel(child.RunId);
            }

            child.Status = RunStatus.Cancelled;
        }

        run.Paths.Clear();
        await this.SettleLanesAsync(engine, context, _ => false, ct);
        foreach (var stepId in machine.Descendants(context.State.StepId).Append(context.State.StepId))
        {
            foreach (var arrow in machine.ArrowsOf(stepId))
            {
                run.Arrows.Remove(FlowMachine.KeyOf(stepId, arrow));
            }
        }

        FlowRouter.Instance.GoBack(from, target);
    }

    /// <summary>
    ///     Fails the host, leaving its paths to be retried.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="error">Why.</param>
    protected virtual void Fail(StepContext context, string error)
    {
        StepLifecycle.Instance.Move(context.State, StepStatus.Failed);
        context.State.Error = error;
        context.State.EndedAt = DateTimeOffset.Now;
    }

    /// <summary>
    ///     Returns the host's record of a path's path run, if it has one.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="path">The path.</param>
    /// <returns>The record, or null.</returns>
    protected ChildRun? ChildOf(StepContext context, ActivePath path)
    {
        return path.RunId is { } id ? this.ChildrenOf(context).LastOrDefault(c => c.RunId == id) : null;
    }

    /// <summary>
    ///     Returns the definition of the step that split the run into a path run's lane, for its first path run.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="child">The record of the path run.</param>
    /// <returns>The step's definition, or null.</returns>
    protected Workflows.AbstractStepDefinition? SplitterOf(StepContext context, ChildRun child)
    {
        return child.SplitFrom is { } id ? FlowMachine.Of(context.Run.Workflow).StepOf(id) : null;
    }

    /// <summary>
    ///     Returns a lookup of whether a lane still has a path in it, or in a lane split off inside it.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <returns>The lookup.</returns>
    protected Func<string, bool> IsLaneOpen(StepContext context)
    {
        return lane => context.Run.Paths.Any(p => this.LanesOf(context, p).Contains(lane));
    }

    /// <summary>
    ///     Returns the lanes a path is in: its own, then the lanes that one sits inside, innermost first.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="path">The path.</param>
    /// <returns>The lanes; empty for a path in the run's own checkout.</returns>
    protected IEnumerable<string> LanesOf(StepContext context, ActivePath path)
    {
        return path.Lane is { } lane ? this.LanesUp(context, path.OuterLane).Prepend(lane) : [];
    }

    /// <summary>
    ///     Returns the lane a lane was split off inside (<see cref="ChildRun.OuterLane" />), if any.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="lane">The lane.</param>
    /// <returns>The outer lane, or null.</returns>
    protected string? OuterOf(StepContext context, string lane)
    {
        return this.ChildrenOf(context).LastOrDefault(c => c.Lane == lane && c.SplitFrom is not null)?.OuterLane;
    }

    /// <summary>
    ///     Returns a lane and the lanes it sits inside, innermost first.
    /// </summary>
    /// <param name="context">The host's context.</param>
    /// <param name="lane">The lane, or null for none.</param>
    /// <returns>The lanes; empty for none.</returns>
    protected IEnumerable<string> LanesUp(StepContext context, string? lane)
    {
        var seen = new HashSet<string>();
        for (var current = lane; current is not null && seen.Add(current); current = this.OuterOf(context, current))
        {
            yield return current;
        }
    }

    /// <summary>
    ///     Copies a step record, so the run and the path run don't share one.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The copy.</returns>
    private static RunStep Copy(RunStep step)
    {
        return JsonSerializer.Deserialize<RunStep>(JsonSerializer.Serialize(step, JsonConstants.Options), JsonConstants.Options)!;
    }
}
