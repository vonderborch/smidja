using System.Text;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Workflows;
using Niddy.Core.IO;

namespace Afl.Core.Authoring;

/// <summary>
///     Registers a kind of document the AI authoring helper can draft with the <see cref="AuthoringKindRegistry" />.
/// </summary>
/// <param name="name">The kind's name, e.g. <c>skill</c>.</param>
public class AuthoringKindRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     The kinds of document the AI authoring helper can draft, by name.
/// </summary>
public class AuthoringKindRegistry() : TypeRegistry<AbstractAuthoringKind, AuthoringKindRegistrationAttribute>("kind of document")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static AuthoringKindRegistry Instance { get; } = new();
}

/// <summary>
///     What the AI authoring helper found in a draft: what it is and what's wrong with it.
/// </summary>
/// <param name="Id">The document's id, or null when the draft can't be read.</param>
/// <param name="Name">The document's name.</param>
/// <param name="Description">The document's description.</param>
/// <param name="Tags">The document's tags.</param>
/// <param name="Detail">A line on what's in it, e.g. <c>4 steps</c>.</param>
/// <param name="Problems">Why it can't be saved; empty when it can.</param>
public record AuthoringDraftCheck(
    string? Id,
    string? Name,
    string? Description,
    IReadOnlyList<string> Tags,
    string? Detail,
    IReadOnlyList<string> Problems)
{
    /// <summary>
    ///     Gets whether the draft can be saved.
    /// </summary>
    public bool IsValid => this.Problems.Count == 0;
}

/// <summary>
///     A document already in a library, as the AI authoring helper starts improving it.
/// </summary>
/// <param name="Id">The document's id.</param>
/// <param name="Scope">The scope it's in.</param>
/// <param name="Json">The document, as JSON.</param>
public record AuthoringExisting(string Id, WorkflowScope Scope, string Json);

/// <summary>
///     Base for a kind of document the AI authoring helper drafts: a skill, a common step or a workflow. It knows the
///     kind's library, what its agent is told and given to read, and how a draft is checked and saved. A new kind is one
///     subclass of <see cref="AbstractAuthoringKind{TDocument, TListing, TLibrary}" /> with an
///     <see cref="AuthoringKindRegistrationAttribute" />.
/// </summary>
/// <param name="name">The kind's name, as registered.</param>
public abstract class AbstractAuthoringKind(string name)
{
    /// <summary>
    ///     The name of the file in a session's reference folder that lists what's already in the library.
    /// </summary>
    public const string CatalogFileName = "catalog.md";

    /// <summary>
    ///     The name of the file in a session's reference folder holding an example document.
    /// </summary>
    public const string ExampleFileName = "example.json";

    /// <summary>
    ///     Gets the kind's name, e.g. <c>skill</c>.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets what one document of this kind is called in messages, e.g. <c>common step</c>.
    /// </summary>
    public abstract string Noun { get; }

    /// <summary>
    ///     Gets the reference files (embedded under <see cref="PathConstants.AuthoringResourcePrefix" />) the agent is
    ///     given to read: the kind's schema, and any guide.
    /// </summary>
    public abstract IReadOnlyList<string> ReferenceFiles { get; }

    /// <summary>
    ///     Gets the id of a built-in document the agent is given as an example.
    /// </summary>
    public abstract string ExampleId { get; }

    /// <summary>
    ///     Gets the kind's own rules for the agent: what makes a good document of this kind.
    /// </summary>
    public abstract string Guide { get; }

    /// <summary>
    ///     Checks a draft file: whether it parses, and whether its library would save it.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <param name="repoPath">The repo whose library it's checked against, if any.</param>
    /// <param name="draftPath">The draft file.</param>
    /// <returns>What was found.</returns>
    public abstract AuthoringDraftCheck Check(AflPaths paths, string? repoPath, string draftPath);

    /// <summary>
    ///     Finds a document in the library, in its highest-priority scope.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <param name="repoPath">The repo whose library is searched too, if any.</param>
    /// <param name="id">The document's id.</param>
    /// <returns>The document.</returns>
    /// <exception cref="AflException">There's no such document.</exception>
    public abstract AuthoringExisting ReadExisting(AflPaths paths, string? repoPath, string id);

    /// <summary>
    ///     Returns whether a document's file is in a writable scope already.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <param name="repoPath">The repo, for <see cref="WorkflowScope.Repo" />.</param>
    /// <param name="id">The document's id.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>Whether the file exists.</returns>
    public abstract bool Exists(AflPaths paths, string? repoPath, string id, WorkflowScope scope);

    /// <summary>
    ///     Saves a draft to a library, after checking it.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <param name="repoPath">The repo, for <see cref="WorkflowScope.Repo" />.</param>
    /// <param name="draftPath">The draft file.</param>
    /// <param name="scope">Where to save it.</param>
    /// <returns>The document's id and the path written.</returns>
    /// <exception cref="AflException">The draft is invalid or the scope isn't writable.</exception>
    public abstract (string Id, string Path) Save(AflPaths paths, string? repoPath, string draftPath, WorkflowScope scope);

