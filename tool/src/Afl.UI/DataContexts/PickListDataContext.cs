using System.Collections.ObjectModel;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     A list of ids picked from a set of choices, shown as the picked ids (each with Remove) above a dropdown of the
///     choices not yet picked and an Add button, e.g. the skills a workflow gives every step. An id typed into the
///     dropdown can be added too, such as a <c>{{params.x}}</c> template.
/// </summary>
public class PickListDataContext : ReactiveObject
{
    /// <summary>
    ///     Every id that can be picked, in the order they're offered.
    /// </summary>
    private List<string> _choices = [];

    /// <summary>
    ///     Creates the list.
    /// </summary>
    public PickListDataContext()
    {
        this.Picked.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(this.HasPicked));
        this.Available.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(this.HasAvailable));
    }

    /// <summary>
    ///     Raised when an id is added or removed by the person editing, not when the list is loaded.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets the picked ids, in the order they were added.
    /// </summary>
    public ObservableCollection<string> Picked { get; } = [];

    /// <summary>
    ///     Gets the choices that aren't picked yet, which the dropdown offers.
    /// </summary>
    public ObservableCollection<string> Available { get; } = [];

    /// <summary>
    ///     Gets whether anything is picked.
    /// </summary>
    public bool HasPicked => this.Picked.Count > 0;

    /// <summary>
    ///     Gets whether there's a choice left to pick.
    /// </summary>
    public bool HasAvailable => this.Available.Count > 0;

    /// <summary>
    ///     Gets whether there are any choices at all.
    /// </summary>
    public bool HasChoices => this._choices.Count > 0;

    /// <summary>
    ///     Gets or sets the id to add: a choice picked from the dropdown, or one typed in.
    /// </summary>
    public string? ToAdd
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.CanAdd));
        }
    }

    /// <summary>
    ///     Gets whether <see cref="ToAdd" /> can be added: it isn't blank or picked already.
    /// </summary>
    public bool CanAdd => this.ToAdd?.Trim() is { Length: > 0 } id && !this.Picked.Contains(id);

    /// <summary>
    ///     Gets the picked ids, or null when none are (how the definitions leave an empty list out).
    /// </summary>
    /// <returns>The ids, or null.</returns>
    public List<string>? ToList()
    {
        return this.Picked.Count > 0 ? [.. this.Picked] : null;
    }

    /// <summary>
    ///     Sets the choices on offer, keeping what's picked (even an id that's no longer a choice, so opening
    ///     something never loses it).
    /// </summary>
    /// <param name="choices">The ids that can be picked.</param>
    public void SetChoices(IEnumerable<string> choices)
    {
        this._choices = choices.Distinct().ToList();
        this.RebuildAvailable();
        this.RaisePropertyChanged(nameof(this.HasChoices));
    }

    /// <summary>
    ///     Replaces what's picked, as when a draft is loaded; doesn't count as an edit.
    /// </summary>
    /// <param name="picked">The picked ids.</param>
    public void Load(IEnumerable<string>? picked)
    {
        this.Picked.Clear();
        foreach (var id in (picked ?? []).Distinct())
        {
            this.Picked.Add(id);
        }

        this.ToAdd = null;
        this.RebuildAvailable();
    }

    /// <summary>
    ///     Adds <see cref="ToAdd" /> to the picked ids, if it can be.
    /// </summary>
    /// <returns>Whether it was added.</returns>
    public bool Add()
    {
        return this.CanAdd && this.Add(this.ToAdd!.Trim());
    }

    /// <summary>
    ///     Adds an id to the picked ids.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it was added (false if it's blank or picked already).</returns>
    public bool Add(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || this.Picked.Contains(id))
        {
            return false;
        }

        this.Picked.Add(id);
        this.Available.Remove(id);
        this.ToAdd = null;
        this.Edited?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    ///     Removes an id from the picked ids, offering it in the dropdown again if it's a choice.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it was picked.</returns>
    public bool Remove(string id)
    {
        if (!this.Picked.Remove(id))
        {
            return false;
        }

        this.RebuildAvailable();
        this.RaisePropertyChanged(nameof(this.CanAdd));
        this.Edited?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    ///     Rebuilds the dropdown from the choices that aren't picked.
    /// </summary>
    private void RebuildAvailable()
    {
        this.Available.Clear();
        foreach (var id in this._choices.Where(c => !this.Picked.Contains(c)))
        {
            this.Available.Add(id);
        }

        this.RaisePropertyChanged(nameof(this.CanAdd));
    }
}
