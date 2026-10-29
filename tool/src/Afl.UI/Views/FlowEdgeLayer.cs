using Afl.Core.Flow;
using Afl.UI.Flow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Afl.UI.Views;

/// <summary>
///     Draws a <see cref="FlowGraphDataContext" />'s arrows under its nodes: each routed path with rounded corners, an
///     arrowhead and its label, in the colour and dash of its <see cref="AbstractFlowEdgeSource" />, plus the arrow
///     being drawn from a node while the mouse is down; the arrow whose editor is open is drawn heavier, with a halo.
/// </summary>
public class FlowEdgeLayer : Control
{
    /// <summary>
    ///     Defines the <see cref="Graph" /> property.
    /// </summary>
    public static readonly StyledProperty<FlowGraphDataContext?> GraphProperty =
        AvaloniaProperty.Register<FlowEdgeLayer, FlowGraphDataContext?>(nameof(Graph));

    /// <summary>
    ///     The radius of an arrow's rounded corners.
    /// </summary>
    private const double CornerRadius = 8;

    /// <summary>
    ///     The length of an arrowhead.
    /// </summary>
    private const double ArrowLength = 8;

    /// <summary>
    ///     Initializes static members of the <see cref="FlowEdgeLayer" /> class.
    /// </summary>
    static FlowEdgeLayer()
    {
        AffectsRender<FlowEdgeLayer>(GraphProperty);
        GraphProperty.Changed.AddClassHandler<FlowEdgeLayer>((layer, e) => layer.OnGraphChanged(e));
    }

    /// <summary>
    ///     Gets or sets the graph whose arrows are drawn.
    /// </summary>
    public FlowGraphDataContext? Graph
    {
        get => this.GetValue(GraphProperty);
        set => this.SetValue(GraphProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (this.Graph is not { } graph)
        {
            return;
        }

        var text = this.Brush("SystemControlForegroundBaseHighBrush", Brushes.Gray);
        var labelBackground = this.Brush("SystemControlBackgroundAltHighBrush", Brushes.White);
        foreach (var path in graph.Paths)
        {
            var source = path.Edge.Source;
            var color = source.ColorOf(path.Edge);
            var selected = path == graph.SelectedPath;
            if (selected)
            {
                // The open arrow: a soft halo under it, and a heavier line.
                DrawLine(context, new Pen(new SolidColorBrush(color, 0.25), 7), path.Points);
            }

            // With an overlay, the arrows that fired are drawn heavy, the ones still to come lighter, and dead ones faint.
            var state = graph.Overlay?.StateOf(path.Edge);
            var opacity = state switch
            {
                ArrowState.Dead => 0.2,
                ArrowState.Pending => 0.45,
                _ => 1.0,
            };
            var width = selected || state == ArrowState.Fired ? 2.4 : 1.6;
            var brush = new SolidColorBrush(color, opacity);
            DrawLine(context, new Pen(brush, width, source.IsDashedFor(path.Edge) ? new DashStyle([4, 3], 0) : null), path.Points);
            DrawArrowhead(context, brush, path.Points[^2], path.Points[^1]);
            if (graph.LabelOf(path.Edge) is { } label)
            {
                DrawLabel(context, path, label, text, labelBackground);
            }
        }

        if (graph.PendingConnection is { } pending)
        {
            var from = new Point(pending.From.Bounds.Right, pending.From.Bounds.Center.Y);
            var pen = new Pen(this.Brush("SystemControlHighlightAccentBrush", Brushes.DodgerBlue), 1.6, new DashStyle([3, 3], 0));
            context.DrawLine(pen, from, pending.To);
            DrawArrowhead(context, pen.Brush!, from, pending.To);
        }
    }

    /// <summary>
    ///     Draws a polyline with rounded corners.
    /// </summary>
    /// <param name="context">The drawing context.</param>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    private static void DrawLine(DrawingContext context, Pen pen, IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], false);
            for (var i = 1; i < points.Count - 1; i++)
            {
                // Stop short of the corner, then curve round it towards the next point.
                var before = Toward(points[i], points[i - 1], CornerRadius);
                var after = Toward(points[i], points[i + 1], CornerRadius);
                g.LineTo(before);
                g.QuadraticBezierTo(points[i], after);
            }

            g.LineTo(points[^1]);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    ///     Draws an arrowhead at the end of a segment.
    /// </summary>
    /// <param name="context">The drawing context.</param>
    /// <param name="brush">The fill.</param>
    /// <param name="from">The segment's start.</param>
    /// <param name="to">The segment's end, where the head's point is.</param>
    private static void DrawArrowhead(DrawingContext context, IBrush brush, Point from, Point to)
    {
        var length = Length(to - from);
        if (length < 0.1)
        {
            return;
        }

        var direction = (to - from) / length;
        var normal = new Vector(-direction.Y, direction.X);
        var back = to - (direction * ArrowLength);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(back + (normal * (ArrowLength / 2)));
            g.LineTo(back - (normal * (ArrowLength / 2)));
            g.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    /// <summary>
    ///     Draws an arrow's label on a rounded background.
    /// </summary>
    /// <param name="context">The drawing context.</param>
    /// <param name="path">The arrow's path, which says where the label goes.</param>
    /// <param name="label">The text.</param>
    /// <param name="foreground">The text colour.</param>
    /// <param name="background">The background.</param>
    private static void DrawLabel(DrawingContext context, FlowEdgePath path, string label, IBrush foreground, IBrush background)
    {
        var text = FlowEdgePath.LabelText(label, foreground);
        if (path.LabelPlacement(text) is not { } placed)
        {
            return;
        }

        context.DrawRectangle(background, null, placed.Box, 4, 4);
        context.DrawText(text, placed.Origin);
    }

    /// <summary>
    ///     Returns the point a distance from one point towards another, at most halfway.
    /// </summary>
    /// <param name="from">The point.</param>
    /// <param name="to">The point it moves towards.</param>
    /// <param name="distance">How far.</param>
    /// <returns>The point.</returns>
    private static Point Toward(Point from, Point to, double distance)
    {
        var length = Length(to - from);
        return length < 0.1 ? from : from + ((to - from) * (Math.Min(distance, length / 2) / length));
    }

    /// <summary>
    ///     Returns a vector's length.
    /// </summary>
    /// <param name="vector">The vector.</param>
    /// <returns>The length.</returns>
    private static double Length(Vector vector)
    {
        return Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
    }

    /// <summary>
    ///     Finds a theme brush.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <param name="fallback">The brush used when the theme has none.</param>
    /// <returns>The brush.</returns>
    private IBrush Brush(string key, IBrush fallback)
    {
        return this.TryFindResource(key, this.ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
    }

    /// <summary>
    ///     Redraws whenever a new graph is laid out.
    /// </summary>
    /// <param name="e">The change.</param>
    private void OnGraphChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is FlowGraphDataContext old)
        {
            old.LayoutChanged -= this.OnLayoutChanged;
        }

        if (e.NewValue is FlowGraphDataContext graph)
        {
            graph.LayoutChanged += this.OnLayoutChanged;
        }
    }

    /// <summary>
    ///     Redraws the arrows.
    /// </summary>
    /// <param name="sender">The graph.</param>
    /// <param name="e">The event.</param>
    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        this.InvalidateVisual();
    }
}
