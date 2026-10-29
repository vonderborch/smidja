using System.Text.RegularExpressions;

namespace Afl.Core.Helpers;

/// <summary>
///     Git repository discovery and remote parsing.
/// </summary>
public static partial class GitHelpers
{
    /// <summary>
    ///     Finds the root of the git repository containing <paramref name="startDirectory" />.
    /// </summary>
    /// <param name="startDirectory">Where to start looking.</param>
    /// <returns>The repo root, or null when not inside a repository.</returns>
    public static string? FindGitRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            // .git is a directory in a normal checkout and a file in a worktree or submodule.
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Returns the repo's remotes' fetch URLs, <c>origin</c> first and the rest by name.
    /// </summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <returns>The remotes, empty when there are none or git is unavailable.</returns>
    public static IReadOnlyList<(string Name, string Url)> GetRemoteUrls(string repoRoot)
    {
        try
        {
            var result = ProcessRunner.Run("git", ["remote", "-v"], repoRoot);
            if (!result.Success)
            {
                return [];
            }

            return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && (parts.Length < 3 || parts[2] == "(fetch)"))
                .Select(parts => (Name: parts[0], Url: parts[1]))
                .DistinctBy(r => r.Name)
                .OrderBy(r => r.Name == "origin" ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    ///     Splits a remote URL into its host and path: <c>https://host/path</c>, <c>ssh://git@host:2222/path</c> and
    ///     scp-style <c>git@host:path</c>. The path has no leading slash and no <c>.git</c>.
    /// </summary>
    /// <param name="remoteUrl">The remote URL.</param>
    /// <returns>The lower-case host and the path, or null for a local path or something unrecognised.</returns>
    public static (string Host, string Path)? ParseRemote(string? remoteUrl)
    {
        var text = remoteUrl?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string host;
        string path;
        if (text.Contains("://"))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git"))
            {
                return null;
            }

            (host, path) = (uri.Host, Uri.UnescapeDataString(uri.AbsolutePath));
        }
        else if (ScpRemoteRegex().Match(text) is { Success: true } match)
        {
            (host, path) = (match.Groups["host"].Value, match.Groups["path"].Value);
        }
        else
        {
            return null;
        }

        path = path.Trim('/');
        path = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        return host.Length == 0 || path.Length == 0 ? null : (host.ToLowerInvariant(), path);
    }

    /// <summary>
    ///     Matches scp-style remotes, <c>[user@]host:path</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^([\w.-]+@)?(?<host>[\w.-]+):(?<path>[^/\\].*)$")]
    private static partial Regex ScpRemoteRegex();
}
