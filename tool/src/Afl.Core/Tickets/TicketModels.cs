namespace Afl.Core.Tickets;

/// <summary>
///     A ticket from any source, normalised. Runs keep a snapshot of the ticket they started from.
/// </summary>
public class Ticket
{
    /// <summary>
    ///     Gets or sets the source name, e.g. <c>github</c> or <c>jira</c>.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the key as the source displays it, e.g. <c>owner/repo#12</c> or <c>PROJ-45</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the description (markdown for GitHub).
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    ///     Gets or sets the status, e.g. <c>open</c> or <c>In Progress</c>.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    ///     Gets or sets the web URL.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    ///     Gets or sets the assignee's login or display name.
    /// </summary>
    public string? Assignee { get; set; }

    /// <summary>
    ///     Gets or sets the labels.
    /// </summary>
    public List<string> Labels { get; set; } = [];
}

/// <summary>
///     A ticket to file, as the AI described it in an <c>afl-ticket</c> block.
/// </summary>
public class NewTicket
{
    /// <summary>
    ///     The kind of ticket used when none is given.
    /// </summary>
    public const string DefaultType = "task";

    /// <summary>
    ///     Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the description, in Markdown.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    ///     Gets or sets labels to add.
    /// </summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>
    ///     Gets or sets the kind of ticket: <c>bug</c>, <c>feature</c> or <c>task</c>. Each source maps it to its own types.
    /// </summary>
    public string Type { get; set; } = DefaultType;
}

/// <summary>
///     Filter options every ticket source understands. Sources extend this with their own options.
/// </summary>
public class TicketFilter
{
    /// <summary>
    ///     Gets or sets which tickets to include: <c>open</c>, <c>closed</c>, or <c>all</c>.
    /// </summary>
    public string State { get; set; } = "open";

    /// <summary>
    ///     Gets or sets free-text search.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    ///     Gets or sets the maximum number of tickets to return.
    /// </summary>
    public int Limit { get; set; } = 50;
}
