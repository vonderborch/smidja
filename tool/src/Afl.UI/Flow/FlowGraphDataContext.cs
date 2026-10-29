using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using Afl.UI.DataContexts;
using Avalonia;
using Avalonia.Media;
using ReactiveUI;

namespace Afl.UI.Flow;

/// <summary>
///     The workflow builder's flow graph: the draft's steps as nodes, with the arrows their flow draws (see
///     <see cref="AbstractFlowEdgeSource" />), laid out in layers from the step the workflow starts at. A step's layer is
///     the longest way to it from the start (leaving out the arrows that loop back), so every arrow forward goes down the
///     page; steps in the same layer sit side by side in the order they're listed. Arrows to the next layer go straight
///     down through the gap between them; longer arrows forward run in lanes on the left, and arrows back (loops and
///     back) in lanes on the right. An end node shows only when an arrow goes to <c>end</c>; a step with no arrow on
///     to another step ends the run, and says so under it. It also holds what can be added (the Add step menu's step types and
///     the pickers for common steps and workflows to include), whether the selected step's properties are showing, and
///     turns what the editor does with the mouse into edits of the draft through its <see cref="LibraryPageDataContext" />.
///     Clicking an arrow opens its mini editor (<see cref="Arrow" />), whose arrow can be repointed by dragging its end.
///     A step moved by hand keeps its place (<see cref="StepDraftDataContext.Position" />) and the rest are laid out
///     around it; <see cref="Tidy" /> lays them all out again. Several steps can be picked (box-select, ⇧-click) to move,
///     delete, copy and paste together.
/// </summary>
public class FlowGraphDataContext : ReactiveObject
{
    /// <summary>
    ///     Gets the width of a step's node.
    /// </summary>
    public const double NodeWidth = 220;

    /// <summary>
    ///     Gets the height of a step's node.
    /// </summary>
    public const double NodeHeight = 62;

    /// <summary>
    ///     Gets the height of the end node.
    /// </summary>
    public const double TerminalHeight = 28;

    /// <summary>
    ///     Gets the width of the end node.
    /// </summary>
    public const double TerminalWidth = 96;

    /// <summary>
    ///     Gets the space between one layer and the next.
    /// </summary>
    public const double RowGap = 46;

    /// <summary>
    ///     Gets the space between steps side by side in a layer.
    /// </summary>
    public const double ColumnGap = 36;

    /// <summary>
    ///     Gets the space between the lanes side arrows run in.
    /// </summary>
    public const double LaneGap = 18;

    /// <summary>
    ///     Gets the room left beside the outermost lane for arrow labels.
    /// </summary>
    public const double LabelRoom = 110;

    /// <summary>
    ///     Gets the space around the graph.
    /// </summary>
    public const double Margin = 24;

