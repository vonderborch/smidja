using Afl.Core.Constants;
using Afl.Core.Helpers;

namespace Afl.Core.Storage;

/// <summary>
///     A store backed by a single JSON document, such as <c>repos.json</c>.
/// </summary>
/// <typeparam name="TDocument">The document type.</typeparam>
/// <param name="path">The document's file path.</param>
public abstract class AbstractJsonStore<TDocument>(string path) : AbstractJsonPersistence<TDocument>
    where TDocument : AbstractVersionedDocument, new()
{
    /// <summary>
    ///     Gets the document's file path.
    /// </summary>
    public string FilePath { get; } = path;

    /// <inheritdoc />
    protected override string WatchDirectory => Path.GetDirectoryName(this.FilePath)!;

    /// <inheritdoc />
    protected override string WatchFilter => Path.GetFileName(this.FilePath);

    /// <summary>
    ///     Loads the document, or a new empty one when the file doesn't exist yet.
    /// </summary>
    /// <returns>The document.</returns>
    public TDocument Load()
    {
        return this.ReadFile(this.FilePath) ?? new TDocument();
    }

    /// <summary>
    ///     Loads, changes and saves the document while holding a lock, so concurrent afl processes can't lose each
    ///     other's changes.
    /// </summary>
    /// <typeparam name="TResult">The type of value the change returns.</typeparam>
    /// <param name="change">The change; it may mutate the document.</param>
    /// <returns>Whatever <paramref name="change" /> returned.</returns>
    public TResult Update<TResult>(Func<TDocument, TResult> change)
    {
        using var fileLock = FileLock.Acquire(this.FilePath + PathConstants.LockExtension, TimeSpan.FromSeconds(10),
            this.FilePath);
        var document = this.Load();
        var result = change(document);
        this.WriteFile(this.FilePath, document);
        return result;
    }
}
