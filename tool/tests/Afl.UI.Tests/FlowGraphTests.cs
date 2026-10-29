using Afl.Core.Workflows;
using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.UI.DataContexts;
using Afl.UI.Flow;
using Avalonia;
using Avalonia.Headless.XUnit;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for the builder's flow graph: nodes and arrows from a draft's steps, their layout, and the edits made by
///     adding steps, choosing the start and drawing arrows.
/// </summary>
public class FlowGraphTests : AbstractUiTest
{
    [AvaloniaFact]
    public void Graph_LaysStepsOutInLayersFromTheStart()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.AddStep();
        var flow = dc.Flow;

        // Only steps: the start is a badge, and a step with no arrow out ends the run.
        Assert.Equal(["Step 1", "Step 2"], flow.Nodes.Select(n => n.Heading));
        Assert.DoesNotContain(flow.End, flow.Nodes);
        Assert.Equal(["step-1→step-2"], Arrows(flow));
        Assert.All(flow.Paths, p => Assert.Equal(2, p.Points.Count));
        var nodes = flow.StepNodes.ToList();
        Assert.Same(nodes[0], flow.StartNode);
        Assert.Contains("▶ start", nodes[0].Badges);
        Assert.False(nodes[0].Ends);
        Assert.True(nodes[1].Ends);
        Assert.True(nodes[1].IsSelected);
        Assert.True(nodes[0].Bounds.Bottom < nodes[1].Bounds.Top);
        Assert.True(flow.Height >= nodes[1].Bounds.Bottom);

        // A fork puts its branches side by side, in the layer after it.
        dc.SelectedStep = null;
        dc.AddStep();
        nodes = flow.StepNodes.ToList();
        Assert.Contains(dc.Warnings, w => w.Contains("'step-3'", StringComparison.Ordinal));
        Assert.True(flow.Connect(nodes[0], nodes[2]));
        Assert.Equal(["step-2", "step-3"], dc.Steps[0].Targets);
        Assert.Empty(dc.Warnings);
        Assert.Contains("⑂ parallel", nodes[0].Badges);
        Assert.Equal(nodes[1].Bounds.Top, nodes[2].Bounds.Top);
        Assert.True(nodes[1].Bounds.Right < nodes[2].Bounds.Left);