    /// <summary>
    ///     Lists what's already in the library, one line each, so the agent reuses it rather than writing it again.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <param name="repoPath">The repo whose library is listed too, if any.</param>
    /// <returns>The catalog, as Markdown.</returns>
    public abstract string Catalog(AflPaths paths, string? repoPath);

    /// <summary>
    ///     Writes the files the agent reads into a session's reference folder: the reference files, an example and the
    ///     catalog.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <param name="repoPath">The repo whose library is listed too, if any.</param>
    /// <param name="folder">The reference folder.</param>
    public void WriteReference(AflPaths paths, string? repoPath, string folder)
    {
        Directory.CreateDirectory(folder);
        var assembly = typeof(AbstractAuthoringKind).Assembly;
        foreach (var file in this.ReferenceFiles)
        {
            using var stream = assembly.GetManifestResourceStream(PathConstants.AuthoringResourcePrefix + file)
                               ?? throw new AflException(AflErrorKind.InvalidState, $"afl is missing its reference file {file}.");
            using var reader = new StreamReader(stream);
            AtomicFile.WriteAllText(Path.Combine(folder, file), reader.ReadToEnd());
        }

        try
        {
            AtomicFile.WriteAllText(Path.Combine(folder, ExampleFileName), this.ReadExisting(paths, null, this.ExampleId).Json);
        }
        catch (AflException)
        {
            // An example helps, but drafting works without one.
        }

        AtomicFile.WriteAllText(Path.Combine(folder, CatalogFileName), this.Catalog(paths, repoPath));
    }
}

/// <summary>
///     Base for a kind of document kept in a <see cref="AbstractLayeredLibrary{TDocument, TListing}" />: everything but
///     making the library and describing a document.
/// </summary>
/// <typeparam name="TDocument">The document type.</typeparam>
/// <typeparam name="TListing">The library's listing type.</typeparam>
/// <typeparam name="TLibrary">The library type.</typeparam>
/// <param name="name">The kind's name, as registered.</param>
public abstract class AbstractAuthoringKind<TDocument, TListing, TLibrary>(string name) : AbstractAuthoringKind(name)
    where TDocument : AbstractLibraryDocument
    where TListing : LibraryListing<TDocument>
    where TLibrary : AbstractLayeredLibrary<TDocument, TListing>
{
    /// <summary>
    ///     Makes the kind's library.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <param name="repoPath">The repo whose own library is in scope, if any.</param>
    /// <returns>The library.</returns>
    public abstract TLibrary CreateLibrary(AflPaths paths, string? repoPath);

    /// <summary>
    ///     Describes what's in a document in a few words, e.g. <c>4 steps</c>.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The description.</returns>
    protected abstract string Detail(TDocument document);

    /// <inheritdoc />
    public override AuthoringDraftCheck Check(AflPaths paths, string? repoPath, string draftPath)
    {
        if (!File.Exists(draftPath))
        {
            return new AuthoringDraftCheck(null, null, null, [], null, [$"There's no draft yet: {Path.GetFileName(draftPath)} hasn't been written."]);
        }

        var library = this.CreateLibrary(paths, repoPath);
        TDocument document;
        try
        {
            document = library.ReadExternal(draftPath);
        }
        catch (AflException ex)
        {
            return new AuthoringDraftCheck(null, null, null, [], null, [ex.Message]);
        }

        var problems = library.ValidateForSave(document);
        return new AuthoringDraftCheck(document.Id, document.Name, document.Description, document.Tags ?? [], this.Detail(document), problems);
    }

    /// <inheritdoc />
    public override AuthoringExisting ReadExisting(AflPaths paths, string? repoPath, string id)
    {
        var library = this.CreateLibrary(paths, repoPath);
        var listing = library.List().FirstOrDefault(l => l.Document.Id == id)
                      ?? throw new AflException(AflErrorKind.NotFound, $"There's no {this.Noun} '{id}'.");
        return new AuthoringExisting(id, listing.Scope, library.Serialize(listing.Document));
    }

    /// <inheritdoc />
    public override bool Exists(AflPaths paths, string? repoPath, string id, WorkflowScope scope)
    {
        return scope != WorkflowScope.BuiltIn && File.Exists(this.CreateLibrary(paths, repoPath).PathFor(id, scope));
    }

    /// <inheritdoc />
    public override (string Id, string Path) Save(AflPaths paths, string? repoPath, string draftPath, WorkflowScope scope)
    {
        var library = this.CreateLibrary(paths, repoPath);
        var document = library.ReadExternal(draftPath);
        return (document.Id, library.Save(document, scope));
    }

    /// <inheritdoc />
    public override string Catalog(AflPaths paths, string? repoPath)
    {
        var text = new StringBuilder($"# {char.ToUpperInvariant(this.Noun[0])}{this.Noun[1..]}s already in the library\n\n");
        foreach (var listing in this.CreateLibrary(paths, repoPath).List())
        {
            var document = listing.Document;
            var tags = document.Tags is { Count: > 0 } ? $" [{string.Join(", ", document.Tags)}]" : string.Empty;
            text.Append($"- `{document.Id}` ({listing.Scope.ToString().ToLowerInvariant()}): {document.Name}{tags}");
            text.Append(string.IsNullOrWhiteSpace(document.Description) ? "\n" : $" — {document.Description}\n");
        }

        return text.ToString();
    }
}
