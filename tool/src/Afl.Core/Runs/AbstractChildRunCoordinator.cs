using Afl.Core.Exceptions;
using Afl.Core.Steps;
using Afl.Core.Workspaces;

namespace Afl.Core.Runs;

/// <summary>
///     Drives the runs a step waits on (a <c>workflow</c> step's child runs, or the path runs of a step whose arrows split the run): drives on the
///     ones that are ready (together, or one after another), gives each its own workspace when the step asks for one,
///     settles that workspace once the run finishes, and keeps the step's record of each run up to date. Subclasses
///     say which runs those are and how the step uses them.
/// </summary>
public abstract class AbstractChildRunCoordinator
{
    /// <summary>
    ///     Returns the step's records of the runs it waits on.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The records, which the coordinator updates in place.</returns>
    public abstract List<ChildRun> ChildrenOf(StepContext context);

    /// <summary>
    ///     Returns the workspace a run works in (a <see cref="WorkspaceRegistry" /> name), or null to share the
    ///     parent's.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="child">The record of the run.</param>
    /// <returns>The workspace name, or null.</returns>
    protected virtual string? WorkspaceOf(StepContext context, ChildRun child)
    {
        return context.Definition.Workspace;
    }

    /// <summary>
    ///     Returns whether a finished run's work is brought back into the parent's working copy.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="child">The record of the run.</param>
    /// <returns>True to merge it back.</returns>
    protected virtual bool MergeBackOf(StepContext context, ChildRun child)
    {
        return context.Definition.MergeBack ?? true;
    }

    /// <summary>
    ///     Returns whether the runs are driven all at once rather than one after another.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>True to drive them together.</returns>
    protected abstract bool DrivesTogether(StepContext context);

    /// <summary>
    ///     Returns the step of a finished run whose output is the run's, or null for its last step with output.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The step id, or null.</returns>
    protected virtual string? OutputStepOf(StepContext context)
    {
        return null;
    }

    /// <summary>
    ///     Returns what a run is for, for its workspace's name and for messages.
    /// </summary>
    /// <param name="child">The record.</param>
    /// <returns>The item.</returns>
    protected virtual string ItemOf(ChildRun child)
    {
        return child.Item ?? child.TicketKey;
    }

    /// <summary>
    ///     Brings each record up to date, drives on the runs that are ready, and settles the workspaces of the ones
    ///     that have finished. Driven one after another, a run's workspace is settled before the next is driven, so
    ///     that one starts from the work merged so far.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual async Task DriveReadyAsync(RunEngine engine, StepContext context, CancellationToken ct)
    {
        var children = this.ChildrenOf(context);
        foreach (var child in children)
        {
            this.Update(context, child, engine.Store.Get(child.RunId));
        }

        if (this.DrivesTogether(context))
        {
            await this.DriveTogetherAsync(engine, context, children.Where(this.IsReadyToDrive).ToList(), ct);
            foreach (var child in children.Where(this.IsFinished))
            {
                await this.SettleAsync(engine, context, child, ct);
            }
        }
        else
        {
            foreach (var child in children.Where(c => !this.IsFinished(c) || this.NeedsSettling(c)))
            {
                if (this.IsDecided(context))
                {
                    break;
                }

                if (this.IsReadyToDrive(child))
                {
                    await this.DriveAsync(engine, context, child, false, ct);
                }

                if (!this.IsFinished(child) || !await this.SettleAsync(engine, context, child, ct))
                {
                    break;
                }
            }
        }

        context.Save();
    }

    /// <summary>
    ///     Drives on, with their failed step retried, the runs that failed.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual async Task RetryFailedAsync(RunEngine engine, StepContext context, CancellationToken ct)
    {
        foreach (var failed in this.ChildrenOf(context).Where(c => c is { Status: RunStatus.Failed, Reused: not true }))
        {
            context.Report($"Retrying run {failed.RunId}");
            await this.DriveAsync(engine, context, failed, true, ct);
        }
    }

    /// <summary>
    ///     Cancels the runs the step started that haven't finished, and forgets them.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The step context.</param>
    public virtual void CancelStarted(RunEngine engine, StepContext context)
    {
        var children = this.ChildrenOf(context);
        foreach (var child in children.Where(c => c.Reused != true))
        {
            if (engine.Store.Get(child.RunId) is { IsFinished: false })
            {
                engine.Cancel(child.RunId);
            }
        }

        children.Clear();
    }

    /// <summary>
    ///     Returns whether a run has finished (done or cancelled).
    /// </summary>
    /// <param name="child">The record.</param>
    /// <returns>True if finished.</returns>
    public virtual bool IsFinished(ChildRun child)
    {
        return child.Status is RunStatus.Done or RunStatus.Cancelled;
    }

    /// <summary>
    ///     Returns whether a finished run's workspace still needs settling.
    /// </summary>
    /// <param name="child">The record.</param>
    /// <returns>True if it does.</returns>
    public virtual bool NeedsSettling(ChildRun child)
    {
        return child.HasUnsettledWorkspace;
    }

    /// <summary>
    ///     Returns whether the step should drive a run: one it started that has steps left to start (not waiting on a
    ///     person, failed or finished).
    /// </summary>
    /// <param name="child">The record.</param>
    /// <returns>True if it should be driven.</returns>
    public virtual bool IsReadyToDrive(ChildRun child)
    {
        return child.Reused != true && child.Status == RunStatus.Running;
    }

