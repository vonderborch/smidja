using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Tickets;

namespace Afl.Core.GitHosts;

/// <summary>
///     GitLab's REST API (v4), on gitlab.com or a self-managed site (<c>&lt;site&gt;/api/v4</c>). Projects are addressed
///     by their URL-encoded path, so subgroups work. Sign-ins can be borrowed from <c>glab</c>.
/// </summary>
/// <param name="connection">The connection.</param>
/// <param name="token">The token, or null to make anonymous requests.</param>
/// <param name="http">The HTTP client; a base address on it replaces the API's.</param>
[GitHostClientRegistration(TicketRef.GitLabSource, "GitLab")]
public class GitLabClient(GitHostConnection connection, string? token, HttpClient http)
    : AbstractGitHostClient(connection, token, http)
{
    /// <inheritdoc />
    public override string DisplayName => "GitLab";

    /// <inheritdoc />
    public override string CliName => "glab";

    /// <inheritdoc />
    public override string CliVariable => EnvironmentConstants.GlabExecutable;

    /// <inheritdoc />
    public override string CloudHost => "gitlab.com";

    /// <inheritdoc />
    public override IReadOnlyList<string> EnvironmentTokens => EnvironmentConstants.GitLabTokens;

    /// <inheritdoc />
    public override string CliSignInCommand => $"glab auth login --hostname {this.Connection.Host}";

    /// <inheritdoc />
    public override string CreateTokenUrl => $"{this.WebBase}/-/user_settings/personal_access_tokens";

    /// <inheritdoc />
    protected override string DefaultApiBase => $"{this.WebBase}/api/v4/";

    /// <inheritdoc />
    protected override IReadOnlyList<string> CliTokenArguments => ["config", "get", "token", "--host", this.Connection.Host];

    /// <inheritdoc />
    public override Task<GitHostUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        this.RequireToken();
        return this.GetAsync("user", root => new GitHostUser(GetString(root, "username") ?? string.Empty, GetString(root, "name")), ct);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticket>> ListIssuesAsync(string project, GitHostTicketFilter filter, CancellationToken ct = default)
    {
        var assignee = filter.Assignee?.Trim();
        var path = WithQuery($"projects/{ProjectId(project)}/issues",
            ("state", filter.State switch { "open" => "opened", "closed" => "closed", _ => null }),
            ("labels", filter.Labels.Count > 0 ? string.Join(',', filter.Labels) : null),
            ("scope", assignee == "@me" ? "assigned_to_me" : null),
            ("assignee_username", string.IsNullOrEmpty(assignee) || assignee == "@me" ? null : assignee.TrimStart('@')),
            ("search", string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim()),
            ("per_page", PageSizeFor(filter)));
        return await this.GetAsync(path, root => root.EnumerateArray().Select(i => this.MapIssue(i, project)).Take(filter.Limit).ToList(), ct);
    }

    /// <inheritdoc />
    public override Task<Ticket> GetIssueAsync(string project, int number, CancellationToken ct = default)
    {
        return this.GetAsync($"projects/{ProjectId(project)}/issues/{number}", root => this.MapIssue(root, project), ct);
    }

    /// <inheritdoc />
    public override Task<Ticket> CreateIssueAsync(string project, NewTicket ticket, CancellationToken ct = default)
    {
        this.RequireToken();
        var body = new JsonObject
        {
            ["title"] = ticket.Title,
            ["description"] = ticket.Body,
            ["labels"] = string.Join(',', ticket.Labels),
        };
        return this.PostAsync($"projects/{ProjectId(project)}/issues", body, root => this.MapIssue(root, project), ct);
    }

    /// <inheritdoc />
    public override string NewIssueUrl(string project, NewTicket ticket)
    {
        // The new-issue form has no field for labels in its query, so they go in the description as quick actions.
        var description = ticket.Labels.Count == 0
            ? ticket.Body
            : $"{ticket.Body}\n\n/label {string.Join(' ', ticket.Labels.Select(l => $"~\"{l}\""))}";
        return $"{this.WebBase}/{project}/-/issues/new?" + QueryString(("issue[title]", ticket.Title), ("issue[description]", description));
    }

    /// <summary>
    ///     Maps a REST issue to a ticket.
    /// </summary>
    /// <param name="issue">The issue JSON.</param>
    /// <param name="project">The project path.</param>
    /// <returns>The ticket.</returns>
    protected virtual Ticket MapIssue(JsonElement issue, string project)
    {
        var state = GetString(issue, "state");
        return new Ticket
        {
            Source = this.Connection.Kind,
            Key = $"{project}#{issue.GetProperty("iid").GetInt32()}",
            Title = GetString(issue, "title") ?? string.Empty,
            Body = GetString(issue, "description"),
            Status = state == "opened" ? "open" : state,
            Url = GetString(issue, "web_url"),
            Assignee = GetStrings(issue, "assignees", "username").FirstOrDefault() ?? GetNested(issue, "assignee", "username"),
            Labels = GetStrings(issue, "labels"),
        };
    }

    /// <summary>
    ///     Returns a project's id for a URL: its path, encoded so the slashes are part of it.
    /// </summary>
    /// <param name="project">The project path.</param>
    /// <returns>The id.</returns>
    private static string ProjectId(string project)
    {
        return Uri.EscapeDataString(project);
    }
}
