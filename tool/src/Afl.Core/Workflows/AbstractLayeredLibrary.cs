using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Storage;

namespace Afl.Core.Workflows;

/// <summary>
///     A document together with where it was found in a layered library.
/// </summary>
/// <param name="Document">The document.</param>
/// <param name="Scope">Where it came from.</param>
/// <param name="Path">The file it was loaded from, or null for built-ins.</param>
/// <typeparam name="TDocument">The document type.</typeparam>
public record LibraryListing<TDocument>(TDocument Document, WorkflowScope Scope, string? Path);

/// <summary>
///     A library of documents found in three layers, later ones overriding earlier ones with the same id: the built-ins
///     embedded in afl, the user's global library under <c>~/.afl/&lt;dir&gt;</c>, then the repo's own
///     <c>.afl/&lt;dir&gt;</c>. Subclasses say where each layer lives, how to make a listing and how to validate.
/// </summary>
/// <typeparam name="TDocument">The document type.</typeparam>
/// <typeparam name="TListing">The listing type returned for each document.</typeparam>
/// <param name="globalDirectory">The global library directory.</param>
/// <param name="repoDirectory">The repo's own directory, if a repo is in scope.</param>
public abstract class AbstractLayeredLibrary<TDocument, TListing>(string globalDirectory, string? repoDirectory)
    : AbstractJsonCollectionStore<TDocument, string>(globalDirectory)
    where TDocument : AbstractLibraryDocument
    where TListing : LibraryListing<TDocument>
{
    /// <summary>
    ///     Backs <see cref="Problems" />.
    /// </summary>
    private readonly List<string> _problems = [];

    /// <summary>
    ///     Gets the repo's own directory, if a repo is in scope.
    /// </summary>
    public string? RepoDirectory { get; } = repoDirectory;

    /// <summary>
    ///     Gets why files were skipped the last time the library was listed: each file in the global library or the repo
    ///     that couldn't be read (e.g. it uses a format afl no longer reads).
    /// </summary>
    public IReadOnlyList<string> Problems => this._problems;

    /// <summary>
    ///     Gets the prefix of the embedded resources holding the built-ins.
    /// </summary>
    protected abstract string ResourcePrefix { get; }

    /// <summary>
    ///     Gets what one document is called in messages, e.g. <c>workflow</c>.
    /// </summary>
    protected abstract string Noun { get; }

    /// <summary>
    ///     Makes the listing for a document.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="scope">Where it came from.</param>
    /// <param name="path">The file, or null for built-ins.</param>
    /// <returns>The listing.</returns>
    protected abstract TListing CreateListing(TDocument document, WorkflowScope scope, string? path);

    /// <summary>
    ///     Checks a document before it is saved.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The problems; empty when it is valid.</returns>
    public abstract IReadOnlyList<string> ValidateForSave(TDocument document);

    /// <inheritdoc />
    public sealed override string GetKey(TDocument item)
    {
        return item.Id;
    }

    /// <summary>
    ///     Gets a document by id, from the highest-priority scope that has it.
    /// </summary>
    /// <param name="key">The id.</param>
    /// <returns>The document, or null.</returns>
    public override TDocument? Get(string key)
    {
        return this.List().FirstOrDefault(l => this.GetKey(l.Document) == key)?.Document;
    }

    /// <summary>
    ///     Gets a document by id, throwing if it doesn't exist.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The document.</returns>
    /// <exception cref="AflException">There is no such document.</exception>
    public TDocument GetRequired(string id)
    {
        var document = this.Get(id);
        if (document is not null)
        {
            return document;
        }

        var available = string.Join(", ", this.List().Select(l => this.GetKey(l.Document)));
        var skipped = this.Problems.Count == 0 ? string.Empty : $"\nSkipped:\n  - {string.Join("\n  - ", this.Problems)}";
        throw new AflException(AflErrorKind.NotFound, $"No {this.Noun} '{id}'. Available: {available}.{skipped}");
    }

    /// <summary>
    ///     Lists the effective documents: one per id, from the highest-priority scope.
    /// </summary>
    /// <returns>The listings, ordered by id.</returns>
    public IReadOnlyList<TListing> List()
    {
        var byId = new Dictionary<string, TListing>();
        foreach (var listing in this.ListAllScopes())
        {
            byId[this.GetKey(listing.Document)] = listing;
        }

        return byId.Values.OrderBy(l => this.GetKey(l.Document), StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Lists the effective documents a query matches: ones matched by name first, then ones matched in their
    ///     contents, each by id.
    /// </summary>
    /// <param name="query">What to look for.</param>
    /// <returns>The matching listings, with how each matched.</returns>
    public IReadOnlyList<(TListing Listing, LibraryMatch Match)> Search(LibraryQuery query)
    {
        return query.Apply(this.List(), l => l.Document, l => l.Scope);
    }

    /// <summary>
    ///     Returns every tag the effective documents use, with how many use it.
    /// </summary>
    /// <returns>The tags and counts, by tag.</returns>
    public IReadOnlyList<(string Tag, int Count)> AllTags()
    {
        return this.List()
            .SelectMany(l => l.Document.Tags ?? [])
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Lists every document in every scope, lowest priority first, including ones that are overridden. Files that
    ///     can't be read are skipped and noted in <see cref="Problems" />.
    /// </summary>
    /// <returns>The listings.</returns>
    public IEnumerable<TListing> ListAllScopes()
    {
        this._problems.Clear();
        foreach (var (name, json) in this.ReadBuiltInResources())
        {
            yield return this.CreateListing(this.Deserialize(json, $"built-in {name}"), WorkflowScope.BuiltIn, null);
        }

        var files = this.EnumerateFiles().Select(p => (Path: p, Scope: WorkflowScope.Global));
        if (this.RepoDirectory is not null && System.IO.Directory.Exists(this.RepoDirectory))
        {
            files = files.Concat(System.IO.Directory.EnumerateFiles(this.RepoDirectory, "*" + PathConstants.JsonExtension).Order()
                .Select(p => (Path: p, Scope: WorkflowScope.Repo)));
        }

        foreach (var (path, scope) in files)
        {
            var document = this.TryReadFile(path);
            if (document is not null)
            {
                yield return this.CreateListing(document, scope, path);
            }
        }
    }

    /// <summary>
    ///     Validates and saves a document to the global library or the repo.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="scope">Where to save it; <see cref="WorkflowScope.BuiltIn" /> isn't writable.</param>
    /// <returns>The path written.</returns>
    /// <exception cref="AflException">The document is invalid or the scope isn't writable.</exception>
    public string Save(TDocument document, WorkflowScope scope)
    {
        var errors = this.ValidateForSave(document);
        if (errors.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidWorkflow,
                $"The {this.Noun} '{this.GetKey(document)}' is invalid:\n  - {string.Join("\n  - ", errors)}");
        }

        var path = this.PathFor(this.GetKey(document), scope);
        this.WriteFile(path, document);
        return path;
    }

    /// <inheritdoc />
    public override void Save(TDocument item)
    {
        this.Save(item, WorkflowScope.Global);
    }

    /// <summary>
    ///     Deletes a document's file from the global library or the repo. A document with the same id in another scope
    ///     (including a built-in) takes its place.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="scope">The scope to delete it from; <see cref="WorkflowScope.BuiltIn" /> isn't writable.</param>
    /// <returns>True if the file existed.</returns>
    /// <exception cref="AflException">The scope isn't writable.</exception>
    public bool Delete(string id, WorkflowScope scope)
    {
        var path = this.PathFor(id, scope);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    ///     Returns the file a document is stored in for a writable scope.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The path.</returns>
    /// <exception cref="AflException">The scope isn't writable.</exception>
    public string PathFor(string id, WorkflowScope scope)
    {
        var directory = scope switch
        {
            WorkflowScope.Global => this.Directory,
            WorkflowScope.Repo => this.RepoDirectory ?? throw new AflException(AflErrorKind.InvalidInput,
                $"No repo is in scope to save the {this.Noun} to."),
            _ => throw new AflException(AflErrorKind.InvalidInput, $"Built-in {this.Noun}s can't be written."),
        };
        return Path.Combine(directory, id + PathConstants.JsonExtension);
    }

    /// <summary>
    ///     Parses a file that isn't in the library, e.g. for <c>afl workflow-validate</c>.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The document.</returns>
    /// <exception cref="AflException">The file doesn't exist or can't be read.</exception>
    public TDocument ReadExternal(string path)
    {
        if (!File.Exists(path))
        {
            throw new AflException(AflErrorKind.NotFound, $"{path} does not exist.");
        }

        return this.ReadFile(path)!;
    }

    /// <summary>
    ///     Reads a file, noting it in <see cref="Problems" /> instead when it can't be read.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The document, or null.</returns>
    private TDocument? TryReadFile(string path)
    {
        try
        {
            return this.ReadFile(path);
        }
        catch (AflException ex)
        {
            this._problems.Add(ex.Message);
            return null;
        }
    }

    /// <summary>
    ///     Reads the built-ins embedded under <see cref="ResourcePrefix" />.
    /// </summary>
    /// <returns>The resource names (without the prefix) and contents.</returns>
    private IEnumerable<(string Name, string Json)> ReadBuiltInResources()
    {
        var assembly = typeof(AbstractLayeredLibrary<,>).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(this.ResourcePrefix, StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name[this.ResourcePrefix.Length..], reader.ReadToEnd());
        }
    }
}
