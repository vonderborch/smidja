using Afl.Core.Constants;
using Afl.UI.DataContexts;
using ReactiveUI;

namespace Afl.UI.Flow;

/// <summary>
///     The flow editor's mini editor for one arrow, opened by clicking it: where it goes, and the settings of what it
///     stands for, without opening the step it leaves. Each kind of arrow's
///     <see cref="AbstractFlowEdgeSource" /> makes its own subclass, so a new kind of arrow brings its own editor.
///     The editor names the arrow by what it stands for rather than by the drawn <see cref="FlowEdge" />, which is
///     made again on every layout, and closes when that's gone.
/// </summary>
public abstract class AbstractFlowArrowDataContext : ReactiveObject
{
    /// <summary>
    ///     The places the arrow could go when they were last offered.
    /// </summary>
    private IReadOnlyList<string> _targetChoices = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractFlowArrowDataContext" /> class.
    /// </summary>
    /// <param name="graph">The graph the arrow is in.</param>
    /// <param name="from">The step the arrow leaves.</param>
    protected AbstractFlowArrowDataContext(FlowGraphDataContext graph, FlowNodeDataContext from)
    {
        this.Graph = graph;
        this.From = from;
    }

    /// <summary>
    ///     Gets the node of the step the arrow leaves.
    /// </summary>
    public FlowNodeDataContext From { get; }

    /// <summary>
    ///     Gets the step the arrow leaves.
    /// </summary>
    public StepDraftDataContext Step => this.From.Step!;

    /// <summary>
    ///     Gets what kind of arrow it is, for the editor's title.
    /// </summary>
    public abstract string Title { get; }

    /// <summary>
    ///     Gets when the arrow is followed, in a sentence.
    /// </summary>
    public abstract string Help { get; }

    /// <summary>
    ///     Gets the line naming the steps the arrow joins, e.g. "Plan → Review".
    /// </summary>
    public string Heading => $"{this.From.Heading} → {this.Graph.NodeFor(this.TargetId)?.Heading ?? this.Target}";

    /// <summary>
    ///     Gets the places the arrow can go, as ids.
    /// </summary>
    public IReadOnlyList<string> TargetChoices => this._targetChoices = this.ComputeTargetChoices();

    /// <summary>
    ///     Gets or sets where the arrow goes, as one of <see cref="TargetChoices" />.
    /// </summary>
    public abstract string Target { get; set; }

    /// <summary>
    ///     Gets the Remove button's text.
    /// </summary>
    public virtual string RemoveLabel => "Remove arrow";

    /// <summary>
    ///     Gets the graph the arrow is in.
    /// </summary>
    protected FlowGraphDataContext Graph { get; }

    /// <summary>
    ///     Gets the id of the step the arrow goes to, or <c>end</c>.
    /// </summary>
    protected virtual string TargetId => this.Target;

    /// <summary>
    ///     Returns whether a drawn arrow is the one this edits.
    /// </summary>
    /// <param name="edge">The drawn arrow.</param>
    /// <returns>True if it is.</returns>
    public abstract bool Matches(FlowEdge edge);

    /// <summary>
    ///     Removes what the arrow stands for from the step.
    /// </summary>
    public abstract void Remove();

    /// <summary>
    ///     Points the arrow at another node, e.g. the one its end was dragged onto.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>True if the draft changed; false if the arrow can't go there or already does.</returns>
    public virtual bool Retarget(FlowNodeDataContext node)
    {
        var target = this.TargetFor(node);
        if (target == this.Target || !this.TargetChoices.Contains(target))
        {
            return false;
        }

        this.Target = target;
        return true;
    }

    /// <summary>
    ///     Updates what the editor shows after the graph is laid out again, e.g. once a step is renamed.
    /// </summary>
    internal void OnLayout()
    {
        if (!this._targetChoices.SequenceEqual(this.ComputeTargetChoices()))
        {
            this.RaisePropertyChanged(nameof(this.TargetChoices));
            this.RaisePropertyChanged(nameof(this.Target));
        }

        this.RaisePropertyChanged(nameof(this.Heading));
        this.RaisePropertyChanged(nameof(this.Help));
        this.OnLayoutChanged();
    }

    /// <summary>
    ///     Updates what a subclass shows after the graph is laid out again.
    /// </summary>
    protected virtual void OnLayoutChanged()
    {
    }

    /// <summary>
    ///     Returns the places the arrow can go.
    /// </summary>
    /// <returns>The ids, as <see cref="Target" /> takes them.</returns>
    protected abstract IReadOnlyList<string> ComputeTargetChoices();

    /// <summary>
    ///     Returns how <see cref="Target" /> names a node.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The step's id, or <c>end</c> for the end node.</returns>
    protected virtual string TargetFor(FlowNodeDataContext node)
    {
        return node.Step?.Id.Trim() ?? ArrowConstants.End;
    }
}

/// <summary>
///     Edits one of a step's arrows: where it goes, the outcome it fires on, its condition and label, and, when it goes
///     back, how often it may; when the step starts parallel paths, its workspace too.
/// </summary>
public class ArrowDataContext : AbstractFlowArrowDataContext
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ArrowDataContext" /> class.
    /// </summary>
    /// <param name="graph">The graph the arrow is in.</param>
    /// <param name="from">The step the arrow leaves.</param>
    /// <param name="arrow">The arrow.</param>
    public ArrowDataContext(FlowGraphDataContext graph, FlowNodeDataContext from, ArrowDraftDataContext arrow)
        : base(graph, from)
    {
        this.Arrow = arrow;
    }

    /// <summary>
    ///     Gets the arrow.
    /// </summary>
    public ArrowDraftDataContext Arrow { get; }

    /// <summary>
    ///     Gets whether the arrow goes back, to its own step or one before it, so it has a limit.
    /// </summary>
    public bool IsLoop => this.Graph.IsBack(this.From, this.Arrow.To);

    /// <inheritdoc />
    public override string Title => this.IsLoop ? "Loop" : "Arrow";

    /// <inheritdoc />
    public override string Help
    {
        get
        {
            var arrows = this.Step.Arrows;
            var order = this.Step.FollowsFirst && arrows.Count > 1
                ? $" The step follows only the first arrow that holds; this is {arrows.IndexOf(this.Arrow) + 1} of {arrows.Count}."
                : string.Empty;
            var when = this.IsLoop
                ? "Goes back and runs the loop again, before any arrow forward is followed."
                : this.Step.IsFork
                    ? "Starts a path alongside the step's other arrows."
                    : "Followed once the step ends.";
            return when + order;
        }
    }

    /// <inheritdoc />
    public override string Target
    {
        get => this.Arrow.To;
        set
        {
            if (value is not null && value != this.Arrow.To)
            {
                this.Arrow.To = value;
                this.RaisePropertyChanged();
                this.OnLayoutChanged();
            }
        }
    }

    /// <inheritdoc />
    public override bool Matches(FlowEdge edge)
    {
        return edge.Arrow == this.Arrow;
    }

    /// <inheritdoc />
    public override void Remove()
    {
        this.Step.RemoveArrow(this.Arrow);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> ComputeTargetChoices()
    {
        return this.Arrow.ToChoices;
    }

    /// <inheritdoc />
    protected override void OnLayoutChanged()
    {
        this.RaisePropertyChanged(nameof(this.IsLoop));
        this.RaisePropertyChanged(nameof(this.Title));
    }
}
