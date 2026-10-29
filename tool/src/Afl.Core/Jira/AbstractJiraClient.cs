using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Tickets;

namespace Afl.Core.Jira;

/// <summary>
///     Registers an <see cref="AbstractJiraClient" /> subclass for a <see cref="JiraInstanceKind" />.
/// </summary>
/// <param name="kind">The flavour of Jira the client talks to.</param>
public class JiraClientRegistrationAttribute(JiraInstanceKind kind) : AbstractRegistrationAttribute(kind.ToString());

/// <summary>
///     Finds the client class for each flavour of Jira.
/// </summary>
public class JiraClientRegistry() : TypeRegistry<AbstractJiraClient, JiraClientRegistrationAttribute>("Jira client")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static JiraClientRegistry Instance { get; } = new();

    /// <summary>
    ///     Creates the client for an instance.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="token">The API token or personal access token.</param>
    /// <param name="http">The HTTP client to send requests with.</param>
    /// <returns>The client.</returns>
    public AbstractJiraClient Create(JiraInstance instance, string token, HttpClient http)
    {
        return this.Create(instance.Kind.ToString(), instance, token, http);
    }
}

/// <summary>
///     Talks to one Jira instance over REST. The base holds everything the flavours share: the Agile board API, issue
///     lookup, paging, retries, error mapping and issue mapping. Subclasses supply the API version, authentication,
///     issue search (which differs between Cloud and Data Center) and the team JQL clause.
/// </summary>
/// <param name="instance">The instance.</param>
/// <param name="token">The API token or personal access token.</param>
/// <param name="http">The HTTP client to send requests with.</param>
public abstract class AbstractJiraClient(JiraInstance instance, string token, HttpClient http) : AbstractRestClient(http)
{
    /// <summary>
    ///     The issue fields requested for tickets.
    /// </summary>
    protected const string IssueFields = "summary,description,status,assignee,labels";

    /// <summary>
    ///     The page size used when paging through results.
    /// </summary>
    protected const int PageSize = 50;

    /// <summary>
    ///     Gets the instance.
    /// </summary>
    public JiraInstance Instance { get; } = instance;

    /// <summary>
    ///     Gets the token.
    /// </summary>
    protected string Token { get; } = token;

    /// <summary>
    ///     Gets the REST API version this flavour uses (<c>2</c> or <c>3</c>).
    /// </summary>
    protected abstract string ApiVersion { get; }

    /// <inheritdoc />
    protected override string ServiceName => $"Jira '{this.Instance.Id}'";

    /// <summary>
    ///     Searches issues with JQL.
    /// </summary>
    /// <param name="jql">The JQL.</param>
    /// <param name="limit">The most issues to return.</param>
    /// <param name="ct">Cancels the search.</param>
    /// <returns>The matching tickets.</returns>
    public abstract Task<IReadOnlyList<Ticket>> SearchAsync(string jql, int limit, CancellationToken ct = default);

    /// <summary>
    ///     Builds the JQL clause for a team filter.
    /// </summary>
    /// <param name="team">The filter's team value.</param>
    /// <returns>The clause.</returns>
    public abstract string TeamClause(string team);

    /// <summary>
    ///     Builds the JQL for a filter on this instance.
    /// </summary>
    /// <param name="criteria">The Jira criteria.</param>
    /// <param name="common">The common ticket options.</param>
    /// <returns>The JQL.</returns>
    public virtual string BuildJql(JiraFilter criteria, TicketFilter common)
    {
        return JqlBuilder.Build(criteria, common, this.TeamClause);
    }

    /// <summary>
    ///     Lists issues matching a filter: through the board when the filter names one, otherwise through search.
    /// </summary>
    /// <param name="criteria">The Jira criteria.</param>
    /// <param name="common">The common ticket options.</param>
    /// <param name="ct">Cancels the listing.</param>
    /// <returns>The matching tickets.</returns>
    public virtual Task<IReadOnlyList<Ticket>> ListAsync(JiraFilter criteria, TicketFilter common, CancellationToken ct = default)
    {
        var jql = this.BuildJql(criteria, common);
        return criteria.BoardId is { } boardId
            ? this.ListBoardIssuesAsync(boardId, jql, common.Limit, ct)
            : this.SearchAsync(jql, common.Limit, ct);
    }

    /// <summary>
    ///     Reads one issue.
    /// </summary>
    /// <param name="key">The issue key.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The ticket.</returns>
    public virtual Task<Ticket> GetIssueAsync(string key, CancellationToken ct = default)
    {
        return this.GetAsync(this.RestPath($"issue/{Uri.EscapeDataString(key)}", ("fields", IssueFields)), this.MapIssue, ct);
    }

