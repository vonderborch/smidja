using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Afl.Core.Exceptions;
using Afl.Core.GitHosts;
using Afl.Core.Helpers;
using Afl.Core.Jira;
using Afl.Core.Storage;
using Afl.Core.Tickets;

namespace Afl.Core.Repos;

/// <summary>
///     The registered repos, shared by every afl front-end. The CLI registers the repo it runs in automatically, so the
///     UI sees it without any setup.
/// </summary>
/// <param name="paths">The global store paths.</param>
public partial class RepoRegistry(AflPaths paths) : AbstractJsonStore<RepoRegistryDocument>(paths.ReposFile)
{
    /// <summary>
    ///     The default workflow assigned to newly registered repos.
    /// </summary>
    public const string InitialDefaultWorkflowId = "feature";

    /// <summary>
    ///     Gets the git host connections remotes are matched against.
    /// </summary>
    public GitHostStore GitHosts { get; init; } = new(paths);

    /// <summary>
    ///     Gets every registered repo, ordered by name.
    /// </summary>
    /// <returns>The repos.</returns>
    public IReadOnlyList<RepoRecord> All()
    {
        return this.Load().Repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     Finds a repo by id, name, or path.
    /// </summary>
    /// <param name="idNameOrPath">The id, the name, or any path inside the repo.</param>
    /// <returns>The repo, or null.</returns>
    /// <exception cref="AflException">A name matches more than one repo.</exception>
    public RepoRecord? Find(string idNameOrPath)
    {
        var repos = this.Load().Repos;
        var byId = repos.FirstOrDefault(r => string.Equals(r.Id, idNameOrPath, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        var byName = repos.Where(r => string.Equals(r.Name, idNameOrPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 1)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"More than one repo is named '{idNameOrPath}'. Use its id: {string.Join(", ", byName.Select(r => r.Id))}.");
        }

        if (byName.Count == 1)
        {
            return byName[0];
        }

        if (Directory.Exists(idNameOrPath))
        {
            var root = GitHelpers.FindGitRoot(idNameOrPath);
            return root is null ? null : repos.FirstOrDefault(r => SamePath(r.Path, root));
        }

        return null;
    }

    /// <summary>
    ///     Returns the registered repo containing <paramref name="directory" />, registering it first if needed. Safe to call
    ///     on every command.
    /// </summary>
    /// <param name="directory">Any directory inside the repo.</param>
    /// <returns>The repo.</returns>
    /// <exception cref="AflException">The directory isn't inside a git repository.</exception>
    public RepoRecord EnsureRegistered(string directory)
    {
        var root = GitHelpers.FindGitRoot(directory)
                   ?? throw new AflException(AflErrorKind.InvalidInput, $"{directory} is not inside a git repository.");

        var existing = this.Load().Repos.FirstOrDefault(r => SamePath(r.Path, root));
        if (existing is not null)
        {
            return existing;
        }

        var host = this.GitHosts.DetectLink(root);
        return this.Update(document =>
        {
            // Another process may have registered it between the read above and taking the lock.
            var raced = document.Repos.FirstOrDefault(r => SamePath(r.Path, root));
            if (raced is not null)
            {
                return raced;
            }

            var record = new RepoRecord
            {
                Id = CreateId(root),
                Name = System.IO.Path.GetFileName(root),
                Path = root,
                Host = host,
                DefaultWorkflowId = InitialDefaultWorkflowId,
                RegisteredAt = DateTimeOffset.Now,
            };
            document.Repos.Add(record);
            return record;
        });
    }

    /// <summary>
    ///     Saves the languages and frameworks a repo uses.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="stack">The stack; null forgets it, so the user is asked again while the repo is empty.</param>
    /// <returns>The updated repo.</returns>
    public RepoRecord SetStack(string repoId, RepoStack? stack)
    {
        var saved = stack is null ? null : RepoStack.Of(stack.Languages, stack.Frameworks);
        return this.Modify(repoId, r => r.Stack = saved);
    }

    /// <summary>
    ///     Gets a repo by id, name, or path, throwing if it isn't registered.
    /// </summary>
    /// <param name="idNameOrPath">The id, the name, or a path inside the repo.</param>
    /// <returns>The repo.</returns>
    /// <exception cref="AflException">No such repo.</exception>
    public RepoRecord GetRequired(string idNameOrPath)
    {
        return this.Find(idNameOrPath)
               ?? throw new AflException(AflErrorKind.NotFound, $"No registered repo matches '{idNameOrPath}'.");
    }

    /// <summary>
    ///     Changes a repo's record.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="change">The change to apply.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">No such repo.</exception>
    public RepoRecord Modify(string repoId, Action<RepoRecord> change)
    {
        return this.Update(document =>
        {
            var record = document.Repos.FirstOrDefault(r => r.Id == repoId)
                         ?? throw new AflException(AflErrorKind.NotFound, $"No registered repo has id '{repoId}'.");
            change(record);
            return record;
        });
    }

    /// <summary>
    ///     Links a repo to a Jira instance, keeping its saved filters (and projects) if it was already linked.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="instanceId">The instance id.</param>
    /// <param name="projectKeys">The Jira projects to associate with the repo, replacing its current ones; null keeps them.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">A key isn't a valid project key.</exception>
    public RepoRecord LinkJira(string repoId, string instanceId, IEnumerable<string>? projectKeys = null)
    {
        var keys = projectKeys is null ? null : NormalizeProjectKeys(projectKeys);
        return this.Modify(repoId, repo =>
        {
            repo.Jira ??= new RepoJiraLink();
            repo.Jira.InstanceId = instanceId;
            if (keys is not null)
            {
                repo.Jira.Projects = keys;
            }
        });
    }

    /// <summary>
    ///     Changes a repo's Jira link.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="change">The change to apply.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">No such repo, or it isn't linked to Jira.</exception>
    public RepoRecord ModifyJiraLink(string repoId, Action<RepoJiraLink> change)
    {
        return this.Modify(repoId, repo => change(RequireJiraLink(repo)));
    }

    /// <summary>
    ///     Saves a named Jira filter on a repo's link, replacing one with the same name. The first filter saved becomes
    ///     the default.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="makeDefault">Whether to make it the default filter.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">The filter has no name, or the repo isn't linked.</exception>
    public RepoRecord SaveJiraFilter(string repoId, JiraFilter filter, bool makeDefault)
    {
        filter.Name = filter.Name.Trim();
        if (filter.Name.Length == 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, "The filter needs a name.");
        }

        return this.ModifyJiraLink(repoId, link =>
        {
            link.Filters.RemoveAll(f => string.Equals(f.Name, filter.Name, StringComparison.OrdinalIgnoreCase));
            link.Filters.Add(filter);
            if (makeDefault || link.Filters.Count == 1)
            {
                link.DefaultFilter = filter.Name;
            }
        });
    }

    /// <summary>
    ///     Removes a saved Jira filter, clearing the default if it was the default.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="name">The filter name.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">No such filter, or the repo isn't linked.</exception>
    public RepoRecord RemoveJiraFilter(string repoId, string name)
    {
        return this.ModifyJiraLink(repoId, link =>
        {
            var filter = link.FindFilter(name) ?? throw new AflException(AflErrorKind.NotFound, $"No Jira filter '{name}'.");
            link.Filters.Remove(filter);
            if (string.Equals(link.DefaultFilter, filter.Name, StringComparison.OrdinalIgnoreCase))
            {
                link.DefaultFilter = null;
            }
        });
    }

    /// <summary>
    ///     Sets or clears the Jira filter used when none is named.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="name">The filter name, or null for none.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">No such filter, or the repo isn't linked.</exception>
    public RepoRecord SetDefaultJiraFilter(string repoId, string? name)
    {
        return this.ModifyJiraLink(repoId, link =>
        {
            link.DefaultFilter = name is null
                ? null
                : (link.FindFilter(name) ?? throw new AflException(AflErrorKind.NotFound, $"No Jira filter '{name}'.")).Name;
        });
    }

    /// <summary>
    ///     Replaces the Jira projects associated with a repo.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="projectKeys">The project keys, in any case; duplicates and blanks are dropped.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">A key isn't a valid project key, or the repo isn't linked.</exception>
    public RepoRecord SetJiraProjects(string repoId, IEnumerable<string> projectKeys)
    {
        var keys = NormalizeProjectKeys(projectKeys);
        return this.ModifyJiraLink(repoId, link => link.Projects = keys);
    }

    /// <summary>
    ///     Adds Jira projects to a repo, keeping the ones it has.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="projectKeys">The project keys.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">A key isn't a valid project key, or the repo isn't linked.</exception>
    public RepoRecord AddJiraProjects(string repoId, IEnumerable<string> projectKeys)
    {
        var keys = NormalizeProjectKeys(projectKeys);
        return this.ModifyJiraLink(repoId, link => link.Projects = NormalizeProjectKeys(link.Projects.Concat(keys)));
    }

    /// <summary>
    ///     Removes Jira projects from a repo.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="projectKeys">The project keys.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">A project isn't associated with the repo, or the repo isn't linked.</exception>
    public RepoRecord RemoveJiraProjects(string repoId, IEnumerable<string> projectKeys)
    {
        var keys = NormalizeProjectKeys(projectKeys);
        return this.ModifyJiraLink(repoId, link =>
        {
            var missing = keys.Where(k => !link.HasProject(k)).ToList();
            if (missing.Count > 0)
            {
                throw new AflException(AflErrorKind.NotFound,
                    $"Not associated with this repo: {string.Join(", ", missing)}. Associated: {string.Join(", ", link.Projects.DefaultIfEmpty("none"))}.");
            }

            link.Projects.RemoveAll(p => keys.Contains(p, StringComparer.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    ///     Links a repo to a GitHub repository or GitLab project, replacing any link it had.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="connectionId">The git host connection id.</param>
    /// <param name="project">The project path, e.g. <c>owner/repo</c>, or its web URL.</param>
    /// <returns>The updated repo.</returns>
    /// <exception cref="AflException">The connection is unknown or the project isn't a path on it.</exception>
    public RepoRecord LinkHost(string repoId, string connectionId, string project)
    {
        var link = this.GitHosts.CreateLink(connectionId, project);
        return this.Modify(repoId, record => record.Host = link);
    }

    /// <summary>
    ///     Removes a repo's GitHub or GitLab link.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <returns>The updated repo.</returns>
    public RepoRecord UnlinkHost(string repoId)
    {
        return this.Modify(repoId, record => record.Host = null);
    }

    /// <summary>
    ///     Links a repo to the project its remotes point at, or unlinks it when none is on a known host.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <returns>The updated repo.</returns>
    public RepoRecord DetectHost(string repoId)
    {
        var link = this.GitHosts.DetectLink(this.GetRequired(repoId).Path);
        return this.Modify(repoId, record => record.Host = link);
    }

    /// <summary>
    ///     Finds the repos associated with a Jira project.
    /// </summary>
    /// <param name="projectKey">The project key.</param>
    /// <returns>The repos, ordered by name.</returns>
    public IReadOnlyList<RepoRecord> FindByJiraProject(string projectKey)
    {
        return this.All().Where(r => r.Jira?.HasProject(projectKey) == true).ToList();
    }

    /// <summary>
    ///     Picks the repo to use a ticket in. The given repo (the current one, or the one asked for) wins, with a note if
    ///     the ticket's Jira project is associated with other repos instead. Without one, the repo associated with the
    ///     ticket's project is used.
    /// </summary>
    /// <param name="reference">The ticket reference.</param>
    /// <param name="repo">The repo the user is in or asked for, if any.</param>
    /// <returns>The repo, with a note for the user.</returns>
    /// <exception cref="AflException">There is no repo to use, or more than one repo is associated with the project.</exception>
    public TicketRepoChoice ResolveForTicket(TicketRef reference, RepoRecord? repo)
    {
        var project = reference.ProjectKey;
        var owners = project is null ? [] : this.FindByJiraProject(project);
        if (repo is not null)
        {
            var mismatch = project is not null && owners.Count > 0 && owners.All(o => o.Id != repo.Id);
            var outside = project is not null && repo.Jira is { Projects.Count: > 0 } link && !link.HasProject(project);
            string? note = null;
            if (mismatch)
            {
                note = $"{reference} is from Jira project {project}, which is associated with {string.Join(", ", owners.Select(o => o.Name))}, not {repo.Name}.";
            }
            else if (outside)
            {
                note = $"{reference} is from Jira project {project}, which isn't one of {repo.Name}'s projects ({string.Join(", ", repo.Jira!.Projects)}).";
            }

            return new TicketRepoChoice(repo, note);
        }

        return owners.Count switch
        {
            1 => new TicketRepoChoice(owners[0], $"Using {owners[0].Name}, which is associated with Jira project {project}."),
            > 1 => throw new AflException(AflErrorKind.InvalidInput,
                $"Jira project {project} is associated with more than one repo ({string.Join(", ", owners.Select(o => $"{o.Name} [{o.Id}]"))}). Pick one with --repo."),
            _ => throw new AflException(AflErrorKind.InvalidInput, project is null
                ? "Not inside a git repo. Run this from a repo, or pass --repo <id|name|path>."
                : $"Not inside a git repo, and no repo is associated with Jira project {project}. Run this from a repo, pass --repo, or associate one with: afl jira project add {project}"),
        };
    }

    /// <summary>
    ///     Upper-cases, checks and de-duplicates Jira project keys.
    /// </summary>
    /// <param name="projectKeys">The keys as typed.</param>
    /// <returns>The keys.</returns>
    /// <exception cref="AflException">A key isn't a valid project key.</exception>
    public static List<string> NormalizeProjectKeys(IEnumerable<string> projectKeys)
    {
        var keys = projectKeys.Select(k => k.Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct().ToList();
        var bad = keys.Where(k => !ProjectKeyRegex().IsMatch(k)).ToList();
        if (bad.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"Not a Jira project key: {string.Join(", ", bad)}. Keys look like PROJ (see afl jira projects).");
        }

        return keys;
    }

    /// <summary>
    ///     Returns a repo's Jira link, or fails if it has none.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <returns>The link.</returns>
    /// <exception cref="AflException">The repo isn't linked.</exception>
    public static RepoJiraLink RequireJiraLink(RepoRecord repo)
    {
        return repo.Jira ?? throw new AflException(AflErrorKind.InvalidState,
            $"{repo.Name} isn't linked to Jira. Link it with: afl jira link --instance <id>");
    }

    /// <summary>
    ///     Unregisters a repo. Its runs are kept.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <returns>True if it was registered.</returns>
    public bool Remove(string repoId)
    {
        return this.Update(document => document.Repos.RemoveAll(r => r.Id == repoId) > 0);
    }

    /// <summary>
    ///     Builds a repo id: the folder name plus a short hash of the full path, so same-named repos don't collide.
    /// </summary>
    /// <param name="root">The repo root.</param>
    /// <returns>The id.</returns>
    public static string CreateId(string root)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizePath(root)));
        var name = new string(System.IO.Path.GetFileName(root).ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return $"{name}-{Convert.ToHexString(hash)[..6].ToLowerInvariant()}";
    }

    /// <summary>
    ///     Compares two paths the way the file system does.
    /// </summary>
    /// <param name="a">The first path.</param>
    /// <param name="b">The second path.</param>
    /// <returns>True if they refer to the same location.</returns>
    private static bool SamePath(string a, string b)
    {
        return string.Equals(NormalizePath(a), NormalizePath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Matches a Jira project key.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("^[A-Z][A-Z0-9_]+$")]
    private static partial Regex ProjectKeyRegex();

    /// <summary>
    ///     Normalizes a path for comparison and hashing.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The full path without a trailing separator.</returns>
    private static string NormalizePath(string path)
    {
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }
}
