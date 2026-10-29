using System.Collections.ObjectModel;
using Afl.Core.Constants;
using Afl.Core.Flow;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An editable copy of a route's or loop's condition in the builder. The fields shown follow the kind's
///     <see cref="AbstractCondition.Fields" />, so a new kind needs no change here. A compound condition (<c>all</c>,
///     <c>any</c>, <c>not</c>) holds nested drafts of its own, to any depth, and the editor's template shows each one
///     with the same template. Every edit raises <see cref="Edited" /> so the workflow is re-validated live.
/// </summary>
public class ConditionDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     The step choice that leaves the field unset, so the condition looks at the step it's on.
    /// </summary>
    public const string ThisStepChoice = "(this step)";

    /// <summary>
    ///     The agent choice that leaves the field unset, so the step's agent answers.
    /// </summary>
    public const string StepAgentChoice = "(step's agent)";

    /// <summary>
    ///     The properties that are worked out from others, so changing them isn't an edit.
    /// </summary>
    private static readonly string[] DerivedProperties =
    [
        nameof(UsesStep), nameof(UsesInput), nameof(UsesText), nameof(UsesQuestion), nameof(UsesNested), nameof(KindHelp),
        nameof(StepChoices), nameof(AgentChoices),
    ];

    /// <summary>
    ///     The ids of the workflow's steps, for the step picker.
    /// </summary>
    private IReadOnlyList<string> _stepIds = [];

    /// <summary>
    ///     The ids of the defined agents, for the agent picker.
    /// </summary>
    private IReadOnlyList<string> _agentIds = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="ConditionDraftDataContext" /> class from a condition.
    /// </summary>
    /// <param name="condition">The condition to copy.</param>
    /// <param name="parent">The compound condition this one is nested in, or null for a top-level condition.</param>
    public ConditionDraftDataContext(ConditionDefinition condition, ConditionDraftDataContext? parent = null)
    {
        this.Parent = parent;
        this.StepRef = condition.Step ?? ThisStepChoice;
        this.Input = condition.Input;
        this.EqualTo = condition.EqualTo;
        this.Contains = condition.Contains;
        this.Matches = condition.Matches;
        this.Question = condition.Question;
        this.Agent = condition.Agent ?? StepAgentChoice;
        foreach (var child in condition.Conditions ?? [])
        {
            this.Children.Add(this.Track(new ConditionDraftDataContext(child, this)));
        }

        this.Kind = condition.Kind;

        this.PropertyChanged += (_, e) =>
        {
            if (!DerivedProperties.Contains(e.PropertyName))
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
    ///     Gets the compound condition this one is nested in, or null for a top-level condition.
    /// </summary>
    public ConditionDraftDataContext? Parent { get; }

    /// <summary>
    ///     Gets the kinds that can be chosen: every registered kind, at any depth.
    /// </summary>
    public static IReadOnlyList<string> KindChoices => [.. ConditionRegistry.Instance.Keys.Order()];

    /// <summary>
    ///     Gets or sets the kind, e.g. <c>output</c>.
    /// </summary>
    public string Kind
    {
        get;
        set
        {
            if (value is null)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.UsesStep));
            this.RaisePropertyChanged(nameof(this.UsesInput));
            this.RaisePropertyChanged(nameof(this.UsesText));
            this.RaisePropertyChanged(nameof(this.UsesQuestion));
            this.RaisePropertyChanged(nameof(this.UsesNested));
            this.RaisePropertyChanged(nameof(this.KindHelp));
            if (this.UsesNested && this.Children.Count == 0)
            {
                this.AddChild();
            }
        }
    } = StepConstants.ConditionAlways;

    /// <summary>
    ///     Gets what the kind checks.
    /// </summary>
    public string KindHelp =>
        ConditionRegistry.Instance.Registrations.FirstOrDefault(r => r.Key == this.Kind)?.Description ?? string.Empty;

    /// <summary>
    ///     Gets whether the kind looks at a step.
    /// </summary>
    public bool UsesStep => this.Fields.HasFlag(ConditionFields.Step);

    /// <summary>
    ///     Gets whether the kind checks an input's answer.
    /// </summary>
    public bool UsesInput => this.Fields.HasFlag(ConditionFields.Input);

    /// <summary>
    ///     Gets whether the kind checks the output's text.
    /// </summary>
    public bool UsesText => this.Fields.HasFlag(ConditionFields.Text);

    /// <summary>
    ///     Gets whether the kind asks an agent a question.
    /// </summary>
    public bool UsesQuestion => this.Fields.HasFlag(ConditionFields.Question);

    /// <summary>
    ///     Gets whether the kind combines nested conditions.
    /// </summary>
    public bool UsesNested => this.Fields.HasFlag(ConditionFields.Nested);

    /// <summary>
    ///     Gets or sets the step the condition looks at, or <see cref="ThisStepChoice" />.
    /// </summary>
    public string StepRef
    {
        get;
        set
        {
            if (value is not null)
            {
                this.RaiseAndSetIfChanged(ref field, value);
            }
        }
    } = ThisStepChoice;

    /// <summary>
    ///     Gets the steps that can be picked: <see cref="ThisStepChoice" />, then every step (validation reports a later
    ///     one). The current pick stays in the list, so renaming a step never loses it.
    /// </summary>
    public IReadOnlyList<string> StepChoices =>
        [ThisStepChoice, .. this._stepIds, .. this._stepIds.Contains(this.StepRef) || this.StepRef == ThisStepChoice ? [] : new[] { this.StepRef }];

    /// <summary>
    ///     Gets or sets the input whose answer is checked.
    /// </summary>
    public string? Input
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the answer wanted.
    /// </summary>
    public string? EqualTo
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the text wanted in the output.
    /// </summary>
    public string? Contains
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the pattern the output must match.
    /// </summary>
    public string? Matches
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the yes/no question asked about the output.
    /// </summary>
    public string? Question
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the agent that answers the question, or <see cref="StepAgentChoice" />.
    /// </summary>
    public string Agent
    {
        get;
        set
        {
            if (value is not null)
            {
                this.RaiseAndSetIfChanged(ref field, value);
            }
        }
    } = StepAgentChoice;

    /// <summary>
    ///     Gets the agents that can answer: <see cref="StepAgentChoice" />, then every agent (and the current pick).
    /// </summary>
    public IReadOnlyList<string> AgentChoices =>
        [StepAgentChoice, .. this._agentIds, .. this._agentIds.Contains(this.Agent) || this.Agent == StepAgentChoice ? [] : new[] { this.Agent }];

    /// <summary>
    ///     Gets the nested conditions of a compound condition.
    /// </summary>
    public ObservableCollection<ConditionDraftDataContext> Children { get; } = [];

    /// <summary>
    ///     Gets the fields the kind uses.
    /// </summary>
    private ConditionFields Fields => FieldsOf(this.Kind);

    /// <summary>
    ///     Sets the steps and agents the pickers offer, here and in the nested conditions.
    /// </summary>
    /// <param name="stepIds">The ids of the workflow's steps.</param>
    /// <param name="agentIds">The ids of the defined agents.</param>
    public void SetChoices(IReadOnlyList<string> stepIds, IReadOnlyList<string> agentIds)
    {
        var (step, agent) = (this.StepRef, this.Agent);
        this._stepIds = stepIds;
        this._agentIds = agentIds;
        this.RaisePropertyChanged(nameof(this.StepChoices));
        this.RaisePropertyChanged(nameof(this.AgentChoices));
        (this.StepRef, this.Agent) = (step, agent);
        foreach (var child in this.Children)
        {
            child.SetChoices(stepIds, agentIds);
        }
    }

    /// <summary>
    ///     Adds a nested condition to a compound condition.
    /// </summary>
    /// <returns>The new condition.</returns>
    public ConditionDraftDataContext AddChild()
    {
        var child = this.Track(new ConditionDraftDataContext(new ConditionDefinition { Kind = StepConstants.ConditionOutput }, this));
        child.SetChoices(this._stepIds, this._agentIds);
        this.Children.Add(child);
        this.Edited?.Invoke(this, EventArgs.Empty);
        return child;
    }

    /// <summary>
    ///     Removes a nested condition.
    /// </summary>
    /// <param name="child">The condition.</param>
    public void RemoveChild(ConditionDraftDataContext child)
    {
        if (this.Children.Remove(child))
        {
            child.Edited -= this.OnChildEdited;
            this.Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     Builds the condition the draft describes. Fields the kind doesn't use are left out.
    /// </summary>
    /// <returns>The condition.</returns>
    public ConditionDefinition ToDefinition()
    {
        return new ConditionDefinition
        {
            Kind = this.Kind,
            Step = this.UsesStep && this.StepRef != ThisStepChoice ? this.StepRef : null,
            Input = this.UsesInput ? NullIfBlank(this.Input)?.Trim() : null,
            EqualTo = this.UsesInput ? NullIfBlank(this.EqualTo) : null,
            Contains = this.UsesText ? NullIfBlank(this.Contains) : null,
            Matches = this.UsesText ? NullIfBlank(this.Matches) : null,
            Question = this.UsesQuestion ? NullIfBlank(this.Question) : null,
            Agent = this.UsesQuestion && this.Agent != StepAgentChoice ? this.Agent : null,
            Conditions = this.UsesNested ? this.Children.Select(c => c.ToDefinition()).ToList() : null,
        };
    }

    /// <summary>
    ///     Returns the fields a kind uses.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The fields; none for an unknown kind.</returns>
    private static ConditionFields FieldsOf(string kind)
    {
        return ConditionRegistry.Instance.Contains(kind) ? ConditionRegistry.Instance.Create(kind).Fields : ConditionFields.None;
    }

    /// <summary>
    ///     Returns null for blank text, and the text otherwise.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text, or null.</returns>
    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    ///     Subscribes to a nested condition's edits.
    /// </summary>
    /// <param name="child">The condition.</param>
    /// <returns>The condition.</returns>
    private ConditionDraftDataContext Track(ConditionDraftDataContext child)
    {
        child.Edited += this.OnChildEdited;
        return child;
    }

    /// <summary>
    ///     Passes a nested condition's edit on as an edit of this one.
    /// </summary>
    /// <param name="sender">The condition.</param>
    /// <param name="e">The event.</param>
    private void OnChildEdited(object? sender, EventArgs e)
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }
}
