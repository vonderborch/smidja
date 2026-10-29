using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Repos;
using Niddy.Core.Resilience;

namespace Afl.Core.Tickets;

/// <summary>
///     Registers a ticket source with the <see cref="TicketSourceRegistry" />.
/// </summary>
/// <param name="name">The source name, matching <see cref="TicketRef.Source" />.</param>
public class TicketSourceRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     Non-generic view of a ticket source, so front-ends can work with any source. Implement
///     <see cref="AbstractTicketSource{TFilter}" /> rather than deriving from this directly.
/// </summary>
/// <param name="name">The source name.</param>
public abstract class AbstractTicketSource(string name)
{
    /// <summary>
    ///     Gets the source name, e.g. <c>github</c>.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Returns whether this source can fetch the referenced ticket.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <returns>True if it can.</returns>
    public virtual bool CanHandle(TicketRef reference)
    {
        return string.Equals(reference.Source, this.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns whether this source is set up for a repo (e.g. the repo has a GitHub remote).
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <returns>True if tickets can be listed for the repo.</returns>
    public abstract bool IsAvailableFor(RepoRecord repo);

    /// <summary>
    ///     Creates this source's filter with default values.
    /// </summary>
    /// <returns>The filter.</returns>
    public abstract TicketFilter CreateFilter();

    /// <summary>
    ///     Lists tickets for a repo.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="filter">The filter; a base <see cref="TicketFilter" /> is widened to the source's own type.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The tickets.</returns>
    public abstract Task<IReadOnlyList<Ticket>> ListAsync(RepoRecord repo, TicketFilter filter, CancellationToken ct = default);

    /// <summary>
    ///     Fetches one ticket.
    /// </summary>
    /// <param name="repo">The repo the ticket is being used in.</param>
    /// <param name="reference">The reference.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The ticket.</returns>
    public abstract Task<Ticket> GetAsync(RepoRecord repo, TicketRef reference, CancellationToken ct = default);

    /// <summary>
    ///     Gets how strongly to prefer this source for filing tickets when the run's ticket doesn't settle it; higher wins.
    /// </summary>
    public virtual int CreatePreference => 0;

    /// <summary>
    ///     Returns whether this source can file tickets for a repo. Sources that can't leave this false.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <returns>True if <see cref="CreateAsync" /> works for it.</returns>
    public virtual bool CanCreateFor(RepoRecord repo)
    {
        return false;
    }

    /// <summary>
    ///     Describes where tickets for a repo go, for the AI's instructions (e.g. <c>GitHub issues in owner/repo</c>).
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="related">The ticket the run works on, which may say where (e.g. its Jira project).</param>
    /// <returns>The description.</returns>
    public virtual string DescribeDestination(RepoRecord repo, Ticket? related)
    {
        return this.Name;
    }

    /// <summary>
    ///     Files a ticket.
    /// </summary>
    /// <param name="repo">The repo the run works in.</param>
    /// <param name="related">The ticket the run works on, which may say where the new one goes.</param>
    /// <param name="ticket">What to file.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The new ticket.</returns>
    /// <exception cref="AflException">It couldn't be filed; <see cref="ManualCreateUrl" /> may say where to file it by hand.</exception>
    public virtual Task<Ticket> CreateAsync(RepoRecord repo, Ticket? related, NewTicket ticket, CancellationToken ct = default)
    {
        throw new AflException(AflErrorKind.InvalidState, $"afl can't file {this.Name} tickets.");
    }

    /// <summary>
    ///     Returns a web address where the user can file the ticket by hand when afl couldn't, or null.
    /// </summary>
    /// <param name="repo">The repo the run works in.</param>
    /// <param name="related">The ticket the run works on.</param>
    /// <param name="ticket">What to file.</param>
    /// <returns>The address, or null.</returns>
    public virtual string? ManualCreateUrl(RepoRecord repo, Ticket? related, NewTicket ticket)
    {
        return null;
    }
}

/// <summary>
///     Base for ticket sources. A new ticket system is one subclass with a
///     <see cref="TicketSourceRegistrationAttribute" />; its filter type carries whatever options the system supports.
/// </summary>
/// <typeparam name="TFilter">The source's filter type.</typeparam>
/// <param name="name">The source name.</param>
public abstract class AbstractTicketSource<TFilter>(string name) : AbstractTicketSource(name)
    where TFilter : TicketFilter, new()
{
    /// <inheritdoc />
    public override TicketFilter CreateFilter()
    {
        return new TFilter();
    }

    /// <inheritdoc />
    public sealed override Task<IReadOnlyList<Ticket>> ListAsync(RepoRecord repo, TicketFilter filter, CancellationToken ct = default)
    {
        if (filter is TFilter typed)
        {
            return this.ListTypedAsync(repo, typed, ct);
        }

        // Widen a base filter to this source's type, keeping the common options.
        return this.ListTypedAsync(repo, new TFilter { State = filter.State, Search = filter.Search, Limit = filter.Limit }, ct);
    }

    /// <summary>
    ///     Lists tickets for a repo.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The tickets.</returns>
    protected abstract Task<IReadOnlyList<Ticket>> ListTypedAsync(RepoRecord repo, TFilter filter, CancellationToken ct);

    /// <summary>
    ///     How <see cref="RunToolAsync" /> retries: only the failures <see cref="IsTransient" /> picks out, which it
    ///     throws as <see cref="IOException" />s, with a little jitter so parallel fetches don't retry in step.
    /// </summary>
    private static readonly RetryOptions TransientToolRetry = new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(250),
        MaxJitter = TimeSpan.FromMilliseconds(100),
        ShouldRetry = ex => ex is IOException,
    };

    /// <summary>
    ///     Gets how <see cref="RunToolAsync" /> retries transient tool failures.
    /// </summary>
    protected virtual RetryOptions ToolRetryOptions => TransientToolRetry;

    /// <summary>
    ///     Runs a command-line tool, retrying transient failures, and returns its stdout.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The standard output.</returns>
    /// <exception cref="AflException">The tool failed on every attempt.</exception>
    protected virtual async Task<string> RunToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await Retry.ExecuteAsync(async () =>
            {
                var attempt = await ProcessRunner.RunAsync(executable, arguments, workingDirectory, ct: ct);
                if (!attempt.Success && this.IsTransient(attempt.StandardError))
                {
                    throw new IOException(attempt.StandardError.Trim());
                }

                return attempt;
            }, this.ToolRetryOptions, ct);
        }
        catch (MaxRetriesException ex)
        {
            throw new AflException(AflErrorKind.ExternalTool,
                $"{executable} {string.Join(' ', arguments.Take(2))} failed {ex.Attempts} times: {ex.InnerException?.Message}", ex);
        }

        if (!result.Success)
        {
            throw new AflException(AflErrorKind.ExternalTool,
                $"{executable} {string.Join(' ', arguments.Take(2))} failed: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    /// <summary>
    ///     Returns whether a tool error looks transient (network trouble) and worth retrying.
    /// </summary>
    /// <param name="standardError">The tool's stderr.</param>
    /// <returns>True if transient.</returns>
    protected virtual bool IsTransient(string standardError)
    {
        return standardError.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               standardError.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               standardError.Contains("502") || standardError.Contains("503");
    }
}
