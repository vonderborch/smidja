using System.IO.Enumeration;
using Afl.Core.Helpers;

namespace Afl.Core.Workflows;

/// <summary>
///     A quick look at the files in a repo, for recommending skills: the files <c>git ls-files</c> lists (tracked and
///     untracked, less what <c>.gitignore</c> ignores), or, outside git, a capped walk that skips build output and
///     dependency folders. Nothing is run and no file is read except to check a <see cref="SkillFileRule.Contains" />.
/// </summary>
public class RepoScan
{
    /// <summary>
    ///     The most files a walk outside git lists.
    /// </summary>
    public const int MaxFiles = 5000;

    /// <summary>
    ///     The deepest folder a walk outside git goes into.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>
    ///     The biggest file read to check what it contains, in bytes.
    /// </summary>
    public const long MaxContainsBytes = 256 * 1024;

    /// <summary>
    ///     The folders a walk outside git skips.
    /// </summary>
    public static readonly IReadOnlySet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".venv", "venv", "dist", "build", "target", "__pycache__", ".idea", ".vs",
    };

    /// <summary>
    ///     The extensions of files that don't say what a repo is built with, so a repo holding only these counts as empty.
    /// </summary>
    private static readonly IReadOnlySet<string> DocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc",
    };

    /// <summary>
    ///     The names (without extension) of files that don't say what a repo is built with, such as its licence.
    /// </summary>
    private static readonly IReadOnlySet<string> DocumentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS", "CODEOWNERS", "README", "CHANGELOG",
    };

    /// <summary>
    ///     What each file read so far contains, by path from the root; null when it is too big or unreadable.
    /// </summary>
    private readonly Dictionary<string, string?> _contents = new(StringComparer.Ordinal);

    /// <summary>
    ///     Initializes a new instance of the <see cref="RepoScan" /> class.
    /// </summary>
    /// <param name="root">The repo root.</param>
    /// <param name="files">The files, as paths from the root with <c>/</c> separators.</param>
    public RepoScan(string root, IReadOnlyList<string> files)
    {
        this.Root = root;
        this.Files = files;
    }

    /// <summary>
    ///     Gets the repo root.
    /// </summary>
    public string Root { get; }

    /// <summary>
    ///     Gets the files, as paths from the root with <c>/</c> separators.
    /// </summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    ///     Gets a value indicating whether the repo has nothing that says what it's built with: only docs, licences and
    ///     dotfiles, or nothing at all.
    /// </summary>
    public bool IsEmpty => this.Files.All(f =>
    {
        var name = Path.GetFileNameWithoutExtension(f);
        return f.Split('/').Any(p => p.StartsWith('.')) || DocumentExtensions.Contains(Path.GetExtension(f)) ||
               DocumentNames.Contains(name);
    });

    /// <summary>
    ///     Lists a repo's files.
    /// </summary>
    /// <param name="root">The repo root.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The scan.</returns>
    public static async Task<RepoScan> OfAsync(string root, CancellationToken ct = default)
    {
        return new RepoScan(root, await GitFilesAsync(root, ct).ConfigureAwait(false) ?? Walk(root));
    }

    /// <summary>
    ///     Returns the files a rule matches: those whose name (or, for a pattern with a <c>/</c>, path) fits its glob and
    ///     that contain its text, if it has any.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The matching paths.</returns>
    public IEnumerable<string> Matching(SkillFileRule rule)
    {
        var glob = rule.Glob.Trim().TrimStart('/');
        var byPath = glob.Contains('/');
        foreach (var file in this.Files)
        {
            var subject = byPath ? file : file[(file.LastIndexOf('/') + 1)..];
            if (!FileSystemName.MatchesSimpleExpression(glob, subject))
            {
                continue;
            }

            if (rule.Contains is { Length: > 0 } text &&
                this.ContentOf(file)?.Contains(text, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>
    ///     Reads a file's text once, if it's small enough.
    /// </summary>
    /// <param name="file">The path from the root.</param>
    /// <returns>The text, or null.</returns>
    private string? ContentOf(string file)
    {
        if (this._contents.TryGetValue(file, out var cached))
        {
            return cached;
        }

        string? text = null;
        try
        {
            var path = Path.Combine(this.Root, file);
            var info = new FileInfo(path);
            if (info.Exists && info.Length <= MaxContainsBytes && (info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                text = File.ReadAllText(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = null;
        }

        this._contents[file] = text;
        return text;
    }

    /// <summary>
    ///     Lists the files git knows about (tracked, and untracked but not ignored).
    /// </summary>
    /// <param name="root">The repo root.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The files, or null when the folder isn't a git work tree or git can't be run.</returns>
    private static async Task<IReadOnlyList<string>?> GitFilesAsync(string root, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
        {
            return null;
        }

        try
        {
            var result = await ProcessRunner.RunAsync("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], root, null, ct)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            return result.StandardOutput.Split('\0')
                .Select(f => f.Trim('\r', '\n'))
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxFiles * 4)
                .ToList();
        }
        catch (Exceptions.AflException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Walks the folder breadth-first, skipping <see cref="SkippedDirectories" /> and links, up to
    ///     <see cref="MaxFiles" /> files and <see cref="MaxDepth" /> folders deep.
    /// </summary>
    /// <param name="root">The folder.</param>
    /// <returns>The files.</returns>
    private static IReadOnlyList<string> Walk(string root)
    {
        var files = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && files.Count < MaxFiles)
        {
            var (directory, depth) = queue.Dequeue();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        if (depth + 1 < MaxDepth && !SkippedDirectories.Contains(entry.Name))
                        {
                            queue.Enqueue((entry.FullName, depth + 1));
                        }
                    }
                    else if (files.Count < MaxFiles)
                    {
                        files.Add(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder just isn't listed.
            }
        }

        return files;
    }
}