    /// <summary>
    ///     The accent colours step types are drawn with, picked by a stable hash of the type so a new type gets one too.
    /// </summary>
    private static readonly Color[] Accents =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x8B, 0x5C, 0xF6),
        Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x06, 0xB6, 0xD4),
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x84, 0xCC, 0x16),
    ];

    /// <summary>
    ///     The builder this graph edits the draft of.
    /// </summary>
    private readonly LibraryPageDataContext _owner;

    /// <summary>
    ///     The nodes by the step they stand for, kept across refreshes.
    /// </summary>
    private readonly Dictionary<StepDraftDataContext, FlowNodeDataContext> _nodes = [];

    /// <summary>
    ///     The property copied steps are kept under in the clipboard text, so other text isn't pasted as steps.
    /// </summary>
    private const string ClipboardProperty = "aflSteps";

    /// <summary>
    ///     How far each paste of the same steps is moved from the last, down and to the right.
    /// </summary>
    private const double PasteOffset = 40;

    /// <summary>
    ///     The steps picked on the canvas, the selected step among them.
    /// </summary>
    private readonly HashSet<StepDraftDataContext> _picked = [];

    /// <summary>
    ///     The steps being moved, with where each was when the move began, in draft coordinates.
    /// </summary>
    private readonly Dictionary<StepDraftDataContext, Point> _moving = [];

    /// <summary>
    ///     The arrows of the last refresh, to lay out again while steps are moved.
    /// </summary>
    private IReadOnlyList<FlowEdge> _edges = [];

    /// <summary>
    ///     How far the moving steps have been moved so far.
    /// </summary>
    private Vector _moveBy;

    /// <summary>
    ///     How far the graph is drawn from draft coordinates, so steps moved up or left (and the lanes beside them) stay
    ///     on the canvas.
    /// </summary>
    private Vector _shift;

    /// <summary>
    ///     The last steps copied, for pasting without the system clipboard.
    /// </summary>
    private string? _copied;

    /// <summary>
    ///     The text pasted last, and how many times in a row, to place each paste a little further on.
    /// </summary>
    private (string Text, int Count) _lastPaste = (string.Empty, 0);

    /// <summary>
    ///     How each step ends in the simulation, by id; a step not in it succeeds.
    /// </summary>
    private readonly Dictionary<string, FlowTrigger> _outcomes = [];

    /// <summary>
    ///     Whether each conditional arrow or arrow back is taken in the simulation, by key; one not in it is taken if it
    ///     goes forward.
    /// </summary>
    private readonly Dictionary<string, bool> _taken = [];

    /// <summary>
    ///     How many simulations have been started, so only the latest one's result is shown.
    /// </summary>
    private int _simulations;

    /// <summary>
    ///     The common steps the draft can use.
    /// </summary>
    private IReadOnlyList<AbstractFlowPaletteItem> _commonSteps = [];

    /// <summary>
    ///     The workflows the draft can include.
    /// </summary>
    private IReadOnlyList<AbstractFlowPaletteItem> _includes = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="FlowGraphDataContext" /> class.
    /// </summary>
    /// <param name="owner">The builder whose draft the graph shows and edits.</param>
    public FlowGraphDataContext(LibraryPageDataContext owner)
    {
        this._owner = owner;
        this.End = new FlowNodeDataContext(null, "End");
        this.SetLibrary([], [], new HashSet<string>());
    }

    /// <summary>
    ///     Raised after the graph is laid out again, so its arrows are redrawn.
    /// </summary>
    public event EventHandler? LayoutChanged;

    /// <summary>
    ///     Gets the end node, where arrows to <c>end</c> go; it's in <see cref="Nodes" /> only while one does.
    /// </summary>
    public FlowNodeDataContext End { get; }

    /// <summary>
    ///     Gets every node: the steps in the order they're listed, then the end node if an arrow goes there.
    /// </summary>
    public ObservableCollection<FlowNodeDataContext> Nodes { get; } = [];

    /// <summary>
    ///     Gets the steps' nodes, in the order they're listed.
    /// </summary>
    public IEnumerable<FlowNodeDataContext> StepNodes => this.Nodes.Where(n => n.Step is not null);

    /// <summary>
    ///     Gets the node of the step the workflow starts at, if it has steps.
    /// </summary>
    public FlowNodeDataContext? StartNode => this._owner.StartStepId is { } id ? this.NodeFor(id) : null;

    /// <summary>
    ///     Gets the steps picked on the canvas, in the order they're listed.
    /// </summary>
    public IReadOnlyList<StepDraftDataContext> PickedSteps => [.. this._owner.Steps.Where(this._picked.Contains)];

    /// <summary>
    ///     Gets whether any step was moved by hand, so <see cref="Tidy" /> has something to do.
    /// </summary>
    public bool HasMovedSteps
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the arrows, laid out.
    /// </summary>
    public IReadOnlyList<FlowEdgePath> Paths { get; private set; } = [];

    /// <summary>
    ///     Gets the graph's width.
    /// </summary>
    public double Width
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = NodeWidth + (2 * Margin);

    /// <summary>
    ///     Gets the graph's height.
    /// </summary>
    public double Height
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the x of the left edge of the area the nodes are in, right of the lanes on the left.
    /// </summary>
    public double ColumnX { get; private set; } = Margin;

    /// <summary>
    ///     Gets the width of the area the nodes are in: the widest layer.
    /// </summary>
    public double ColumnWidth { get; private set; } = NodeWidth;

    /// <summary>
    ///     Gets the new steps the Add step menu offers, one per step type people can use.
    /// </summary>
    public IReadOnlyList<StepTypePaletteItem> StepTypes { get; } =
        StepHandlerRegistry.Instance.PublicKeys.Order(StringComparer.Ordinal).Select(t => new StepTypePaletteItem(t)).ToList();

    /// <summary>
    ///     Gets whether there are common steps to pick from.
    /// </summary>
    public bool HasCommonSteps => this._commonSteps.Count > 0;

    /// <summary>
    ///     Gets whether there are workflows to include.
    /// </summary>
    public bool HasIncludes => this._includes.Count > 0;

    /// <summary>
    ///     Gets the open picker, or null when none is.
    /// </summary>
    public FlowPickerDataContext? Picker
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsPickerOpen));
        }
    }

    /// <summary>
    ///     Gets whether a picker is open.
    /// </summary>
    public bool IsPickerOpen => this.Picker is not null;

    /// <summary>
    ///     Gets or sets whether the selected step's properties are showing beside the flow. Clicking a node or adding a
    ///     step opens them; they close with their close button or when another draft is opened.
    /// </summary>
    public bool IsPropertiesOpen
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this._owner.RaisePropertyChanged(nameof(LibraryPageDataContext.ShowsStepPanel));
        }
    }

    /// <summary>
    ///     Gets the open arrow editor, for the arrow last clicked, or null when none is open.
    /// </summary>
    public AbstractFlowArrowDataContext? Arrow
    {
        get;
        internal set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsArrowOpen));
            this.FindSelectedPath();
            this.LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     Gets whether an arrow editor is open.
    /// </summary>
    public bool IsArrowOpen => this.Arrow is not null;

    /// <summary>
    ///     Gets the open "make this step conditional" editor, or null when none is open.
    /// </summary>
    public ConditionalStepDataContext? Conditional
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsConditionalOpen));
        }
    }

    /// <summary>
    ///     Gets whether the "make this step conditional" editor is open.
    /// </summary>
    public bool IsConditionalOpen => this.Conditional is not null;

    /// <summary>
    ///     Gets the open arrow editor's arrow, laid out, so it's drawn highlighted with a handle at its end.
    /// </summary>
    public FlowEdgePath? SelectedPath
    {
        get;
        private set
        {
            field = value;
            this.RaisePropertyChanged(nameof(this.HasArrowHandle));
            this.RaisePropertyChanged(nameof(this.ArrowHandleX));
            this.RaisePropertyChanged(nameof(this.ArrowHandleY));
        }
    }

    /// <summary>
    ///     Gets whether the open arrow has a handle to drag its end by.
    /// </summary>
    public bool HasArrowHandle => this.SelectedPath is not null;

    /// <summary>
    ///     Gets the x of the open arrow's end, where its handle is.
    /// </summary>
    public double ArrowHandleX => this.SelectedPath?.Points[^1].X ?? 0;

    /// <summary>
    ///     Gets the y of the open arrow's end, where its handle is.
    /// </summary>
    public double ArrowHandleY => this.SelectedPath?.Points[^1].Y ?? 0;

    /// <summary>
    ///     Gets or sets the arrow being drawn from a node, for the editor to show while the mouse is down.
    /// </summary>
    public (FlowNodeDataContext From, Point To)? PendingConnection
    {
        get;
        set
        {
            field = value;
            this.LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     Gets or sets whether the editor is simulating a run: each step gets an outcome to pick, each arrow that might
    ///     not be taken can be clicked to take it or not, and the graph shows how the run would go
    ///     (<see cref="SimulationOverlay" />). Editing the draft simulates again.
    /// </summary>
    public bool IsSimulating
    {
        get;
        set
        {
            if (field == value || (value && this.IsReadOnly))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.CanEdit));
            if (value)
            {
                this.Arrow = null;
                this.Conditional = null;
                this.Picker = null;
                _ = this.SimulateAsync();
            }
            else
            {
                this.Overlay = null;
            }

            this.ApplyMode();
        }
    }

    /// <summary>
    ///     Gets or sets whether the graph only shows a workflow, e.g. a run's on the Runs page: nothing can be added,
    ///     moved, connected or simulated, and clicking a step only selects it.
    /// </summary>
    public bool IsReadOnly
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.CanEdit));
            this.ApplyMode();
        }
    }

    /// <summary>
    ///     Gets whether steps and arrows can be added, moved, connected or removed on the canvas: not while simulating
    ///     or when the graph is read-only.
    /// </summary>
    public bool CanEdit => !this.IsSimulating && !this.IsReadOnly;

    /// <summary>
    ///     Gets or sets what is drawn over the graph to show how a run goes, or null for nothing.
    /// </summary>
    public AbstractFlowOverlay? Overlay
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.OverlaySummary));
            this.RaisePropertyChanged(nameof(this.HasOverlay));
            this.ApplyOverlay();
            this.LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     Gets whether an overlay is drawn.
    /// </summary>
    public bool HasOverlay => this.Overlay is not null;

    /// <summary>
    ///     Gets the overlay's line about the run as a whole.
    /// </summary>
    public string? OverlaySummary => this.Overlay?.Summary;

    /// <summary>
    ///     Raised when a step is clicked, with its id.
    /// </summary>
    public event EventHandler<string>? StepClicked;

    /// <summary>
    ///     Gives the palette what the library holds.
    /// </summary>
    /// <param name="commonSteps">The common steps.</param>
    /// <param name="workflows">The workflows.</param>
    /// <param name="excluded">The workflows the draft can't include: itself, and those that lead back to it.</param>
    public void SetLibrary(IReadOnlyList<CommonStepDefinition> commonSteps, IReadOnlyList<WorkflowDefinition> workflows, IReadOnlySet<string> excluded)
    {
        this._commonSteps = commonSteps.Select(c => new CommonStepPaletteItem(c)).ToList();
        this._includes = workflows.Where(w => !excluded.Contains(w.Id)).Select(w => new IncludePaletteItem(w)).ToList();
        this.RaisePropertyChanged(nameof(this.HasCommonSteps));
        this.RaisePropertyChanged(nameof(this.HasIncludes));
    }

    /// <summary>
    ///     Opens the picker of common steps.
    /// </summary>
    public void ShowCommonSteps()
    {
        this.Picker = new FlowPickerDataContext("Add a common step", this._commonSteps);
    }

    /// <summary>
    ///     Opens the picker of workflows to include.
    /// </summary>
    public void ShowIncludes()
    {
        this.Picker = new FlowPickerDataContext("Include a workflow's steps", this._includes);
    }

    /// <summary>
    ///     Closes the picker without adding anything.
    /// </summary>
    public void ClosePicker()
    {
        this.Picker = null;
    }

    /// <summary>
    ///     Adds an item after the selected step, or unconnected, closing the picker and showing the new step.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The new step.</returns>
    public StepDraftDataContext Add(AbstractFlowPaletteItem item)
    {
        this.Picker = null;
        return this.DropAfterSelection(item);
    }

    /// <summary>
    ///     Rebuilds the nodes from the draft's steps and lays the graph out again.
    /// </summary>
    public void Refresh()
    {
        var steps = this._owner.Steps.ToList();
        foreach (var gone in this._nodes.Keys.Except(steps).ToList())
        {
            this._nodes.Remove(gone);
        }

        var nodes = steps.Select(s => this._nodes.TryGetValue(s, out var node) ? node : this._nodes[s] = new FlowNodeDataContext(s, s.Id)).ToList();
        var edges = new List<FlowEdge>();
        foreach (var node in nodes)
        {
            edges.AddRange(FlowEdgeSourceRegistry.Instance.Sources.SelectMany(s => s.EdgesFrom(this, node)));
        }

        edges.RemoveAll(e => e.To is null);
        if (edges.Any(e => e.To == this.End))
        {
            nodes.Add(this.End);
        }

        if (!this.Nodes.SequenceEqual(nodes))
        {
            this.Nodes.Clear();
            foreach (var node in nodes)
            {
                this.Nodes.Add(node);
            }
        }

        this._picked.IntersectWith(steps);
        if (this._owner.SelectedStep is { } selected && !this._picked.Contains(selected))
        {
            this._picked.Clear();
            this._picked.Add(selected);
        }

        var errors = this._owner.Errors.ToList();
        var start = this._owner.StartStepId;
        var joins = this.Joins();
        foreach (var node in this.StepNodes)
        {
            var id = node.Step!.Id.Trim();
            node.Update(AccentFor(node.Step), this._picked.Contains(node.Step),
                errors.Any(e => e.Contains($"'{id}'", StringComparison.Ordinal)), id == start, joins.GetValueOrDefault(id));
        }

        this._edges = edges;
        this.HasMovedSteps = steps.Any(s => s.Position is not null);
        this.Layout(edges);
        foreach (var node in this.StepNodes)
        {
            node.Outcome = this._outcomes.GetValueOrDefault(node.Step!.Id.Trim());
        }

        this.ApplyMode();

        this.ApplyOverlay();
        if (this.IsSimulating)
        {
            _ = this.SimulateAsync();
        }
    }

    /// <summary>
    ///     Simulates a run of the draft with the outcomes and arrows picked so far, and shows it.
    /// </summary>
    /// <returns>A task that completes once the simulation is shown.</returns>
    public async Task SimulateAsync()
    {
        var simulation = ++this._simulations;
        var choices = new Dictionary<string, bool>();
        foreach (var edge in this._edges.Where(e => e.Arrow is { } arrow && (arrow.HasCondition || e.IsBack)))
        {
            if (AbstractFlowOverlay.KeyOf(edge) is { } key)
            {
                choices[key] = this._taken.TryGetValue(key, out var taken) ? taken : !edge.IsBack;
            }
        }

        try
        {
            var result = await new FlowSimulator(this._outcomes, choices).SimulateAsync(this._owner.BuildDraft(), new WorkflowComposer(this._owner.Library()));
            if (simulation == this._simulations && this.IsSimulating)
            {
                this.Overlay = new SimulationOverlay(result, choices);
            }
        }
        catch (Exception ex) when (ex is AflException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            if (simulation == this._simulations && this.IsSimulating)
            {
                var failed = new FlowSimulation { Failed = true, Error = ex.Message };
                this.Overlay = new SimulationOverlay(failed, choices);
            }
        }
    }

    /// <summary>
    ///     Moves a step on to the next way it can end in the simulation: success, failure, timeout, left out, then
    ///     success again.
    /// </summary>
    /// <param name="node">The step's node.</param>
    /// <returns>A task that completes once the simulation is shown again.</returns>
    public Task CycleOutcomeAsync(FlowNodeDataContext node)
    {
        if (node.Step is not { } step)
        {
            return Task.CompletedTask;
        }

        var id = step.Id.Trim();
        var outcomes = Enum.GetValues<FlowTrigger>();
        var next = outcomes[(Array.IndexOf(outcomes, this._outcomes.GetValueOrDefault(id)) + 1) % outcomes.Length];
        return this.SetOutcomeAsync(node, next);
    }

    /// <summary>
    ///     Sets how a step ends in the simulation.
    /// </summary>
    /// <param name="node">The step's node.</param>
    /// <param name="outcome">How it ends.</param>
    /// <returns>A task that completes once the simulation is shown again.</returns>
    public Task SetOutcomeAsync(FlowNodeDataContext node, FlowTrigger outcome)
    {
        if (node.Step is not { } step || !this.IsSimulating)
        {
            return Task.CompletedTask;
        }

        var id = step.Id.Trim();
        if (outcome == FlowTrigger.Succeeded)
        {
            this._outcomes.Remove(id);
        }
        else
        {
            this._outcomes[id] = outcome;
        }

        node.Outcome = outcome;
        return this.SimulateAsync();
    }

    /// <summary>
    ///     Takes an arrow in the simulation if it wasn't taken, or stops taking it if it was. Only an arrow with a
    ///     condition, or one going back, can be either.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>True if the arrow can be either, and the simulation is shown again.</returns>
    public async Task<bool> ToggleTakenAsync(FlowEdge edge)
    {
        if (!this.IsSimulating || edge.Arrow is not { } arrow || (!arrow.HasCondition && !edge.IsBack) || AbstractFlowOverlay.KeyOf(edge) is not { } key)
        {
            return false;
        }

        this._taken[key] = !(this._taken.TryGetValue(key, out var taken) ? taken : !edge.IsBack);
        await this.SimulateAsync();
        return true;
    }

    /// <summary>
    ///     Shows on each step whether it can be connected from and whether it shows its simulated outcome.
    /// </summary>
    private void ApplyMode()
    {
        foreach (var node in this.StepNodes)
        {
            node.ShowsOutcome = this.IsSimulating;
            node.CanConnect = this.CanEdit;
        }
    }

    /// <summary>
    ///     Copies how each step went from the overlay onto its node.
    /// </summary>
    private void ApplyOverlay()
    {
        foreach (var node in this.StepNodes)
        {
            var id = node.Step!.Id.Trim();
            node.ShowOverlay(this.Overlay?.MarkOf(id) ?? FlowNodeMark.None, this.Overlay?.StatusOf(id));
        }
    }

    /// <summary>
    ///     Returns the node of the step with an id, the end node for <c>end</c>, or null if there's no such step.
    /// </summary>
    /// <param name="id">The step id.</param>
    /// <returns>The node.</returns>
    public FlowNodeDataContext? NodeFor(string id)
    {
        if (id == ArrowConstants.End)
        {
            return this.End;
        }

        var step = this._owner.Steps.FirstOrDefault(s => s.Id.Trim() == id.Trim());
        return step is not null && this._nodes.TryGetValue(step, out var node) ? node : null;
    }

    /// <summary>
    ///     Returns the node under a point, if any.
    /// </summary>
    /// <param name="point">The point, in graph coordinates.</param>
    /// <returns>The node.</returns>
    public FlowNodeDataContext? NodeAt(Point point)
    {
        return this.Nodes.FirstOrDefault(n => n.Bounds.Contains(point));
    }

    /// <summary>
    ///     Adds the step a palette item writes, listed at an index but not connected to anything, and selects it.
    /// </summary>
    /// <param name="item">The palette item.</param>
    /// <param name="index">Where it's listed in the draft's steps.</param>
    /// <returns>The new step.</returns>
    public StepDraftDataContext Drop(AbstractFlowPaletteItem item, int index)
    {
        var ids = this._owner.Steps.Select(s => s.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        var id = item.BaseId;
        for (var n = 2; ids.Contains(id); n++)
        {
            id = $"{item.BaseId}-{n}";
        }

        var step = this._owner.InsertStep(item.CreateStep(id), index, item.HasPlaceholders);
        this.IsPropertiesOpen = true;
        return step;
    }

    /// <summary>
    ///     Adds the step a palette item writes after the selected step (between it and where it went on to), or
    ///     unconnected at the end when no step is selected, and selects it.
    /// </summary>
    /// <param name="item">The palette item.</param>
    /// <returns>The new step.</returns>
    public StepDraftDataContext DropAfterSelection(AbstractFlowPaletteItem item)
    {
        return this._owner.Change(() =>
        {
            var selected = this._owner.SelectedStep;
            var step = this.Drop(item, selected is null ? this._owner.Steps.Count : this._owner.Steps.IndexOf(selected) + 1);
            this._owner.LinkAfter(selected, step);
            return step;
        });
    }

    /// <summary>
    ///     Removes a step; the steps that went on to it go on to where it went, if that was one step.
    /// </summary>
    /// <param name="node">The step's node.</param>
    public void Remove(FlowNodeDataContext node)
    {
        if (node.Step is null)
        {
            return;
        }

        this._owner.SelectedStep = node.Step;
        this._owner.RemoveStep();
    }

    /// <summary>
    ///     Makes a step the one the workflow starts at.
    /// </summary>
    /// <param name="node">The step's node.</param>
    public void MakeStart(FlowNodeDataContext node)
    {
        if (node.Step is not null)
        {
            this._owner.MakeStart(node.Step);
        }
    }

    /// <summary>
    ///     Draws an arrow from one step to another node, unless the step already has one there on success. An arrow to
    ///     itself or to the end also selects the step, since it usually wants a condition.
    /// </summary>
    /// <param name="from">The step's node.</param>
    /// <param name="to">The node it goes to.</param>
    /// <returns>True if the draft changed; false if the step already has that arrow.</returns>
    public bool Connect(FlowNodeDataContext from, FlowNodeDataContext to)
    {
        if (from.Step is not { } step)
        {
            return false;
        }

        if (this._owner.Change(() => step.AddArrow(to.Step?.Id.Trim() ?? ArrowConstants.End)) is null)
        {
            return false;
        }

        if (to == from || to.Step is null)
        {
            this._owner.SelectedStep = step;
            this.IsPropertiesOpen = true;
        }

        return true;
    }

    /// <summary>
    ///     Returns whether an arrow from a step to another goes back: to its own step, or to one before it.
    /// </summary>
    /// <param name="from">The step's node.</param>
    /// <param name="to">Where the arrow goes: a step id, or <c>end</c>.</param>
    /// <returns>True if it goes back.</returns>
    public bool IsBack(FlowNodeDataContext from, string to)
    {
        var id = from.Step?.Id.Trim();
        return id is not null && to != ArrowConstants.End && (to == id || (this._owner.Machine?.IsBackEdge(id, to) ?? false));
    }

    /// <summary>
    ///     Returns the nodes an arrow from a step can be drawn to: every step (itself included, to run it again) and
    ///     the end.
    /// </summary>
    /// <param name="from">The step's node.</param>
    /// <returns>The nodes, in the order the steps are listed.</returns>
    public IReadOnlyList<FlowNodeDataContext> TargetsFrom(FlowNodeDataContext from)
    {
        return from.Step is null ? [] : [.. this.StepNodes, this.End];
    }

    /// <summary>
    ///     Removes the step's arrow that a drawn arrow stands for.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>True if it stood for one.</returns>
    public bool Disconnect(FlowEdge edge)
    {
        if (edge.From.Step is not { } step)
        {
            return false;
        }

        if (edge.Arrow is { } arrow)
        {
            this._owner.Change(() => step.RemoveArrow(arrow));
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Opens the mini editor for an arrow.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    public void OpenArrow(FlowEdge edge)
    {
        this.Conditional = null;
        this.Arrow = edge.Source.CreateArrow(this, edge);
    }

    /// <summary>
    ///     Closes the arrow editor.
    /// </summary>
    public void CloseArrow()
    {
        this.Arrow = null;
    }

    /// <summary>
    ///     Points the open arrow at another node, e.g. the one its end was dragged onto.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>True if the draft changed; false if the arrow can't go there or already does.</returns>
    public bool Retarget(FlowNodeDataContext node)
    {
        return this.Arrow is { } arrow && this._owner.Change(() => arrow.Retarget(node));
    }

    /// <summary>
    ///     Removes what the open arrow stands for, and closes its editor.
    /// </summary>
    public void RemoveArrow()
    {
        if (this.Arrow is { } arrow)
        {
            this.Arrow = null;
            this._owner.Change(arrow.Remove);
        }
    }

    /// <summary>
    ///     Returns whether a step can be made conditional: arrows lead forward into it.
    /// </summary>
    /// <param name="node">The step's node.</param>
    /// <returns>True if it can.</returns>
    public bool CanMakeConditional(FlowNodeDataContext node)
    {
        return node.Step is { } step && this._owner.CanMakeConditional(step);
    }

    /// <summary>
    ///     Opens the editor for the condition a step is gone round on, closing the arrow editor.
    /// </summary>
    /// <param name="node">The step's node.</param>
    public void StartConditional(FlowNodeDataContext node)
    {
        if (node.Step is { } step && this._owner.CanMakeConditional(step))
        {
            this.Arrow = null;
            this.Conditional = new ConditionalStepDataContext(node, [.. this._owner.Steps.Select(s => s.Id.Trim())], step.AgentIds);
        }
    }

    /// <summary>
    ///     Makes the open editor's step conditional on its condition, and closes the editor.
    /// </summary>
    /// <returns>How many arrows were changed or added.</returns>
    public int ApplyConditional()
    {
        if (this.Conditional is not { Node.Step: { } step } conditional)
        {
            return 0;
        }

        this.Conditional = null;
        return this._owner.MakeConditional(step, conditional.Condition.ToDefinition());
    }

    /// <summary>
    ///     Closes the "make this step conditional" editor without changing anything.
    /// </summary>
    public void CloseConditional()
    {
        this.Conditional = null;
    }

    /// <summary>
    ///     Selects a step, closing the arrow editor.
    /// </summary>
    /// <param name="node">The step's node; the end can't be selected.</param>
    public void Select(FlowNodeDataContext node)
    {
        if (node.Step is not null)
        {
            this.StepClicked?.Invoke(this, node.Step.Id.Trim());
            this.Arrow = null;
            this._picked.Clear();
            this._picked.Add(node.Step);
            this._owner.SelectedStep = node.Step;
            this.IsPropertiesOpen = true;
        }
    }

    /// <summary>
    ///     Adds a step to the picked ones, or takes it out if it's picked already (⇧-click). The step added becomes the
    ///     selected one; taking out the selected step selects another picked one, if any.
    /// </summary>
    /// <param name="node">The step's node; the end can't be picked.</param>
    public void TogglePicked(FlowNodeDataContext node)
    {
        if (node.Step is not { } step)
        {
            return;
        }

        this.Arrow = null;
        if (this._picked.Remove(step))
        {
            if (this._owner.SelectedStep == step)
            {
                this._owner.SelectedStep = this.PickedSteps.FirstOrDefault();
            }

            this.Refresh();
            return;
        }

        this._picked.Add(step);
        this._owner.SelectedStep = step;
    }

    /// <summary>
    ///     Picks the steps whose nodes a box touches (a drag on empty canvas), selecting the first of them.
    /// </summary>
    /// <param name="box">The box, in graph coordinates.</param>
    /// <param name="add">Whether to add to the steps already picked (⇧ held) rather than start again.</param>
    public void PickIn(Rect box, bool add)
    {
        this.Arrow = null;
        if (!add)
        {
            this._picked.Clear();
        }

        var inBox = this.StepNodes.Where(n => n.Bounds.Intersects(box)).Select(n => n.Step!).ToList();
        this._picked.UnionWith(inBox);
        var selected = this._owner.SelectedStep;
        this._owner.SelectedStep = selected is not null && this._picked.Contains(selected) ? selected : this.PickedSteps.FirstOrDefault();
    }

    /// <summary>
    ///     Removes the picked steps, as one edit.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public int RemovePicked()
    {
        var picked = this.PickedSteps;
        this.Arrow = null;
        this._owner.RemoveSteps(picked);
        return picked.Count;
    }

    /// <summary>
    ///     Starts moving a step, with the other picked steps if it's one of them.
    /// </summary>
    /// <param name="node">The node being dragged.</param>
    /// <returns>True if it can be moved; the end can't.</returns>
    public bool BeginMove(FlowNodeDataContext node)
    {
        if (node.Step is not { } step)
        {
            return false;
        }

        this._owner.CommitEdit();
        this._moving.Clear();
        this._moveBy = default;
        foreach (var moving in this._picked.Contains(step) ? this.PickedSteps : [step])
        {
            if (this._nodes.TryGetValue(moving, out var movingNode))
            {
                this._moving[moving] = movingNode.Bounds.TopLeft - this._shift;
            }
        }

        return true;
    }

    /// <summary>
    ///     Moves the steps being moved, live, without changing the draft until <see cref="EndMove" />.
    /// </summary>
    /// <param name="by">How far from where they were when the move began.</param>
    public void Move(Vector by)
    {
        if (this._moving.Count > 0)
        {
            this._moveBy = by;
            this.Layout(this._edges);
        }
    }

    /// <summary>
    ///     Finishes a move, saving where the moved steps are as one edit.
    /// </summary>
    /// <returns>True if they moved.</returns>
    public bool EndMove()
    {
        var moved = this._moving.ToList();
        var by = this._moveBy;
        this._moving.Clear();
        this._moveBy = default;
        if (moved.Count == 0 || by == default)
        {
            return false;
        }

        this._owner.Change(() =>
        {
            foreach (var (step, from) in moved)
            {
                step.Position = new StepPosition { X = Math.Round(from.X + by.X), Y = Math.Round(from.Y + by.Y) };
            }
        });
        return true;
    }

    /// <summary>
    ///     Lays every step out again, forgetting where any were moved to, as one edit.
    /// </summary>
    /// <returns>True if any had been moved.</returns>
    public bool Tidy()
    {
        var moved = this._owner.Steps.Where(s => s.Position is not null).ToList();
        if (moved.Count == 0)
        {
            return false;
        }

        this._owner.Change(() =>
        {
            foreach (var step in moved)
            {
                step.Position = null;
            }
        });
        return true;
    }

    /// <summary>
    ///     Copies the picked steps, with the arrows between them (and to the end), keeping them for <see cref="Paste" />.
    /// </summary>
    /// <returns>The steps as clipboard text, or null when none are picked.</returns>
    public string? CopyPicked()
    {
        var text = this.WritePicked();
        if (text is not null)
        {
            this._copied = text;
        }

        return text;
    }

    /// <summary>
    ///     Pastes copied steps: each gets a new id (<c>-copy</c>, then <c>-copy-2</c> and so on), the arrows between them
    ///     and the step ids in their templates follow, and they're placed a little below and right of where they were
    ///     copied from. The pasted steps are picked.
    /// </summary>
    /// <param name="text">The clipboard text, or null to paste the steps copied last.</param>
    /// <returns>How many steps were pasted; 0 if the text isn't copied steps.</returns>
    public int Paste(string? text = null)
    {
        text ??= this._copied;
        if (ReadCopied(text) is not { Count: > 0 } steps)
        {
            return 0;
        }

        this._lastPaste = this._lastPaste.Text == text ? (text, this._lastPaste.Count + 1) : (text!, 1);
        var offset = PasteOffset * this._lastPaste.Count;
        var taken = this._owner.Steps.Select(s => s.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var id = $"{step.Id}-copy";
            for (var n = 2; taken.Contains(id); n++)
            {
                id = $"{step.Id}-copy-{n}";
            }

            taken.Add(id);
            ids[step.Id] = id;
        }

        var renamed = WorkflowComposer.RenameSteps(steps, ids);
        foreach (var step in renamed.Where(s => s.Position is not null))
        {
            step.Position = new StepPosition { X = step.Position!.X + offset, Y = step.Position.Y + offset };
        }

        this.Arrow = null;
        var added = this._owner.AddSteps(renamed);
        this._picked.Clear();
        this._picked.UnionWith(added);
        this.Refresh();
        return added.Count;
    }

    /// <summary>
    ///     Pastes a copy of the picked steps, leaving what was copied before as it was.
    /// </summary>
    /// <returns>How many steps were added.</returns>
    public int DuplicatePicked()
    {
        return this.WritePicked() is { } text ? this.Paste(text) : 0;
    }

    /// <summary>
    ///     Returns the label an arrow shows: the overlay's while simulating or showing a run, else its own.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>The label, or null for none.</returns>
    public string? LabelOf(FlowEdge edge)
    {
        return this.Overlay is { } overlay ? overlay.LabelOf(edge) : edge.Label;
    }

    /// <summary>
    ///     Returns the arrow under a point (its line or its label), if any.
    /// </summary>
    /// <param name="point">The point, in graph coordinates.</param>
    /// <param name="tolerance">How far from the line still counts.</param>
    /// <returns>The arrow's path.</returns>
    public FlowEdgePath? PathAt(Point point, double tolerance = 5)
    {
        // Labels are drawn over the lines, the later ones on top, so a click on one picks its arrow first.
        var labelled = this.Paths.LastOrDefault(p => this.LabelOf(p.Edge) is { } label
                                                     && p.LabelPlacement(FlowEdgePath.LabelText(label, Brushes.Black)) is { } placed
                                                     && placed.Box.Contains(point));
        return labelled ?? this.Paths.FirstOrDefault(p => p.Points.Zip(p.Points.Skip(1)).Any(s => DistanceToSegment(point, s.First, s.Second) <= tolerance));
    }

    /// <summary>
    ///     Reads steps back from clipboard text written by <see cref="CopyPicked" />.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The steps, or null if the text isn't copied steps.</returns>
    private static List<AbstractStepDefinition>? ReadCopied(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) is JsonObject { } json && json[ClipboardProperty] is JsonArray steps
                ? steps.Deserialize<List<AbstractStepDefinition>>(JsonConstants.Options)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Writes the picked steps as clipboard text: their arrows to steps that aren't copied are left out (and a
    ///     <c>waitFor</c> naming one), and each keeps where it is drawn, so a paste looks the same.
    /// </summary>
    /// <returns>The text, or null when none are picked.</returns>
    private string? WritePicked()
    {
        var picked = this.PickedSteps;
        if (picked.Count == 0)
        {
            return null;
        }

        var ids = picked.Select(s => s.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        var steps = new JsonArray();
        foreach (var draft in picked)
        {
            var step = WorkflowComposer.FromNode<AbstractStepDefinition>(WorkflowComposer.ToNode(draft.ToDefinition()));
            step.Arrows = step.Arrows?.Where(a => a.To == ArrowConstants.End || ids.Contains(a.To)).ToList() is { Count: > 0 } kept ? kept : null;
            if (step.WaitFor?.Sources([]).Any(s => !ids.Contains(s)) ?? false)
            {
                step.WaitFor = null;
            }

            if (step.Position is null && this._nodes.TryGetValue(draft, out var node))
            {
                var at = node.Bounds.TopLeft - this._shift;
                step.Position = new StepPosition { X = Math.Round(at.X), Y = Math.Round(at.Y) };
            }

            steps.Add(WorkflowComposer.ToNode(step));
        }

        return new JsonObject { [ClipboardProperty] = steps }.ToJsonString(JsonConstants.Options);
    }

    /// <summary>
    ///     Picks the accent colour a step's node is drawn with.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The colour; grey for an include.</returns>
    private static Color AccentFor(StepDraftDataContext step)
    {
        if (step.IsInclude)
        {
            return Color.FromRgb(0x8A, 0x8F, 0x98);
        }

        var hash = step.StepType.Aggregate(17, (h, c) => unchecked((h * 31) + c));
        return Accents[(hash & int.MaxValue) % Accents.Length];
    }

    /// <summary>
    ///     Returns the top of a layer.
    /// </summary>
    /// <param name="rank">The layer: 0 is the top.</param>
    /// <returns>The y.</returns>
    private static double RowTop(int rank)
    {
        return Margin + (rank * (NodeHeight + RowGap));
    }

    /// <summary>
    ///     Returns how far a point is from a line segment.
    /// </summary>
    /// <param name="p">The point.</param>
    /// <param name="a">The segment's start.</param>
    /// <param name="b">The segment's end.</param>
    /// <returns>The distance.</returns>
    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var lengthSquared = (ab.X * ab.X) + (ab.Y * ab.Y);
        var t = lengthSquared == 0 ? 0 : Math.Clamp((((p.X - a.X) * ab.X) + ((p.Y - a.Y) * ab.Y)) / lengthSquared, 0, 1);
        var closest = a + (ab * t);
        return Math.Sqrt(Math.Pow(p.X - closest.X, 2) + Math.Pow(p.Y - closest.Y, 2));
    }

    /// <summary>
    ///     Returns the steps arrows from several steps lead forward into, with what each waits for, from the draft's
    ///     flow.
    /// </summary>
    /// <returns>The badge for each such step, by id.</returns>
    private Dictionary<string, string> Joins()
    {
        var joins = new Dictionary<string, string>(StringComparer.Ordinal);
        if (this._owner.Machine is not { } machine)
        {
            return joins;
        }

        foreach (var step in this._owner.Steps)
        {
            var id = step.Id.Trim();
            if (machine.IncomingForward(id).Count >= 2)
            {
                joins[id] = machine.StepOf(id)?.WaitFor switch
                {
                    null => "⑂ waits for all",
                    AbstractWaitGroup { Items: null } shorthand => $"⑂ waits for {shorthand.Describe()}",
                    _ => "⑂ waits for: custom",
                };
            }
        }

        return joins;
    }

    /// <summary>
    ///     Finds the open arrow editor's arrow among the laid out ones, closing the editor when the arrow's gone, e.g.
    ///     once its step is removed.
    /// </summary>
    private void FindSelectedPath()
    {
        var arrow = this.Arrow;
        this.SelectedPath = arrow is null ? null : this.Paths.FirstOrDefault(p => arrow.Matches(p.Edge));
        if (arrow is not null && this.SelectedPath is null)
        {
            this.Arrow = null;
        }
    }

    /// <summary>
    ///     Places the nodes in layers, sorts the arrows into ones to the next layer and lanes, and routes them.
    /// </summary>
    /// <param name="edges">The arrows, all with a node to go to.</param>
    private void Layout(IReadOnlyList<FlowEdge> edges)
    {
        var nodes = this.Nodes.ToList();
        var listed = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var outgoing = edges.ToLookup(e => e.From);

        // The arrows back: those that reach a step still being walked, walking from the start, then from the steps it
        // doesn't reach, in the order they're listed.
        var back = new HashSet<FlowEdge>();
        var state = new Dictionary<FlowNodeDataContext, bool>();
        void Walk(FlowNodeDataContext node)
        {
            state[node] = true;
            foreach (var edge in outgoing[node])
            {
                if (!state.TryGetValue(edge.To!, out var walking))
                {
                    Walk(edge.To!);
                }
                else if (walking)
                {
                    back.Add(edge);
                }
            }

            state[node] = false;
        }

        if (this.StartNode is { } start)
        {
            Walk(start);
        }

        foreach (var node in nodes.Where(n => n != this.End && !state.ContainsKey(n)))
        {
            Walk(node);
        }

        // Each node's layer is the longest way to it over the arrows forward; the end goes below everything.
        var forward = edges.Where(e => !back.Contains(e)).ToList();
        var ranks = nodes.ToDictionary(n => n, _ => 0);
        var waiting = nodes.ToDictionary(n => n, n => forward.Count(e => e.To == n));
        var ready = new Queue<FlowNodeDataContext>(nodes.Where(n => waiting[n] == 0));
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            foreach (var edge in forward.Where(e => e.From == node))
            {
                ranks[edge.To!] = Math.Max(ranks[edge.To!], ranks[node] + 1);
                if (--waiting[edge.To!] == 0)
                {
                    ready.Enqueue(edge.To!);
                }
            }
        }

        if (nodes.Contains(this.End))
        {
            ranks[this.End] = nodes.Where(n => n != this.End).Select(n => ranks[n]).DefaultIfEmpty(-1).Max() + 1;
        }

        // Straight down to the next layer, or into a lane: forward jumps on the left, back on the right, the shortest
        // nearest the nodes so lanes cross as little as possible.
        var lanes = new Dictionary<FlowEdge, (bool Right, int Lane)>();
        var taken = new Dictionary<bool, List<List<(int Low, int High)>>> { [false] = [], [true] = [] };
        foreach (var edge in edges.Where(e => back.Contains(e) || ranks[e.To!] > ranks[e.From] + 1).OrderBy(e => Math.Abs(ranks[e.To!] - ranks[e.From])))
        {
            var right = back.Contains(edge);
            var span = (Low: Math.Min(ranks[edge.From], ranks[edge.To!]), High: Math.Max(ranks[edge.From], ranks[edge.To!]));
            var side = taken[right];
            var lane = side.FindIndex(l => l.All(t => t.High <= span.Low || t.Low >= span.High));
            if (lane < 0)
            {
                side.Add([]);
                lane = side.Count - 1;
            }

            side[lane].Add(span);
            lanes[edge] = (right, lane);
        }

        var rows = nodes.GroupBy(n => ranks[n]).OrderBy(g => g.Key)
            .Select(g => (Rank: g.Key, Nodes: g.OrderBy(n => n == this.StartNode ? -1 : listed[n]).ToList())).ToList();
        this.ColumnWidth = rows.Select(r => RowWidth(r.Nodes)).DefaultIfEmpty(NodeWidth).Max();
        var leftRoom = taken[false].Count == 0 ? 0 : (taken[false].Count * LaneGap) + LabelRoom;
        var rightRoom = taken[true].Count == 0 ? 0 : (taken[true].Count * LaneGap) + LabelRoom;
        this.ColumnX = Margin + leftRoom;
        foreach (var row in rows)
        {
            var x = this.ColumnX + ((this.ColumnWidth - RowWidth(row.Nodes)) / 2);
            foreach (var node in row.Nodes)
            {
                var width = node.Step is null ? TerminalWidth : NodeWidth;
                node.Bounds = node.Step is null
                    ? new Rect(x, RowTop(row.Rank) + ((NodeHeight - TerminalHeight) / 2), TerminalWidth, TerminalHeight)
                    : new Rect(x, RowTop(row.Rank), NodeWidth, NodeHeight);
                x += width + ColumnGap;
            }
        }

        // Steps moved by hand go where they were put; then everything shifts so nothing (lanes included) is off the
        // top or left, and the lanes run beside all the nodes.
        foreach (var node in nodes.Where(n => n.Step is not null))
        {
            Point? at = this._moving.TryGetValue(node.Step!, out var from) ? from + this._moveBy
                : node.Step!.Position is { } position ? new Point(position.X, position.Y) : null;
            if (at is { } topLeft)
            {
                node.Bounds = new Rect(topLeft, node.Bounds.Size);
            }
        }

        var left = nodes.Select(n => n.Bounds.Left).DefaultIfEmpty(this.ColumnX).Min();
        var top = nodes.Select(n => n.Bounds.Top).DefaultIfEmpty(Margin).Min();
        this._shift = new Vector(Math.Max(0, this.ColumnX - left), Math.Max(0, Margin - top));
        if (this._shift != default)
        {
            foreach (var node in nodes)
            {
                node.Bounds = node.Bounds.Translate(this._shift);
            }
        }

        this.ColumnX = nodes.Select(n => n.Bounds.Left).DefaultIfEmpty(this.ColumnX).Min();
        this.ColumnWidth = nodes.Select(n => n.Bounds.Right).DefaultIfEmpty(this.ColumnX + NodeWidth).Max() - this.ColumnX;
        this.Paths = this.Route(edges, lanes);
        this.FindSelectedPath();
        this.Arrow?.OnLayout();
        this.Width = this.ColumnX + this.ColumnWidth + rightRoom + Margin;
        this.Height = nodes.Select(n => n.Bounds.Bottom).DefaultIfEmpty(0).Max() + RowGap + Margin;
        this.LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Returns the width of a layer's nodes side by side.
    /// </summary>
    /// <param name="row">The layer's nodes.</param>
    /// <returns>The width.</returns>
    private static double RowWidth(IReadOnlyList<FlowNodeDataContext> row)
    {
        return row.Sum(n => n.Step is null ? TerminalWidth : NodeWidth) + (Math.Max(0, row.Count - 1) * ColumnGap);
    }

    /// <summary>
    ///     Routes the arrows. Every arrow leaves a node's bottom and reaches another's top, spread along the edge in the
    ///     order they go (lanes on the left first, then by where the other node is, then lanes on the right). An arrow
    ///     to the next layer turns in the gap between the two; one in a lane goes out along the gap below the node it
    ///     leaves, along its lane and back in along the gap above the node it reaches.
    /// </summary>
    /// <param name="edges">The arrows.</param>
    /// <param name="lanes">The lane of each arrow that isn't to the next layer.</param>
    /// <returns>The paths.</returns>
    private List<FlowEdgePath> Route(IReadOnlyList<FlowEdge> edges, IReadOnlyDictionary<FlowEdge, (bool Right, int Lane)> lanes)
    {
        double SortKey(FlowEdge edge, FlowNodeDataContext other)
        {
            return lanes.TryGetValue(edge, out var lane) ? lane.Right ? 1e6 + lane.Lane : -1e6 - lane.Lane : other.Bounds.Center.X;
        }

        var outs = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.OrderBy(e => SortKey(e, e.To!)).ToList());
        var ins = edges.GroupBy(e => e.To!).ToDictionary(g => g.Key, g => g.OrderBy(e => SortKey(e, e.From)).ToList());
        var paths = new List<FlowEdgePath>();
        foreach (var edge in edges)
        {
            var from = new Point(PortX(edge.From.Bounds, outs[edge.From].IndexOf(edge), outs[edge.From].Count), edge.From.Bounds.Bottom);
            var to = new Point(PortX(edge.To!.Bounds, ins[edge.To].IndexOf(edge), ins[edge.To].Count), edge.To.Bounds.Top);
            if (!lanes.TryGetValue(edge, out var lane))
            {
                if (Math.Abs(from.X - to.X) < 0.5)
                {
                    paths.Add(new FlowEdgePath(edge, [from, to], new Point(from.X + 4, (from.Y + to.Y) / 2), false));
                    continue;
                }

                var turnY = (from.Y + to.Y) / 2;
                paths.Add(new FlowEdgePath(edge, [from, new Point(from.X, turnY), new Point(to.X, turnY), to],
                    new Point(to.X + 4, (turnY + to.Y) / 2), false));
                continue;
            }

            var laneX = lane.Right
                ? this.ColumnX + this.ColumnWidth + ((lane.Lane + 1) * LaneGap)
                : this.ColumnX - ((lane.Lane + 1) * LaneGap);
            var outY = from.Y + (RowGap / 2) + (lane.Right ? 4 : -4);
            var inY = to.Y - (RowGap / 2) + (lane.Right ? 4 : -4);
            var label = new Point(laneX + (lane.Right ? 6 : -6), (outY + inY) / 2);
            paths.Add(new FlowEdgePath(edge,
                [from, new Point(from.X, outY), new Point(laneX, outY), new Point(laneX, inY), new Point(to.X, inY), to], label, !lane.Right));
        }

        return paths;
    }

    /// <summary>
    ///     Returns the x an arrow leaves or reaches a node at, spreading several along its edge.
    /// </summary>
    /// <param name="bounds">The node's bounds.</param>
    /// <param name="index">The arrow's place among those on that edge.</param>
    /// <param name="count">How many arrows use that edge.</param>
    /// <returns>The x.</returns>
    private static double PortX(Rect bounds, int index, int count)
    {
        return bounds.Left + ((index + 1) * bounds.Width / (count + 1));
    }
}

/// <summary>
///     The editor behind a step's "Make this step conditional…": the condition the run goes round the step on. Applying
///     it puts <c>not</c> the condition on the arrows into the step and adds arrows past it <c>when</c> it holds
///     (<see cref="LibraryPageDataContext.MakeConditional" />).
/// </summary>
public class ConditionalStepDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ConditionalStepDataContext" /> class.
    /// </summary>
    /// <param name="node">The step's node.</param>
    /// <param name="stepIds">The workflow's step ids, for the condition's pickers.</param>
    /// <param name="agentIds">The defined agents' ids, for the condition's pickers.</param>
    public ConditionalStepDataContext(FlowNodeDataContext node, IReadOnlyList<string> stepIds, IReadOnlyList<string> agentIds)
    {
        this.Node = node;
        this.Condition = new ConditionDraftDataContext(new ConditionDefinition { Kind = StepConstants.ConditionOutput });
        this.Condition.SetChoices(stepIds, agentIds);
    }

    /// <summary>
    ///     Gets the step's node.
    /// </summary>
    public FlowNodeDataContext Node { get; }

    /// <summary>
    ///     Gets the line naming the step.
    /// </summary>
    public string Heading => this.Node.Heading;

    /// <summary>
    ///     Gets the condition the run goes round the step on. A condition on "(this step)" is checked against each
    ///     step that leads into it.
    /// </summary>
    public ConditionDraftDataContext Condition { get; }
}