    /// <summary>
    ///     Returns whether the step already has what it waits for, so the runs not yet driven needn't be.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>True if decided; by default the step waits for every run.</returns>
    protected virtual bool IsDecided(StepContext context)
    {
        return false;
    }

    /// <summary>
    ///     Drives runs on all at once, or at most <see cref="Run.MaxParallel" /> at a time.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The step context.</param>
    /// <param name="ready">The runs ready to drive.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task DriveTogetherAsync(RunEngine engine, StepContext context, IReadOnlyList<ChildRun> ready, CancellationToken ct)
    {
        if (context.Run.MaxParallel is not { } max || max >= ready.Count)
        {
            await Task.WhenAll(ready.Select(c => this.DriveAsync(engine, context, c, false, ct)));
            return;
        }

        using var gate = new SemaphoreSlim(Math.Max(max, 1));
        await Task.WhenAll(ready.Select(async child =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await this.DriveAsync(engine, context, child, false, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    /// <summary>
    ///     Drives a run on, recording how it ended up. A run the step started is first given its workspace, if the step
    ///     gives its runs their own and it doesn't have one yet.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The step context.</param>
    /// <param name="child">The record.</param>
    /// <param name="retry">Whether to retry its failed step first.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task DriveAsync(RunEngine engine, StepContext context, ChildRun child, bool retry, CancellationToken ct)
    {
        var name = this.WorkspaceOf(context, child);
        var workspace = WorkspaceRegistry.Instance.CreateOrShared(name);
        Func<Run, Task>? prepare = null;
        if (workspace.IsIsolated && child.Reused != true && child.Workspace is null)
        {
            prepare = async run =>
            {
                var lease = await workspace.PrepareAsync(this.WorkspaceContext(engine, context, run, child), ct);
                run.WorkingPath = lease.Path;
                run.WorkspaceBranch = lease.Branch;
                run.Workspace = name;
                engine.Store.Save(run);
                child.Workspace = run.Workspace;
                context.Report($"Run {run.Id} works in {lease.Path}{(lease.Branch is null ? string.Empty : $" on branch {lease.Branch}")}");
            };
        }

        try
        {
            this.Update(context, child, await engine.DriveChildAsync(child.RunId, retry, context.Progress, ct, prepare));
        }
        catch (AflException ex)
        {
            child.Status = RunStatus.Failed;
            child.Error = ex.Message;
        }
    }

    /// <summary>
    ///     Settles a finished run's workspace if it has one that isn't settled: brings a done run's work back (or keeps
    ///     it, without <c>mergeBack</c>), or tidies away a cancelled run's.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The step context.</param>
    /// <param name="child">The finished run's record.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True when it is settled (or had nothing to settle); false when its work couldn't be brought back.</returns>
    protected virtual async Task<bool> SettleAsync(RunEngine engine, StepContext context, ChildRun child, CancellationToken ct)
    {
        if (!child.HasUnsettledWorkspace || engine.Store.Get(child.RunId) is not { } run)
        {
            return true;
        }

        var workspace = WorkspaceRegistry.Instance.CreateOrShared(run.Workspace);
        var workspaceContext = this.WorkspaceContext(engine, context, run, child);
        try
        {
            if (child.Status == RunStatus.Cancelled)
            {
                await workspace.DiscardAsync(workspaceContext, ct);
                child.WorkspaceSettled = true;
                child.WorkspaceNote = run.WorkspaceBranch is null ? null : $"cancelled; branch {run.WorkspaceBranch} kept";
                return true;
            }

            var outcome = await workspace.CompleteAsync(workspaceContext, ct);
            child.WorkspaceSettled = outcome.IsSettled;
            child.WorkspaceNote = outcome.Message;
            context.Report($"Run {run.Id}: {outcome.Message}");
            return outcome.IsSettled;
        }
        catch (AflException ex)
        {
            child.WorkspaceSettled = false;
            child.WorkspaceNote = $"couldn't settle its workspace: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    ///     Builds what a workspace needs to prepare or settle a run.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="context">The step context.</param>
    /// <param name="run">The run.</param>
    /// <param name="child">The step's record of it.</param>
    /// <returns>The workspace context.</returns>
    protected virtual WorkspaceContext WorkspaceContext(RunEngine engine, StepContext context, Run run, ChildRun child)
    {
        return new WorkspaceContext(engine.Store.Paths, context.Run, run, this.ItemOf(child), this.MergeBackOf(context, child));
    }

    /// <summary>
    ///     Copies a run's status and output onto its record.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="child">The record.</param>
    /// <param name="run">The run, or null if it's gone.</param>
    protected virtual void Update(StepContext context, ChildRun child, Run? run)
    {
        if (run is null)
        {
            child.Status = RunStatus.Failed;
            child.Error = $"Run {child.RunId} no longer exists.";
            return;
        }

        child.Status = run.Status;
        child.Error = run.Status == RunStatus.Failed ? run.CurrentStep?.Error : null;
        var outputStep = this.OutputStepOf(context) is { } id
            ? run.StepOf(id)
            : run.Steps.LastOrDefault(s => s.Status == StepStatus.Done && !string.IsNullOrWhiteSpace(s.Output));
        child.Output = outputStep?.Output;
    }
}
