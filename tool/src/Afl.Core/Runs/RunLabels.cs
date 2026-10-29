using Afl.Core.Exceptions;

namespace Afl.Core.Runs;

/// <summary>
///     The rules for what the user labels runs with: their titles, tags and the folders they're filed in. Folders are
///     paths of names joined by <c>/</c> (e.g. <c>billing/q4</c>), matched ignoring case; a folder holds the folders
///     under it.
/// </summary>
public static class RunLabels
{
    /// <summary>
    ///     The character between the names in a folder path.
    /// </summary>
    public const char FolderSeparator = '/';

    /// <summary>
    ///     How folder paths and tags are compared.
    /// </summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    ///     Trims a title, turning a blank one into null (show the ticket's title).
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The trimmed title, or null.</returns>
    public static string? NormalizeTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    /// <summary>
    ///     Trims tags (and a leading <c>#</c>), drops blank ones and keeps the first of those that differ only by case.
    /// </summary>
    /// <param name="tags">The tags.</param>
    /// <returns>The tags, in the order given.</returns>
    /// <exception cref="AflException">A tag has a comma in it.</exception>
    public static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var normalized = new List<string>();
        foreach (var tag in tags.Select(t => t.Trim().TrimStart('#').Trim()).Where(t => t.Length > 0))
        {
            if (tag.Contains(','))
            {
                throw new AflException(AflErrorKind.InvalidInput, $"Tag '{tag}' has a comma in it; tags are listed with commas between them.");
            }

            if (!normalized.Contains(tag, Comparer))
            {
                normalized.Add(tag);
            }
        }

        return normalized;
    }

    /// <summary>
    ///     Tidies a folder path: trims each name, drops empty ones and accepts <c>\</c> between them. A blank path is the
    ///     top level.
    /// </summary>
    /// <param name="folder">The folder path.</param>
    /// <returns>The path, or null for the top level.</returns>
    /// <exception cref="AflException">A name in it is <c>.</c> or <c>..</c>.</exception>
    public static string? NormalizeFolder(string? folder)
    {
        var names = (folder ?? string.Empty).Split([FolderSeparator, '\\']).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        if (names.Any(n => n is "." or ".."))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"Folder '{folder}' can't have '.' or '..' in it.");
        }

        return names.Count == 0 ? null : string.Join(FolderSeparator, names);
    }

    /// <summary>
    ///     Tidies a folder path that must name a folder (<see cref="NormalizeFolder" />).
    /// </summary>
    /// <param name="folder">The folder path.</param>
    /// <returns>The path.</returns>
    /// <exception cref="AflException">The path is blank, or has <c>.</c> or <c>..</c> in it.</exception>
    public static string RequireFolder(string? folder)
    {
        return NormalizeFolder(folder) ?? throw new AflException(AflErrorKind.InvalidInput, "Name the folder, e.g. billing/q4.");
    }

    /// <summary>
    ///     Gets the folder a folder is in.
    /// </summary>
    /// <param name="folder">The folder path.</param>
    /// <returns>The parent's path, or null when the folder is at the top level.</returns>
    public static string? ParentOf(string folder)
    {
        var last = folder.LastIndexOf(FolderSeparator);
        return last < 0 ? null : folder[..last];
    }

    /// <summary>
    ///     Gets a folder's own name, the last in its path.
    /// </summary>
    /// <param name="folder">The folder path.</param>
    /// <returns>The name.</returns>
    public static string NameOf(string folder)
    {
        return folder[(folder.LastIndexOf(FolderSeparator) + 1)..];
    }

    /// <summary>
    ///     Gets whether a path is a folder or inside it, at any depth.
    /// </summary>
    /// <param name="path">The path, or null for the top level.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>True if it is the folder or inside it.</returns>
    public static bool IsWithin(string? path, string folder)
    {
        return path is not null && (Comparer.Equals(path, folder) ||
                                    path.StartsWith(folder + FolderSeparator, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Moves a path from one folder to another: the folder itself becomes the new one, and what's inside it keeps its
    ///     place under the new one. Paths outside the folder stay as they are.
    /// </summary>
    /// <param name="path">The path, or null for the top level.</param>
    /// <param name="from">The folder being moved.</param>
    /// <param name="to">Where it goes, or null to the top level.</param>
    /// <returns>The moved path (null for the top level).</returns>
    public static string? Rebase(string? path, string from, string? to)
    {
        if (!IsWithin(path, from))
        {
            return path;
        }

        var rest = path![from.Length..].TrimStart(FolderSeparator);
        return rest.Length == 0 ? to : to is null ? rest : to + FolderSeparator + rest;
    }

    /// <summary>
    ///     Lists folders with every folder they're in, once each (ignoring case), sorted.
    /// </summary>
    /// <param name="folders">The folder paths.</param>
    /// <returns>The folders and their parents.</returns>
    public static List<string> WithParents(IEnumerable<string?> folders)
    {
        var all = new HashSet<string>(Comparer);
        foreach (var folder in folders.OfType<string>())
        {
            for (string? f = folder; f is not null; f = ParentOf(f))
            {
                all.Add(f);
            }
        }

        return [.. all.Order(StringComparer.OrdinalIgnoreCase)];
    }
}
