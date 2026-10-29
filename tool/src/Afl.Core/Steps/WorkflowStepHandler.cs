using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Workflows;
using Afl.Core.Workspaces;

namespace Afl.Core.Steps;

/// <summary>
///     A step that runs another workflow as child runs: one per item of its <see cref="WorkflowStepDefinition.ForEach" />
///     source (by default one, on the run's ticket), each filling the child workflow's parameters from
///     <see cref="AbstractStepDefinition.With" />. The children are driven one after another (or all at once with
///     <see cref="WorkflowStepDefinition.ChildrenTogether" />), and the step waits at <see cref="StepStatus.AwaitingChildren" /> until
///     they finish; a child waiting on a person wakes this run when it's done. Its output is the children's. With
///     <see cref="AbstractStepDefinition.Workspace" /> each child can work in a checkout of its own (an
///     <see cref="AbstractWorkspace" />), prepared just before it is first driven and settled once it finishes.
/// </summary>
[StepHandlerRegistration(StepConstants.TypeWorkflow)]
public class WorkflowStepHandler : AbstractStepHandler<WorkflowStepDefinition>
{
    /// <inheritdoc />
    public override string DisplayName => "Run a workflow";

    /// <inheritdoc />
    public override string Summary => "Starts child runs of another workflow, once or for each of several items.";

    /// <inheritdoc />
    public override bool StartsChildRuns => true;

