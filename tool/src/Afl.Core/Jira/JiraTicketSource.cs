using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Repos;
using Afl.Core.Tickets;

namespace Afl.Core.Jira;

/// <summary>
///     Jira filter options on top of the common ones: a saved filter to start from, and criteria that override it.
/// </summary>
public class JiraTicketFilter : TicketFilter
{
    /// <summary>
    ///     Gets or sets the name of a saved filter on the repo's Jira link. When null, the link's default filter is used
    ///     unless <see cref="SkipDefaultFilter" /> is set.
    /// </summary>
    public string? SavedFilter { get; set; }

    /// <summary>
    ///     Gets or sets whether to ignore the link's default filter when no saved filter is named.
    /// </summary>
    public bool SkipDefaultFilter { get; set; }

    /// <summary>
    ///     Gets or sets whether to search every project rather than only the repo's associated ones.
    /// </summary>
    public bool AllProjects { get; set; }

    /// <summary>
    ///     Gets or sets criteria applied on top of the saved filter; any that are set replace the saved ones.
    /// </summary>
    public JiraFilter Criteria { get; set; } = new();
}

/// <summary>
///     Jira issues, from the instance the repo is linked to (or, for a Jira URL, the instance serving it).
/// </summary>
[TicketSourceRegistration(TicketRef.JiraSource)]
public class JiraTicketSource() : AbstractTicketSource<JiraTicketFilter>(TicketRef.JiraSource)
{
    /// <summary>
    ///     Gets or sets the instance store to use. When null, the store in <c>$AFL_HOME</c> (or <c>~/.afl</c>) is used.
    /// </summary>
    public JiraInstanceStore? Instances { get; set; }

    /// <summary>
    ///     Gets or sets the HTTP client the Jira clients use. When null, each client creates its own.
    /// </summary>
    public HttpClient? Http { get; set; }

    /// <inheritdoc />
    public override bool IsAvailableFor(RepoRecord repo)
    {
        return repo.Jira is not null;
    }

    /// <inheritdoc />
    public override Task<Ticket> GetAsync(RepoRecord repo, TicketRef reference, CancellationToken ct = default)
    {
        var store = this.CreateStore();
        var instance = ResolveInstance(store, repo, reference.BaseUrl);
        return this.CreateClient(store, instance).GetIssueAsync(reference.Key!, ct);
    }

    /// <summary>
    ///     Maps a ticket's kind (<see cref="NewTicket.Type" />) to the Jira issue type it's filed as.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> IssueTypes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["bug"] = "Bug", ["feature"] = "Story", ["task"] = "Task" };

    /// <summary>
    ///     Gets the issue type used when a kind isn't mapped, or the project doesn't have the mapped one.
    /// </summary>
    protected virtual string FallbackIssueType => "Task";

    /// <inheritdoc />
    public override int CreatePreference => 1;

    /// <inheritdoc />
    public override bool CanCreateFor(RepoRecord repo)
    {
        return repo.Jira is { Projects.Count: > 0 };
    }

    /// <inheritdoc />
    public override string DescribeDestination(RepoRecord repo, Ticket? related)
    {
        return ProjectFor(repo, related) is { } project ? $"Jira project {project}" : "Jira";
    }

    /// <inheritdoc />
    public override async Task<Ticket> CreateAsync(RepoRecord repo, Ticket? related, NewTicket ticket, CancellationToken ct = default)
    {
        var project = ProjectFor(repo, related) ?? throw new AflException(AflErrorKind.InvalidState,
            "afl doesn't know which Jira project to file in. Associate one with: afl jira project add <KEY>");
        var store = this.CreateStore();
        var instance = ResolveInstance(store, repo, repo.Jira is null && related is { Source: TicketRef.JiraSource } ? related.Url : null);
        var client = this.CreateClient(store, instance);
        var type = this.IssueTypes.GetValueOrDefault(ticket.Type, this.FallbackIssueType);
        try
        {
            return await client.CreateIssueAsync(project, type, ticket, ct);
        }
        catch (AflException ex) when (ex.Kind == AflErrorKind.InvalidInput && type != this.FallbackIssueType)
        {
            // The project may not have that issue type; file it as the fallback rather than not at all.
            return await client.CreateIssueAsync(project, this.FallbackIssueType, ticket, ct);
        }
    }

