using Afl.Core.Exceptions;
using Afl.Core.GitHosts;
using Afl.Core.Jira;
using Afl.Core.Registration;
using Afl.Core.Repos;

namespace Afl.Core.Tickets;

/// <summary>
///     The ticket sources afl knows about, discovered from <see cref="TicketSourceRegistrationAttribute" />.
/// </summary>
public class TicketSourceRegistry() : TypeRegistry<AbstractTicketSource, TicketSourceRegistrationAttribute>("ticket source")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static TicketSourceRegistry Instance { get; } = new();

    /// <summary>
    ///     Gets the Jira instance store the sources this registry creates use. When null, they use the one in
    ///     <c>$AFL_HOME</c> (or <c>~/.afl</c>).
    /// </summary>
    public JiraInstanceStore? JiraInstances { get; init; }

    /// <summary>
    ///     Gets the HTTP client the Jira sources this registry creates use. When null, each Jira client creates its own.
    /// </summary>
    public HttpClient? JiraHttp { get; init; }

    /// <summary>
    ///     Gets the git host connections the GitHub and GitLab sources this registry creates use. When null, they use the
    ///     ones in <c>$AFL_HOME</c> (or <c>~/.afl</c>).
    /// </summary>
    public GitHostStore? GitHosts { get; init; }

    /// <summary>
    ///     Gets the HTTP client the GitHub and GitLab sources this registry creates use (tests point it at a fake API).
    ///     When null, the store's shared one is used.
    /// </summary>
    public HttpClient? GitHostHttp { get; init; }

    /// <summary>
    ///     Returns the source that can fetch a reference.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The source.</returns>
    /// <exception cref="AflException">No registered source handles it.</exception>
    public AbstractTicketSource Resolve(TicketRef reference)
    {
        return this.CreateAll().FirstOrDefault(s => s.CanHandle(reference))
               ?? throw new AflException(AflErrorKind.InvalidInput,
                   $"No ticket source can open '{reference.Raw}' ({reference.Source} isn't set up).");
    }

    /// <summary>
    ///     Returns the source new tickets for a run go to: the run's ticket's source if it can file them for the repo,
    ///     else the one that can that's most preferred (<see cref="AbstractTicketSource.CreatePreference" />).
    /// </summary>
    /// <param name="repo">The repo the run works in.</param>
    /// <param name="related">The run's ticket, if any.</param>
    /// <returns>The source, or null when none can file tickets for the repo.</returns>
    public AbstractTicketSource? CreatorFor(RepoRecord repo, Ticket? related)
    {
        var able = this.CreateAll().Where(s => s.CanCreateFor(repo)).ToList();
        return able.FirstOrDefault(s => string.Equals(s.Name, related?.Source, StringComparison.OrdinalIgnoreCase))
               ?? able.OrderByDescending(s => s.CreatePreference).FirstOrDefault();
    }

    /// <summary>
    ///     Parses and fetches a ticket.
    /// </summary>
    /// <param name="repo">The repo the ticket is being used in.</param>
    /// <param name="text">The reference as typed.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The ticket.</returns>
    public Task<Ticket> FetchAsync(RepoRecord repo, string text, CancellationToken ct = default)
    {
        var reference = TicketRef.Parse(text).ForRepo(repo);
        return this.Resolve(reference).GetAsync(repo, reference, ct);
    }

    /// <inheritdoc />
    protected override AbstractTicketSource OnCreated(AbstractTicketSource instance)
    {
        if (instance is JiraTicketSource jira)
        {
            jira.Instances ??= this.JiraInstances;
            jira.Http ??= this.JiraHttp;
        }
        else if (instance is AbstractGitHostTicketSource gitHost)
        {
            gitHost.Hosts ??= this.GitHosts;
            gitHost.Http ??= this.GitHostHttp;
        }

        return instance;
    }
}
