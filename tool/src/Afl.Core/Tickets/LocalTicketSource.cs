using Afl.Core.Repos;
using Afl.Core.Workflows;

namespace Afl.Core.Tickets;

/// <summary>
///     Work with no ticket: <c>local:&lt;name&gt;</c> (or <c>afl start --name</c>) gives a run a ticket made from the
///     name alone, keyed by its slug, so workflows that only need a title and a stable key (e.g. for their scratch
///     directory) run without a ticket system. It has nothing to list and can't file tickets.
/// </summary>
[TicketSourceRegistration(TicketRef.LocalSource)]
public class LocalTicketSource() : AbstractTicketSource<TicketFilter>(TicketRef.LocalSource)
{
    /// <inheritdoc />
    /// <remarks>Always false: there are no local tickets to list, so it never shows as a source to browse.</remarks>
    public override bool IsAvailableFor(RepoRecord repo)
    {
        return false;
    }

    /// <inheritdoc />
    public override Task<Ticket> GetAsync(RepoRecord repo, TicketRef reference, CancellationToken ct = default)
    {
        var name = reference.Key ?? reference.Raw;
        return Task.FromResult(new Ticket
        {
            Source = TicketRef.LocalSource,
            Key = TemplateVariables.Slug(name),
            Title = name,
            Body = string.Empty,
        });
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<Ticket>> ListTypedAsync(RepoRecord repo, TicketFilter filter, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Ticket>>([]);
    }
}
