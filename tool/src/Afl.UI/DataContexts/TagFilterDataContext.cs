using System.Collections.ObjectModel;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     A tag the filter offers, with how many of the items shown now have it (so how many would be left after picking it).
/// </summary>
/// <param name="Tag">The tag.</param>
/// <param name="Count">How many shown items have it.</param>
public record TagChoice(string Tag, int Count)
{
    /// <summary>
    ///     Gets the text shown for the choice, e.g. "python (6)".
    /// </summary>
    public string Label => $"{this.Tag} ({this.Count})";

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Tag;
    }
}

/// <summary>
///     A filter by tags that stays small however many tags there are: the picked tags as removable chips, the most
///     common other tags as one-click suggestions, and a search box over the rest. An item must have every picked tag.
///     Its choices are worked out from the items shown now (<see cref="Update" />), so each count is how many items would
///     be left, and a tag that would leave nothing isn't offered.
/// </summary>
public class TagFilterDataContext : ReactiveObject
{
    /// <summary>
    ///     How many of the most common tags are offered as suggestions.
    /// </summary>
    public const int SuggestionCount = 6;

    /// <summary>
    ///     Creates the filter.
    /// </summary>
    public TagFilterDataContext()
    {
        this.Selected.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(this.HasSelected));
        this.Choices.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(this.HasChoices));
            this.RaisePropertyChanged(nameof(this.SearchPlaceholder));
        };
    }

    /// <summary>
    ///     Raised when a tag is picked or removed, so the owner can filter again (and then call <see cref="Update" />).
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets the picked tags, in the order they were picked.
    /// </summary>
    public ObservableCollection<string> Selected { get; } = [];

    /// <summary>
    ///     Gets the most common tags that aren't picked, most common first.
    /// </summary>
    public ObservableCollection<TagChoice> Suggestions { get; } = [];

    /// <summary>
    ///     Gets every tag that isn't picked and would leave something shown, by name, for the search box.
    /// </summary>
    public ObservableCollection<TagChoice> Choices { get; } = [];

    /// <summary>
    ///     Gets whether any tag is picked.
    /// </summary>
    public bool HasSelected => this.Selected.Count > 0;

    /// <summary>
    ///     Gets whether there's a tag left to pick.
    /// </summary>
    public bool HasChoices => this.Choices.Count > 0;

    /// <summary>
    ///     Gets whether the filter has anything to show: a picked tag or one to pick.
    /// </summary>
    public bool IsVisible => this.HasSelected || this.HasChoices;

    /// <summary>
    ///     Gets the search box's placeholder, saying how many tags it searches.
    /// </summary>
    public string SearchPlaceholder => this.Choices.Count == 1 ? "Search 1 tag…" : $"Search {this.Choices.Count} tags…";

    /// <summary>
    ///     Gets the picked tags as a list, for a query.
    /// </summary>
    public IReadOnlyList<string> Tags => [.. this.Selected];

    /// <summary>
    ///     Works out the choices from the tags of the items shown now (which already have every picked tag).
    /// </summary>
    /// <param name="shownTags">Each shown item's tags.</param>
    public void Update(IEnumerable<IEnumerable<string>> shownTags)
    {
        var counts = shownTags
            .SelectMany(tags => tags.Distinct(StringComparer.Ordinal))
            .Where(t => !this.Selected.Contains(t))
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => new TagChoice(g.Key, g.Count()))
            .ToList();

        this.Choices.Clear();
        foreach (var choice in counts.OrderBy(c => c.Tag, StringComparer.Ordinal))
        {
            this.Choices.Add(choice);
        }

        this.Suggestions.Clear();
        foreach (var choice in counts.OrderByDescending(c => c.Count).ThenBy(c => c.Tag, StringComparer.Ordinal).Take(SuggestionCount))
        {
            this.Suggestions.Add(choice);
        }

        this.RaisePropertyChanged(nameof(this.IsVisible));
    }

    /// <summary>
    ///     Picks a tag.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it was picked (false if it's blank or picked already).</returns>
    public bool Add(string? tag)
    {
        var trimmed = tag?.Trim();
        if (string.IsNullOrEmpty(trimmed) || this.Selected.Contains(trimmed))
        {
            return false;
        }

        this.Selected.Add(trimmed);
        this.OnEdited();
        return true;
    }

    /// <summary>
    ///     Picks the tag typed into the search box: the choice it names exactly (ignoring case), or the only choice
    ///     containing it.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <returns>Whether a tag was picked.</returns>
    public bool AddTyped(string? text)
    {
        var trimmed = text?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var exact = this.Choices.FirstOrDefault(c => c.Tag.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        var containing = this.Choices.Where(c => c.Tag.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return this.Add(exact?.Tag ?? (containing.Count == 1 ? containing[0].Tag : null));
    }

    /// <summary>
    ///     Removes a picked tag.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it was picked.</returns>
    public bool Remove(string tag)
    {
        if (!this.Selected.Remove(tag))
        {
            return false;
        }

        this.OnEdited();
        return true;
    }

    /// <summary>
    ///     Removes every picked tag.
    /// </summary>
    /// <returns>Whether any was picked.</returns>
    public bool Clear()
    {
        if (this.Selected.Count == 0)
        {
            return false;
        }

        this.Selected.Clear();
        this.OnEdited();
        return true;
    }

    /// <summary>
    ///     Raises <see cref="Edited" />.
    /// </summary>
    private void OnEdited()
    {
        this.RaisePropertyChanged(nameof(this.IsVisible));
        this.Edited?.Invoke(this, EventArgs.Empty);
    }
}
