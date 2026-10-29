using System.Text.Json.Serialization;
using Afl.Core.Storage;

namespace Afl.Core.Jira;

/// <summary>
///     The flavour of Jira an instance runs, which decides the client (API version and authentication) used to talk to it.
/// </summary>
public enum JiraInstanceKind
{
    /// <summary>
    ///     Jira Cloud (<c>*.atlassian.net</c>): REST v3 with an email address and API token.
    /// </summary>
    Cloud,

    /// <summary>
    ///     Jira Data Center or Server: REST v2 with a personal access token.
    /// </summary>
    Datacenter,
}

/// <summary>
///     A Jira site afl can talk to. The token is not stored here; it lives in the OS secure store.
/// </summary>
public class JiraInstance
{
    /// <summary>
    ///     Gets or sets the short id used on the command line and in repo links.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets which flavour of Jira this is.
    /// </summary>
    public JiraInstanceKind Kind { get; set; }

    /// <summary>
    ///     Gets or sets the site's base URL, e.g. <c>https://acme.atlassian.net</c> or <c>https://jira.acme.com/jira</c>.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the account email, which Cloud needs alongside the API token.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    ///     Gets or sets when the instance was added.
    /// </summary>
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
///     The <c>jira.json</c> document.
/// </summary>
[DocumentVersion(1, 0)]
public class JiraInstanceDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the configured instances.
    /// </summary>
    public List<JiraInstance> Instances { get; set; } = [];
}

/// <summary>
///     Criteria for finding Jira issues. Every field that is set narrows the search (they are ANDed together). Saved on a
///     repo's Jira link under a name, and also used for one-off searches.
/// </summary>
public class JiraFilter
{
    /// <summary>
    ///     Gets or sets the name the filter is saved under.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a project key.
    /// </summary>
    public string? Project { get; set; }

    /// <summary>
    ///     Gets or sets an agile board id; issues are then read from that board.
    /// </summary>
    public int? BoardId { get; set; }

    /// <summary>
    ///     Gets or sets a team. On Cloud this is the team id (Teams field); on Data Center it is a raw JQL clause.
    /// </summary>
    public string? Team { get; set; }

    /// <summary>
    ///     Gets or sets an assignee: <c>me</c> for yourself, <c>none</c> for unassigned, or a user name or account id.
    /// </summary>
    public string? Assignee { get; set; }

    /// <summary>
    ///     Gets or sets statuses, any of which match. When set, they replace the open/closed state filter.
    /// </summary>
    public List<string> Statuses { get; set; } = [];

    /// <summary>
    ///     Gets or sets labels that must all be present.
    /// </summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>
    ///     Gets or sets extra raw JQL, ANDed with the rest. An <c>ORDER BY</c> in it replaces the default ordering.
    /// </summary>
    public string? Jql { get; set; }

    /// <summary>
    ///     Gets or sets the projects to search when <see cref="Project" /> isn't set: the repo's associated projects.
    ///     Not saved with the filter; <see cref="JiraTicketSource" /> fills it in from the repo's Jira link.
    /// </summary>
    [JsonIgnore]
    public List<string> ProjectScope { get; set; } = [];

    /// <summary>
    ///     Gets whether no criteria are set.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => this.Project is null && this.BoardId is null && this.Team is null && this.Assignee is null &&
                           this.Statuses.Count == 0 && this.Labels.Count == 0 && string.IsNullOrWhiteSpace(this.Jql);

    /// <summary>
    ///     Returns a copy of this filter with every criterion that is set on <paramref name="overrides" /> replaced.
    /// </summary>
    /// <param name="overrides">The criteria that win.</param>
    /// <returns>The merged filter.</returns>
    public JiraFilter MergedWith(JiraFilter overrides)
    {
        return new JiraFilter
        {
            Name = this.Name,
            Project = overrides.Project ?? this.Project,
            BoardId = overrides.BoardId ?? this.BoardId,
            Team = overrides.Team ?? this.Team,
            Assignee = overrides.Assignee ?? this.Assignee,
            Statuses = overrides.Statuses.Count > 0 ? overrides.Statuses.ToList() : this.Statuses.ToList(),
            Labels = overrides.Labels.Count > 0 ? overrides.Labels.ToList() : this.Labels.ToList(),
            Jql = overrides.Jql ?? this.Jql,
            ProjectScope = overrides.ProjectScope.Count > 0 ? overrides.ProjectScope.ToList() : this.ProjectScope.ToList(),
        };
    }
}

/// <summary>
///     A repo's link to a Jira instance, with its saved filters.
/// </summary>
public class RepoJiraLink
{
    /// <summary>
    ///     Gets or sets the id of the linked <see cref="JiraInstance" />.
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the keys of the Jira projects whose tickets belong in this repo. Listings that don't name a
    ///     project (or board) are limited to them, and a ticket key from one of them finds this repo.
    /// </summary>
    public List<string> Projects { get; set; } = [];

    /// <summary>
    ///     Gets or sets the saved filters.
    /// </summary>
    public List<JiraFilter> Filters { get; set; } = [];

    /// <summary>
    ///     Gets or sets the filter used when none is named.
    /// </summary>
    public string? DefaultFilter { get; set; }

    /// <summary>
    ///     Gets whether a project is one of the repo's associated projects.
    /// </summary>
    /// <param name="projectKey">The project key.</param>
    /// <returns>True if it is.</returns>
    public bool HasProject(string projectKey)
    {
        return this.Projects.Contains(projectKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Finds a saved filter by name.
    /// </summary>
    /// <param name="name">The filter name.</param>
    /// <returns>The filter, or null.</returns>
    public JiraFilter? FindFilter(string name)
    {
        return this.Filters.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     A Jira project, as listed for discovery.
/// </summary>
/// <param name="Key">The project key.</param>
/// <param name="Name">The project name.</param>
public record JiraProject(string Key, string Name);

/// <summary>
///     A Jira agile board, as listed for discovery.
/// </summary>
/// <param name="Id">The board id, used in filters.</param>
/// <param name="Name">The board name.</param>
/// <param name="Type">The board type (scrum, kanban, simple).</param>
/// <param name="ProjectKey">The board's project, if it has one.</param>
public record JiraBoard(int Id, string Name, string? Type, string? ProjectKey);

/// <summary>
///     The signed-in Jira user, used to check a connection.
/// </summary>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="AccountName">The account id (Cloud) or user name (Data Center).</param>
public record JiraUser(string DisplayName, string? AccountName);