    /// <summary>
    ///     Creates an issue.
    /// </summary>
    /// <param name="projectKey">The project to create it in.</param>
    /// <param name="issueType">The issue type's name, e.g. <c>Bug</c>.</param>
    /// <param name="ticket">What to file.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The new issue.</returns>
    /// <exception cref="AflException">Jira refused it.</exception>
    public virtual async Task<Ticket> CreateIssueAsync(string projectKey, string issueType, NewTicket ticket, CancellationToken ct = default)
    {
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey },
            ["summary"] = ticket.Title,
            ["issuetype"] = new JsonObject { ["name"] = issueType },
        };
        if (!string.IsNullOrWhiteSpace(ticket.Body))
        {
            fields["description"] = this.DescriptionField(ticket.Body);
        }

        if (ticket.Labels.Count > 0)
        {
            // Jira labels can't hold spaces.
            fields["labels"] = new JsonArray(ticket.Labels.Select(l => (JsonNode?)JsonValue.Create(l.Trim().Replace(' ', '-'))).ToArray());
        }

        using var document = await this.PostJsonAsync(this.RestPath("issue"), new JsonObject { ["fields"] = fields }, ct);
        var key = GetString(document.RootElement, "key") ?? throw new AflException(AflErrorKind.ExternalTool,
            $"Jira '{this.Instance.Id}' created the issue but didn't say its key.");
        return new Ticket
        {
            Source = TicketRef.JiraSource, Key = key, Title = ticket.Title, Body = ticket.Body, Status = "open",
            Url = $"{this.Instance.BaseUrl.TrimEnd('/')}/browse/{key}", Labels = [..ticket.Labels],
        };
    }

    /// <summary>
    ///     Returns the value of a new issue's description field: plain (wiki) text, which Data Center takes as is.
    /// </summary>
    /// <param name="text">The description.</param>
    /// <returns>The field's value.</returns>
    protected virtual JsonNode DescriptionField(string text)
    {
        return JsonValue.Create(text);
    }

    /// <summary>
    ///     Reads the signed-in user, which is also how a connection is tested.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The user.</returns>
    public virtual Task<JiraUser> GetMyselfAsync(CancellationToken ct = default)
    {
        return this.GetAsync(this.RestPath("myself"), root => new JiraUser(
            GetString(root, "displayName") ?? string.Empty,
            GetString(root, "accountId") ?? GetString(root, "name")), ct);
    }

    /// <summary>
    ///     Lists the projects the user can see.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The projects.</returns>
    public virtual Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        return this.GetAsync<IReadOnlyList<JiraProject>>(this.RestPath("project"),
            root => root.EnumerateArray().Select(MapProject).ToList(), ct);
    }

    /// <summary>
    ///     Lists agile boards, optionally only those of one project.
    /// </summary>
    /// <param name="projectKey">The project key, or null for all boards.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The boards.</returns>
    public virtual async Task<IReadOnlyList<JiraBoard>> ListBoardsAsync(string? projectKey = null, CancellationToken ct = default)
    {
        var boards = new List<JiraBoard>();
        var startAt = 0;
        while (true)
        {
            var (page, isLast) = await this.GetAsync(AgilePath("board",
                    ("projectKeyOrId", projectKey), ("startAt", startAt.ToString()), ("maxResults", PageSize.ToString())),
                root => (root.GetProperty("values").EnumerateArray().Select(MapBoard).ToList(), IsLastPage(root, false)), ct);
            boards.AddRange(page);
            startAt += page.Count;
            if (page.Count == 0 || isLast)
            {
                return boards;
            }
        }
    }

    /// <summary>
    ///     Lists the issues on an agile board, narrowed by JQL.
    /// </summary>
    /// <param name="boardId">The board id.</param>
    /// <param name="jql">The JQL applied on top of the board's own filter.</param>
    /// <param name="limit">The most issues to return.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tickets.</returns>
    public virtual Task<IReadOnlyList<Ticket>> ListBoardIssuesAsync(int boardId, string jql, int limit, CancellationToken ct = default)
    {
        // The board keeps its own rank order, so any ORDER BY is dropped.
        var condition = JqlBuilder.SplitOrderBy(jql).Condition;
        return this.SearchByOffsetAsync(AgilePath($"board/{boardId}/issue"), condition.Length == 0 ? null : condition, limit, ct);
    }

    /// <summary>
    ///     Pages through a <c>startAt</c>/<c>maxResults</c> issue endpoint (Data Center search and the Agile board API).
    /// </summary>
    /// <param name="path">The endpoint path, without query.</param>
    /// <param name="jql">The JQL, or null for none.</param>
    /// <param name="limit">The most issues to return.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tickets.</returns>
    protected async Task<IReadOnlyList<Ticket>> SearchByOffsetAsync(string path, string? jql, int limit, CancellationToken ct)
    {
        var tickets = new List<Ticket>();
        while (tickets.Count < limit)
        {
            var pageSize = Math.Min(PageSize, limit - tickets.Count);
            var (page, total) = await this.GetAsync(WithQuery(path,
                    ("jql", jql), ("startAt", tickets.Count.ToString()), ("maxResults", pageSize.ToString()),
                    ("fields", IssueFields)),
                root => (this.ReadIssues(root),
                    root.TryGetProperty("total", out var totalElement) ? totalElement.GetInt32() : int.MaxValue), ct);
            tickets.AddRange(page);
            if (page.Count == 0 || tickets.Count >= total)
            {
                break;
            }
        }

        return tickets.Take(limit).ToList();
    }

    /// <summary>
    ///     Creates the authorization header sent with every request.
    /// </summary>
    /// <returns>The header.</returns>
    protected abstract AuthenticationHeaderValue CreateAuthHeader();

    /// <summary>
    ///     Maps a Jira issue to a ticket.
    /// </summary>
    /// <param name="issue">The issue JSON.</param>
    /// <returns>The ticket.</returns>
    protected virtual Ticket MapIssue(JsonElement issue)
    {
        var key = issue.GetProperty("key").GetString() ?? string.Empty;
        var fields = issue.TryGetProperty("fields", out var f) ? f : default;
        return new Ticket
        {
            Source = TicketRef.JiraSource,
            Key = key,
            Title = GetString(fields, "summary") ?? string.Empty,
            Body = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("description", out var description)
                ? this.ReadDescription(description)
                : null,
            Status = GetNested(fields, "status", "name"),
            Url = $"{this.Instance.BaseUrl.TrimEnd('/')}/browse/{key}",
            Assignee = GetNested(fields, "assignee", "displayName"),
            Labels = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("labels", out var labels) &&
                     labels.ValueKind == JsonValueKind.Array
                ? labels.EnumerateArray().Select(l => l.GetString() ?? string.Empty).ToList()
                : [],
        };
    }

    /// <summary>
    ///     Reads an issue description: wiki-markup text (v2 and the Agile API) or an Atlassian Document (v3).
    /// </summary>
    /// <param name="description">The description JSON.</param>
    /// <returns>The plain text, or null if empty.</returns>
    protected virtual string? ReadDescription(JsonElement description)
    {
        return description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object => AdfToText(description),
            _ => null,
        };
    }

    /// <summary>
    ///     Builds a REST API path for this flavour, e.g. <c>rest/api/3/myself</c>.
    /// </summary>
    /// <param name="resource">The resource path.</param>
    /// <param name="query">Query parameters; null values are skipped.</param>
    /// <returns>The path.</returns>
    protected string RestPath(string resource, params (string Name, string? Value)[] query)
    {
        return WithQuery($"rest/api/{this.ApiVersion}/{resource}", query);
    }

    /// <summary>
    ///     Builds an Agile API path, e.g. <c>rest/agile/1.0/board</c>.
    /// </summary>
    /// <param name="resource">The resource path.</param>
    /// <param name="query">Query parameters; null values are skipped.</param>
    /// <returns>The path.</returns>
    protected static string AgilePath(string resource, params (string Name, string? Value)[] query)
    {
        return WithQuery($"rest/agile/1.0/{resource}", query);
    }

    /// <summary>
    ///     Reads the <c>issues</c> array of a search response.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <returns>The tickets.</returns>
    protected List<Ticket> ReadIssues(JsonElement root)
    {
        return root.GetProperty("issues").EnumerateArray().Select(this.MapIssue).ToList();
    }

    /// <summary>
    ///     Reads a paged response's <c>isLast</c> flag.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <param name="whenMissing">What to assume when the flag is absent.</param>
    /// <returns>True if this is the last page.</returns>
    protected static bool IsLastPage(JsonElement root, bool whenMissing)
    {
        return root.TryGetProperty("isLast", out var last) ? last.ValueKind == JsonValueKind.True : whenMissing;
    }

    /// <inheritdoc />
    protected override string RequestUrl(string path)
    {
        return $"{this.Instance.BaseUrl.TrimEnd('/')}/{path}";
    }

    /// <inheritdoc />
    protected override void Authorize(HttpRequestMessage request)
    {
        request.Headers.Authorization = this.CreateAuthHeader();
    }

    /// <inheritdoc />
    protected override AflException MapError(HttpStatusCode status, string body)
    {
        return status == HttpStatusCode.Unauthorized
            ? new AflException(AflErrorKind.InvalidState,
                $"Jira '{this.Instance.Id}' rejected the token (401). Re-add it with: afl jira instance add {this.Instance.Id} …")
            : base.MapError(status, body);
    }

    /// <inheritdoc />
    protected override string? ReadErrorDetail(string body)
    {
        return ReadErrorMessages(body);
    }

    /// <summary>
    ///     Maps a project.
    /// </summary>
    /// <param name="project">The project JSON.</param>
    /// <returns>The project.</returns>
    protected static JiraProject MapProject(JsonElement project)
    {
        return new JiraProject(GetString(project, "key") ?? string.Empty, GetString(project, "name") ?? string.Empty);
    }

    /// <summary>
    ///     Flattens an Atlassian Document Format tree to readable text.
    /// </summary>
    /// <param name="document">The ADF root node.</param>
    /// <returns>The text, or null if empty.</returns>
    protected static string? AdfToText(JsonElement document)
    {
        var builder = new StringBuilder();
        AppendAdf(builder, document, string.Empty);
        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    ///     Appends one ADF node and its children.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="node">The node.</param>
    /// <param name="listPrefix">The prefix for list items at this depth.</param>
    private static void AppendAdf(StringBuilder builder, JsonElement node, string listPrefix)
    {
        var type = GetString(node, "type");
        switch (type)
        {
            case "text":
                builder.Append(GetString(node, "text"));
                return;
            case "hardBreak":
                builder.Append('\n');
                return;
            case "mention" or "emoji" or "status" or "date":
                builder.Append(node.TryGetProperty("attrs", out var attrs) ? GetString(attrs, "text") ?? GetString(attrs, "shortName") : null);
                return;
            case "inlineCard" or "blockCard":
                builder.Append(node.TryGetProperty("attrs", out var cardAttrs) ? GetString(cardAttrs, "url") : null);
                return;
            case "codeBlock":
                builder.Append("```\n");
                AppendChildren(builder, node, listPrefix);
                builder.Append("\n```\n\n");
                return;
            case "bulletList" or "orderedList":
                AppendChildren(builder, node, listPrefix + "  ");
                builder.Append('\n');
                return;
            case "listItem":
                builder.Append(listPrefix.Length >= 2 ? listPrefix[2..] : string.Empty).Append("- ");
                var start = builder.Length;
                AppendChildren(builder, node, listPrefix);
                TrimTrailingNewlines(builder, start);
                builder.Append('\n');
                return;
            case "paragraph" or "heading" or "blockquote" or "panel":
                AppendChildren(builder, node, listPrefix);
                builder.Append(listPrefix.Length > 0 ? "\n" : "\n\n");
                return;
            case "rule":
                builder.Append("---\n\n");
                return;
            default:
                AppendChildren(builder, node, listPrefix);
                return;
        }
    }

    /// <summary>
    ///     Appends an ADF node's children.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="node">The node.</param>
    /// <param name="listPrefix">The list prefix.</param>
    private static void AppendChildren(StringBuilder builder, JsonElement node, string listPrefix)
    {
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                AppendAdf(builder, child, listPrefix);
            }
        }
    }

    /// <summary>
    ///     Removes newlines at the end of the builder, back to <paramref name="start" />.
    /// </summary>
    /// <param name="builder">The output.</param>
    /// <param name="start">The earliest index to trim to.</param>
    private static void TrimTrailingNewlines(StringBuilder builder, int start)
    {
        while (builder.Length > start && builder[^1] == '\n')
        {
            builder.Length--;
        }
    }

    /// <summary>
    ///     Maps a board.
    /// </summary>
    /// <param name="board">The board JSON.</param>
    /// <returns>The board.</returns>
    private static JiraBoard MapBoard(JsonElement board)
    {
        return new JiraBoard(
            board.GetProperty("id").GetInt32(),
            GetString(board, "name") ?? string.Empty,
            GetString(board, "type"),
            board.TryGetProperty("location", out var location) ? GetString(location, "projectKey") : null);
    }

    /// <summary>
    ///     Reads Jira's <c>errorMessages</c> and <c>errors</c> from an error body.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <returns>The messages joined, or null.</returns>
    private static string? ReadErrorMessages(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var messages = new List<string>();
            if (root.TryGetProperty("errorMessages", out var errorMessages) && errorMessages.ValueKind == JsonValueKind.Array)
            {
                messages.AddRange(errorMessages.EnumerateArray().Select(m => m.GetString() ?? string.Empty));
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                messages.AddRange(errors.EnumerateObject().Select(e => $"{e.Name}: {e.Value.GetString()}"));
            }

            messages.RemoveAll(string.IsNullOrWhiteSpace);
            return messages.Count == 0 ? null : string.Join("; ", messages);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
