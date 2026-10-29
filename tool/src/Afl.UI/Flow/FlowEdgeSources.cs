using Afl.Core.Constants;
using Afl.Core.Registration;
using Afl.UI.DataContexts;
using Avalonia.Media;

namespace Afl.UI.Flow;

/// <summary>
///     The kind of arrow a step's flow draws in the flow graph, e.g. the step's own arrows. Each
///     kind is a subclass tagged with <see cref="FlowEdgeSourceRegistrationAttribute" />, so drawing a new flow feature
///     is a matter of writing one: the graph asks every registered source for its edges and draws them all.
/// </summary>
public abstract class AbstractFlowEdgeSource
{
    /// <summary>
    ///     Gets the longest label drawn beside an arrow; longer ones are cut short.
    /// </summary>
    public const int MaxLabelLength = 22;

    /// <summary>
    ///     Gets the colour the source's arrows are drawn in.
    /// </summary>
    public abstract Color Color { get; }

    /// <summary>
    ///     Gets whether the source's arrows are dashed.
    /// </summary>
    public virtual bool IsDashed => false;

    /// <summary>
    ///     Returns the colour one of the source's arrows is drawn in.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>The colour; <see cref="Color" /> by default.</returns>
    public virtual Color ColorOf(FlowEdge edge)
    {
        return this.Color;
    }

    /// <summary>
    ///     Returns whether one of the source's arrows is dashed.
    /// </summary>
    /// <param name="edge">The arrow.</param>
    /// <returns>True if it is; <see cref="IsDashed" /> by default.</returns>
    public virtual bool IsDashedFor(FlowEdge edge)
    {
        return this.IsDashed;
    }

    /// <summary>
    ///     Gets the order sources are drawn and their arrows leave a step in; lower first.
    /// </summary>
    public virtual int Order => 0;

    /// <summary>
    ///     Returns the arrows leaving a step.
    /// </summary>
    /// <param name="graph">The graph, to find the nodes arrows go to.</param>
    /// <param name="node">The step's node.</param>
    /// <returns>The arrows; a <c>null</c> target is a step that doesn't exist, and isn't drawn.</returns>
    public abstract IEnumerable<FlowEdge> EdgesFrom(FlowGraphDataContext graph, FlowNodeDataContext node);

    /// <summary>
    ///     Makes the mini editor for one of the source's arrows, opened when it's clicked.
    /// </summary>
    /// <param name="graph">The graph the arrow is in.</param>
    /// <param name="edge">The arrow.</param>
    /// <returns>The editor.</returns>
    public abstract AbstractFlowArrowDataContext CreateArrow(FlowGraphDataContext graph, FlowEdge edge);

    /// <summary>
    ///     Describes a condition in a few words, for an arrow's label.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <returns>The description.</returns>
    protected static string Describe(ConditionDraftDataContext condition)
    {
        var text = condition switch
        {
            { UsesText: true, Contains: { Length: > 0 } contains } => $"has \"{contains}\"",
            { UsesText: true, Matches: { Length: > 0 } matches } => $"matches /{matches}/",
            { UsesInput: true, Input: { Length: > 0 } input } => $"{input} = {condition.EqualTo}",
            { UsesQuestion: true, Question: { Length: > 0 } question } => $"asks: {question}",
            { Children.Count: > 0 } => $"{condition.Kind} ({string.Join(", ", condition.Children.Select(Describe))})",
            _ => condition.Kind,
        };
        return Shorten(text);
    }

    /// <summary>
    ///     Cuts a label to <see cref="MaxLabelLength" />.
    /// </summary>
    /// <param name="text">The label.</param>
    /// <returns>The label, cut short with an ellipsis if it was too long.</returns>
    protected static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxLabelLength ? line : $"{line[..(MaxLabelLength - 1)].TrimEnd()}…";
    }
}

/// <summary>
///     Registers a flow edge source with the <see cref="FlowEdgeSourceRegistry" />.
/// </summary>
/// <param name="key">The source's name.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class FlowEdgeSourceRegistrationAttribute(string key) : AbstractRegistrationAttribute(key);

/// <summary>
///     Finds the flow edge sources.
/// </summary>
public class FlowEdgeSourceRegistry() : TypeRegistry<AbstractFlowEdgeSource, FlowEdgeSourceRegistrationAttribute>("flow edge source")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static FlowEdgeSourceRegistry Instance { get; } = new();

    /// <summary>
    ///     Gets one of every source, in drawing order.
    /// </summary>
    public IReadOnlyList<AbstractFlowEdgeSource> Sources => field ??= this.CreateAll().OrderBy(s => s.Order).ToList();
}

