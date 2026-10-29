using System.Collections.ObjectModel;
using Afl.Core.Repos;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     The skills picked when starting a run, shown by <c>Views/SkillPickerView</c>: the skills recommended for the repo,
///     each with a check box, above the run's skills as a <see cref="PickListDataContext" /> (each with Remove, then a
///     dropdown of every other skill and Add). Ticking a recommendation adds it to the list and unticking removes it;
///     adding or removing it in the list ticks or unticks it.
/// </summary>
public class SkillPickerDataContext : ReactiveObject
{
    /// <summary>
    ///     Whether a change is being copied between the check boxes and the list, so it isn't copied back.
    /// </summary>
    private bool _syncing;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SkillPickerDataContext" /> class.
    /// </summary>
    /// <param name="advice">What to offer: the workflow's skills and the recommendations.</param>
    /// <param name="skills">Every skill that can be picked.</param>
    public SkillPickerDataContext(SkillAdvice advice, IEnumerable<SkillDefinition> skills)
    {
        this.Choices = skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        this.Skills.SetChoices(this.Choices.Select(s => s.Id));
        this.Skills.Load(advice.Defaults);
        this.Skills.Edited += (_, _) => this.TickFromList();
        this.Advice = advice;
        this.Reload(advice);
    }

    /// <summary>
    ///     Gets every skill that can be picked, by name.
    /// </summary>
    public IReadOnlyList<SkillDefinition> Choices { get; }

    /// <summary>
    ///     Gets the run's skills.
    /// </summary>
    public PickListDataContext Skills { get; } = new();

    /// <summary>
    ///     Gets the skills recommended for the repo, ticked when they're in <see cref="Skills" />.
    /// </summary>
    public ObservableCollection<SelectableItem<SkillRecommendation>> Recommended { get; } = [];

    /// <summary>
    ///     Gets what was offered.
    /// </summary>
    public SkillAdvice Advice
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.HasRecommended));
            this.RaisePropertyChanged(nameof(this.StackText));
        }
    }

    /// <summary>
    ///     Gets a value indicating whether anything is recommended.
    /// </summary>
    public bool HasRecommended => this.Advice.Recommended.Count > 0;

    /// <summary>
    ///     Gets what the repo is built with, as the recommendations saw it.
    /// </summary>
    public string StackText => this.Advice.Stack is { } stack
        ? "Project: " + string.Join(", ", stack.Languages.Concat(stack.Frameworks))
        : "afl couldn't tell what this project is built with.";

    /// <summary>
    ///     Gets the skills picked for the run.
    /// </summary>
    /// <returns>The skill ids, in order; empty for none.</returns>
    public List<string> Picked()
    {
        return [.. this.Skills.Picked];
    }

    /// <summary>
    ///     Shows new recommendations, as after the project's languages and frameworks change, keeping what's picked.
    /// </summary>
    /// <param name="advice">The new advice.</param>
    public void Reload(SkillAdvice advice)
    {
        this.Advice = advice;
        foreach (var item in this.Recommended)
        {
            item.PropertyChanged -= this.OnTicked;
        }

        this.Recommended.Clear();
        foreach (var recommendation in advice.Recommended)
        {
            var item = new SelectableItem<SkillRecommendation>(recommendation, recommendation.Skill.Name,
                this.Skills.Picked.Contains(recommendation.Skill.Id));
            item.PropertyChanged += this.OnTicked;
            this.Recommended.Add(item);
        }
    }

    /// <summary>
    ///     Ticks every recommendation, adding them all to the run's skills.
    /// </summary>
    public void TickAll()
    {
        foreach (var item in this.Recommended)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>
    ///     Adds a ticked recommendation to the list, or removes an unticked one.
    /// </summary>
    /// <param name="sender">The recommendation.</param>
    /// <param name="e">The change.</param>
    private void OnTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (this._syncing || e.PropertyName != nameof(SelectableItem<SkillRecommendation>.IsSelected) ||
            sender is not SelectableItem<SkillRecommendation> item)
        {
            return;
        }

        this._syncing = true;
        try
        {
            if (item.IsSelected)
            {
                this.Skills.Add(item.Value.Skill.Id);
            }
            else
            {
                this.Skills.Remove(item.Value.Skill.Id);
            }
        }
        finally
        {
            this._syncing = false;
        }
    }

    /// <summary>
    ///     Ticks the recommendations that are in the list, after the list was edited.
    /// </summary>
    private void TickFromList()
    {
        if (this._syncing)
        {
            return;
        }

        this._syncing = true;
        try
        {
            foreach (var item in this.Recommended)
            {
                item.IsSelected = this.Skills.Picked.Contains(item.Value.Skill.Id);
            }
        }
        finally
        {
            this._syncing = false;
        }
    }
}

/// <summary>
///     What a repo is built with, shown by <c>Views/StackView</c>: its languages and frameworks as two
///     <see cref="PickListDataContext" />s offering the ones skills know about (any other can be typed).
/// </summary>
public class StackDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="StackDataContext" /> class.
    /// </summary>
    /// <param name="choices">The languages and frameworks to offer.</param>
    /// <param name="saved">The stack saved on the repo, if any.</param>
    public StackDataContext(StackChoices choices, RepoStack? saved)
    {
        this.Languages.SetChoices(choices.Languages);
        this.Languages.Load(saved?.Languages);
        this.Frameworks.SetChoices(choices.Frameworks);
        this.Frameworks.Load(saved?.Frameworks);
    }

    /// <summary>
    ///     Gets the languages.
    /// </summary>
    public PickListDataContext Languages { get; } = new();

    /// <summary>
    ///     Gets the frameworks.
    /// </summary>
    public PickListDataContext Frameworks { get; } = new();

    /// <summary>
    ///     Gets what's picked as a stack to save.
    /// </summary>
    /// <returns>The stack, or null when nothing is picked (which forgets a saved one).</returns>
    public RepoStack? ToStack()
    {
        var stack = RepoStack.Of(this.Languages.Picked, this.Frameworks.Picked);
        return stack.IsEmpty ? null : stack;
    }
}