    /// <inheritdoc />
    protected override IEnumerable<string> Validate(WorkflowStepDefinition step)
    {
        if (string.IsNullOrWhiteSpace(step.Workflow))
        {
            yield return "a workflow step needs 'workflow', the id of the workflow its child runs follow.";
        }

        if (step.Children is not null && step.Children is not (StepConstants.ChildrenWait or StepConstants.ChildrenStart))
        {
            yield return $"unknown children '{step.Children}'; use '{StepConstants.ChildrenWait}' or '{StepConstants.ChildrenStart}'.";
        }

        if (step.Children == StepConstants.ChildrenStart && step.ChildOutput is not null)
        {
            yield return $"'childOutput' needs the step to wait for its children (wait '{StepConstants.ChildrenWait}').";
        }

        if (step.Workspace is { } workspace && !WorkspaceRegistry.Instance.Contains(workspace))
        {
            yield return $"unknown workspace '{workspace}'; use one of {string.Join(", ", WorkspaceRegistry.Instance.Keys.Order())}.";
        }
        else if (step.MergeBack is not null && !WorkspaceRegistry.Instance.CreateOrShared(step.Workspace).IsIsolated)
        {
            yield return "'mergeBack' needs the children to have a workspace of their own (e.g. 'workspace': 'worktree').";
        }

        if (step.Workspace is not null && step.Workspace != StepConstants.WorkspaceShared && step.Children == StepConstants.ChildrenStart)
        {
            yield return $"a step whose children have their own workspace has to wait for them (wait '{StepConstants.ChildrenWait}') to bring their work back.";
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<string> ValidateInWorkflow(WorkflowDefinition workflow, WorkflowStepDefinition step)
    {
        var problems = new List<string>();
        if (step.ForEach is not null)
        {
            problems.AddRange(ChildSourceRegistry.Instance.Validate(step.ForEach, workflow, step));
        }

        var machine = new FlowMachine(workflow);
        var earlier = TemplateVariables.StepsBefore(machine, step);
        var possible = TemplateVariables.StepsMaybeBefore(machine, step);
        var templates = (step.With ?? []).Select(p => (Where: $"with '{p.Key}'", Text: p.Value))
            .Concat((step.ForEach?.Tickets ?? []).Select(t => (Where: "forEach tickets", Text: t)));
        foreach (var (where, text) in templates)
        {
            foreach (var reference in PromptTemplate.References(text).Where(r => r != StepConstants.ItemVariable &&
                         !TemplateVariables.IsAvailable(r, earlier, workflow.Steps, step, possible)))
            {
                problems.Add($"{where}: '{{{{{reference}}}}}' is not available here. {TemplateVariables.Describe(earlier, workflow.Steps, step, possible)}, {StepConstants.ItemVariable}");
            }
        }

        return problems;
    }

    /// <summary>
    ///     Starts the child runs, one per item of the step's source (reusing existing runs when
    ///     <see cref="WorkflowStepDefinition.Reuse" /> is set), then waits for them. When retried after a child failed, retries
    ///     the failed children instead of starting new ones.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public override async Task StartAsync(StepContext context, CancellationToken ct)
    {
        if (context.Engine is not { } engine)
        {
            Fail(context, "A workflow step can only run inside the run engine.");
            return;
        }

        if (context.State.ChildRuns.Count > 0)
        {
            await WorkflowChildCoordinator.Instance.RetryFailedAsync(engine, context, ct);

            Move(context, StepStatus.AwaitingChildren);
            return;
        }

        var definition = DefinitionOf(context);
        var workflow = new WorkflowLibrary(engine.Store.Paths, context.Run.RepoPath).Get(definition.Workflow!);
        if (workflow is null)
        {
            Fail(context, $"There's no workflow '{definition.Workflow}' to start.");
            return;
        }

        var source = definition.ForEach ?? new ChildSourceDefinition();
        IReadOnlyList<ChildItem> items;
        try
        {
            items = await ChildSourceRegistry.Instance.Create(source.Kind).ResolveAsync(source, context, ct);
        }
        catch (AflException ex)
        {
            Fail(context, $"Couldn't work out the child runs: {ex.Message}");
            return;
        }

        if (items.Count == 0)
        {
            await this.FinishAsync(context, "There were no child runs to start.", ct);
            return;
        }

        if (items.Count > StepConstants.MaxChildRuns)
        {
            Fail(context, $"The step would start {items.Count} child runs; a step can start at most {StepConstants.MaxChildRuns}.");
            return;
        }

        foreach (var item in items)
        {
            var variables = new Dictionary<string, string> { [StepConstants.ItemVariable] = item.Item };
            var with = definition.With?.ToDictionary(p => p.Key, p => context.Render(PromptTemplate.Substitute(p.Value, variables)));
            if (definition.Reuse == true && FindReusable(engine, context.Run, workflow.Id, item.Ticket.Key) is { } existing)
            {
                context.State.ChildRuns.Add(new ChildRun
                {
                    RunId = existing.Id, WorkflowId = workflow.Id, TicketKey = item.Ticket.Key, Item = item.Item, Reused = true,
                    Status = existing.Status,
                });
                context.State.Notes.Add(string.Format(StepConstants.ReusedRunNoteFormat, existing.Id, workflow.Id, item.Ticket.Key));
                continue;
            }

            try
            {
                var child = engine.CreateChild(context.Run, context.State.StepId, workflow, item.Ticket,
                    definition.Agent ?? context.Run.Executor, with);
                context.State.ChildRuns.Add(new ChildRun
                {
                    RunId = child.Id, WorkflowId = workflow.Id, TicketKey = item.Ticket.Key, Item = item.Item,
                });
                context.Report($"Started child run {child.Id} of '{workflow.Id}' on {item.Ticket.Key}");
            }
            catch (AflException ex)
            {
                WorkflowChildCoordinator.Instance.CancelStarted(engine, context);
                Fail(context, $"Couldn't start a child run of '{workflow.Id}' for {item.Item}: {ex.Message}");
                return;
            }
        }

        context.Save();
        Move(context, StepStatus.AwaitingChildren);
    }

    /// <summary>
    ///     Drives on the children that are ready (the next one in order, or all of them when parallel), then finishes
    ///     the step once they're all done, fails it if one failed, or leaves it waiting.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public override async Task RefreshAsync(StepContext context, CancellationToken ct)
    {
        if (context.Engine is not { } engine)
        {
            return;
        }

        var coordinator = WorkflowChildCoordinator.Instance;
        await coordinator.DriveReadyAsync(engine, context, ct);
        var children = coordinator.ChildrenOf(context);
        if (DefinitionOf(context).Children == StepConstants.ChildrenStart)
        {
            await this.FinishAsync(context, $"Started {children.Count} child run(s):\n" + string.Join("\n", children.Select(c => "- " + c.Describe())), ct);
        }
        else if (children.FirstOrDefault(c => c.Status == RunStatus.Failed) is { } failed)
        {
            Fail(context, $"Child run {failed.RunId} ({failed.WorkflowId} on {failed.TicketKey}) failed: {failed.Error}");
        }
        else if (children.FirstOrDefault(c => coordinator.IsFinished(c) && c.HasUnsettledWorkspace) is { } conflicted)
        {
            Fail(context, $"Child run {conflicted.RunId} ({conflicted.Item ?? conflicted.TicketKey}) is done, but: {conflicted.WorkspaceNote}");
        }
        else if (children.All(coordinator.IsFinished))
        {
            await this.FinishAsync(context, Combine(children), ct);
        }
        else
        {
            Move(context, StepStatus.AwaitingChildren);
        }
    }

    /// <summary>
    ///     Finds an existing run of a workflow on a ticket in the same repo to reuse: a done one if there is one,
    ///     otherwise the latest unfinished one. Cancelled and failed runs, and the run's own ancestors, don't count.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="run">The run looking.</param>
    /// <param name="workflowId">The workflow.</param>
    /// <param name="ticketKey">The ticket.</param>
    /// <returns>The run, or null.</returns>
    private static Run? FindReusable(RunEngine engine, Run run, string workflowId, string ticketKey)
    {
        var ancestors = new HashSet<string> { run.Id };
        for (var parent = run.ParentRunId; parent is not null && ancestors.Add(parent);)
        {
            parent = engine.Store.Get(parent)?.ParentRunId;
        }

        return engine.Store.List(run.RepoId)
            .Where(r => r.WorkflowId == workflowId && r.Ticket.Key == ticketKey && !ancestors.Contains(r.Id) &&
                        r.Status is not (RunStatus.Cancelled or RunStatus.Failed))
            .OrderByDescending(r => r.Status == RunStatus.Done)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Combines the children's output: one child's as it is, several under a heading each.
    /// </summary>
    /// <param name="children">The children.</param>
    /// <returns>The output.</returns>
    private static string Combine(IReadOnlyList<ChildRun> children)
    {
        if (children.Count == 1)
        {
            return children[0].Output ?? "Done.";
        }

        return string.Join("\n\n", children.Select(c =>
            string.Format(StepConstants.ChildOutputHeadingFormat, c.Item ?? c.TicketKey, c.RunId) + "\n\n" + (c.Output ?? $"({c.Status.ToString().ToLowerInvariant()})")));
    }
}
