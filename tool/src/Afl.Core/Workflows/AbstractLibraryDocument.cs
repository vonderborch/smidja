using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Storage;

namespace Afl.Core.Workflows;

/// <summary>
///     A document kept in a layered library (<see cref="AbstractLayeredLibrary{TDocument,TListing}" />): a workflow, a
///     common step or a skill. Every one has an id, a name, a description and tags, which is what the libraries list,
///     filter and search by (<see cref="LibraryQuery" />).
/// </summary>
public abstract partial class AbstractLibraryDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     The most tags one document can have.
    /// </summary>
    public const int MaxTags = 20;

    /// <summary>
    ///     The longest a tag can be.
    /// </summary>
    public const int MaxTagLength = 40;

    /// <summary>
    ///     Gets or sets the id, used to refer to the document and as its file name.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets what the document is for.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Gets or sets the tags people filter the library by, e.g. <c>review</c> or <c>planning</c>: lowercase letters,
    ///     digits and hyphens.
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    ///     Turns tags as someone typed them into the form they are kept in: trimmed, lowercase, spaces as hyphens, without
    ///     blanks or repeats, in order.
    /// </summary>
    /// <param name="tags">The tags.</param>
    /// <returns>The normalized tags.</returns>
    public static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return (tags ?? [])
            .Select(t => Whitespace().Replace(t.Trim().ToLowerInvariant(), "-"))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Checks the document's tags: how many there are, and that each is lowercase letters, digits and hyphens.
    /// </summary>
    /// <returns>The problems; empty when they are valid.</returns>
    public IEnumerable<string> ValidateTags()
    {
        var tags = this.Tags ?? [];
        if (tags.Count > MaxTags)
        {
            yield return $"has {tags.Count} tags; the most is {MaxTags}.";
        }

        foreach (var tag in tags)
        {
            if (!TagPattern().IsMatch(tag) || tag.Length > MaxTagLength)
            {
                yield return $"tag '{tag}' must be lowercase letters, digits and hyphens, at most {MaxTagLength} characters.";
            }
        }

        foreach (var repeat in tags.GroupBy(t => t, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return $"tag '{repeat.Key}' is listed more than once.";
        }
    }

    /// <summary>
    ///     Gets whether the document has a tag.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>True if it has it.</returns>
    public bool HasTag(string tag)
    {
        return this.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    ///     Returns the text a search by name looks in: the id, name, description and tags.
    /// </summary>
    /// <returns>The text.</returns>
    public virtual IEnumerable<string> NameText()
    {
        yield return this.Id;
        yield return this.Name;
        if (this.Description is not null)
        {
            yield return this.Description;
        }

        foreach (var tag in this.Tags ?? [])
        {
            yield return tag;
        }
    }

    /// <summary>
    ///     Returns the text a search of the contents looks in: by default, every string in the document as it is saved
    ///     (prompts, instructions, parameters, files), so a new field is searched without anyone listing it.
    /// </summary>
    /// <returns>The text.</returns>
    public virtual IEnumerable<string> ContentText()
    {
        var node = JsonSerializer.SerializeToNode(this, this.GetType(), JsonConstants.Options);
        return Strings(node);
    }

    /// <summary>
    ///     Returns every string value under a JSON node, depth first.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The strings.</returns>
    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    foreach (var text in Strings(child))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var text in Strings(child))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return text;
                break;
        }
    }

    /// <summary>
    ///     The shape of a tag.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex TagPattern();

    /// <summary>
    ///     A run of whitespace, which becomes one hyphen in a tag.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
