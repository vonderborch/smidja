using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Tickets;
using Niddy.Core.GitHub;
using Niddy.Core.Results;

namespace Afl.Core.GitHosts;

/// <summary>
///     GitHub's REST API (v3): <c>api.github.com</c> for github.com, and <c>&lt;site&gt;/api/v3</c> for GitHub Enterprise
///     Server. Sign-ins can be borrowed from <c>gh</c>.
/// </summary>
/// <param name="connection">The connection.</param>
/// <param name="token">The token, or null to make anonymous requests.</param>
/// <param name="http">The HTTP client; a base address on it replaces the API's.</param>
[GitHostClientRegistration(TicketRef.GitHubSource, "GitHub")]
public class GitHubClient(GitHostConnection connection, string? token, HttpClient http)
    : AbstractGitHostClient(connection, token, http)
{
    /// <summary>
    ///     The REST API version requested.
    /// </summary>
    private const string ApiVersionHeader = "2022-11-28";

    /// <summary>
    ///     HTTP clients for GitHub Enterprise sites, one per API address. Niddy's readers take the API address from the
    ///     client's base address, so a site that isn't github.com needs a client of its own.
    /// </summary>
    private static readonly ConcurrentDictionary<string, HttpClient> EnterpriseHttp = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string DisplayName => "GitHub";

    /// <inheritdoc />
    public override string CliName => "gh";

    /// <inheritdoc />
    public override string CliVariable => EnvironmentConstants.GhExecutable;

    /// <inheritdoc />
    public override string CloudHost => "github.com";

    /// <inheritdoc />
    public override IReadOnlyList<string> EnvironmentTokens => EnvironmentConstants.GitHubTokens;

    /// <inheritdoc />
    public override string CliSignInCommand => $"gh auth login --hostname {this.Connection.Host}";

    /// <inheritdoc />
    public override string CreateTokenUrl => $"{this.WebBase}/settings/tokens";

    /// <inheritdoc />
    protected override string DefaultApiBase => this.IsCloud ? "https://api.github.com/" : $"{this.WebBase}/api/v3/";

    /// <inheritdoc />
    protected override IReadOnlyList<string> CliTokenArguments => ["auth", "token", "--hostname", this.Connection.Host];

    /// <summary>
    ///     Gets what Niddy's readers send and cache with: the token, and the cache directory if there is one. Responses
    ///     are always checked with GitHub (a 304 costs no rate limit), and the cache is used when GitHub can't be reached.
    /// </summary>
    protected GitHubReadOptions ReadOptions => new() { Token = this.HasToken ? this.Token : null, CacheDirectory = this.CacheDirectory };

    /// <summary>
    ///     Gets the HTTP client for Niddy's readers, whose base address is the API's: the one given when it has a base
    ///     address or the connection is github.com, otherwise a client for the Enterprise site's API.
    /// </summary>
    protected HttpClient ApiHttp => this.Http.BaseAddress is not null || this.IsCloud
        ? this.Http
        : EnterpriseHttp.GetOrAdd(this.ApiBase, a => new HttpClient { BaseAddress = new Uri(a), Timeout = this.Http.Timeout });

    /// <inheritdoc />
    public override Task<GitHostUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        this.RequireToken();
        return this.GetAsync("user", root => new GitHostUser(GetString(root, "login") ?? string.Empty, GetString(root, "name")), ct);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticket>> ListIssuesAsync(string project, GitHostTicketFilter filter, CancellationToken ct = default)
    {
        // The issues endpoint can't search text, so a search goes through the search API, which can express every filter.
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var found = await GitHubIssues.SearchAsync(SearchQuery(project, filter), filter.Limit, this.ReadOptions, this.ApiHttp, ct);
            return this.Unwrap(found).Select(i => this.MapIssue(i, project)).ToList();
        }

        var issues = await GitHubIssues.ListAsync(project, new GitHubIssueFilter
        {
            State = Enum.TryParse<GitHubStateFilter>(filter.State, true, out var state) ? state : GitHubStateFilter.Open,
            Labels = filter.Labels,
            Assignee = await this.AssigneeLoginAsync(filter.Assignee, ct),
            Sort = GitHubIssueSort.Updated,
        }, filter.Limit, this.ReadOptions, this.ApiHttp, ct);
        return this.Unwrap(issues).Select(i => this.MapIssue(i, project)).ToList();
    }

    /// <inheritdoc />
    public override async Task<Ticket> GetIssueAsync(string project, int number, CancellationToken ct = default)
    {
        return this.MapIssue(this.Unwrap(await GitHubIssues.GetAsync(project, number, this.ReadOptions, this.ApiHttp, ct)), project);
    }

    /// <inheritdoc />
    public override Task<Ticket> CreateIssueAsync(string project, NewTicket ticket, CancellationToken ct = default)
    {
        this.RequireToken();

        // GitHub's issue types are set per organization, so the kind isn't sent; labels carry it if the AI gave any.
        var body = new JsonObject
        {
            ["title"] = ticket.Title,
            ["body"] = ticket.Body,
            ["labels"] = new JsonArray(ticket.Labels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
        };
        return this.PostAsync($"repos/{project}/issues", body, root => this.MapIssue(root, project), ct);
    }

    /// <inheritdoc />
    public override string NewIssueUrl(string project, NewTicket ticket)
    {
        return $"{this.WebBase}/{project}/issues/new?" + QueryString(
            ("title", ticket.Title),
            ("body", ticket.Body),
            ("labels", ticket.Labels.Count > 0 ? string.Join(',', ticket.Labels) : null));
    }

    /// <inheritdoc />
    protected override void Authorize(HttpRequestMessage request)
    {
        base.Authorize(request);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersionHeader);
    }

    /// <summary>
    ///     Maps an issue read through Niddy's reader to a ticket.
    /// </summary>
    /// <param name="issue">The issue.</param>
    /// <param name="project">The repository, <c>owner/name</c>.</param>
    /// <returns>The ticket.</returns>
    protected virtual Ticket MapIssue(GitHubIssueInfo issue, string project)
    {
        return new Ticket
        {
            Source = this.Connection.Kind,
            Key = $"{project}#{issue.Number}",
            Title = issue.Title,
            Body = issue.Body,
            Status = issue.State.ToString().ToLowerInvariant(),
            Url = issue.Url?.ToString(),
            Assignee = issue.Assignees.FirstOrDefault(),
            Labels = issue.Labels.ToList(),
        };
    }

    /// <summary>
    ///     Maps a REST issue to a ticket.
    /// </summary>
    /// <param name="issue">The issue JSON.</param>
    /// <param name="project">The repository, <c>owner/name</c>.</param>
    /// <returns>The ticket.</returns>
    protected virtual Ticket MapIssue(JsonElement issue, string project)
    {
        return new Ticket
        {
            Source = this.Connection.Kind,
            Key = $"{project}#{issue.GetProperty("number").GetInt32()}",
            Title = GetString(issue, "title") ?? string.Empty,
            Body = GetString(issue, "body"),
            Status = GetString(issue, "state")?.ToLowerInvariant(),
            Url = GetString(issue, "html_url"),
            Assignee = GetStrings(issue, "assignees", "login").FirstOrDefault() ?? GetNested(issue, "assignee", "login"),
            Labels = GetStrings(issue, "labels", "name"),
        };
    }

    /// <summary>
    ///     Returns what a Niddy reader read, or throws the same error a failed request through this client would.
    /// </summary>
    /// <typeparam name="T">What was read.</typeparam>
    /// <param name="result">The result.</param>
    /// <returns>The value.</returns>
    /// <exception cref="AflException">The read failed.</exception>
    private T Unwrap<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return result.Value;
        }

        var error = result.Error!;
        if (error.Exception is HttpRequestException { StatusCode: { } status })
        {
            throw this.MapError(status, new JsonObject { ["message"] = error.Message }.ToJsonString());
        }

        throw new AflException(AflErrorKind.ExternalTool, $"{this.ServiceName} couldn't be read: {error.Message}", error.Exception);
    }

    /// <summary>
    ///     Builds a search query for a repository's issues.
    /// </summary>
    /// <param name="project">The repository.</param>
    /// <param name="filter">The filter.</param>
    /// <returns>The query.</returns>
    private static string SearchQuery(string project, GitHostTicketFilter filter)
    {
        var terms = new List<string> { $"repo:{project}", "is:issue" };
        if (filter.State is "open" or "closed")
        {
            terms.Add($"state:{filter.State}");
        }

        terms.AddRange(filter.Labels.Select(l => $"label:\"{l}\""));
        if (!string.IsNullOrWhiteSpace(filter.Assignee))
        {
            var assignee = filter.Assignee.Trim();
            terms.Add(assignee == "@me" ? "assignee:@me" : $"assignee:{assignee.TrimStart('@')}");
        }

        terms.Add(filter.Search!.Trim());
        return string.Join(' ', terms);
    }

    /// <summary>
    ///     Returns the login to filter by: <c>@me</c> becomes the signed-in user's, which the issues endpoint needs.
    /// </summary>
    /// <param name="assignee">The assignee asked for.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The login, or null for anyone.</returns>
    private async Task<string?> AssigneeLoginAsync(string? assignee, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignee))
        {
            return null;
        }

        return assignee.Trim() == "@me" ? (await this.GetCurrentUserAsync(ct)).Login : assignee.Trim().TrimStart('@');
    }
}
