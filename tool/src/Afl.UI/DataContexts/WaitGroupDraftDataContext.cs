using System.Collections.ObjectModel;
using Afl.Core.Constants;
using Afl.Core.Flow;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     One item of a custom <c>waitFor</c> in the builder: the arrow from one step, or a group of further items. Items
///     nest to any depth, each drawn by its own DataTemplate, and every edit raises <see cref="Edited" /> up to the
///     step so the workflow is re-validated live.
/// </summary>
public abstract class AbstractWaitItemDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractWaitItemDraftDataContext" /> class.
    /// </summary>
    /// <param name="parent">The group the item is in, or null for the step's whole <c>waitFor</c>.</param>
    protected AbstractWaitItemDraftDataContext(WaitGroupDraftDataContext? parent)
    {
        this.Parent = parent;
        this.PropertyChanged += (_, e) =>
        {
            if (!this.IsDerived(e.PropertyName))
            {
                this.RaiseEdited();
            }
        };
    }

    /// <summary>
    ///     Raised when the item, or anything in it, changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets the group the item is in, or null for the step's whole <c>waitFor</c>.
    /// </summary>
    public WaitGroupDraftDataContext? Parent { get; }

    /// <summary>
    ///     Gets whether the item can be removed from its group.
    /// </summary>
    public bool CanRemove => this.Parent is not null;

    /// <summary>
    ///     Makes the draft of a rule.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="parent">The group it is in, or null for the step's whole <c>waitFor</c>.</param>
    /// <returns>The draft.</returns>
    public static AbstractWaitItemDraftDataContext From(AbstractWaitRule rule, WaitGroupDraftDataContext? parent)
    {
        return rule is ArrowWaitRule arrow
            ? new WaitStepDraftDataContext(arrow.Step, parent)
            : new WaitGroupDraftDataContext((AbstractWaitGroup)rule, parent);
    }

    /// <summary>
    ///     Builds the rule the draft describes.
    /// </summary>
    /// <returns>The rule.</returns>
    public abstract AbstractWaitRule ToRule();

    /// <summary>
    ///     Sets the steps the pickers offer, here and in any nested items.
    /// </summary>
    /// <param name="stepIds">The steps whose arrows lead into the step.</param>
    public abstract void SetChoices(IReadOnlyList<string> stepIds);

    /// <summary>
    ///     Points the item, and any nested items, at a step's new id.
    /// </summary>
    /// <param name="oldId">The step's old id.</param>
    /// <param name="newId">Its new id.</param>
    public abstract void RenameStep(string oldId, string newId);

    /// <summary>
    ///     Gets whether a property is worked out from others, so changing it isn't an edit.
    /// </summary>
    /// <param name="propertyName">The property.</param>
    /// <returns>True if it is derived.</returns>
    protected virtual bool IsDerived(string? propertyName)
    {
        return propertyName == nameof(this.CanRemove);
    }

    /// <summary>
    ///     Raises <see cref="Edited" />.
    /// </summary>
    protected void RaiseEdited()
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
///     The arrow from one step, as an item of a custom <c>waitFor</c>.
/// </summary>
public class WaitStepDraftDataContext : AbstractWaitItemDraftDataContext
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="WaitStepDraftDataContext" /> class.
    /// </summary>
    /// <param name="step">The step whose arrow it is.</param>
    /// <param name="parent">The group it is in.</param>
    public WaitStepDraftDataContext(string step, WaitGroupDraftDataContext? parent)
        : base(parent)
    {
        this.Step = step;
    }

    /// <summary>
    ///     Gets the steps that can be picked: those whose arrows lead in, plus the one picked now.
    /// </summary>
    public IReadOnlyList<string> StepChoices
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [];

    /// <summary>
    ///     Gets or sets the step whose arrow it is.
    /// </summary>
    public string Step
    {
        get;
        set
        {
            if (value is not null)
            {
                this.RaiseAndSetIfChanged(ref field, value);
            }
        }
    }

    /// <inheritdoc />
    public override AbstractWaitRule ToRule()
    {
        return new ArrowWaitRule { Step = this.Step };
    }

    /// <inheritdoc />
    public override void SetChoices(IReadOnlyList<string> stepIds)
    {
        var step = this.Step;
        this.StepChoices = stepIds.Contains(step) || step.Length == 0 ? stepIds : [.. stepIds, step];
        this.Step = step;
    }

    /// <inheritdoc />
    public override void RenameStep(string oldId, string newId)
    {
        if (this.Step == oldId)
        {
            this.Step = newId;
        }
    }

    /// <inheritdoc />
    protected override bool IsDerived(string? propertyName)
    {
        return propertyName == nameof(this.StepChoices) || base.IsDerived(propertyName);
    }
}

