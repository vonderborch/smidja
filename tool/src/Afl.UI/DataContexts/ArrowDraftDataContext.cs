using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An editable copy of one of a step's arrows in the builder: where the run goes once the step ends, on which
///     outcome, when, and how often if it goes back round a loop. Every edit raises <see cref="Edited" /> so the
///     workflow is re-validated live.
/// </summary>
public class ArrowDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     The condition choice for an arrow that fires whatever the step's output.
    /// </summary>
    public const string NoCondition = "always";

    /// <summary>
    ///     The condition choice for an arrow that fires only when its condition holds.
    /// </summary>
    public const string WhenCondition = "when…";

    /// <summary>
    ///     The condition choice for an arrow that fires while its condition doesn't hold yet.
    /// </summary>
    public const string UntilCondition = "until…";

    /// <summary>
    ///     The ids of the workflow's steps, for the go-to picker.
    /// </summary>
    private IReadOnlyList<string> _stepIds = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="ArrowDraftDataContext" /> class from an arrow.
    /// </summary>
    /// <param name="arrow">The arrow to copy.</param>
    public ArrowDraftDataContext(ArrowDefinition arrow)
    {
        this.To = arrow.To;
        this.On = arrow.Outcome;
        this.ConditionMode = arrow.When is not null ? WhenCondition : arrow.Until is not null ? UntilCondition : NoCondition;
        this.Max = arrow.Max;
        this.MoveOnAtLimit = arrow.OnLimit == StepConstants.OnLimitContinue;
        this.Label = arrow.Label;
        this.Condition = new ConditionDraftDataContext(arrow.Condition ?? new ConditionDefinition { Kind = StepConstants.ConditionOutput });
        this.Condition.Edited += (_, _) => this.Edited?.Invoke(this, EventArgs.Empty);
        this.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(this.ToChoices) or nameof(this.HasCondition)))
            {
                this.Edited?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>
    ///     Raised when any field changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets the outcomes an arrow can fire on.
    /// </summary>
    public static IReadOnlyList<string> OnChoices { get; } =
        [.. new[] { ArrowConstants.OnSuccess, ArrowConstants.OnFailure, ArrowConstants.OnTimeout, ArrowConstants.OnAlways }.Union(OutcomeRegistry.Instance.Keys.Order(StringComparer.Ordinal))];

    /// <summary>
    ///     Gets the ways an arrow can depend on a condition.
    /// </summary>
    public static IReadOnlyList<string> ConditionModes { get; } = [NoCondition, WhenCondition, UntilCondition];

    /// <summary>
    ///     Gets or sets the step the arrow goes to, or <c>end</c> to end the path.
    /// </summary>
    public string To
    {
        get;
        set
        {
            if (value is not null)
            {
                this.RaiseAndSetIfChanged(ref field, value);
            }
        }
    } = ArrowConstants.End;

    /// <summary>
    ///     Gets the places the arrow can go: every step, then <c>end</c> (and the current pick, so renaming a step never
    ///     loses it).
    /// </summary>
    public IReadOnlyList<string> ToChoices =>
        [.. this._stepIds, ArrowConstants.End, .. this._stepIds.Contains(this.To) || this.To == ArrowConstants.End ? [] : new[] { this.To }];

    /// <summary>
    ///     Gets or sets the outcome the arrow fires on, one of <see cref="OnChoices" />.
    /// </summary>
    public string On
    {
        get;
        set
        {
            if (value is not null)
            {
                this.RaiseAndSetIfChanged(ref field, value);
            }
        }
    } = ArrowConstants.OnSuccess;

    /// <summary>
    ///     Gets or sets how the arrow depends on <see cref="Condition" />, one of <see cref="ConditionModes" />.
    /// </summary>
    public string ConditionMode
    {
        get;
        set
        {
            if (value is not null)
            {
                this.RaiseAndSetIfChanged(ref field, value);
                this.RaisePropertyChanged(nameof(this.HasCondition));
            }
        }
    } = NoCondition;

    /// <summary>
    ///     Gets whether the arrow fires only by its condition.
    /// </summary>
    public bool HasCondition => this.ConditionMode != NoCondition;

    /// <summary>
    ///     Gets the condition the arrow depends on, when <see cref="HasCondition" />.
    /// </summary>
    public ConditionDraftDataContext Condition { get; }

    /// <summary>
    ///     Gets or sets, for an arrow that goes back, how many times it may run its target, or null for the default.
    /// </summary>
    public decimal? Max
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether, at its limit, the arrow counts as not taken and the run moves on, rather than the step
    ///     failing.
    /// </summary>
    public bool MoveOnAtLimit
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the text drawn on the arrow instead of a summary of its condition.
    /// </summary>
    public string? Label
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Sets the steps and agents the pickers offer.
    /// </summary>
    /// <param name="stepIds">The ids of the workflow's steps.</param>
    /// <param name="agentIds">The ids of the defined agents.</param>
    public void SetChoices(IReadOnlyList<string> stepIds, IReadOnlyList<string> agentIds)
    {
        var target = this.To;
        this._stepIds = stepIds;
        this.RaisePropertyChanged(nameof(this.ToChoices));
        this.To = target;
        this.Condition.SetChoices(stepIds, agentIds);
    }

    /// <summary>
    ///     Builds the arrow the draft describes.
    /// </summary>
    /// <returns>The arrow.</returns>
    public ArrowDefinition ToDefinition()
    {
        var condition = this.HasCondition ? this.Condition.ToDefinition() : null;
        return new ArrowDefinition
        {
            To = this.To,
            On = this.On == ArrowConstants.OnSuccess ? null : this.On,
            When = this.ConditionMode == WhenCondition ? condition : null,
            Until = this.ConditionMode == UntilCondition ? condition : null,
            Max = this.Max is { } max ? (int)max : null,
            OnLimit = this.MoveOnAtLimit ? StepConstants.OnLimitContinue : null,
            Label = string.IsNullOrWhiteSpace(this.Label) ? null : this.Label.Trim(),
        };
    }
}