/// <summary>
///     One node in the flow graph: a step, or the end of the run.
/// </summary>
public class FlowNodeDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FlowNodeDataContext" /> class.
    /// </summary>
    /// <param name="step">The step, or null for the end.</param>
    /// <param name="heading">The heading until the node is updated.</param>
    public FlowNodeDataContext(StepDraftDataContext? step, string heading)
    {
        this.Step = step;
        this.Heading = heading;
    }

    /// <summary>
    ///     Gets the step, or null for the end node.
    /// </summary>
    public StepDraftDataContext? Step { get; }

    /// <summary>
    ///     Gets whether the node is the end of the run.
    /// </summary>
    public bool IsTerminal => this.Step is null;

    /// <summary>
    ///     Gets whether the node is a step.
    /// </summary>
    public bool IsStep => this.Step is not null;

    /// <summary>
    ///     Gets the node's heading: the step's name.
    /// </summary>
    public string Heading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the line under the heading: the step's type and what it's based on.
    /// </summary>
    public string? Detail
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the step's id.
    /// </summary>
    public string? Id
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the short flags shown on the node, e.g. optional or may be skipped.
    /// </summary>
    public IReadOnlyList<string> Badges
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>
    ///     Gets the node's accent colour, by step type.
    /// </summary>
    public IBrush Accent
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = Brushes.Gray;

    /// <summary>
    ///     Gets whether the node's step is the one being edited.
    /// </summary>
    public bool IsSelected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether validation reports a problem with the node's step.
    /// </summary>
    public bool HasError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the node's step is the one the workflow starts at.
    /// </summary>
    public bool IsStart
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the node's step ends the run: it has no arrow that always goes on to another step.
    /// </summary>
    public bool Ends
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets how the step went in the overlay shown, if any.
    /// </summary>
    public FlowNodeMark Mark
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsFaded));
            this.RaisePropertyChanged(nameof(this.IsReached));
            this.RaisePropertyChanged(nameof(this.IsActive));
            this.RaisePropertyChanged(nameof(this.IsFailedRun));
        }
    }

    /// <summary>
    ///     Gets whether the overlay shows the step wasn't reached or was cancelled, so it's drawn faded.
    /// </summary>
    public bool IsFaded => this.Mark is FlowNodeMark.NotReached or FlowNodeMark.Cancelled or FlowNodeMark.Skipped;

    /// <summary>
    ///     Gets whether the overlay shows the step ran and finished.
    /// </summary>
    public bool IsReached => this.Mark == FlowNodeMark.Reached;

    /// <summary>
    ///     Gets whether the overlay shows the step running or waiting.
    /// </summary>
    public bool IsActive => this.Mark == FlowNodeMark.Active;

    /// <summary>
    ///     Gets whether the overlay shows the step failed.
    /// </summary>
    public bool IsFailedRun => this.Mark == FlowNodeMark.Failed;

    /// <summary>
    ///     Gets the few words the overlay shows under the step, e.g. <c>✓ ran ×2</c>.
    /// </summary>
    public string? Status
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.HasStatus));
        }
    }

    /// <summary>
    ///     Gets whether the overlay shows words under the step.
    /// </summary>
    public bool HasStatus => !string.IsNullOrEmpty(this.Status);

    /// <summary>
    ///     Gets or sets whether the node shows the outcome picked for the simulation in place of its id.
    /// </summary>
    public bool ShowsOutcome
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.ShowsId));
        }
    }

    /// <summary>
    ///     Gets whether the node shows its id.
    /// </summary>
    public bool ShowsId => !this.ShowsOutcome;

    /// <summary>
    ///     Gets or sets whether the node shows the handle arrows are drawn from: only on a step, while the canvas can
    ///     be edited.
    /// </summary>
    public bool CanConnect
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value && this.IsStep);
    }

    /// <summary>
    ///     Gets or sets how the step ends in the simulation.
    /// </summary>
    public FlowTrigger Outcome
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.OutcomeText));
        }
    }

    /// <summary>
    ///     Gets the words on the simulation's outcome button, e.g. <c>ends: success</c>.
    /// </summary>
    public string OutcomeText => this.Outcome switch
    {
        FlowTrigger.Failed => "ends: failure",
        FlowTrigger.TimedOut => "ends: timeout",
        FlowTrigger.LeftOut => "ends: left out",
        _ => "ends: success",
    };

    /// <summary>
    ///     Gets the node's place in the graph.
    /// </summary>
    public Rect Bounds
    {
        get;
        internal set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.X));
            this.RaisePropertyChanged(nameof(this.Y));
            this.RaisePropertyChanged(nameof(this.Width));
            this.RaisePropertyChanged(nameof(this.Height));
        }
    }

    /// <summary>
    ///     Gets the node's left edge.
    /// </summary>
    public double X => this.Bounds.X;

    /// <summary>
    ///     Gets the node's top edge.
    /// </summary>
    public double Y => this.Bounds.Y;

    /// <summary>
    ///     Gets the node's width.
    /// </summary>
    public double Width => this.Bounds.Width;

    /// <summary>
    ///     Gets the node's height.
    /// </summary>
    public double Height => this.Bounds.Height;

    /// <summary>
    ///     Shows how the step went in an overlay.
    /// </summary>
    /// <param name="mark">How it went; <see cref="FlowNodeMark.None" /> for no overlay.</param>
    /// <param name="status">The words shown under it, if any.</param>
    internal void ShowOverlay(FlowNodeMark mark, string? status)
    {
        this.Mark = mark;
        this.Status = status;
    }

    /// <summary>
    ///     Copies what the node shows from its step.
    /// </summary>
    /// <param name="accent">The accent colour.</param>
    /// <param name="selected">Whether the step is being edited.</param>
    /// <param name="hasError">Whether validation reports a problem with it.</param>
    /// <param name="isStart">Whether the workflow starts at it.</param>
    /// <param name="joinBadge">What it waits for, if arrows from several steps lead into it.</param>
    internal void Update(Color accent, bool selected, bool hasError, bool isStart, string? joinBadge)
    {
        var step = this.Step!;
        this.Heading = string.IsNullOrWhiteSpace(step.Name) ? step.NamePlaceholder ?? step.Id : step.Name;
        this.Id = step.Id;
        this.Detail = step.IsInclude ? $"includes {step.Include}"
            : step.IsUse ? $"{step.StepType} · uses {step.Use}"
            : step.Details?.Summary is { } summary ? $"{step.StepType} · {summary}"
            : step.StepType;
        var badges = new List<string>();
        if (isStart)
        {
            badges.Add("▶ start");
        }

        if (step.IsFork)
        {
            badges.Add("⑂ parallel");
        }

        if (joinBadge is not null)
        {
            badges.Add(joinBadge);
        }

        if (step.IsOptional)
        {
            badges.Add(step.OptionalDefault ? "optional" : "optional · off");
        }

        if (step.ShowsStepFields && step.Advance != StepDraftDataContext.DefaultChoice)
        {
            badges.Add(step.Advance);
        }

        if (step.ShowsStepFields && step.Details is { } details)
        {
            badges.AddRange(details.Badges);
        }

        if (!this.Badges.SequenceEqual(badges))
        {
            this.Badges = badges;
        }

        if (this.Accent is not ISolidColorBrush { Color: var current } || current != accent)
        {
            this.Accent = new SolidColorBrush(accent);
        }

        this.IsSelected = selected;
        this.HasError = hasError;
        this.IsStart = isStart;
        this.Ends = !step.Arrows.Any(a => a.To != ArrowConstants.End && !a.HasCondition && a.On is ArrowConstants.OnSuccess or ArrowConstants.OnAlways);
    }
}

