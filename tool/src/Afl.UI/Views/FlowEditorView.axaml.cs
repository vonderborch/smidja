using Afl.UI.Flow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Afl.UI.Views;

/// <summary>
///     The workflow builder's flow editor: the draft's flow drawn as nodes and arrows, filling the step area. Steps are
///     added from the Add step menu (common steps and includes through a searchable picker), after the selected step;
///     arrows are drawn by dragging from a node's handle to another node, by clicking the handle and then the target,
///     or from a node's right-click menu, which asks how to connect when the step already goes on somewhere; clicking
///     a node opens its step's properties, and clicking an arrow opens the arrow's own editor, whose end dot can be
///     dragged onto another step to repoint it. Dragging a node moves it (with the other picked steps); dragging on the
///     empty canvas picks the steps in the box, and ⇧-click adds or takes out one; Delete removes the picked steps and
///     ⌘C / ⌘V / ⌘D (Ctrl elsewhere) copy, paste and duplicate them. The editing itself is the
///     <see cref="FlowGraphDataContext" />'s; this only turns the mouse and keyboard into calls to it.
/// </summary>
public partial class FlowEditorView : UserControl
{
    /// <summary>
    ///     How far the mouse moves while down before it counts as a drag rather than a click.
    /// </summary>
    private const double DragThreshold = 4;

    /// <summary>
    ///     The smallest zoom.
    /// </summary>
    private const double MinZoom = 0.4;

    /// <summary>
    ///     The largest zoom.
    /// </summary>
    private const double MaxZoom = 1.5;

    /// <summary>
    ///     How much one zoom step changes the zoom by.
    /// </summary>
    private const double ZoomStep = 1.15;

    /// <summary>
    ///     How near the open arrow's end dot a press must be to pick it up.
    /// </summary>
    private const double HandleReach = 10;

    /// <summary>
    ///     The smallest zoom picked by itself to fit a flow's width, below which it scrolls instead.
    /// </summary>
    private const double MinAutoZoom = 0.7;

    /// <summary>
    ///     The graph whose layout changes are followed.
    /// </summary>
    private FlowGraphDataContext? _graph;

    /// <summary>
    ///     The middle of the graph's nodes when it was last laid out, to scroll it into view when it moves.
    /// </summary>
    private double? _center;

    /// <summary>
    ///     The zoom.
    /// </summary>
    private double _zoom = 1;

    /// <summary>
    ///     Whether the zoom was chosen by hand, after which it's no longer fitted to the flow's width.
    /// </summary>
    private bool _zoomChosen;

    /// <summary>
    ///     Where the mouse went down, in this view's coordinates.
    /// </summary>
    private Point _pressedAt;

    /// <summary>
    ///     Whether the mouse has moved far enough to be dragging.
    /// </summary>
    private bool _dragging;

    /// <summary>
    ///     The node a route is being drawn from, if any.
    /// </summary>
    private FlowNodeDataContext? _connectFrom;

    /// <summary>
    ///     Whether the route being drawn waits for a click on its target, rather than following a drag.
    /// </summary>
    private bool _connectByClick;

    /// <summary>
    ///     Whether the drag under way moves the open arrow's end rather than drawing a new arrow.
    /// </summary>
    private bool _repointing;

    /// <summary>
    ///     The node pressed to move, if any; it moves once the mouse is dragging.
    /// </summary>
    private FlowNodeDataContext? _moveNode;

    /// <summary>
    ///     Whether the steps are being moved (the drag on a node got going).
    /// </summary>
    private bool _moving;

    /// <summary>
    ///     Whether the drag under way boxes steps to pick, from a press on the empty canvas.
    /// </summary>
    private bool _boxing;

    /// <summary>
    ///     Whether ⇧ was held when the press began, so the box adds to the picked steps.
    /// </summary>
    private bool _adding;

    /// <summary>
    ///     Where the mouse went down, in graph coordinates.
    /// </summary>
    private Point _pressedOnSurface;

