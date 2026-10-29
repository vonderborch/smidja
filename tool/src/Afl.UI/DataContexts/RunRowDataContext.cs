using Afl.Core.Runs;

namespace Afl.UI.DataContexts;

/// <summary>
///     A run in the Runs page's run list, with the child and path runs folded under it while it's collapsed.
/// </summary>
/// <param name="run">The run.</param>
/// <param name="level">How deep it is nested under the folders and runs listed above it.</param>
/// <param name="descendants">Every child and path run under it, at any depth.</param>
/// <param name="isExpanded">Whether its child runs are shown.</param>
public class RunRowDataContext(Run run, int level, IReadOnlyList<Run> descendants, bool isExpanded)
    : AbstractRunListRowDataContext(level, descendants, isExpanded)
{
    /// <summary>
    ///     Gets the run.
    /// </summary>
    public Run Run { get; } = run;

    /// <inheritdoc />
    public override string ExpandKey => KeyOf(this.Run.Id);

    /// <summary>
    ///     Gets the name the run is shown with: its own, its ticket's title, or for a path run the step it runs.
    /// </summary>
    public string Title => this.Run.Title ?? (this.Run.IsPathRun ? $"⑂ path {this.Run.PathStepId}" : this.Run.Ticket.Title);

    /// <summary>
    ///     Gets the run's tags as shown, e.g. "#ui #urgent", or null when it has none.
    /// </summary>
    public string? TagsText => this.Run.Tags.Count == 0 ? null : "#" + string.Join("  #", this.Run.Tags);

    /// <summary>
    ///     Gets how the run's state is shown: its symbol, colour (<see cref="UiConverters.StateBrush" />) and name.
    /// </summary>
    public RunStateInfo State => RunStates.Info(this.Run.State);

    /// <summary>
    ///     Gets whether the run itself is stopped at a step waiting on a person.
    /// </summary>
    public bool NeedsYou => this.Run.WaitsForUser;

    /// <summary>
    ///     Gets the key a run's row is remembered as expanded by.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string runId)
    {
        return "run:" + runId;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Run.Id;
    }

    /// <inheritdoc />
    protected override string? DescribeContents()
    {
        var paths = this.Descendants.Count(d => d.IsPathRun);
        var parts = new[] { Count(this.Descendants.Count - paths, "child run"), Count(paths, "path") }.OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
