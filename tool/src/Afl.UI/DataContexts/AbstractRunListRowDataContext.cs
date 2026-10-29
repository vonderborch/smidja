using Afl.Core.Runs;
using Avalonia;

namespace Afl.UI.DataContexts;

/// <summary>
///     How many runs under a row of the run list are in a state.
/// </summary>
/// <param name="Info">How the state is shown.</param>
/// <param name="Count">How many runs are in it.</param>
public record RunStateCount(RunStateInfo Info, int Count)
{
    /// <summary>
    ///     Gets the text shown, e.g. "● 2".
    /// </summary>
    public string Text => $"{this.Info.Symbol} {this.Count}";

    /// <summary>
    ///     Gets the tooltip, e.g. "2 needs you".
    /// </summary>
    public string Tip => $"{this.Count} {this.Info.Label.ToLowerInvariant()}";
}

/// <summary>
///     One row of the Runs page's run list: a folder or a run, at its nesting level, that can be expanded to show what's
///     under it. Rows are rebuilt whenever the list or what's expanded changes.
/// </summary>
/// <param name="level">How deep the row is nested.</param>
/// <param name="descendants">Every run under it, at any depth.</param>
/// <param name="isExpanded">Whether what's under it is shown.</param>
public abstract class AbstractRunListRowDataContext(int level, IReadOnlyList<Run> descendants, bool isExpanded)
{
    /// <summary>
    ///     How far each nesting level is indented.
    /// </summary>
    private const int IndentPerLevel = 16;

    /// <summary>
    ///     Gets how deep the row is nested.
    /// </summary>
    public int Level { get; } = level;

    /// <summary>
    ///     Gets the row's indent.
    /// </summary>
    public Thickness Indent => new(this.Level * IndentPerLevel, 0, 0, 0);

    /// <summary>
    ///     Gets the key the page remembers whether the row is expanded by, unique among every row.
    /// </summary>
    public abstract string ExpandKey { get; }

    /// <summary>
    ///     Gets whether the row has anything under it to show or hide.
    /// </summary>
    public virtual bool HasChildren => this.Descendants.Count > 0;

    /// <summary>
    ///     Gets whether what's under the row is shown.
    /// </summary>
    public bool IsExpanded { get; } = isExpanded;

    /// <summary>
    ///     Gets how many runs under the row wait on a person.
    /// </summary>
    public int WaitingCount { get; } = descendants.Count(d => d.WaitsForUser);

    /// <summary>
    ///     Gets whether a run under the row waits on a person, so the collapsed row can call it out.
    /// </summary>
    public bool HasWaitingChildren => this.WaitingCount > 0;

    /// <summary>
    ///     Gets what's under the row, e.g. "2 child runs, 3 paths · 1 needs you", or null when nothing is.
    /// </summary>
    public string? Summary => this.DescribeContents() is { } text
        ? this.WaitingCount > 0 ? $"{text} · {this.WaitingCount} need{(this.WaitingCount == 1 ? "s" : string.Empty)} you" : text
        : null;

    /// <summary>
    ///     Gets how many runs under the row are in each state, in the order <see cref="RunStates.All" /> lists them,
    ///     leaving out states no run is in.
    /// </summary>
    public IReadOnlyList<RunStateCount> StateCounts => [.. RunStates.All
        .Select(s => new RunStateCount(s, this.Descendants.Count(d => d.State == s.State)))
        .Where(c => c.Count > 0)];

    /// <summary>
    ///     Gets every run under the row, at any depth.
    /// </summary>
    protected IReadOnlyList<Run> Descendants { get; } = descendants;

    /// <summary>
    ///     Describes what's under the row, without what waits on a person.
    /// </summary>
    /// <returns>The description, or null when nothing is under it.</returns>
    protected abstract string? DescribeContents();

    /// <summary>
    ///     Counts something for a description, e.g. "1 path" or "3 paths".
    /// </summary>
    /// <param name="count">How many.</param>
    /// <param name="noun">What, singular.</param>
    /// <returns>The count, or null when it's 0.</returns>
    protected static string? Count(int count, string noun)
    {
        return count switch
        {
            0 => null,
            1 => $"1 {noun}",
            _ => $"{count} {noun}s",
        };
    }
}
