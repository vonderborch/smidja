namespace Afl.Core.Workflows;

/// <summary>
///     How a document matched a <see cref="LibraryQuery" />.
/// </summary>
/// <param name="InName">
///     Whether every search term was found in the document's id, name, description or tags; false when some were only
///     found in its contents.
/// </param>
/// <param name="Snippet">The line of the contents a term was found on, when it wasn't found by name; otherwise null.</param>
public record LibraryMatch(bool InName, string? Snippet);

/// <summary>
///     What to look for in a layered library: search text, tags a document must all have, and the scope it must come
///     from. An empty query matches everything.
/// </summary>
public class LibraryQuery
{
    /// <summary>
    ///     The most characters of a line a snippet keeps.
    /// </summary>
    private const int SnippetLength = 90;

    /// <summary>
    ///     Gets or sets the search text. Each word must be found somewhere, ignoring case.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    ///     Gets or sets whether the search looks in the contents (prompts, instructions, files) as well as the id, name,
    ///     description and tags.
    /// </summary>
    public bool SearchContents { get; set; } = true;

    /// <summary>
    ///     Gets or sets the tags a document must all have.
    /// </summary>
    public IReadOnlyCollection<string> Tags { get; set; } = [];

    /// <summary>
    ///     Gets or sets the scope a document must come from, or null for any.
    /// </summary>
    public WorkflowScope? Scope { get; set; }

    /// <summary>
    ///     Gets the words of <see cref="Text" />.
    /// </summary>
    public IReadOnlyList<string> Terms =>
        (this.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    ///     Gets whether the query matches everything.
    /// </summary>
    public bool IsEmpty => this.Terms.Count == 0 && this.Tags.Count == 0 && this.Scope is null;

    /// <summary>
    ///     Checks a document against the query.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="scope">Where it came from.</param>
    /// <returns>How it matched, or null when it doesn't.</returns>
    public LibraryMatch? Match(AbstractLibraryDocument document, WorkflowScope scope)
    {
        if (this.Scope is { } wanted && wanted != scope)
        {
            return null;
        }

        if (this.Tags.Any(t => !document.HasTag(t)))
        {
            return null;
        }

        var terms = this.Terms;
        var name = document.NameText().ToList();
        var missing = terms.Where(t => !name.Any(n => n.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count == 0)
        {
            return new LibraryMatch(true, null);
        }

        if (!this.SearchContents)
        {
            return null;
        }

        var contents = document.ContentText().ToList();
        if (missing.Any(t => !contents.Any(c => c.Contains(t, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        return new LibraryMatch(false, Snippet(contents, missing[0]));
    }

    /// <summary>
    ///     Filters and orders listings by the query: documents matched by name first, then ones matched in their
    ///     contents, keeping the listings' order within each.
    /// </summary>
    /// <param name="listings">The listings.</param>
    /// <param name="documentOf">Returns a listing's document.</param>
    /// <param name="scopeOf">Returns a listing's scope.</param>
    /// <typeparam name="T">The listing type.</typeparam>
    /// <returns>The matching listings, with how each matched.</returns>
    public IReadOnlyList<(T Listing, LibraryMatch Match)> Apply<T>(IEnumerable<T> listings, Func<T, AbstractLibraryDocument> documentOf,
        Func<T, WorkflowScope> scopeOf)
    {
        return listings
            .Select(l => (Listing: l, Match: this.Match(documentOf(l), scopeOf(l))))
            .Where(m => m.Match is not null)
            .OrderBy(m => m.Match!.InName ? 0 : 1)
            .Select(m => (m.Listing, m.Match!))
            .ToList();
    }

    /// <summary>
    ///     Returns the trimmed line of the contents a term is first found on, shortened around it.
    /// </summary>
    /// <param name="contents">The contents.</param>
    /// <param name="term">The term.</param>
    /// <returns>The snippet.</returns>
    private static string? Snippet(IEnumerable<string> contents, string term)
    {
        var line = contents
            .SelectMany(c => c.Split('\n'))
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (line is null || line.Length <= SnippetLength)
        {
            return line;
        }

        var at = line.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        var start = Math.Clamp(at - (SnippetLength / 3), 0, line.Length - SnippetLength);
        return (start > 0 ? "…" : string.Empty) + line.Substring(start, SnippetLength).Trim() +
               (start + SnippetLength < line.Length ? "…" : string.Empty);
    }
}
