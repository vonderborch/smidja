using System.Text.RegularExpressions;
using Afl.Core.Exceptions;
using Afl.Core.Repos;

namespace Afl.Core.Tickets;

/// <summary>
///     A reference to a ticket as the user typed it: <c>123</c>, <c>#123</c>, <c>owner/repo#123</c>,
///     <c>group/sub/project#123</c>, a GitHub or GitLab issue URL (on any site), a Jira key such as <c>PROJ-45</c>, or a
///     Jira browse URL. A number or project reference without a site means the repo's own host (<see cref="ForRepo" />).
/// </summary>
public partial class TicketRef
{
    /// <summary>
    ///     The source name for GitHub references.
    /// </summary>
    public const string GitHubSource = "github";

    /// <summary>
    ///     The source name for GitLab references.
    /// </summary>
    public const string GitLabSource = "gitlab";

    /// <summary>
    ///     The source name for Jira references.
    /// </summary>
    public const string JiraSource = "jira";

    /// <summary>
    ///     The source name for work with no ticket (<c>local:&lt;name&gt;</c>), see <see cref="LocalTicketSource" />.
    /// </summary>
    public const string LocalSource = "local";

    /// <summary>
    ///     The prefix of a reference to work with no ticket.
    /// </summary>
    public const string LocalPrefix = LocalSource + ":";

    /// <summary>
    ///     Gets the text as the user typed it.
    /// </summary>
    public string Raw { get; private init; } = string.Empty;

    /// <summary>
    ///     Gets the source the reference points at.
    /// </summary>
    public string Source { get; private init; } = string.Empty;

    /// <summary>
    ///     Gets the GitHub repository (<c>owner/name</c>) or GitLab project (<c>group/sub/project</c>), when the reference
    ///     names one. For a URL it's the whole path before the issue, including any path the site is served under.
    /// </summary>
    public string? Repository { get; private init; }

    /// <summary>
    ///     Gets the GitHub or GitLab issue number.
    /// </summary>
    public int? Number { get; private init; }

    /// <summary>
    ///     Gets the Jira issue key.
    /// </summary>
    public string? Key { get; private init; }

    /// <summary>
    ///     Gets the Jira project key, the part of <see cref="Key" /> before the issue number.
    /// </summary>
    public string? ProjectKey => this.Source == JiraSource && this.Key is { } key ? key[..key.LastIndexOf('-')] : null;

    /// <summary>
    ///     Gets the site the reference came from (<c>https://host</c>), when it was a URL.
    /// </summary>
    public string? BaseUrl { get; private init; }

    /// <summary>
    ///     Gets whether the reference is to a GitHub or GitLab issue.
    /// </summary>
    public bool IsGitHost => this.Source is GitHubSource or GitLabSource;

    /// <summary>
    ///     Parses a ticket reference.
    /// </summary>
    /// <param name="text">The reference.</param>
    /// <returns>The parsed reference.</returns>
    /// <exception cref="AflException">The text isn't a recognised reference.</exception>
    public static TicketRef Parse(string text)
    {
        return TryParse(text, out var reference)
            ? reference!
            : throw new AflException(AflErrorKind.InvalidInput,
                $"'{text}' is not a ticket reference. Use 123, #123, owner/repo#123, group/project#123, a GitHub or GitLab issue URL, PROJ-45, a Jira URL, or local:<name> for work with no ticket.");
    }

    /// <summary>
    ///     Tries to parse a ticket reference.
    /// </summary>
    /// <param name="text">The reference.</param>
    /// <param name="reference">The parsed reference, when successful.</param>
    /// <returns>True if parsed.</returns>
    public static bool TryParse(string? text, out TicketRef? reference)
    {
        reference = null;
        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return false;
        }

