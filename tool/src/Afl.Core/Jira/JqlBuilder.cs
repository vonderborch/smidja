using System.Text.RegularExpressions;
using Afl.Core.Tickets;

namespace Afl.Core.Jira;

/// <summary>
///     Turns a <see cref="JiraFilter" /> plus the common ticket options into JQL, ANDing every criterion that is set.
///     When the filter names no project, board or project JQL, its <see cref="JiraFilter.ProjectScope" /> (the repo's
///     projects) limits the search.
///     The one flavour-specific clause (team) is supplied by the client, so this stays independent of Cloud vs Data Center.
/// </summary>
public static partial class JqlBuilder
{
    /// <summary>
    ///     The ordering used when the filter's raw JQL doesn't give one.
    /// </summary>
    public const string DefaultOrderBy = "ORDER BY updated DESC";

    /// <summary>
    ///     Builds the JQL for a search.
    /// </summary>
    /// <param name="criteria">The Jira criteria.</param>
    /// <param name="common">The common options: state (open, closed, all), free-text search.</param>
    /// <param name="teamClause">Turns a team value into a JQL clause for this Jira flavour.</param>
    /// <returns>The JQL, always ending in an <c>ORDER BY</c>.</returns>
    public static string Build(JiraFilter criteria, TicketFilter common, Func<string, string> teamClause)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(criteria.Project))
        {
            clauses.Add($"project = {Quote(criteria.Project)}");
        }
        else if (criteria.ProjectScope.Count > 0 && criteria.BoardId is null && !MentionsProject(criteria.Jql))
        {
            clauses.Add(criteria.ProjectScope.Count == 1
                ? $"project = {Quote(criteria.ProjectScope[0])}"
                : $"project in ({string.Join(", ", criteria.ProjectScope.Select(Quote))})");
        }

        if (!string.IsNullOrWhiteSpace(criteria.Team))
        {
            clauses.Add(teamClause(criteria.Team.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Assignee))
        {
            clauses.Add(AssigneeClause(criteria.Assignee.Trim()));
        }

        if (criteria.Statuses.Count > 0)
        {
            clauses.Add($"status in ({string.Join(", ", criteria.Statuses.Select(Quote))})");
        }
        else if (StateClause(common.State) is { } state)
        {
            clauses.Add(state);
        }

        clauses.AddRange(criteria.Labels.Select(label => $"labels = {Quote(label)}"));

        if (!string.IsNullOrWhiteSpace(common.Search))
        {
            clauses.Add($"text ~ {Quote(common.Search.Trim())}");
        }

        var orderBy = DefaultOrderBy;
        if (!string.IsNullOrWhiteSpace(criteria.Jql))
        {
            var (condition, order) = SplitOrderBy(criteria.Jql);
            if (condition.Length > 0)
            {
                clauses.Add($"({condition})");
            }

            orderBy = order ?? orderBy;
        }

        return clauses.Count == 0 ? orderBy : $"{string.Join(" AND ", clauses)} {orderBy}";
    }

    /// <summary>
    ///     Quotes a value as a JQL string literal.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The quoted value.</returns>
    public static string Quote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    ///     Splits raw JQL into its condition and its <c>ORDER BY</c> clause, if any.
    /// </summary>
    /// <param name="jql">The JQL.</param>
    /// <returns>The trimmed condition and the order clause (null when there is none).</returns>
    public static (string Condition, string? OrderBy) SplitOrderBy(string jql)
    {
        var match = OrderByRegex().Match(jql);
        return match.Success
            ? (jql[..match.Index].Trim(), jql[match.Index..].Trim())
            : (jql.Trim(), null);
    }

    /// <summary>
    ///     Gets whether raw JQL already picks projects, in which case the repo's project scope is left out so it doesn't
    ///     contradict it.
    /// </summary>
    /// <param name="jql">The raw JQL, if any.</param>
    /// <returns>True if it mentions the project field.</returns>
    private static bool MentionsProject(string? jql)
    {
        return jql is not null && ProjectFieldRegex().IsMatch(SplitOrderBy(jql).Condition);
    }

    /// <summary>
    ///     Builds the assignee clause, understanding <c>me</c> and <c>none</c>.
    /// </summary>
    /// <param name="assignee">The assignee value.</param>
    /// <returns>The clause.</returns>
    private static string AssigneeClause(string assignee)
    {
        return assignee.ToLowerInvariant() switch
        {
            "me" or "@me" => "assignee = currentUser()",
            "none" or "unassigned" => "assignee is EMPTY",
            _ => $"assignee = {Quote(assignee)}",
        };
    }

    /// <summary>
    ///     Maps the common open/closed/all state onto Jira status categories.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The clause, or null for all.</returns>
    private static string? StateClause(string? state)
    {
        return state?.ToLowerInvariant() switch
        {
            "closed" or "done" => "statusCategory = Done",
            "all" => null,
            _ => "statusCategory != Done",
        };
    }

    /// <summary>
    ///     Matches the <c>project</c> field in JQL.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"\bproject\s*(=|!=|\bin\b|\bnot\s+in\b|\bis\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectFieldRegex();

    /// <summary>
    ///     Matches a top-level <c>ORDER BY</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"\border\s+by\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByRegex();
}
