using System.Text.Json.Serialization;
using Afl.Core.GitHosts;
using Afl.Core.Jira;
using Afl.Core.Permissions;
using Afl.Core.Storage;

namespace Afl.Core.Repos;

/// <summary>
///     The contents of <c>~/.afl/repos.json</c>.
/// </summary>
[DocumentVersion(1, 0)]
public class RepoRegistryDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the registered repos.
    /// </summary>
    public List<RepoRecord> Repos { get; set; } = [];
}

/// <summary>
///     A repo afl knows about.
/// </summary>
public class RepoRecord
{
    /// <summary>
    ///     Gets or sets the stable id: the folder name plus a short hash of the path.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name (the folder name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the absolute path of the repo root.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the GitHub repository or GitLab project the repo's issues come from, detected from its remotes
    ///     when it's registered; null if it isn't linked to one.
    /// </summary>
    public RepoHostLink? Host { get; set; }

    /// <summary>
    ///     Gets or sets the workflow used when a run is started without <c>--workflow</c>.
    /// </summary>
    public string? DefaultWorkflowId { get; set; }

    /// <summary>
    ///     Gets or sets when the repo was first registered.
    /// </summary>
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>
    ///     Gets or sets the repo's Jira instance and saved filters, or null if it isn't linked to Jira.
    /// </summary>
    public RepoJiraLink? Jira { get; set; }

    /// <summary>
    ///     Gets or sets the folders created for the repo's runs, as paths such as <c>billing/q4</c>, so a folder stays
    ///     listed while it's empty. The folders runs are filed in are listed too, whether or not they're here.
    /// </summary>
    public List<string> RunFolders { get; set; } = [];

    /// <summary>
    ///     Gets or sets the permission rules for every run in the repo.
    /// </summary>
    public List<PermissionRule> PermissionRules { get; set; } = [];

    /// <summary>
    ///     Gets or sets the languages and frameworks the user said the repo uses, asked once while it was empty, so skills
    ///     can be recommended for it (<c>SkillRecommender</c>); null when never asked.
    /// </summary>
    public RepoStack? Stack { get; set; }
}

/// <summary>
///     The languages and frameworks a repo uses, as lowercase ids such as <c>csharp</c> or <c>fastapi</c>.
/// </summary>
public class RepoStack
{
    /// <summary>
    ///     Gets or sets the languages.
    /// </summary>
    public List<string> Languages { get; set; } = [];

    /// <summary>
    ///     Gets or sets the frameworks.
    /// </summary>
    public List<string> Frameworks { get; set; } = [];

    /// <summary>
    ///     Gets a value indicating whether it names nothing.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => this.Languages.Count == 0 && this.Frameworks.Count == 0;

    /// <summary>
    ///     Makes a stack from typed names: trimmed, lowercased, spaces turned into dashes, blanks and repeats dropped.
    /// </summary>
    /// <param name="languages">The languages.</param>
    /// <param name="frameworks">The frameworks.</param>
    /// <returns>The stack.</returns>
    public static RepoStack Of(IEnumerable<string>? languages, IEnumerable<string>? frameworks)
    {
        return new RepoStack { Languages = Normalize(languages), Frameworks = Normalize(frameworks) };
    }

    /// <summary>
    ///     Normalizes typed names into ids.
    /// </summary>
    /// <param name="names">The names.</param>
    /// <returns>The ids, in the order given.</returns>
    private static List<string> Normalize(IEnumerable<string>? names)
    {
        return (names ?? [])
            .Select(n => string.Join('-', n.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
///     The repo a ticket is used in, and a note for the user when the ticket's Jira project points somewhere else.
/// </summary>
/// <param name="Repo">The repo.</param>
/// <param name="Note">Why this repo was picked, or a warning that the ticket's project belongs to other repos; null when there is nothing to say.</param>
public record TicketRepoChoice(RepoRecord Repo, string? Note);