        Match match;
        if (raw.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = raw[LocalPrefix.Length..].Trim();
            reference = name.Length == 0 ? null : new TicketRef { Raw = raw, Source = LocalSource, Key = name };
        }
        else if ((match = GitHubNumberRegex().Match(raw)).Success)
        {
            reference = new TicketRef { Raw = raw, Source = GitHubSource, Number = int.Parse(match.Groups["n"].Value) };
        }
        else if ((match = QualifiedRegex().Match(raw)).Success)
        {
            // GitHub repositories are always owner/name; a deeper path can only be a GitLab subgroup project.
            var repository = match.Groups["repo"].Value;
            reference = new TicketRef
            {
                Raw = raw, Source = repository.Count(c => c == '/') > 1 ? GitLabSource : GitHubSource, Repository = repository,
                Number = int.Parse(match.Groups["n"].Value),
            };
        }
        else if ((match = GitLabUrlRegex().Match(raw)).Success || (match = GitHubUrlRegex().Match(raw)).Success)
        {
            reference = new TicketRef
            {
                Raw = raw, Source = match.Groups["gitlab"].Success ? GitLabSource : GitHubSource,
                Repository = match.Groups["repo"].Value.Trim('/'), Number = int.Parse(match.Groups["n"].Value),
                BaseUrl = match.Groups["base"].Value,
            };
        }
        else if ((match = JiraKeyRegex().Match(raw)).Success)
        {
            reference = new TicketRef { Raw = raw, Source = JiraSource, Key = match.Groups["key"].Value.ToUpperInvariant() };
        }
        else if ((match = JiraUrlRegex().Match(raw)).Success)
        {
            reference = new TicketRef
            {
                Raw = raw, Source = JiraSource, Key = match.Groups["key"].Value.ToUpperInvariant(),
                BaseUrl = match.Groups["base"].Value,
            };
        }

        return reference is not null;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.IsGitHost
            ? (this.Repository is null ? $"#{this.Number}" : $"{this.Repository}#{this.Number}")
            : this.Source == LocalSource ? LocalPrefix + this.Key : this.Key ?? this.Raw;
    }

    /// <summary>
    ///     Returns the reference as meant in a repo: an issue number or project reference without a site is on the
    ///     repo's own git host, so on a GitLab-linked repo <c>#12</c> is a GitLab issue.
    /// </summary>
    /// <param name="repo">The repo the reference is used in, if any.</param>
    /// <returns>This reference, or a copy pointing at the repo's host.</returns>
    public TicketRef ForRepo(RepoRecord? repo)
    {
        if (!this.IsGitHost || this.BaseUrl is not null || repo?.Host is not { } host || host.Kind == this.Source)
        {
            return this;
        }

        // A deeper path than owner/name can't be on GitHub, whatever the repo is linked to.
        return this.Repository is { } repository && repository.Count(c => c == '/') > 1 && host.Kind == GitHubSource
            ? this
            : new TicketRef { Raw = this.Raw, Source = host.Kind, Repository = this.Repository, Number = this.Number };
    }

    /// <summary>
    ///     Returns the reference for work with no ticket, named <paramref name="name" />.
    /// </summary>
    /// <param name="name">What the work is called, e.g. <c>dark mode</c>.</param>
    /// <returns>The reference text, <c>local:&lt;name&gt;</c>.</returns>
    public static string ForLocal(string name)
    {
        return LocalPrefix + name.Trim();
    }

    /// <summary>
    ///     Matches <c>123</c> and <c>#123</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^#?(?<n>\d+)$")]
    private static partial Regex GitHubNumberRegex();

    /// <summary>
    ///     Matches <c>owner/repo#123</c> and <c>group/sub/project#123</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^(?<repo>[\w.-]+(/[\w.-]+)+)#(?<n>\d+)$")]
    private static partial Regex QualifiedRegex();

    /// <summary>
    ///     Matches GitLab issue, merge request and work item URLs on any site, e.g.
    ///     <c>https://gitlab.com/group/sub/project/-/issues/12</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^(?<base>https?://[^/?#]+)(?<repo>(/[\w.-]+)+)/-/(?<gitlab>issues|merge_requests|work_items)/(?<n>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GitLabUrlRegex();

    /// <summary>
    ///     Matches GitHub-shaped issue and pull request URLs on any site, e.g. <c>https://github.com/owner/repo/issues/12</c>
    ///     or the same on a GitHub Enterprise Server. Which kind of site it really is comes from its connection.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^(?<base>https?://[^/?#]+)(?<repo>(/[\w.-]+)*/[\w.-]+/[\w.-]+)/(issues|pull)/(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlRegex();

    /// <summary>
    ///     Matches Jira keys such as <c>PROJ-45</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z0-9_]+-\d+)$")]
    private static partial Regex JiraKeyRegex();

    /// <summary>
    ///     Matches Jira browse URLs and board URLs with a <c>selectedIssue</c> parameter.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^(?<base>https?://[^/]+(/[^?]*?)?)/(browse/|.*[?&]selectedIssue=)(?<key>[A-Za-z][A-Za-z0-9_]+-\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex JiraUrlRegex();
}
