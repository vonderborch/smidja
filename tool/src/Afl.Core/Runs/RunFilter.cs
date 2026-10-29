namespace Afl.Core.Runs;

/// <summary>
///     What runs to list: those matching a search, in some states, with some tags. Each part left empty matches every
///     run.
/// </summary>
public class RunFilter
{
    /// <summary>
    ///     Gets the search: every word in it must be found in the run's id, title, ticket, tags, folder, workflow, repo,
    ///     agent or state, ignoring case.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    ///     Gets the states a run must be in one of, or none for any state.
    /// </summary>
    public IReadOnlyCollection<RunState> States { get; init; } = [];

    /// <summary>
    ///     Gets the tags a run must have, every one.
    /// </summary>
    public IReadOnlyCollection<string> Tags { get; init; } = [];

    /// <summary>
    ///     Gets whether the filter matches every run.
    /// </summary>
    public bool IsEmpty => this.SearchWords.Count == 0 && this.States.Count == 0 && this.Tags.Count == 0;

    /// <summary>
    ///     Gets the search's words.
    /// </summary>
    private IReadOnlyList<string> SearchWords =>
        (this.Search ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    ///     Gets the same filter, without its states: e.g. to count how many runs each state would show.
    /// </summary>
    /// <returns>The filter.</returns>
    public RunFilter WithoutStates()
    {
        return new RunFilter { Search = this.Search, Tags = this.Tags };
    }

    /// <summary>
    ///     Gets whether a run matches the filter.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>True if it does.</returns>
    public bool Matches(Run run)
    {
        if (this.States.Count > 0 && !this.States.Contains(run.State))
        {
            return false;
        }

        if (!this.Tags.All(tag => run.Tags.Contains(tag, RunLabels.Comparer)))
        {
            return false;
        }

        var words = this.SearchWords;
        if (words.Count == 0)
        {
            return true;
        }

        var text = SearchedText(run);
        return words.All(word => text.Any(t => t.Contains(word.TrimStart('#'), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    ///     Gets the text a search looks in for a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The text.</returns>
    private static List<string> SearchedText(Run run)
    {
        string?[] text =
        [
            run.Id, run.Title, run.Ticket.Key, run.Ticket.Title, run.Folder, run.Workflow.Name, run.WorkflowId, run.RepoName,
            run.Executor, run.PathStepId, RunStates.Info(run.State).Label, .. run.Tags,
        ];
        return [.. text.OfType<string>()];
    }
}
