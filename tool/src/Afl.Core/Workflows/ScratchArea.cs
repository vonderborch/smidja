using Afl.Core.Exceptions;
using Afl.Core.Runs;
using Afl.Core.Tickets;

namespace Afl.Core.Workflows;

/// <summary>
///     A run's scratch directory: a folder per ticket (or piece of work) under the scratch root at the repo's root
///     (<see cref="Settings.AflSettings.ScratchDirectory" />), where workflows keep working documents such as intents,
///     specs and reports. The root holds a <c>.gitignore</c> that ignores everything, itself included, so git never
///     tracks it and the repo's own ignore files are left alone.
/// </summary>
public static class ScratchArea
{
    /// <summary>
    ///     The prefix of the scratch template variables.
    /// </summary>
    public const string VariablePrefix = "scratch.";

    /// <summary>
    ///     The variable holding the scratch root, relative to the repo.
    /// </summary>
    public const string RootVariable = VariablePrefix + "root";

    /// <summary>
    ///     The variable holding the run's scratch directory, relative to the repo.
    /// </summary>
    public const string DirectoryVariable = VariablePrefix + "dir";

    /// <summary>
    ///     The variable holding the run's scratch directory as an absolute path in the main checkout, which agents
    ///     working in a worktree use to reach it.
    /// </summary>
    public const string PathVariable = VariablePrefix + "path";

    /// <summary>
    ///     The scratch variables.
    /// </summary>
    public static readonly IReadOnlyList<string> Variables = [RootVariable, DirectoryVariable, PathVariable];

    /// <summary>
    ///     The ignore file written at the scratch root: everything in it, itself included.
    /// </summary>
    private const string IgnoreFileContent = "# Made by afl: this folder holds working documents and is never tracked.\n*\n";

    /// <summary>
    ///     Checks and tidies a scratch root setting: a relative path inside the repo, with forward slashes.
    /// </summary>
    /// <param name="root">The setting.</param>
    /// <returns>The tidied root.</returns>
    /// <exception cref="AflException">It's absolute, leaves the repo or is inside <c>.git</c>.</exception>
    public static string Normalize(string root)
    {
        var tidy = root.Trim().Replace('\\', '/').Trim('/');
        var parts = tidy.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (tidy.Length == 0 || Path.IsPathRooted(root.Trim()) || tidy.StartsWith('~') ||
            parts.Any(p => p is "." or "..") || parts[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"The scratch directory '{root}' must be a relative folder inside the repo (not .git), e.g. .scratch.");
        }

        return string.Join('/', parts);
    }

    /// <summary>
    ///     Returns the scratch directory for a ticket, relative to the repo: <c>&lt;root&gt;/&lt;ticket slug&gt;</c>.
    /// </summary>
    /// <param name="root">The scratch root setting.</param>
    /// <param name="ticket">The ticket.</param>
    /// <returns>The directory.</returns>
    public static string DirectoryFor(string root, Ticket ticket)
    {
        return $"{Normalize(root)}/{TemplateVariables.Slug(ticket.Key)}";
    }

    /// <summary>
    ///     Returns the scratch variables' values for a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The values by name; empty values when the run has no scratch directory.</returns>
    public static IReadOnlyDictionary<string, string> Values(Run run)
    {
        var directory = run.ScratchDirectory ?? string.Empty;
        var slash = directory.LastIndexOf('/');
        return new Dictionary<string, string>
        {
            [RootVariable] = slash < 0 ? directory : directory[..slash],
            [DirectoryVariable] = directory,
            [PathVariable] = directory.Length == 0 ? string.Empty : Path.GetFullPath(Path.Combine(run.RepoPath, directory)),
        };
    }

    /// <summary>
    ///     Returns whether a path is inside the scratch root of a run's scratch directory.
    /// </summary>
    /// <param name="repoPath">The repo root.</param>
    /// <param name="scratchDirectory">The run's scratch directory, relative to the repo.</param>
    /// <param name="path">The path, absolute or relative to the repo.</param>
    /// <returns>True if it is.</returns>
    public static bool Contains(string repoPath, string? scratchDirectory, string path)
    {
        if (string.IsNullOrEmpty(scratchDirectory))
        {
            return false;
        }

        var root = RootPath(repoPath, scratchDirectory);
        var full = Path.GetFullPath(Path.Combine(repoPath, path));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns the full path of a scratch directory's root.
    /// </summary>
    /// <param name="repoPath">The repo root.</param>
    /// <param name="scratchDirectory">The scratch directory, relative to the repo.</param>
    /// <returns>The root's full path.</returns>
    private static string RootPath(string repoPath, string scratchDirectory)
    {
        var slash = scratchDirectory.LastIndexOf('/');
        return Path.GetFullPath(Path.Combine(repoPath, slash < 0 ? scratchDirectory : scratchDirectory[..slash]));
    }

    /// <summary>
    ///     Makes a run's scratch directory, and the ignore file at its root, if they aren't there yet.
    /// </summary>
    /// <param name="repoPath">The repo root.</param>
    /// <param name="scratchDirectory">The scratch directory, relative to the repo.</param>
    /// <returns>The directory's full path.</returns>
    public static string Ensure(string repoPath, string scratchDirectory)
    {
        var full = Path.GetFullPath(Path.Combine(repoPath, scratchDirectory));
        Directory.CreateDirectory(full);
        var ignore = Path.Combine(RootPath(repoPath, scratchDirectory), ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, IgnoreFileContent);
        }

        return full;
    }
}
