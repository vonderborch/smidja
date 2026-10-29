using System.Collections.ObjectModel;
using Afl.Core.Constants;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using Afl.UI.DataContexts;
using ReactiveUI;

namespace Afl.UI.Flow;

/// <summary>
///     Something the flow editor offers to add to a workflow: a new step of some type (the Add step menu), a common
///     step, or another workflow's steps (picked from a <see cref="FlowPickerDataContext" />). Each kind is a subclass
///     that knows how to write the step it adds.
/// </summary>
/// <param name="label">The text shown for it.</param>
/// <param name="description">What it adds, shown under its label and searched.</param>
public abstract class AbstractFlowPaletteItem(string label, string? description)
{
    /// <summary>
    ///     Gets the text shown in the palette.
    /// </summary>
    public string Label { get; } = label;

    /// <summary>
    ///     Gets what the item adds, shown as its tooltip.
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>
    ///     Gets the id of the document the item comes from, shown beside its label, or null for a new step.
    /// </summary>
    public virtual string? Key => null;

    /// <summary>
    ///     Gets the tags of the document the item comes from, which the picker filters by.
    /// </summary>
    public virtual IReadOnlyList<string> Tags => [];

    /// <summary>
    ///     Gets the tags as one line, e.g. <c>#review #planning</c>.
    /// </summary>
    public string TagLine => string.Join(" ", this.Tags.Select(t => $"#{t}"));

    /// <summary>
    ///     Gets whether the item has tags.
    /// </summary>
    public bool HasTags => this.Tags.Count > 0;

    /// <summary>
    ///     Gets the id the new step starts from; the graph numbers it when the workflow already has one.
    /// </summary>
    public abstract string BaseId { get; }

    /// <summary>
    ///     Gets whether the new step's name, prompt and instructions are only starting values, cleared if the step is
    ///     switched to a common step or an include before they're edited.
    /// </summary>
    public virtual bool HasPlaceholders => false;

    /// <summary>
    ///     Returns whether the item matches a search.
    /// </summary>
    /// <param name="text">The search text; empty matches everything.</param>
    /// <returns>True if it matches.</returns>
    public bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.Trim();
        return this.Label.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
               || this.Key?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) == true
               || this.Description?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) == true
               || this.Tags.Any(t => t.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Writes the step the item adds.
    /// </summary>
    /// <param name="id">The new step's id, unique in the workflow.</param>
    /// <returns>The step.</returns>
    public abstract AbstractStepDefinition CreateStep(string id);

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Label;
    }
}

/// <summary>
///     A new step written out in the workflow, of one of the registered step types.
/// </summary>
/// <param name="type">The step type.</param>
public class StepTypePaletteItem(string type)
    : AbstractFlowPaletteItem(StepHandlerRegistry.Instance.Create(type).DisplayName, StepHandlerRegistry.Instance.Create(type).Summary)
{
    /// <summary>
    ///     Gets the step type.
    /// </summary>
    public string Type { get; } = type;

    /// <inheritdoc />
    public override string BaseId => this.Type == StepConstants.TypeAi ? "step" : this.Type;

    /// <inheritdoc />
    public override bool HasPlaceholders => true;

    /// <inheritdoc />
    public override AbstractStepDefinition CreateStep(string id)
    {
        var step = StepHandlerRegistry.Instance.CreateDefinition(this.Type);
        step.Id = id;
        step.Name = $"New {this.Label.ToLowerInvariant()}";
        step.FillStarter();
        return step;
    }
}

/// <summary>
///     A step based on a common step from the library.
/// </summary>
/// <param name="common">The common step.</param>
public class CommonStepPaletteItem(CommonStepDefinition common)
    : AbstractFlowPaletteItem(string.IsNullOrWhiteSpace(common.Name) ? common.Id : common.Name, common.Description)
{
    /// <summary>
    ///     Gets the common step.
    /// </summary>
    public CommonStepDefinition Common { get; } = common;

    /// <inheritdoc />
    public override string BaseId => string.IsNullOrWhiteSpace(this.Common.Step.Id) ? this.Common.Id : this.Common.Step.Id;

    /// <inheritdoc />
    public override string Key => this.Common.Id;

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => this.Common.Tags ?? [];

    /// <inheritdoc />
    public override AbstractStepDefinition CreateStep(string id)
    {
        // Workflow and user steps carry their type, as the builder saves them, so the draft shows the right fields.
        var step = StepHandlerRegistry.Instance.CreateDefinition(this.Common.Step.Type);
        step.Id = id;
        step.Use = this.Common.Id;
        return step;
    }
}

/// <summary>
///     Another workflow's steps, spliced in where the step is.
/// </summary>
/// <param name="workflow">The workflow.</param>
public class IncludePaletteItem(WorkflowDefinition workflow)
    : AbstractFlowPaletteItem(string.IsNullOrWhiteSpace(workflow.Name) ? workflow.Id : workflow.Name, workflow.Description)
{
    /// <summary>
    ///     Gets the workflow.
    /// </summary>
    public WorkflowDefinition Workflow { get; } = workflow;

    /// <inheritdoc />
    public override string BaseId => this.Workflow.Id;

    /// <inheritdoc />
    public override string Key => this.Workflow.Id;

    /// <inheritdoc />
    public override IReadOnlyList<string> Tags => this.Workflow.Tags ?? [];

    /// <inheritdoc />
    public override AbstractStepDefinition CreateStep(string id)
    {
        return new AiStepDefinition { Id = id, Include = this.Workflow.Id };
    }
}

/// <summary>
///     The flow editor's pop-up for picking a common step or a workflow to include: the items, a search and a tag
///     filter.
/// </summary>
public class FlowPickerDataContext : ReactiveObject
{
    /// <summary>
    ///     Every item, in the order shown.
    /// </summary>
    private readonly IReadOnlyList<AbstractFlowPaletteItem> _all;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FlowPickerDataContext" /> class.
    /// </summary>
    /// <param name="title">The pop-up's title.</param>
    /// <param name="items">The items.</param>
    public FlowPickerDataContext(string title, IEnumerable<AbstractFlowPaletteItem> items)
    {
        this.Title = title;
        this._all = items.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList();
        this.Tags.Edited += (_, _) => this.Filter();
        this.Filter();
    }

    /// <summary>
    ///     Gets the pop-up's title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    ///     Gets or sets the search text, matched against each item's name, id, description and tags.
    /// </summary>
    public string? Search
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.Filter();
        }
    }

    /// <summary>
    ///     Gets the tag filter: an item must have every picked tag.
    /// </summary>
    public TagFilterDataContext Tags { get; } = new();

    /// <summary>
    ///     Gets the items the search and tags let through.
    /// </summary>
    public ObservableCollection<AbstractFlowPaletteItem> Items { get; } = [];

    /// <summary>
    ///     Gets whether nothing matches.
    /// </summary>
    public bool IsEmpty => this.Items.Count == 0;

    /// <summary>
    ///     Re-filters the items.
    /// </summary>
    private void Filter()
    {
        var tags = this.Tags.Tags;
        this.Items.Clear();
        foreach (var item in this._all.Where(i => i.Matches(this.Search) && tags.All(t => i.Tags.Contains(t, StringComparer.Ordinal))))
        {
            this.Items.Add(item);
        }

        this.Tags.Update(this.Items.Select(i => i.Tags));
        this.RaisePropertyChanged(nameof(this.IsEmpty));
    }
}