    /// <summary>
    ///     The hint shown in the toolbar when no route is being drawn.
    /// </summary>
    private readonly string _hint;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FlowEditorView" /> class.
    /// </summary>
    public FlowEditorView()
    {
        this.InitializeComponent();
        this.Scroller.AddHandler(PointerWheelChangedEvent, this.OnScrollerWheel, RoutingStrategies.Tunnel);
        this.AddHandler(PointerPressedEvent, this.OnPressedForTarget, RoutingStrategies.Tunnel);
        this._hint = this.Hint.Text ?? string.Empty;
        this.Focusable = true;
    }

    /// <summary>
    ///     Gets the graph being edited.
    /// </summary>
    private FlowGraphDataContext? Graph => this.DataContext as FlowGraphDataContext;

    /// <summary>
    ///     Gets whether a press is being followed.
    /// </summary>
    private bool IsTracking => this._connectFrom is not null || this._moveNode is not null || this._boxing;

    /// <summary>
    ///     Gets the key that goes with shortcuts: ⌘ on macOS, Ctrl elsewhere.
    /// </summary>
    private static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this._graph is not null)
        {
            this._graph.LayoutChanged -= this.OnLayoutChanged;
        }

        this._graph = this.Graph;
        this._center = null;
        if (this._graph is not null)
        {
            this._graph.LayoutChanged += this.OnLayoutChanged;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (this.Graph is not { } graph || !this.IsTracking)
        {
            return;
        }

        var at = e.GetPosition(this);
        this._dragging = this._dragging || Math.Abs(at.X - this._pressedAt.X) + Math.Abs(at.Y - this._pressedAt.Y) >= DragThreshold;
        var onSurface = e.GetPosition(this.Surface);
        if (this._moveNode is { } node)
        {
            if (this._dragging)
            {
                this._moving = this._moving || graph.BeginMove(node);
                graph.Move(onSurface - this._pressedOnSurface);
            }

            return;
        }

        if (this._boxing)
        {
            this.ShowMarquee(this._dragging ? this.BoxTo(onSurface) : null);
            return;
        }

        graph.PendingConnection = (this._connectFrom!, onSurface);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (this.Graph is not { } graph || !this.IsTracking)
        {
            return;
        }

        if (this._moveNode is { } node)
        {
            if (this._moving)
            {
                graph.EndMove();
            }
            else if (!this._adding)
            {
                // A click on one of several picked steps picks just it.
                graph.Select(node);
            }
        }
        else if (this._boxing)
        {
            this.ShowMarquee(null);
            if (this._dragging)
            {
                graph.PickIn(this.BoxTo(e.GetPosition(this.Surface)), this._adding);
            }
        }
        else if (this._repointing)
        {
            graph.PendingConnection = null;
            if (this._dragging && graph.NodeAt(e.GetPosition(this.Surface)) is { } target)
            {
                graph.Retarget(target);
            }
        }
        else if (this._connectFrom is { } from)
        {
            if (this._connectByClick)
            {
                return;
            }

            if (!this._dragging)
            {
                // A click on the handle: the next click picks the target.
                this.StartConnectByClick(from);
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }

            graph.PendingConnection = null;
            if (graph.NodeAt(e.GetPosition(this.Surface)) is { } target)
            {
                this.Connect(from, target);
            }
        }

        this.Reset();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (this._connectByClick)
        {
            return;
        }

        if (this.Graph is { } graph)
        {
            graph.PendingConnection = null;
            if (this._moving)
            {
                graph.EndMove();
            }
        }

        this.ShowMarquee(null);
        this.Reset();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || this.Graph is not { } graph)
        {
            return;
        }

        if (e.Key != Key.Escape)
        {
            this.OnShortcut(graph, e);
            return;
        }

        if (this._connectFrom is not null)
        {
            this.CancelConnect();
            e.Handled = true;
        }
        else if (graph.IsPickerOpen)
        {
            graph.ClosePicker();
            e.Handled = true;
        }
        else if (graph.IsArrowOpen)
        {
            graph.CloseArrow();
            e.Handled = true;
        }
        else if (graph.IsConditionalOpen)
        {
            graph.CloseConditional();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Acts on the picked steps from the keyboard, unless a text box has the key: Delete (or Backspace) removes them,
    ///     the command key with C, V, D or A copies, pastes, duplicates or picks every step.
    /// </summary>
    /// <param name="graph">The graph.</param>
    /// <param name="e">The key.</param>
    private void OnShortcut(FlowGraphDataContext graph, KeyEventArgs e)
    {
        if (e.Source is TextBox || this.IsTracking || !graph.CanEdit)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Delete or Key.Back)
        {
            e.Handled = graph.RemovePicked() > 0;
            return;
        }

        if (e.KeyModifiers != CommandKey)
        {
            return;
        }

        e.Handled = e.Key switch
        {
            Key.C => this.Copy(graph),
            Key.V => this.Paste(graph),
            Key.D => graph.DuplicatePicked() > 0,
            Key.A => this.PickAll(graph),
            _ => false,
        };
    }

    /// <summary>
    ///     Copies the picked steps, to the system clipboard too.
    /// </summary>
    /// <param name="graph">The graph.</param>
    /// <returns>True if any were picked.</returns>
    private bool Copy(FlowGraphDataContext graph)
    {
        if (graph.CopyPicked() is not { } text)
        {
            return false;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(text);
        }

        return true;
    }

    /// <summary>
    ///     Pastes steps from the system clipboard, or those copied last when it holds something else.
    /// </summary>
    /// <param name="graph">The graph.</param>
    /// <returns>True; the paste finishes once the clipboard is read.</returns>
    private bool Paste(FlowGraphDataContext graph)
    {
        this.PasteAsync(graph);
        return true;
    }

    /// <summary>
    ///     Reads the system clipboard and pastes it, or the steps copied last when it holds something else.
    /// </summary>
    /// <param name="graph">The graph.</param>
    private async void PasteAsync(FlowGraphDataContext graph)
    {
        var text = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        if (graph.Paste(text) == 0)
        {
            graph.Paste();
        }
    }

    /// <summary>
    ///     Picks every step.
    /// </summary>
    /// <param name="graph">The graph.</param>
    /// <returns>True.</returns>
    private bool PickAll(FlowGraphDataContext graph)
    {
        graph.PickIn(new Rect(0, 0, graph.Width, graph.Height), false);
        return true;
    }

    /// <summary>
    ///     Returns the box from where the press began on the canvas to a point.
    /// </summary>
    /// <param name="to">The point, in graph coordinates.</param>
    /// <returns>The box.</returns>
    private Rect BoxTo(Point to)
    {
        return new Rect(this._pressedOnSurface, to).Normalize();
    }

    /// <summary>
    ///     Draws the box being dragged out, or hides it.
    /// </summary>
    /// <param name="box">The box, or null to hide it.</param>
    private void ShowMarquee(Rect? box)
    {
        this.Marquee.IsVisible = box is not null;
        if (box is { } shown)
        {
            this.Marquee.Margin = new Thickness(shown.X, shown.Y, 0, 0);
            this.Marquee.Width = shown.Width;
            this.Marquee.Height = shown.Height;
        }
    }

    /// <summary>
    ///     Lays every step out again.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnTidy(object? sender, RoutedEventArgs e)
    {
        this.Graph?.Tidy();
    }

    /// <summary>
    ///     Scrolls the nodes into view when a layout moves them, e.g. when another draft is opened.
    /// </summary>
    /// <param name="sender">The graph.</param>
    /// <param name="e">The event.</param>
    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (this._graph is not { } graph || this._center == graph.ColumnX + (graph.ColumnWidth / 2))
        {
            return;
        }

        this._center = graph.ColumnX + (graph.ColumnWidth / 2);
        Dispatcher.UIThread.Post(() =>
        {
            if (!this._zoomChosen && this.Scroller.Viewport.Width > 0)
            {
                this.SetZoom(Math.Clamp(this.Scroller.Viewport.Width / graph.Width, MinAutoZoom, 1));
            }

            var center = (graph.ColumnX + (graph.ColumnWidth / 2)) * this._zoom;
            this.Scroller.Offset = this.Scroller.Offset.WithX(Math.Max(0, center - (this.Scroller.Viewport.Width / 2)));
        }, DispatcherPriority.Background);
    }

    /// <summary>
    ///     Sets the zoom, keeping it in range.
    /// </summary>
    /// <param name="zoom">The zoom; 1 is actual size.</param>
    private void SetZoom(double zoom)
    {
        this._zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        this.Zoomer.LayoutTransform = new ScaleTransform(this._zoom, this._zoom);
        this.ZoomReset.Content = $"{this._zoom:P0}";
    }

    /// <summary>
    ///     Zooms out a step.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnZoomOut(object? sender, RoutedEventArgs e)
    {
        this._zoomChosen = true;
        this.SetZoom(this._zoom / ZoomStep);
    }

    /// <summary>
    ///     Zooms in a step.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnZoomIn(object? sender, RoutedEventArgs e)
    {
        this._zoomChosen = true;
        this.SetZoom(this._zoom * ZoomStep);
    }

    /// <summary>
    ///     Goes back to actual size.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnZoomReset(object? sender, RoutedEventArgs e)
    {
        this._zoomChosen = true;
        this.SetZoom(1);
    }

    /// <summary>
    ///     Zooms so the whole flow fits, up to actual size.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnZoomFit(object? sender, RoutedEventArgs e)
    {
        if (this.Graph is { } graph)
        {
            var viewport = this.Scroller.Bounds.Size;
            this._zoomChosen = true;
            this.SetZoom(Math.Min(1, Math.Min(viewport.Width / graph.Width, viewport.Height / graph.Height)));
        }
    }

    /// <summary>
    ///     Zooms with Ctrl (or Cmd) and the scroll wheel.
    /// </summary>
    /// <param name="sender">The scroller.</param>
    /// <param name="e">The wheel event.</param>
    private void OnScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || e.Delta.Y == 0)
        {
            return;
        }

        this._zoomChosen = true;
        this.SetZoom(e.Delta.Y > 0 ? this._zoom * ZoomStep : this._zoom / ZoomStep);
        e.Handled = true;
    }

    /// <summary>
    ///     Starts following a press: the drag starts once the mouse moves.
    /// </summary>
    /// <param name="e">The press.</param>
    private void Begin(PointerPressedEventArgs e)
    {
        this._pressedAt = e.GetPosition(this);
        this._pressedOnSurface = e.GetPosition(this.Surface);
        this._adding = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        this._dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <summary>
    ///     Stops following the press.
    /// </summary>
    private void Reset()
    {
        this._connectFrom = null;
        this._connectByClick = false;
        this._repointing = false;
        this._moveNode = null;
        this._moving = false;
        this._boxing = false;
        this.Hint.Text = this._hint;
        this._dragging = false;
    }

    /// <summary>
    ///     Selects a node's step with the left button (⇧ adds it to the picked steps or takes it out), and follows the
    ///     press so a drag moves it.
    /// </summary>
    /// <param name="sender">The node's control.</param>
    /// <param name="e">The press.</param>
    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: FlowNodeDataContext node } control || this.Graph is not { } graph
                                                                                    || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!graph.CanEdit)
        {
            // Simulating or only showing a run: a click selects the step, and nothing moves.
            graph.Select(node);
            e.Handled = true;
            return;
        }

        this.Begin(e);
        this.Focus();
        if (this._adding)
        {
            graph.TogglePicked(node);
        }
        else if (!node.IsSelected)
        {
            graph.Select(node);
        }

        this._moveNode = node;
    }

    /// <summary>
    ///     Starts drawing a route from a node's handle.
    /// </summary>
    /// <param name="sender">The handle.</param>
    /// <param name="e">The press.</param>
    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: FlowNodeDataContext node } control && this.Graph is { CanEdit: true } graph
                                                                                 && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            this.Begin(e);
            this._connectFrom = node;
            graph.PendingConnection = (node, e.GetPosition(this.Surface));
        }
    }

    /// <summary>
    ///     Waits for a click on the node a route from another node goes to, following the mouse with the arrow.
    /// </summary>
    /// <param name="from">The node the route leaves.</param>
    private void StartConnectByClick(FlowNodeDataContext from)
    {
        this._connectFrom = from;
        this._connectByClick = true;
        this._dragging = false;
        this.Hint.Text = $"Click the step the arrow from \u201c{from.Heading}\u201d goes to (itself to run it again) · Esc to cancel";
        this.Focus();
    }

    /// <summary>
    ///     Stops drawing a route without adding it.
    /// </summary>
    private void CancelConnect()
    {
        if (this.Graph is { } graph)
        {
            graph.PendingConnection = null;
        }

        this.Reset();
    }

    /// <summary>
    ///     Takes a press before the nodes see it: on the open arrow's end dot it starts dragging the end to another
    ///     step, and while waiting for a route's target, a click on a node adds the route and anywhere else cancels.
    /// </summary>
    /// <param name="sender">This view.</param>
    /// <param name="e">The press.</param>
    private void OnPressedForTarget(object? sender, PointerPressedEventArgs e)
    {
        if (!this._connectByClick && this.Graph is { HasArrowHandle: true, Arrow: { } arrow } open
                                  && e.GetCurrentPoint(this.Surface).Properties.IsLeftButtonPressed)
        {
            var at = e.GetPosition(this.Surface);
            if (Math.Abs(at.X - open.ArrowHandleX) <= HandleReach && Math.Abs(at.Y - open.ArrowHandleY) <= HandleReach)
            {
                this.Begin(e);
                this._connectFrom = arrow.From;
                this._repointing = true;
                this.Hint.Text = "Drop the arrow's end on the step it should go to · Esc to cancel";
                open.PendingConnection = (arrow.From, at);
                return;
            }
        }

        if (!this._connectByClick || this._connectFrom is not { } from || this.Graph is not { } graph)
        {
            return;
        }

        var target = graph.NodeAt(e.GetPosition(this.Surface));
        this.CancelConnect();
        if (target is not null)
        {
            this.Connect(from, target);
        }

        e.Handled = true;
    }

    /// <summary>
    ///     Draws an arrow from one step to a node.
    /// </summary>
    /// <param name="from">The step's node.</param>
    /// <param name="to">The node the arrow goes to.</param>
    private void Connect(FlowNodeDataContext from, FlowNodeDataContext to)
    {
        this.Graph?.Connect(from, to);
    }

    /// <summary>
    ///     Opens the Add step menu: the step types, then the pickers for common steps and workflows to include.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddStep(object? sender, RoutedEventArgs e)
    {
        if (this.Graph is not { } graph || sender is not Control button)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var type in graph.StepTypes)
        {
            var header = new StackPanel { Spacing = 1, MaxWidth = 320 };
            header.Children.Add(new TextBlock { Text = type.Label, FontWeight = FontWeight.SemiBold });
            header.Children.Add(new TextBlock { Text = type.Description, Classes = { "meta" }, TextWrapping = TextWrapping.Wrap });
            menu.Items.Add(MenuItemFor(header, () => graph.Add(type)));
        }

        menu.Items.Add(new Separator());
        var common = MenuItemFor("Common step…", () => this.OpenPicker(graph.ShowCommonSteps));
        common.IsEnabled = graph.HasCommonSteps;
        menu.Items.Add(common);
        var include = MenuItemFor("Include a workflow…", () => this.OpenPicker(graph.ShowIncludes));
        include.IsEnabled = graph.HasIncludes;
        menu.Items.Add(include);
        menu.ShowAt(button);
    }

    /// <summary>
    ///     Opens a picker and puts the cursor in its search box.
    /// </summary>
    /// <param name="show">Opens the picker.</param>
    private void OpenPicker(Action show)
    {
        show();
        Dispatcher.UIThread.Post(() => this.PickerSearch.Focus(), DispatcherPriority.Background);
    }

    /// <summary>
    ///     Adds the picked item's step.
    /// </summary>
    /// <param name="sender">The item's button.</param>
    /// <param name="e">The event.</param>
    private void OnPickerItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AbstractFlowPaletteItem item })
        {
            this.Graph?.Add(item);
        }
    }

    /// <summary>
    ///     Closes the picker.
    /// </summary>
    /// <param name="sender">The close button.</param>
    /// <param name="e">The event.</param>
    private void OnClosePicker(object? sender, RoutedEventArgs e)
    {
        this.Graph?.ClosePicker();
    }

    /// <summary>
    ///     Closes the picker when the dimmed area around it is clicked.
    /// </summary>
    /// <param name="sender">The backdrop.</param>
    /// <param name="e">The press.</param>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        this.Graph?.ClosePicker();
        e.Handled = true;
    }

    /// <summary>
    ///     Keeps a click inside the picker from reaching the backdrop.
    /// </summary>
    /// <param name="sender">The picker.</param>
    /// <param name="e">The press.</param>
    private void OnPickerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    ///     Closes the arrow editor.
    /// </summary>
    /// <param name="sender">The close button.</param>
    /// <param name="e">The click.</param>
    private void OnCloseArrow(object? sender, RoutedEventArgs e)
    {
        this.Graph?.CloseArrow();
    }

    /// <summary>
    ///     Removes the arrow the editor is open on.
    /// </summary>
    /// <param name="sender">The remove button.</param>
    /// <param name="e">The click.</param>
    private void OnRemoveArrow(object? sender, RoutedEventArgs e)
    {
        this.Graph?.RemoveArrow();
    }

    /// <summary>
    ///     Makes the step the conditional editor is open on conditional.
    /// </summary>
    /// <param name="sender">The apply button.</param>
    /// <param name="e">The click.</param>
    private void OnApplyConditional(object? sender, RoutedEventArgs e)
    {
        this.Graph?.ApplyConditional();
    }

    /// <summary>
    ///     Closes the conditional editor.
    /// </summary>
    /// <param name="sender">The close button.</param>
    /// <param name="e">The click.</param>
    private void OnCloseConditional(object? sender, RoutedEventArgs e)
    {
        this.Graph?.CloseConditional();
    }

    /// <summary>
    ///     Opens a node's menu: edit it, add or remove its arrows, start the workflow at it, make it conditional, or remove it.
    /// </summary>
    /// <param name="sender">The node's control.</param>
    /// <param name="e">The request.</param>
    private void OnNodeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: FlowNodeDataContext node } control || this.Graph is not { CanEdit: true } graph)
        {
            return;
        }

        var addArrow = new MenuItem { Header = "Add arrow to" };
        foreach (var target in graph.TargetsFrom(node))
        {
            var header = target.Id is { } id ? $"{target.Heading}  ({id})" : target.Heading;
            addArrow.Items.Add(MenuItemFor(header, () => graph.Connect(node, target)));
        }

        var removeArrow = new MenuItem { Header = "Remove arrow" };
        foreach (var path in graph.Paths.Where(p => p.Edge.From == node && p.Edge.Arrow is not null))
        {
            var label = path.Edge.Label is { } when ? $"to {path.Edge.To?.Heading} ({when})" : $"to {path.Edge.To?.Heading}";
            removeArrow.Items.Add(MenuItemFor(label, () => graph.Disconnect(path.Edge)));
        }

        if (!node.IsSelected)
        {
            graph.Select(node);
        }

        var picked = graph.PickedSteps.Count;
        var conditional = MenuItemFor("Make this step conditional…", () => graph.StartConditional(node));
        conditional.IsEnabled = graph.CanMakeConditional(node);
        addArrow.IsEnabled = addArrow.Items.Count > 0;
        removeArrow.IsEnabled = removeArrow.Items.Count > 0;
        var menu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                MenuItemFor("Edit step", () => graph.Select(node)),
                MenuItemFor("Draw arrow…", () => this.StartConnectByClick(node)),
                addArrow,
                removeArrow,
                new Separator(),
                MenuItemFor("Make this the start", () => graph.MakeStart(node)),
                conditional,
                new Separator(),
                MenuItemFor("Copy", () => this.Copy(graph)),
                MenuItemFor("Duplicate", () => graph.DuplicatePicked()),
                MenuItemFor("Paste", () => this.Paste(graph)),
                new Separator(),
                MenuItemFor(picked > 1 ? $"Remove {picked} steps" : "Remove step", () => graph.RemovePicked()),
            },
        };
        menu.Open(control);
        e.Handled = true;
    }

    /// <summary>
    ///     Moves a step on to the next way it ends in the simulation.
    /// </summary>
    /// <param name="sender">The step's outcome button.</param>
    /// <param name="e">The event.</param>
    private async void OnCycleOutcome(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FlowNodeDataContext node } && this.Graph is { } graph)
        {
            e.Handled = true;
            await graph.CycleOutcomeAsync(node);
        }
    }

    /// <summary>
    ///     Makes a menu item that runs an action when clicked.
    /// </summary>
    /// <param name="header">The item's header.</param>
    /// <param name="action">What it does.</param>
    /// <returns>The item.</returns>
    private static MenuItem MenuItemFor(object header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    ///     Opens an arrow's editor when it's clicked (its menu opens from <see cref="OnSurfaceContextRequested" />); a
    ///     click on the empty canvas closes the editor.
    /// </summary>
    /// <param name="sender">The surface.</param>
    /// <param name="e">The press.</param>
    private async void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (this.Graph is not { } graph)
        {
            return;
        }

        if (!graph.CanEdit)
        {
            // Simulating: a click on an arrow that might or might not be taken flips it; nothing else opens.
            if (graph.PathAt(e.GetPosition(this.Surface)) is { } clicked)
            {
                e.Handled = true;
                await graph.ToggleTakenAsync(clicked.Edge);
            }

            return;
        }

        if (graph.PathAt(e.GetPosition(this.Surface)) is not { } path)
        {
            graph.CloseArrow();
            if (e.GetCurrentPoint(this.Surface).Properties.IsLeftButtonPressed)
            {
                this.Begin(e);
                this.Focus();
                this._boxing = true;
            }

            return;
        }

        graph.OpenArrow(path.Edge);
        e.Handled = true;
    }

    /// <summary>
    ///     Offers to remove an arrow when it's right-clicked (or Control+clicked on macOS), opening its editor too.
    /// </summary>
    /// <param name="sender">The surface.</param>
    /// <param name="e">The request.</param>
    private void OnSurfaceContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (this.Graph is not { CanEdit: true } graph || !e.TryGetPosition(this.Surface, out var at)
                                                     || graph.PathAt(at) is not { Edge.Arrow: not null } path)
        {
            return;
        }

        graph.OpenArrow(path.Edge);
        var remove = new MenuItem { Header = "Remove arrow" };
        remove.Click += (_, _) => graph.Disconnect(path.Edge);
        new ContextMenu { ItemsSource = new[] { remove } }.Open(this.Surface);
        e.Handled = true;
    }
}
