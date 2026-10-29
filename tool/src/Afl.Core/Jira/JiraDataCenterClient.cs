using System.Net.Http.Headers;
using Afl.Core.Tickets;

namespace Afl.Core.Jira;

/// <summary>
///     Jira Data Center and Server: REST v2, a personal access token sent as a Bearer token, and offset-paged
///     <c>/search</c>. Teams have no standard field here, so a filter's team is used as a raw JQL clause.
/// </summary>
/// <param name="instance">The instance.</param>
/// <param name="token">The personal access token.</param>
/// <param name="http">The HTTP client.</param>
[JiraClientRegistration(JiraInstanceKind.Datacenter)]
public class JiraDataCenterClient(JiraInstance instance, string token, HttpClient http) : AbstractJiraClient(instance, token, http)
{
    /// <inheritdoc />
    protected override string ApiVersion => "2";

    /// <inheritdoc />
    public override Task<IReadOnlyList<Ticket>> SearchAsync(string jql, int limit, CancellationToken ct = default)
    {
        return this.SearchByOffsetAsync(this.RestPath("search"), jql, limit, ct);
    }

    /// <summary>
    ///     Uses the team value as raw JQL, e.g. <c>Team = 42</c> for an Advanced Roadmaps team.
    /// </summary>
    /// <param name="team">The JQL clause.</param>
    /// <returns>The clause, parenthesised.</returns>
    public override string TeamClause(string team)
    {
        return $"({team})";
    }

    /// <inheritdoc />
    protected override AuthenticationHeaderValue CreateAuthHeader()
    {
        return new AuthenticationHeaderValue("Bearer", this.Token);
    }
}
