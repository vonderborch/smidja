using Afl.Core.Runs;

namespace Afl.UI.DataContexts;

/// <summary>
///     A folder in the Runs page's run list, with the runs and folders filed in it folded under it while it's collapsed.
/// </summary>
/// <param name="folder">The folder's path, e.g. <c>billing/q4</c>.</param>
/// <param name="level">How deep it is nested.</param>
/// <param name="descendants">Every run in it, at any depth, child runs included.</param>
/// <param name="runCount">How many runs are filed in it or the folders in it.</param>
/// <param name="hasSubfolders">Whether it has folders in it.</param>
/// <param name="isExpanded">Whether what's in it is shown.</param>
public class FolderRowDataContext(string folder, int level, IReadOnlyList<Run> descendants, int runCount, bool hasSubfolders, bool isExpanded)
    : AbstractRunListRowDataContext(level, descendants, isExpanded)
{
    /// <summary>
    ///     Gets the folder's path.
    /// </summary>
    public string Folder { get; } = folder;

    /// <summary>
    ///     Gets the folder's own name.
    /// </summary>
    public string Name => RunLabels.NameOf(this.Folder);

    /// <summary>
    ///     Gets how many runs are filed in the folder or the folders in it.
    /// </summary>
    public int RunCount { get; } = runCount;

    /// <inheritdoc />
    public override string ExpandKey => KeyOf(this.Folder);

    /// <inheritdoc />
    public override bool HasChildren => base.HasChildren || hasSubfolders;

    /// <summary>
    ///     Gets the key a folder's row is remembered as expanded by.
    /// </summary>
    /// <param name="folder">The folder's path.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string folder)
    {
        return "folder:" + folder.ToLowerInvariant();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Folder;
    }

    /// <inheritdoc />
    protected override string? DescribeContents()
    {
        return Count(this.RunCount, "run") ?? "Empty";
    }
}
