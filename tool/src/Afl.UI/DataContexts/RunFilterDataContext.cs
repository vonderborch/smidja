using Afl.Core.Runs;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     A run state the filter offers, with how many runs the rest of the filter leaves in it.
/// </summary>
/// <param name="info">How the state is shown.</param>
public class RunStateChoiceDataContext(RunStateInfo info) : ReactiveObject
{
    private int _count;
    private bool _isPicked;

    /// <summary>
    ///     Gets how the state is shown.
    /// </summary>
    public RunStateInfo Info { get; } = info;

    /// <summary>
    ///     Gets the state.
    /// </summary>
    public RunState State => this.Info.State;

    /// <summary>
    ///     Gets or sets how many runs the rest of the filter leaves in the state.
    /// </summary>
    public int Count
    {
        get => this._count;
        set
        {
            this.RaiseAndSetIfChanged(ref this._count, value);
            this.RaisePropertyChanged(nameof(this.Text));
            this.RaisePropertyChanged(nameof(this.IsVisible));
        }
    }

    /// <summary>
    ///     Gets or sets whether the list is narrowed to runs in the state (and the others picked).
    /// </summary>
    public bool IsPicked
    {
        get => this._isPicked;
        set
        {
            this.RaiseAndSetIfChanged(ref this._isPicked, value);
            this.RaisePropertyChanged(nameof(this.IsVisible));
        }
    }

    /// <summary>
    ///     Gets whether the choice is offered: some run is in the state, or it's picked.
    /// </summary>
    public bool IsVisible => this.Count > 0 || this.IsPicked;

    /// <summary>
    ///     Gets the text shown after the state's symbol, e.g. "Needs you (2)".
    /// </summary>
    public string Text => $"{this.Info.Label} ({this.Count})";

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Info.Label;
    }
}

/// <summary>
///     A filter over runs by a search and by state: the search box's words must all be found in a run (see
///     <see cref="RunFilter.Search" />), and when states are picked the run must be in one of them. The tags are filtered
///     by a <see cref="TagFilterDataContext" /> alongside it.
/// </summary>
public class RunFilterDataContext : ReactiveObject
{
    private string? _searchText;

    /// <summary>
    ///     Raised when the search or the picked states change, so the owner can filter again (and then call
    ///     <see cref="Update" />).
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets or sets the search.
    /// </summary>
    public string? SearchText
    {
        get => this._searchText;
        set
        {
            if (this._searchText == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref this._searchText, value);
            this.OnEdited();
        }
    }

    /// <summary>
    ///     Gets every state, in the order <see cref="RunStates.All" /> lists them.
    /// </summary>
    public IReadOnlyList<RunStateChoiceDataContext> States { get; } = [.. RunStates.All.Select(s => new RunStateChoiceDataContext(s))];

    /// <summary>
    ///     Gets the picked states.
    /// </summary>
    public IReadOnlyList<RunState> PickedStates => [.. this.States.Where(s => s.IsPicked).Select(s => s.State)];

    /// <summary>
    ///     Gets whether the search or a picked state is narrowing the list.
    /// </summary>
    public bool HasFilter => !string.IsNullOrWhiteSpace(this.SearchText) || this.States.Any(s => s.IsPicked);

    /// <summary>
    ///     Builds the filter to list runs with.
    /// </summary>
    /// <param name="tags">The tags a run must have, every one.</param>
    /// <returns>The filter.</returns>
    public RunFilter ToFilter(IReadOnlyCollection<string> tags)
    {
        return new RunFilter { Search = this.SearchText, States = this.PickedStates, Tags = tags };
    }

    /// <summary>
    ///     Counts the runs in each state, from the runs the rest of the filter leaves.
    /// </summary>
    /// <param name="runs">The runs the search and tags leave.</param>
    public void Update(IEnumerable<Run> runs)
    {
        var counts = runs.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        foreach (var choice in this.States)
        {
            choice.Count = counts.GetValueOrDefault(choice.State);
        }
    }

    /// <summary>
    ///     Picks a state, or unpicks it.
    /// </summary>
    /// <param name="state">The state.</param>
    public void Toggle(RunState state)
    {
        var choice = this.States.First(s => s.State == state);
        choice.IsPicked = !choice.IsPicked;
        this.OnEdited();
    }

    /// <summary>
    ///     Empties the search and unpicks every state.
    /// </summary>
    /// <returns>Whether anything was narrowing the list.</returns>
    public bool Clear()
    {
        if (!this.HasFilter)
        {
            return false;
        }

        foreach (var choice in this.States)
        {
            choice.IsPicked = false;
        }

        this._searchText = null;
        this.RaisePropertyChanged(nameof(this.SearchText));
        this.OnEdited();
        return true;
    }

    /// <summary>
    ///     Raises <see cref="Edited" />.
    /// </summary>
    private void OnEdited()
    {
        this.RaisePropertyChanged(nameof(this.HasFilter));
        this.Edited?.Invoke(this, EventArgs.Empty);
    }
}