/// <summary>
///     A step's arrows: one per arrow, coloured by the outcome it fires on and dashed when it goes back.
/// </summary>
[FlowEdgeSourceRegistration("arrow")]
public class ArrowEdgeSource : AbstractFlowEdgeSource
{
    /// <summary>
    ///     The colours arrows are drawn in, by the outcome they fire on; any other outcome is drawn in <see cref="Color" />.
    /// </summary>
    private static readonly Dictionary<string, Color> OutcomeColors = new(StringComparer.Ordinal)
    {
        [ArrowConstants.OnSuccess] = Color.FromRgb(0x8A, 0x8F, 0x98),
        [ArrowConstants.OnFailure] = Color.FromRgb(0xEF, 0x44, 0x44),
        [ArrowConstants.OnTimeout] = Color.FromRgb(0xD9, 0x77, 0x06),
        [ArrowConstants.OnAlways] = Color.FromRgb(0x8B, 0x5C, 0xF6),
    };

    /// <inheritdoc />
    public override Color Color => Color.FromRgb(0x3B, 0x82, 0xF6);

    /// <inheritdoc />
    public override Color ColorOf(FlowEdge edge)
    {
        return edge.Arrow is { } arrow && OutcomeColors.TryGetValue(arrow.On, out var color) ? color : this.Color;
    }

    /// <inheritdoc />
    public override bool IsDashedFor(FlowEdge edge)
    {
        return edge.IsBack;
    }

    /// <inheritdoc />
    public override IEnumerable<FlowEdge> EdgesFrom(FlowGraphDataContext graph, FlowNodeDataContext node)
    {
        var step = node.Step!;
        var ordered = step.FollowsFirst && step.Arrows.Count > 1;
        for (var i = 0; i < step.Arrows.Count; i++)
        {
            var arrow = step.Arrows[i];
            yield return new FlowEdge(this, node, graph.NodeFor(arrow.To), Label(arrow, ordered ? i + 1 : null), arrow, graph.IsBack(node, arrow.To));
        }
    }

    /// <inheritdoc />
    public override AbstractFlowArrowDataContext CreateArrow(FlowGraphDataContext graph, FlowEdge edge)
    {
        return new ArrowDataContext(graph, edge.From, edge.Arrow!);
    }

    /// <summary>
    ///     Writes the label drawn beside an arrow: its own label, else its condition and limit, after its place in the
    ///     order when the step follows only the first.
    /// </summary>
    /// <param name="arrow">The arrow.</param>
    /// <param name="place">Its place in the order, or null when the order doesn't matter.</param>
    /// <returns>The label, or null for a plain arrow.</returns>
    private static string? Label(ArrowDraftDataContext arrow, int? place)
    {
        var parts = new List<string>();
        if (place is { } n)
        {
            parts.Add($"{n}.");
        }

        if (!string.IsNullOrWhiteSpace(arrow.Label))
        {
            parts.Add(Shorten(arrow.Label));
        }
        else
        {
            if (arrow.On != ArrowConstants.OnSuccess)
            {
                parts.Add($"on {arrow.On}");
            }

            if (arrow.HasCondition)
            {
                parts.Add($"{(arrow.ConditionMode == ArrowDraftDataContext.UntilCondition ? "until" : "when")} {Describe(arrow.Condition)}");
            }
        }

        if (arrow.Max is { } max)
        {
            parts.Add($"×{max:0}");
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}

/// <summary>
///     One arrow in the flow graph.
/// </summary>
/// <param name="Source">The kind of arrow, which picks how it's drawn.</param>
/// <param name="From">The step it leaves.</param>
/// <param name="To">The node it goes to; the end node for the end of the run, or null for a step that doesn't exist.</param>
/// <param name="Label">The text drawn beside it, if any.</param>
/// <param name="Arrow">The step's arrow it stands for, if any, so it can be edited and removed.</param>
/// <param name="IsBack">Whether it goes back: to its own step, or to one before it.</param>
public record FlowEdge(AbstractFlowEdgeSource Source, FlowNodeDataContext From, FlowNodeDataContext? To, string? Label,
    ArrowDraftDataContext? Arrow = null, bool IsBack = false);
