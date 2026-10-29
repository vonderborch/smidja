using Afl.Core.Exceptions;
using Afl.Core.GitHosts;
using Afl.Core.Helpers;
using Afl.Core.Repos;

namespace Afl.Core.Tickets;

/// <summary>
///     GitHub and GitLab filter options on top of the common ones.
/// </summary>
public class GitHostTicketFilter : TicketFilter
{
    /// <summary>
    ///     Gets or sets an assignee login to filter by (<c>@me</c> for yourself).
    /// </summary>
    public string? Assignee { get; set; }

    /// <summary>
    ///     Gets or sets labels that must all be present.
    /// </summary>
    public List<string> Labels { get; set; } = [];
}

/// <summary>
///     Issues on a git host (GitHub or GitLab, cloud or self-hosted). A repo's issues come from the project it's linked
///     to (<see cref="RepoRecord.Host" />), through the connection's REST client; a subclass only names its kind, which
///     must match a registered <see cref="AbstractGitHostClient" />.
/// </summary>
/// <param name="kind">The kind of host, also the source name.</param>
public abstract class AbstractGitHostTicketSource(string kind) : AbstractTicketSource<GitHostTicketFilter>(kind)
{
    /// <summary>
    ///     Gets or sets the git host connections and tokens. When null, the ones in <c>$AFL_HOME</c> (or <c>~/.afl</c>)
    ///     are used.
    /// </summary>
    public GitHostStore? Hosts { get; set; }

    /// <summary>
    ///     Gets or sets the HTTP client requests go through (tests point it at a fake API). When null, the store's shared
    ///     one is used.
    /// </summary>
    public HttpClient? Http { get; set; }

    /// <summary>
    ///     Gets the name of the kind of host, e.g. <c>GitHub</c>.
    /// </summary>
    public string DisplayName => GitHostClientRegistry.Instance.DisplayNameOf(this.Name);

    /// <inheritdoc />
    public override bool CanHandle(TicketRef reference)
    {
        // A URL's kind comes from the connection its site is on, so a self-managed GitLab's URL isn't taken for GitHub's.
        return reference.BaseUrl is not null && this.Store().FindByUrl(reference.Raw) is { } connection
            ? connection.Kind == this.Name
            : base.CanHandle(reference);
    }

    /// <inheritdoc />
    public override bool IsAvailableFor(RepoRecord repo)
    {
        return repo.Host?.Kind == this.Name;
    }

    /// <inheritdoc />
    public override bool CanCreateFor(RepoRecord repo)
    {
        return this.IsAvailableFor(repo);
    }

    /// <inheritdoc />
    public override async Task<Ticket> GetAsync(RepoRecord repo, TicketRef reference, CancellationToken ct = default)
    {
        var (connection, project) = this.TargetFor(repo, reference);
        var client = await this.Store().CreateClientAsync(connection, this.Http, ct);
        return await client.GetIssueAsync(project, reference.Number!.Value, ct);
    }

    /// <inheritdoc />
    public override string DescribeDestination(RepoRecord repo, Ticket? related)
    {
        return $"{this.DisplayName} issues in {this.TryTargetFor(repo, related)?.Project ?? "the repo"}";
    }

    /// <inheritdoc />
    public override async Task<Ticket> CreateAsync(RepoRecord repo, Ticket? related, NewTicket ticket, CancellationToken ct = default)
    {
        var (connection, project) = this.TryTargetFor(repo, related) ?? this.TargetFor(repo, null);
        var client = await this.Store().CreateClientAsync(connection, this.Http, ct);
        return await client.CreateIssueAsync(project, ticket, ct);
    }

    /// <inheritdoc />
    public override string? ManualCreateUrl(RepoRecord repo, Ticket? related, NewTicket ticket)
    {
        return this.TryTargetFor(repo, related) is var (connection, project)
            ? this.Store().CreateClient(connection, null, this.Http).NewIssueUrl(project, ticket)
            : null;
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<Ticket>> ListTypedAsync(RepoRecord repo, GitHostTicketFilter filter, CancellationToken ct)
    {
        var (connection, project) = this.TargetFor(repo, null);
        var client = await this.Store().CreateClientAsync(connection, this.Http, ct);
        return await client.ListIssuesAsync(project, filter, ct);
    }

    /// <summary>
    ///     Returns the store connections come from.
    /// </summary>
    /// <returns>The store.</returns>
    protected GitHostStore Store()
    {
        return this.Hosts ??= new GitHostStore(AflPaths.FromEnvironment());
    }

    /// <summary>
    ///     Works out which connection and project a reference is on: a URL's site and path, a named project on the repo's
    ///     connection (or the cloud one), else the repo's linked project.
    /// </summary>
    /// <param name="repo">The repo the reference is used in.</param>
    /// <param name="reference">The reference, or null for the repo's own project.</param>
    /// <returns>The connection and project path.</returns>
    /// <exception cref="AflException">The URL's site isn't a connection, or the repo isn't linked to a project.</exception>
    protected virtual (GitHostConnection Connection, string Project) TargetFor(RepoRecord repo, TicketRef? reference)
    {
        var store = this.Store();
        if (reference is { BaseUrl: { } baseUrl, Repository: { } path })
        {
            var connection = store.FindByUrl(reference.Raw) ?? throw new AflException(AflErrorKind.InvalidInput,
                $"There's no {this.DisplayName} connection for {baseUrl}. Add one in Settings → Git hosts or with: afl host add <id> --kind {this.Name} --url {baseUrl}");
            var prefix = connection.PathPrefix.Trim('/');
            return (connection, prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? path[(prefix.Length + 1)..] : path);
        }

        if (reference?.Repository is { } project)
        {
            // owner/repo#123 is on the repo's own site when it's linked to this kind, else on the cloud one.
            var connectionId = repo.Host is { } host && host.Kind == this.Name ? host.ConnectionId : this.Name;
            return (store.GetRequired(connectionId), project);
        }

        if (repo.Host is not { } link || link.Kind != this.Name)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"{repo.Name} isn't linked to a {this.DisplayName} project: run 'afl repo host detect' or 'afl repo host link <connection> <project>', or link it in Repo settings.");
        }

        return (store.GetRequired(link.ConnectionId), link.Project);
    }

    /// <summary>
    ///     Works out where new issues go: the run's ticket's project when it's one of this source's, else the repo's.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="related">The run's ticket, if any.</param>
    /// <returns>The connection and project, or null when there's nowhere to file them.</returns>
    private (GitHostConnection Connection, string Project)? TryTargetFor(RepoRecord repo, Ticket? related)
    {
        try
        {
            var reference = related is not null && related.Source == this.Name && TicketRef.TryParse(related.Url ?? related.Key, out var parsed)
                ? parsed
                : null;
            return reference?.Repository is not null || this.IsAvailableFor(repo) ? this.TargetFor(repo, reference) : null;
        }
        catch (AflException)
        {
            return null;
        }
    }
}

/// <summary>
///     GitHub issues, on github.com or a GitHub Enterprise Server.
/// </summary>
[TicketSourceRegistration(TicketRef.GitHubSource)]
public class GitHubTicketSource() : AbstractGitHostTicketSource(TicketRef.GitHubSource);

/// <summary>
///     GitLab issues, on gitlab.com or a self-managed GitLab.
/// </summary>
[TicketSourceRegistration(TicketRef.GitLabSource)]
public class GitLabTicketSource() : AbstractGitHostTicketSource(TicketRef.GitLabSource);