/// <summary>
///     An arrow, routed: the points of its line and where its label goes.
/// </summary>
/// <param name="Edge">The arrow.</param>
/// <param name="Points">The line's points, from the step it leaves to the node it reaches.</param>
/// <param name="LabelAt">Where the label is drawn from, if the arrow or an overlay gives it one.</param>
/// <param name="LabelBefore">True if the label ends at <paramref name="LabelAt" /> rather than starting there.</param>
public record FlowEdgePath(FlowEdge Edge, IReadOnlyList<Point> Points, Point? LabelAt, bool LabelBefore)
{
    /// <summary>
    ///     The size of a label's text.
    /// </summary>
    public const double LabelSize = 11;

    /// <summary>
    ///     Lays out a label's text.
    /// </summary>
    /// <param name="label">The text.</param>
    /// <param name="foreground">The text colour.</param>
    /// <returns>The laid-out text.</returns>
    public static FormattedText LabelText(string label, IBrush foreground)
    {
        return new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, LabelSize, foreground);
    }

    /// <summary>
    ///     Returns where a label is drawn on this path: the rounded box behind it and the point its text starts at.
    /// </summary>
    /// <param name="text">The laid-out label.</param>
    /// <returns>The box and the text's origin, or null when the path has nowhere for a label.</returns>
    public (Rect Box, Point Origin)? LabelPlacement(FormattedText text)
    {
        if (this.LabelAt is not { } at)
        {
            return null;
        }

        var x = this.LabelBefore ? at.X - text.Width - 4 : at.X + 4;
        return (new Rect(x - 4, at.Y - (text.Height / 2) - 1, text.Width + 8, text.Height + 2), new Point(x, at.Y - (text.Height / 2)));
    }
}