        // The step both branches go on to is the join.
        dc.SelectedStep = null;
        dc.AddStep();
        nodes = flow.StepNodes.ToList();
        Assert.True(flow.Connect(nodes[1], nodes[3]));
        Assert.True(flow.Connect(nodes[2], nodes[3]));
        Assert.Contains("⑂ waits for all", nodes[3].Badges);
        Assert.True(nodes[3].Bounds.Top > nodes[1].Bounds.Bottom);
        dc.Steps[3].WaitChoice = ArrowConstants.WaitAny;
        Assert.Contains("⑂ waits for any", nodes[3].Badges);
        Assert.IsType<AnyWaitRule>(dc.Steps[3].ToDefinition().WaitFor);
        dc.Steps[0].Workspace = "worktree";
        Assert.True(dc.Steps[0].ShowsMergeBack);
        dc.Steps[0].MergeBack = false;
        var fork = dc.Steps[0].ToDefinition();
        Assert.Equal("worktree", fork.Workspace);
        Assert.False(fork.MergeBack);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        // Clicking a node edits its step; the end can't be edited.
        flow.Select(nodes[0]);
        Assert.Same(dc.Steps[0], dc.SelectedStep);
        flow.Select(flow.End);
        Assert.Same(dc.Steps[0], dc.SelectedStep);
    }

    [AvaloniaFact]
    public void WaitsFor_BuildsACustomGroupWithANestedOne()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("waits");
        dc.AddStep();
        dc.SelectedStep = null;
        dc.AddStep();
        dc.SelectedStep = null;
        dc.AddStep();
        dc.SelectedStep = null;
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();
        Assert.True(flow.Connect(nodes[0], nodes[2]));
        Assert.True(flow.Connect(nodes[0], nodes[3]));
        Assert.True(flow.Connect(nodes[2], nodes[4]));
        Assert.True(flow.Connect(nodes[3], nodes[4]));
        Assert.True(flow.Connect(nodes[1], nodes[4]));
        var join = dc.Steps[4];
        Assert.False(dc.Steps[2].ShowsWaitFor);
        Assert.True(join.ShowsWaitFor);
        Assert.Equal(["step-2", "step-3", "step-4"], join.IncomingIds);

        // Custom starts from the shorthand it replaces, one item per step with an arrow in.
        join.WaitChoice = ArrowConstants.WaitAny;
        join.WaitChoice = StepDraftDataContext.WaitCustom;
        Assert.True(join.ShowsWaitGroup);
        var group = join.WaitGroup!;
        Assert.Equal(ArrowConstants.WaitAny, group.Kind);
        Assert.Equal("any(step-2, step-3, step-4)", group.Summary);

        // all(step-3, step-4) in place of those two, then at least 1 of the whole lot.
        group.Remove(group.Items[2]);
        group.Remove(group.Items[1]);
        var nested = group.AddGroup();
        Assert.Equal(ArrowConstants.WaitAll, nested.Kind);
        Assert.Equal("step-2", ((WaitStepDraftDataContext)nested.Items[0]).Step);
        ((WaitStepDraftDataContext)nested.Items[0]).Step = "step-3";
        Assert.Equal("step-2", nested.AddStep().Step);
        ((WaitStepDraftDataContext)nested.Items[1]).Step = "step-4";
        Assert.Equal(["step-2", "step-3", "step-4"], ((WaitStepDraftDataContext)nested.Items[1]).StepChoices);
        Assert.Equal("any(step-2, all(step-3, step-4))", group.Summary);
        Assert.Contains("⑂ waits for: custom", flow.StepNodes.ToList()[4].Badges);
        group.Kind = ArrowConstants.WaitAtLeast;
        Assert.True(group.UsesCount);
        group.Count = 2;
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        var json = join.ToDefinition().WaitFor!.ToJson(true).ToJsonString();
        Assert.Equal("""{"atLeast":2,"of":["step-2",{"all":["step-3","step-4"]}]}""", json);

        // Renaming a step follows into the group, and the group survives a switch away and back.
        dc.Steps[3].Id = "test";
        Assert.Equal("2 of (step-2, all(step-3, test))", join.WaitGroup!.Summary);
        join.WaitChoice = ArrowConstants.WaitAll;
        Assert.False(join.ShowsWaitGroup);
        Assert.Null(join.ToDefinition().WaitFor);
        join.WaitChoice = StepDraftDataContext.WaitCustom;
        Assert.Same(group, join.WaitGroup);
    }

    [AvaloniaFact]
    public void MakeConditional_GuardsTheArrowsInAndGoesRoundTheStep()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("conditional");
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();
        Assert.Equal(["step-1→step-2", "step-2→step-3"], Arrows(flow));

        // The start has no arrows in, so there's nothing to go round it from.
        Assert.False(flow.CanMakeConditional(nodes[0]));
        Assert.True(flow.CanMakeConditional(nodes[1]));
        flow.StartConditional(nodes[1]);
        Assert.True(flow.IsConditionalOpen);
        var condition = flow.Conditional!.Condition;
        condition.Contains = "no changes";
        Assert.Equal(2, flow.ApplyConditional());
        Assert.False(flow.IsConditionalOpen);

        var first = dc.Steps[0].ToDefinition().Arrows!;
        Assert.Equal(["step-2", "step-3"], first.Select(a => a.To));
        Assert.Equal(StepConstants.ConditionNot, first[0].When!.Kind);
        Assert.Equal("no changes", first[0].When!.Conditions![0].Contains);
        Assert.Equal("no changes", first[1].When!.Contains);
        Assert.Contains("⑂ waits for all", flow.StepNodes.ToList()[2].Badges);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        Assert.Empty(dc.Warnings);

        // Again, on top of the condition already there: both must hold to run it.
        flow.StartConditional(flow.StepNodes.ToList()[1]);
        flow.Conditional!.Condition.Contains = "docs only";
        Assert.Equal(2, flow.ApplyConditional());
        var arrows = dc.Steps[0].ToDefinition().Arrows!;
        Assert.Equal(2, arrows.Count);
        Assert.Equal(StepConstants.ConditionAll, arrows[0].When!.Kind);
        Assert.Equal(2, arrows[0].When!.Conditions!.Count);
        Assert.Equal(StepConstants.ConditionAny, arrows[1].When!.Kind);
        Assert.Equal(["no changes", "docs only"], arrows[1].When!.Conditions!.Select(c => c.Contains));
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
    }

    [AvaloniaFact]
    public async Task Simulate_HighlightsFiredAndDeadArrows()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("simulate");
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        flow.StartConditional(flow.StepNodes.ToList()[1]);
        flow.Conditional!.Condition.Contains = "no changes";
        Assert.Equal(2, flow.ApplyConditional());
        FlowEdge Edge(string from, string to) => flow.Paths.Single(p => p.Edge.From.Step?.Id == from && p.Edge.To?.Step?.Id == to).Edge;

        // Every conditional arrow going forward is taken to begin with, so every step runs.
        flow.IsSimulating = true;
        Assert.False(flow.CanEdit);
        Assert.Null(flow.Arrow);
        await flow.SimulateAsync();
        var overlay = Assert.IsType<SimulationOverlay>(flow.Overlay);
        Assert.All(flow.StepNodes, n => Assert.Equal(FlowNodeMark.Reached, n.Mark));
        Assert.Equal(ArrowState.Fired, overlay.StateOf(Edge("step-1", "step-2")));
        Assert.Equal("Simulated: 3 step runs; the run ends done.", flow.OverlaySummary);

        // Not taking the arrow into step-2 leaves it out: its arrow on dies, and step-3 still runs.
        Assert.True(await flow.ToggleTakenAsync(Edge("step-1", "step-2")));
        overlay = Assert.IsType<SimulationOverlay>(flow.Overlay);
        var nodes = flow.StepNodes.ToList();
        Assert.Equal(FlowNodeMark.NotReached, nodes[1].Mark);
        Assert.True(nodes[1].IsFaded);
        Assert.Equal("not reached", nodes[1].Status);
        Assert.Equal(ArrowState.Dead, overlay.StateOf(Edge("step-1", "step-2")));
        Assert.Equal(ArrowState.Dead, overlay.StateOf(Edge("step-2", "step-3")));
        Assert.Equal(ArrowState.Fired, overlay.StateOf(Edge("step-1", "step-3")));
        Assert.Equal(FlowNodeMark.Reached, nodes[2].Mark);
        Assert.EndsWith("✗ not taken", overlay.LabelOf(Edge("step-1", "step-2")));

        // A plain arrow can't be flipped; a step that fails stops the run there.
        Assert.False(await flow.ToggleTakenAsync(Edge("step-2", "step-3")));
        await flow.CycleOutcomeAsync(nodes[0]);
        Assert.Equal(FlowTrigger.Failed, nodes[0].Outcome);
        Assert.Equal(FlowNodeMark.Failed, nodes[0].Mark);
        Assert.Equal(FlowNodeMark.NotReached, nodes[2].Mark);
        Assert.Equal("Simulated: 1 step run, then the run fails at 'step-1'.", flow.OverlaySummary);

        // Turning it off clears the overlay and the draft is untouched.
        flow.IsSimulating = false;
        Assert.Null(flow.Overlay);
        Assert.All(flow.StepNodes, n => Assert.Equal(FlowNodeMark.None, n.Mark));
        Assert.True(flow.CanEdit);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
    }

    [AvaloniaFact]
    public void UndoRedo_StepsThroughDropsConnectsAndDeletes()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("history");
        Assert.False(dc.CanUndo);
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        Assert.Equal(["step-1→step-2", "step-2→step-3"], Arrows(flow));
        Assert.True(dc.CanUndo);
        Assert.False(dc.CanRedo);

        // A drop after a selected step (insert plus relink) is one edit.
        Assert.True(dc.Undo());
        Assert.Equal(["step-1→step-2"], Arrows(flow));
        Assert.True(dc.CanRedo);
        Assert.True(dc.Redo());
        Assert.Equal(["step-1→step-2", "step-2→step-3"], Arrows(flow));

        var nodes = flow.StepNodes.ToList();
        Assert.True(flow.Connect(nodes[0], nodes[2]));
        Assert.Contains("step-1→step-3", Arrows(flow));
        flow.Remove(flow.StepNodes.ToList()[1]);
        Assert.Equal(["step-1", "step-3"], dc.Steps.Select(s => s.Id));

        Assert.True(dc.Undo());
        Assert.Equal(["step-1", "step-2", "step-3"], dc.Steps.Select(s => s.Id));
        Assert.Contains("step-1→step-3", Arrows(flow));
        Assert.True(dc.Undo());
        Assert.Equal(["step-1→step-2", "step-2→step-3"], Arrows(flow));

        // A new edit drops what could have been redone.
        Assert.True(dc.CanRedo);
        flow.Connect(flow.StepNodes.ToList()[0], flow.End);
        Assert.False(dc.CanRedo);
        Assert.False(dc.Redo());

        // Typing is one edit, recorded when it's committed (or on undo), not per keystroke.
        dc.DraftName = "H";
        dc.DraftName = "Hi";
        Assert.True(dc.CanUndo);
        Assert.False(dc.CanRedo);
        Assert.True(dc.Undo());
        Assert.NotEqual("Hi", dc.DraftName);
        Assert.True(dc.Redo());
        Assert.Equal("Hi", dc.DraftName);

        // Back at the saved draft, there's nothing unsaved.
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors) + dc.ErrorMessage);
        Assert.NotNull(dc.Save());
        Assert.False(dc.IsDirty);
        dc.Flow.Connect(dc.Flow.StepNodes.ToList()[1], dc.Flow.End);
        Assert.True(dc.IsDirty);
        Assert.True(dc.Undo());
        Assert.False(dc.IsDirty);
        Assert.True(dc.Redo());
        Assert.True(dc.IsDirty);
    }

    [AvaloniaFact]
    public void CopyPaste_KeepsTheInnerArrowsAndRenamesIds()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("copy");
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        dc.Steps[1].AgentPart().Prompt = "Use {{steps.step-1.output}} and {{ticket.title}}.";

        // Pick the first two steps only, by a box around them.
        var nodes = flow.StepNodes.ToList();
        flow.PickIn(nodes[0].Bounds.Union(nodes[1].Bounds), false);
        Assert.Equal(["step-1", "step-2"], flow.PickedSteps.Select(s => s.Id));

        var copied = flow.CopyPicked();
        Assert.NotNull(copied);
        Assert.Equal(2, flow.Paste(copied));
        Assert.Equal(["step-1", "step-2", "step-3", "step-1-copy", "step-2-copy"], dc.Steps.Select(s => s.Id));
        Assert.Equal(["step-1-copy", "step-2-copy"], flow.PickedSteps.Select(s => s.Id));

        // Only the arrow between the copied steps comes along; references inside the copy follow it, others stay.
        var arrows = Arrows(flow);
        Assert.Contains("step-1-copy→step-2-copy", arrows);
        Assert.DoesNotContain(arrows, a => a.StartsWith("step-2-copy→", StringComparison.Ordinal));
        Assert.Equal("Use {{steps.step-1-copy.output}} and {{ticket.title}}.", dc.Steps[4].AgentPart().Prompt);

        // Pasting the same again gives the next suffix, offset further, as one undo step.
        Assert.Equal(2, flow.Paste(copied));
        Assert.Contains("step-1-copy-2→step-2-copy-2", Arrows(flow));
        var first = dc.Steps.Single(s => s.Id == "step-1-copy").Position!;
        var second = dc.Steps.Single(s => s.Id == "step-1-copy-2").Position!;
        Assert.True(second.X > first.X && second.Y > first.Y);
        Assert.True(dc.Undo());
        Assert.Equal(5, dc.Steps.Count);

        // Duplicate copies and pastes the picked steps in one go.
        flow.Select(flow.NodeFor("step-3")!);
        Assert.Equal(1, flow.DuplicatePicked());
        Assert.Contains(dc.Steps, s => s.Id == "step-3-copy");
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
    }

    [AvaloniaFact]
    public void MoveAndTidy_SavePositionsAsOneUndoStep()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("move");
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        Assert.False(flow.HasMovedSteps);
        var node = flow.NodeFor("step-2")!;
        var before = node.Bounds.TopLeft;

        Assert.True(flow.BeginMove(node));
        flow.Move(new Vector(20, 10));
        flow.Move(new Vector(50, 30));
        Assert.Equal(before + new Vector(50, 30), flow.NodeFor("step-2")!.Bounds.TopLeft);
        Assert.Null(dc.Steps[1].Position);
        Assert.True(flow.EndMove());
        Assert.NotNull(dc.Steps[1].Position);
        Assert.True(flow.HasMovedSteps);
        Assert.Null(dc.Steps[0].Position);

        // The whole drag is one edit.
        Assert.True(dc.Undo());
        Assert.Null(dc.Steps[1].Position);
        Assert.False(flow.HasMovedSteps);
        Assert.True(dc.Redo());
        Assert.NotNull(dc.Steps[1].Position);

        // Moving a picked set moves every picked step; Tidy clears every position.
        flow.PickIn(new Rect(0, 0, flow.Width, flow.Height), false);
        Assert.Equal(3, flow.PickedSteps.Count);
        Assert.True(flow.BeginMove(flow.NodeFor("step-1")!));
        flow.Move(new Vector(0, 40));
        Assert.True(flow.EndMove());
        Assert.All(dc.Steps, s => Assert.NotNull(s.Position));
        Assert.True(flow.Tidy());
        Assert.All(dc.Steps, s => Assert.Null(s.Position));
        Assert.False(flow.HasMovedSteps);
    }

    [AvaloniaFact]
    public void RemovePicked_RemovesSeveralStepsAsOneUndoStep()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("remove");
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        flow.Select(flow.NodeFor("step-2")!);
        flow.TogglePicked(flow.NodeFor("step-3")!);
        Assert.Equal(2, flow.RemovePicked());
        Assert.Equal(["step-1"], dc.Steps.Select(s => s.Id));
        Assert.DoesNotContain(Arrows(flow), a => a.Contains("step-2", StringComparison.Ordinal));

        Assert.True(dc.Undo());
        Assert.Equal(["step-1", "step-2", "step-3"], dc.Steps.Select(s => s.Id));
        Assert.Equal(["step-1→step-2", "step-2→step-3"], Arrows(flow));
    }

    [AvaloniaFact]
    public void MakeStart_MovesTheStartBadge()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();

        flow.MakeStart(nodes[1]);
        Assert.Equal("step-2", dc.DraftStart);
        Assert.Same(nodes[1], flow.StartNode);
        Assert.Contains("▶ start", nodes[1].Badges);
        Assert.DoesNotContain("▶ start", nodes[0].Badges);
        Assert.Contains(dc.Warnings, w => w.Contains("'step-1'", StringComparison.Ordinal));

        // The first listed step is the start anyway, so no start is set.
        flow.MakeStart(nodes[0]);
        Assert.Null(dc.DraftStart);
        Assert.Empty(dc.Warnings);
    }

    [AvaloniaFact]
    public void Connect_AddsAnArrow()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.SelectedStep = null;
        dc.AddStep();
        dc.SelectedStep = null;
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();
        var first = dc.Steps[0];

        // Each arrow drawn is one more arrow on success; the same one again is refused.
        Assert.True(flow.Connect(nodes[0], nodes[1]));
        Assert.Equal(["step-2"], first.Targets);
        Assert.False(flow.Connect(nodes[0], nodes[1]));
        Assert.True(flow.Connect(nodes[0], nodes[2]));
        Assert.Equal(["step-2", "step-3"], first.Targets);
        Assert.True(first.IsFork);
        Assert.All(first.Arrows, a => Assert.Equal(ArrowConstants.OnSuccess, a.On));

        // Another outcome to the same step is a different arrow.
        Assert.NotNull(first.AddArrow("step-2", ArrowConstants.OnFailure));
        Assert.Equal(3, first.Arrows.Count);
        Assert.Equal(ArrowConstants.OnFailure, first.ToDefinition().Arrows![2].On);

        // Following only the first makes it no longer a fork, and the arrows can be put in order.
        first.FollowsFirst = true;
        Assert.False(first.IsFork);
        first.MoveArrow(first.Arrows[1], -1);
        Assert.Equal(["step-3", "step-2", "step-2"], first.Arrows.Select(a => a.To));
        Assert.Equal(ArrowConstants.FollowFirst, first.ToDefinition().Follow);
        Assert.StartsWith("1.", flow.Paths.First(p => p.Edge.Arrow == first.Arrows[0]).Edge.Label);

        // An arrow to itself opens the step, since it usually wants a condition.
        flow.IsPropertiesOpen = false;
        dc.SelectedStep = dc.Steps[2];
        Assert.True(flow.Connect(nodes[0], nodes[0]));
        Assert.Same(first, dc.SelectedStep);
        Assert.True(flow.IsPropertiesOpen);
    }

    [AvaloniaFact]
    public void TargetsFrom_OffersStepsAndTheEnd()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();

        Assert.Equal(["Step 1", "Step 2", "End"], flow.TargetsFrom(nodes[0]).Select(n => n.Heading));
        Assert.Empty(flow.TargetsFrom(flow.End));

        // A node can have several arrows; one to the end or to itself is a route.
        Assert.True(flow.Connect(nodes[0], flow.End));
        Assert.True(flow.Connect(nodes[0], nodes[0]));
        Assert.Equal(["step-2", ArrowConstants.End, "step-1"], dc.Steps[0].Arrows.Select(a => a.To));
        Assert.Equal(3, flow.Paths.Count(p => p.Edge.From == nodes[0] && p.Edge.Arrow is not null));
        Assert.True(flow.Paths.Single(p => p.Edge.Arrow?.To == "step-1").Edge.IsBack);
        Assert.Contains(flow.End, flow.Nodes);
    }

    [AvaloniaFact]
    public void Remove_RelinksAroundTheStep()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();

        flow.Remove(nodes[1]);
        Assert.Equal(["step-1", "step-3"], dc.Steps.Select(s => s.Id));
        Assert.Equal(["step-3"], dc.Steps[0].Targets);
        Assert.Equal(["step-1→step-3"], Arrows(flow));

        // Removing the start moves it on.
        flow.MakeStart(nodes[2]);
        flow.MakeStart(nodes[0]);
        flow.Remove(nodes[0]);
        Assert.Same(nodes[2], flow.StartNode);
    }

    [AvaloniaFact]
    public void Arrows_AreDrawnInLanes()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.AddStep();
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();

        // An arrow forward past the next layer runs on the left, labelled with its condition.
        Assert.False(flow.Connect(flow.End, nodes[0]));
        Assert.True(flow.Connect(nodes[0], nodes[3]));
        var jumpArrow = dc.Steps[0].Arrows[1];
        Assert.Equal("step-4", jumpArrow.To);
        jumpArrow.ConditionMode = ArrowDraftDataContext.WhenCondition;
        jumpArrow.Condition.Contains = "SKIP";
        var jump = flow.Paths.Single(p => p.Edge.Arrow == jumpArrow);
        Assert.Equal("when has \"SKIP\"", jump.Edge.Label);
        Assert.Equal(6, jump.Points.Count);
        Assert.True(jump.Points[2].X < flow.ColumnX);
        Assert.True(jump.LabelBefore);
        Assert.Null(flow.Paths.Single(p => p.Edge.Arrow == dc.Steps[0].Arrows[0]).Edge.Label);

        // An arrow to the end brings in the end, below everything, in a lane of its own.
        flow.Connect(nodes[1], flow.End);
        var toEndArrow = dc.Steps[1].Arrows[1];
        toEndArrow.ConditionMode = ArrowDraftDataContext.WhenCondition;
        toEndArrow.Condition.Contains = "DONE";
        Assert.Contains(flow.End, flow.Nodes);
        Assert.True(flow.End.Bounds.Top > nodes[3].Bounds.Bottom);
        var toEnd = flow.Paths.Single(p => p.Edge.Arrow == toEndArrow);
        Assert.True(toEnd.Points[2].X < flow.ColumnX);
        Assert.NotEqual(toEnd.Points[2].X, flow.Paths.Single(p => p.Edge.Arrow == jumpArrow).Points[2].X);

        // An arrow back runs on the right, dashed, with its limit.
        var loopArrow = dc.Steps[2].AddArrow("step-1")!;
        loopArrow.ConditionMode = ArrowDraftDataContext.UntilCondition;
        loopArrow.Condition.Contains = "PASS";
        loopArrow.Max = 3;
        var loop = flow.Paths.Single(p => p.Edge.Arrow == loopArrow);
        Assert.Same(nodes[0], loop.Edge.To);
        Assert.True(loop.Edge.IsBack);
        Assert.True(loop.Edge.Source.IsDashedFor(loop.Edge));
        Assert.Equal("until has \"PASS\" ×3", loop.Edge.Label);
        Assert.True(loop.Points[2].X > flow.ColumnX + flow.ColumnWidth);
        Assert.True(flow.Width > loop.Points[2].X);

        // Arrows come off again.
        Assert.True(flow.Disconnect(flow.Paths.Single(p => p.Edge.Arrow == jumpArrow).Edge));
        Assert.Equal(["step-2"], dc.Steps[0].Targets);
        Assert.True(flow.Disconnect(flow.Paths.Single(p => p.Edge.From == nodes[0]).Edge));
        Assert.Empty(dc.Steps[0].Arrows);
        Assert.True(nodes[0].Ends);

        // Clicking on an arrow finds it.
        loop = flow.Paths.Single(p => p.Edge.Arrow == loopArrow);
        var hit = flow.PathAt(loop.Points[2] + new Avalonia.Vector(0, 5));
        Assert.Same(dc.Steps[2], hit?.Edge.From.Step);

        // So does clicking its label, which sits off the line.
        var label = FlowEdgePath.LabelText(flow.LabelOf(loop.Edge)!, Avalonia.Media.Brushes.Black);
        var box = loop.LabelPlacement(label)!.Value.Box;
        Assert.Same(loop.Edge.Arrow, flow.PathAt(box.TopLeft + new Avalonia.Vector(2, 2))?.Edge.Arrow);
    }

    [AvaloniaFact]
    public void Arrows_OpenTheirOwnEditor_AndRepoint()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.AddStep();
        dc.AddStep();
        var flow = dc.Flow;
        var nodes = flow.StepNodes.ToList();
        var first = dc.Steps[0];

        // Clicking an arrow opens its editor, marks the arrow, and puts the handle on its end.
        flow.OpenArrow(flow.Paths.Single(p => p.Edge.From == nodes[0]).Edge);
        var arrow = Assert.IsType<ArrowDataContext>(flow.Arrow);
        Assert.True(flow.IsArrowOpen);
        Assert.Equal("Arrow", arrow.Title);
        Assert.False(arrow.IsLoop);
        Assert.Equal("step-2", arrow.Target);
        Assert.Equal(["step-1", "step-2", "step-3", ArrowConstants.End], arrow.TargetChoices);
        Assert.Same(nodes[1], flow.SelectedPath!.Edge.To);
        Assert.True(flow.HasArrowHandle);
        Assert.Equal(flow.SelectedPath.Points[^1].X, flow.ArrowHandleX);

        // Choosing where it goes, or dropping its end on another step, repoints it; the editor stays open.
        arrow.Target = "step-3";
        Assert.Equal(["step-3"], first.Targets);
        Assert.Same(arrow, flow.Arrow);
        Assert.Same(nodes[2], flow.SelectedPath!.Edge.To);
        Assert.False(flow.Retarget(nodes[2]));
        Assert.True(flow.Retarget(nodes[1]));
        Assert.Equal(["step-2"], first.Targets);
        Assert.Same(nodes[1], flow.SelectedPath!.Edge.To);

        // Dropped on its own step, it's a loop; dropped forward again, it isn't.
        Assert.True(flow.Retarget(nodes[0]));
        Assert.True(arrow.IsLoop);
        Assert.Equal("Loop", arrow.Title);
        Assert.Equal("step-1", Assert.Single(first.Arrows).To);
        Assert.True(flow.Retarget(nodes[2]));
        Assert.False(arrow.IsLoop);
        Assert.Equal("step-3", first.Arrows[0].To);

        // Its outcome and condition are the arrow's own.
        arrow.Arrow.On = ArrowConstants.OnFailure;
        arrow.Arrow.ConditionMode = ArrowDraftDataContext.WhenCondition;
        arrow.Arrow.Condition.Contains = "FLAKY";
        var definition = Assert.Single(first.ToDefinition().Arrows!);
        Assert.Equal(ArrowConstants.OnFailure, definition.On);
        Assert.NotNull(definition.When);
        Assert.Null(definition.Until);

        // Removing the arrow closes the editor.
        flow.RemoveArrow();
        Assert.Empty(first.Arrows);
        Assert.False(flow.IsArrowOpen);
        Assert.Null(flow.SelectedPath);
        Assert.False(flow.HasArrowHandle);

        // A loop's end goes back to another step, and comes off the same way.
        Assert.True(flow.Connect(nodes[0], nodes[1]));
        var last = dc.Steps[2];
        var back = last.AddArrow("step-1")!;
        back.ConditionMode = ArrowDraftDataContext.UntilCondition;
        back.Condition.Contains = "PASS";
        flow.OpenArrow(flow.Paths.Single(p => p.Edge.Arrow == back).Edge);
        Assert.True(Assert.IsType<ArrowDataContext>(flow.Arrow).IsLoop);
        Assert.True(flow.Retarget(nodes[1]));
        Assert.Equal("step-2", back.To);
        Assert.Same(nodes[1], flow.SelectedPath!.Edge.To);
        flow.RemoveArrow();
        Assert.Empty(last.Arrows);

        // The editor closes when its arrow is gone, or a step is clicked.
        flow.OpenArrow(flow.Paths.Single(p => p.Edge.From == nodes[1]).Edge);
        Assert.True(flow.IsArrowOpen);
        dc.Steps[1].RemoveArrow(dc.Steps[1].Arrows[0]);
        Assert.False(flow.IsArrowOpen);
        Assert.False(flow.Connect(nodes[0], nodes[1]));
        flow.OpenArrow(flow.Paths.Single().Edge);
        Assert.True(flow.IsArrowOpen);
        flow.Select(nodes[2]);
        Assert.False(flow.IsArrowOpen);
    }

    [AvaloniaFact]
    public void AddStep_OffersStepTypesAndPickers()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        var flow = dc.Flow;
        Assert.Equal(["AI step", "Interactive session", "Manual step", "Run a workflow"], flow.StepTypes.Select(t => t.Label));
        Assert.True(flow.HasCommonSteps);
        Assert.True(flow.HasIncludes);

        // Step types make new steps with unique ids, which open their properties.
        var ai = flow.StepTypes.Single(i => i.Type == StepConstants.TypeAi);
        flow.IsPropertiesOpen = false;
        var first = flow.Drop(ai, 0);
        var second = flow.Drop(ai, 0);
        Assert.Equal("step", first.Id);
        Assert.Equal("step-2", second.Id);
        Assert.Equal(["step-2", "step", "step-1"], dc.Steps.Select(s => s.Id));
        Assert.Same(second, dc.SelectedStep);
        Assert.True(dc.ShowsStepPanel);
        Assert.Equal(StepConstants.ReadOnlyPermissionMode, Assert.IsAssignableFrom<AbstractAgentStepDefinition>(second.ToDefinition()).PermissionMode);

        // The common steps picker searches and filters by tag; picking one uses it, not copies it.
        flow.ShowCommonSteps();
        var picker = Assert.IsType<FlowPickerDataContext>(flow.Picker);
        Assert.True(flow.IsPickerOpen);
        picker.Search = "zzz-nothing";
        Assert.True(picker.IsEmpty);
        picker.Search = "code-review";
        var review = Assert.IsType<CommonStepPaletteItem>(picker.Items.First(i => i.Key == "code-review"));
        picker.Search = null;
        var tag = picker.Tags.Choices.First(t => review.Tags.Contains(t.Tag)).Tag;
        Assert.True(picker.Tags.Add(tag));
        Assert.All(picker.Items, i => Assert.Contains(tag, i.Tags));
        Assert.DoesNotContain(picker.Tags.Choices, c => c.Tag == tag);
        Assert.Contains(review, picker.Items);
        var used = flow.Add(review);
        Assert.False(flow.IsPickerOpen);
        Assert.Equal(1, dc.Steps.IndexOf(used));
        Assert.Equal("code-review", used.ToDefinition().Use);

        // The include picker leaves out the draft itself; closing it adds nothing.
        flow.ShowIncludes();
        Assert.DoesNotContain(flow.Picker!.Items, i => i.Key == "graph");
        flow.ClosePicker();
        Assert.Null(flow.Picker);
        flow.ShowIncludes();
        var bugfix = flow.Picker!.Items.OfType<IncludePaletteItem>().First(i => i.Workflow.Id == "bugfix");
        dc.SelectedStep = dc.Steps[^1];
        var included = flow.Add(bugfix);
        Assert.Equal("bugfix", included.ToDefinition().Include);
        Assert.Equal("includes bugfix", flow.NodeFor(included.Id)!.Detail);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
    }

    [AvaloniaFact]
    public void Properties_OpenOnClickAndClose()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        dc.AddStep();
        var flow = dc.Flow;

        // A newly opened draft shows only the flow.
        dc.New("other");
        Assert.False(flow.IsPropertiesOpen);
        Assert.False(dc.ShowsStepPanel);
        flow.Select(flow.StepNodes.First());
        Assert.True(dc.ShowsStepPanel);
        flow.IsPropertiesOpen = false;
        Assert.False(dc.ShowsStepPanel);
        flow.Select(flow.End);
        Assert.False(dc.ShowsStepPanel);

        // A common step has no flow, so its properties always show.
        using var common = new LibraryPageDataContext(shell, new CommonStepLibraryEditor());
        common.New("solo");
        Assert.True(common.ShowsStepPanel);
    }

    [AvaloniaFact]
    public void Nodes_ShowBadgesAndErrors()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("graph");
        var step = dc.SelectedStep!;
        step.IsOptional = true;
        step.Advance = StepConstants.AdvanceApprove;
        var node = dc.Flow.StepNodes.Single();
        Assert.Contains("optional", node.Badges);
        Assert.Contains(StepConstants.AdvanceApprove, node.Badges);
        Assert.False(node.HasError);

        dc.AddStep();
        dc.SelectedStep!.Id = step.Id;
        Assert.False(dc.IsValid);
        Assert.Contains(dc.Flow.StepNodes, n => n.HasError);
    }

    /// <summary>
    ///     Describes the graph's arrows as "from→to" by heading or id.
    /// </summary>
    /// <param name="flow">The graph.</param>
    /// <returns>The arrows.</returns>
    private static List<string> Arrows(FlowGraphDataContext flow)
    {
        return flow.Paths.Select(p => $"{p.Edge.From.Id ?? p.Edge.From.Heading}→{p.Edge.To!.Id ?? p.Edge.To.Heading}").ToList();
    }
}
