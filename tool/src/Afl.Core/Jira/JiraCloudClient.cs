using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Tickets;

namespace Afl.Core.Jira;

/// <summary>
///     Jira Cloud: REST v3, Basic authentication with the account email and an API token, token-paged search through
///     <c>/search/jql</c>, and teams through the Teams field.
/// </summary>
/// <param name="instance">The instance.</param>
/// <param name="token">The API token.</param>
/// <param name="http">The HTTP client.</param>
[JiraClientRegistration(JiraInstanceKind.Cloud)]
public class JiraCloudClient(JiraInstance instance, string token, HttpClient http) : AbstractJiraClient(instance, token, http)
{
    /// <inheritdoc />
    protected override string ApiVersion => "3";

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticket>> SearchAsync(string jql, int limit, CancellationToken ct = default)
    {
        var tickets = new List<Ticket>();
        string? nextPageToken = null;
        while (tickets.Count < limit)
        {
            var pageSize = Math.Min(PageSize, limit - tickets.Count);
            var (page, next, isLast) = await this.GetAsync(this.RestPath("search/jql",
                    ("jql", jql), ("maxResults", pageSize.ToString()), ("fields", IssueFields),
                    ("nextPageToken", nextPageToken)),
                root => (this.ReadIssues(root), GetString(root, "nextPageToken"), IsLastPage(root, false)), ct);
            tickets.AddRange(page);
            nextPageToken = next;
            if (page.Count == 0 || nextPageToken is null || isLast)
            {
                break;
            }
        }

        return tickets.Take(limit).ToList();
    }

    /// <summary>
    ///     Matches the Teams field by team id (the id at the end of the team's URL).
    /// </summary>
    /// <param name="team">The team id.</param>
    /// <returns>The clause.</returns>
    public override string TeamClause(string team)
    {
        return $"\"Team[Team]\" = {JqlBuilder.Quote(team)}";
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        // Cloud's plain /project list is deprecated; /project/search pages instead.
        var projects = new List<JiraProject>();
        while (true)
        {
            var (page, isLast) = await this.GetAsync(this.RestPath("project/search",
                    ("startAt", projects.Count.ToString()), ("maxResults", PageSize.ToString())),
                root => (root.GetProperty("values").EnumerateArray().Select(MapProject).ToList(), IsLastPage(root, true)), ct);
            projects.AddRange(page);
            if (page.Count == 0 || isLast)
            {
                return projects;
            }
        }
    }

    /// <summary>
    ///     Returns the description as an Atlassian Document: Cloud's v3 API won't take plain text. Each paragraph
    ///     (separated by a blank line) becomes one, with its line breaks kept.
    /// </summary>
    /// <param name="text">The description.</param>
    /// <returns>The document.</returns>
    protected override JsonNode DescriptionField(string text)
    {
        var paragraphs = text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(paragraph =>
            {
                var content = new JsonArray();
                foreach (var (line, index) in paragraph.Split('\n').Select((l, i) => (l, i)))
                {
                    if (index > 0)
                    {
                        content.Add(new JsonObject { ["type"] = "hardBreak" });
                    }

                    if (line.Length > 0)
                    {
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = line });
                    }
                }

                return (JsonNode?)new JsonObject { ["type"] = "paragraph", ["content"] = content };
            })
            .ToArray();
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = new JsonArray(paragraphs) };
    }

    /// <inheritdoc />
    protected override AuthenticationHeaderValue CreateAuthHeader()
    {
        var credentials = Encoding.UTF8.GetBytes($"{this.Instance.Email}:{this.Token}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));
    }
}