    /// <summary>
    ///     Works out the project new issues go to: the run's ticket's, else the repo's first associated project.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="related">The run's ticket.</param>
    /// <returns>The project key, or null.</returns>
    private static string? ProjectFor(RepoRecord repo, Ticket? related)
    {
        return related is { Source: TicketRef.JiraSource } && TicketRef.TryParse(related.Key, out var reference) &&
               reference!.ProjectKey is { } project
            ? project
            : repo.Jira?.Projects.FirstOrDefault();
    }

    /// <summary>
    ///     Works out the criteria for a listing: the named saved filter (or the default one), overridden by any criteria
    ///     given directly, and limited to the repo's projects unless <see cref="JiraTicketFilter.AllProjects" /> is set.
    /// </summary>
    /// <param name="link">The repo's Jira link.</param>
    /// <param name="filter">The listing options.</param>
    /// <returns>The criteria to search with.</returns>
    /// <exception cref="AflException">The named filter doesn't exist.</exception>
    public static JiraFilter ResolveCriteria(RepoJiraLink link, JiraTicketFilter filter)
    {
        var name = filter.SavedFilter ?? (filter.SkipDefaultFilter ? null : link.DefaultFilter);
        var saved = name is null
            ? new JiraFilter()
            : link.FindFilter(name) ?? throw new AflException(AflErrorKind.NotFound,
                $"No Jira filter '{name}'. Saved: {string.Join(", ", link.Filters.Select(f => f.Name).DefaultIfEmpty("none"))}.");
        var criteria = saved.MergedWith(filter.Criteria);
        if (!filter.AllProjects)
        {
            criteria.ProjectScope = link.Projects.ToList();
        }

        return criteria;
    }

    /// <summary>
    ///     Picks the instance for a repo: the one serving <paramref name="url" /> if given, else the linked one, else the
    ///     only one configured.
    /// </summary>
    /// <param name="store">The instance store.</param>
    /// <param name="repo">The repo.</param>
    /// <param name="url">A Jira URL the ticket came from, if any.</param>
    /// <returns>The instance.</returns>
    /// <exception cref="AflException">No instance fits.</exception>
    public static JiraInstance ResolveInstance(JiraInstanceStore store, RepoRecord repo, string? url)
    {
        if (url is not null)
        {
            return store.FindByUrl(url) ?? throw new AflException(AflErrorKind.InvalidState,
                $"No Jira instance is set up for {url}. Add it with: afl jira instance add <id> --url {url} …");
        }

        if (repo.Jira is { } link)
        {
            return store.GetRequired(link.InstanceId);
        }

        var all = store.All();
        return all.Count == 1
            ? all[0]
            : throw new AflException(AflErrorKind.InvalidState, all.Count == 0
                ? "No Jira instances are set up. Add one with: afl jira instance add <id> --kind cloud|datacenter --url <url>"
                : $"{repo.Name} isn't linked to a Jira instance. Link it with: afl jira link --instance <id>");
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<Ticket>> ListTypedAsync(RepoRecord repo, JiraTicketFilter filter, CancellationToken ct)
    {
        var link = repo.Jira ?? throw new AflException(AflErrorKind.InvalidState,
            $"{repo.Name} isn't linked to Jira. Link it with: afl jira link --instance <id>");
        var store = this.CreateStore();
        var client = this.CreateClient(store, store.GetRequired(link.InstanceId));
        return client.ListAsync(ResolveCriteria(link, filter), filter, ct);
    }

    /// <summary>
    ///     Creates the instance store.
    /// </summary>
    /// <returns>The store.</returns>
    protected virtual JiraInstanceStore CreateStore()
    {
        return this.Instances ?? new JiraInstanceStore(AflPaths.FromEnvironment());
    }

    /// <summary>
    ///     Creates the client for an instance.
    /// </summary>
    /// <param name="store">The instance store.</param>
    /// <param name="instance">The instance.</param>
    /// <returns>The client.</returns>
    protected virtual AbstractJiraClient CreateClient(JiraInstanceStore store, JiraInstance instance)
    {
        return store.CreateClient(instance, this.Http);
    }
}
