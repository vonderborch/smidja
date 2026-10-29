using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Jira;
using Afl.Core.Repos;
using Afl.Core.Tickets;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Lists the repo's tickets from GitHub, GitLab or Jira.
/// </summary>
[Verb("issues", HelpText = "List the repo's tickets from GitHub, GitLab or Jira (Jira uses the repo's default saved filter unless told otherwise).")]
public class IssuesCommand : AbstractJiraCriteriaCommand
{
    /// <summary>
    ///     Gets or sets the ticket source.
    /// </summary>
    [Option("source", HelpText = "github, gitlab or jira (defaults to the repo's GitHub or GitLab project when it's linked to one and no Jira options are given).")]
    public string? Source { get; set; }

    /// <summary>
    ///     Gets or sets the saved Jira filter.
    /// </summary>
    [Option('f', "filter", HelpText = "Jira: a saved filter name (see afl jira filter list).")]
    public string? Filter { get; set; }

    /// <summary>
    ///     Gets or sets whether to skip the default Jira filter.
    /// </summary>
    [Option("no-default-filter", HelpText = "Jira: don't apply the repo's default saved filter.")]
    public bool NoDefaultFilter { get; set; }

    /// <summary>
    ///     Gets or sets whether to search every project rather than only the repo's.
    /// </summary>
    [Option("all-projects", HelpText = "Jira: search every project, not just the repo's associated ones (see afl jira project add).")]
    public bool AllProjects { get; set; }

    /// <summary>
    ///     Gets or sets the state filter.
    /// </summary>
    [Option('s', "state", Default = "open", HelpText = "open, closed or all.")]
    public string State { get; set; } = "open";

    /// <summary>
    ///     Gets or sets the search text.
    /// </summary>
    [Option('q', "search", HelpText = "Search text.")]
    public string? Search { get; set; }

    /// <summary>
    ///     Gets or sets the maximum number of issues.
    /// </summary>
    [Option('n', "limit", Default = 50, HelpText = "The most tickets to list.")]
    public int Limit { get; set; } = 50;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.RequireRepo();
        var source = TicketSourceRegistry.Instance.Create(this.Source ?? this.DefaultSource(repo));
        if (!source.IsAvailableFor(repo))
        {
            throw new AflException(AflErrorKind.InvalidInput, source is AbstractGitHostTicketSource hostSource
                ? $"{repo.Name} isn't linked to a {hostSource.DisplayName} project. Run 'afl repo host detect' or 'afl repo host link <connection> <project>'."
                : $"{repo.Name} isn't linked to Jira. Link it with: afl jira link --instance <id>");
        }

        var filter = source.CreateFilter();
        filter.State = this.State;
        filter.Search = this.Search;
        filter.Limit = this.Limit;
        if (filter is GitHostTicketFilter gitHost)
        {
            gitHost.Assignee = this.Assignee;
            gitHost.Labels = this.Labels.ToList();
        }
        else if (filter is JiraTicketFilter jira)
        {
            jira.SavedFilter = this.Filter;
            jira.SkipDefaultFilter = this.NoDefaultFilter;
            jira.AllProjects = this.AllProjects;
            jira.Criteria = this.ToCriteria();
        }

        this.Output.Result(await source.ListAsync(repo, filter, ct), TextRendering.TicketList);
        return ExitSuccess;
    }

    /// <summary>
    ///     Picks the source when <c>--source</c> isn't given: Jira when Jira options are used, else the kind of the
    ///     repo's GitHub or GitLab link, else Jira when it's linked to Jira, otherwise GitHub.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <returns>The source name.</returns>
    private string DefaultSource(RepoRecord repo)
    {
        var wantsJira = this.HasJiraOnlyCriteria || this.Filter is not null || this.NoDefaultFilter || this.AllProjects;
        if (wantsJira)
        {
            return TicketRef.JiraSource;
        }

        return repo.Host?.Kind ?? (repo.Jira is not null ? TicketRef.JiraSource : TicketRef.GitHubSource);
    }
}

/// <summary>
///     Shows a ticket.
/// </summary>
[Verb("ticket", HelpText = "Show a ticket: afl ticket <#123 | owner/repo#123 | group/project#123 | PROJ-45 | URL>.")]
public class TicketCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the ticket reference.
    /// </summary>
    [Value(0, MetaName = "ticket", Required = true, HelpText = "The ticket reference.")]
    public string Ticket { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(await TicketSourceRegistry.Instance.FetchAsync(this.RequireRepoFor(this.Ticket), this.Ticket, ct), TextRendering.Ticket);
        return ExitSuccess;
    }
}