/// <summary>
///     A group of a custom <c>waitFor</c>: all, any or at least n of its items, each a step's arrow or a further
///     group. The kinds offered are the registered groups (<see cref="WaitRuleRegistry.GroupKeys" />).
/// </summary>
public class WaitGroupDraftDataContext : AbstractWaitItemDraftDataContext
{
    /// <summary>
    ///     The steps whose arrows lead into the step, for the pickers.
    /// </summary>
    private IReadOnlyList<string> _stepIds = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="WaitGroupDraftDataContext" /> class.
    /// </summary>
    /// <param name="group">The group to copy. One without items (every arrow in) gets one item per step in <paramref name="stepIds" />.</param>
    /// <param name="parent">The group it is in, or null for the step's whole <c>waitFor</c>.</param>
    /// <param name="stepIds">The steps whose arrows lead in, used when the group has no items of its own.</param>
    public WaitGroupDraftDataContext(AbstractWaitGroup group, WaitGroupDraftDataContext? parent, IReadOnlyList<string>? stepIds = null)
        : base(parent)
    {
        this.Kind = group.Kind;
        this.Count = group is AtLeastWaitRule atLeast ? atLeast.Count : 1;
        foreach (var item in group.Items ?? [.. (stepIds ?? []).Select(s => new ArrowWaitRule { Step = s })])
        {
            this.Items.Add(this.Track(From(item, this)));
        }

        this.Items.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(this.Summary));
            this.RaiseEdited();
        };
    }

    /// <summary>
    ///     Gets the kinds of group that can be picked.
    /// </summary>
    public static IReadOnlyList<string> KindChoices => WaitRuleRegistry.Instance.GroupKeys;

    /// <summary>
    ///     Gets or sets the kind of group: <c>all</c>, <c>any</c> or <c>atLeast</c>.
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
            this.RaisePropertyChanged(nameof(this.UsesCount));
            this.RaisePropertyChanged(nameof(this.Summary));
        }
    } = ArrowConstants.WaitAll;

    /// <summary>
    ///     Gets or sets how many items must hold, for an <c>atLeast</c> group.
    /// </summary>
    public decimal? Count
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.Summary));
        }
    }

    /// <summary>
    ///     Gets whether the kind counts its items (<c>atLeast</c>).
    /// </summary>
    public bool UsesCount => this.Kind == ArrowConstants.WaitAtLeast;

    /// <summary>
    ///     Gets the group written out, e.g. <c>any(all(build, test), docs)</c>.
    /// </summary>
    public string Summary => this.ToRule().Describe();

    /// <summary>
    ///     Gets the group's items.
    /// </summary>
    public ObservableCollection<AbstractWaitItemDraftDataContext> Items { get; } = [];

    /// <summary>
    ///     Adds the arrow from a step to the group: the first step not in it yet.
    /// </summary>
    /// <returns>The new item.</returns>
    public WaitStepDraftDataContext AddStep()
    {
        var used = this.Items.OfType<WaitStepDraftDataContext>().Select(i => i.Step).ToHashSet();
        var item = new WaitStepDraftDataContext(this._stepIds.FirstOrDefault(s => !used.Contains(s)) ?? this._stepIds.FirstOrDefault() ?? string.Empty, this);
        this.Add(item);
        return item;
    }

    /// <summary>
    ///     Adds a nested group, of the other kind to this one's, holding one step.
    /// </summary>
    /// <returns>The new group.</returns>
    public WaitGroupDraftDataContext AddGroup()
    {
        AbstractWaitGroup rule = this.Kind == ArrowConstants.WaitAny ? new AllWaitRule() : new AnyWaitRule();
        rule.Items = [];
        var group = new WaitGroupDraftDataContext(rule, this);
        this.Add(group);
        group.AddStep();
        return group;
    }

    /// <summary>
    ///     Removes an item.
    /// </summary>
    /// <param name="item">The item.</param>
    public void Remove(AbstractWaitItemDraftDataContext item)
    {
        if (this.Items.Remove(item))
        {
            item.Edited -= this.OnItemEdited;
        }
    }

    /// <inheritdoc />
    public override AbstractWaitRule ToRule()
    {
        var group = (AbstractWaitGroup)WaitRuleRegistry.Instance.Create(this.Kind);
        group.Items = [.. this.Items.Select(i => i.ToRule())];
        if (group is AtLeastWaitRule atLeast)
        {
            atLeast.Count = (int)(this.Count ?? 1);
        }

        return group;
    }

    /// <inheritdoc />
    public override void SetChoices(IReadOnlyList<string> stepIds)
    {
        this._stepIds = stepIds;
        foreach (var item in this.Items)
        {
            item.SetChoices(stepIds);
        }
    }

    /// <inheritdoc />
    public override void RenameStep(string oldId, string newId)
    {
        foreach (var item in this.Items)
        {
            item.RenameStep(oldId, newId);
        }
    }

    /// <inheritdoc />
    protected override bool IsDerived(string? propertyName)
    {
        return propertyName is nameof(this.UsesCount) or nameof(this.Summary) || base.IsDerived(propertyName);
    }

    /// <summary>
    ///     Adds an item, with this group's choices.
    /// </summary>
    /// <param name="item">The item.</param>
    private void Add(AbstractWaitItemDraftDataContext item)
    {
        item.SetChoices(this._stepIds);
        this.Items.Add(this.Track(item));
    }

    /// <summary>
    ///     Passes an item's edits up.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The item.</returns>
    private AbstractWaitItemDraftDataContext Track(AbstractWaitItemDraftDataContext item)
    {
        item.Edited += this.OnItemEdited;
        return item;
    }

    /// <summary>
    ///     Reports an item's edit as this group's.
    /// </summary>
    /// <param name="sender">The item.</param>
    /// <param name="e">The event.</param>
    private void OnItemEdited(object? sender, EventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.Summary));
        this.RaiseEdited();
    }
}
