using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Niddy.Core.IO;

namespace Afl.Core.Storage;

/// <summary>
///     Shared JSON persistence for afl's stores: schema-version upgrade on read, atomic writes (so a watching front-end
///     never sees a half-written file), and change notifications.
/// </summary>
/// <typeparam name="TDocument">The persisted document type.</typeparam>
public abstract class AbstractJsonPersistence<TDocument> : IDisposable
    where TDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     The watcher backing <see cref="Changed" />, once enabled.
    /// </summary>
    private DebouncedFileWatcher? _watcher;

    /// <summary>
    ///     Raised when a document in the store changes on disk, including changes made by other afl processes. A burst
    ///     of writes (e.g. a run saved after each step) is reported once it settles. Raised on a thread-pool thread.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    ///     Gets the schema version this build of afl writes.
    /// </summary>
    public Version CurrentVersion { get; } = AbstractVersionedDocument.CurrentVersionOf(typeof(TDocument));

    /// <summary>
    ///     Gets the directory to watch for <see cref="Changed" />.
    /// </summary>
    protected abstract string WatchDirectory { get; }

    /// <summary>
    ///     Gets the file filter to watch for <see cref="Changed" />.
    /// </summary>
    protected virtual string WatchFilter => "*" + PathConstants.JsonExtension;

    /// <summary>
    ///     Gets whether <see cref="Changed" /> also covers subdirectories.
    /// </summary>
    protected virtual bool WatchSubdirectories => false;

    /// <summary>
    ///     Stops watching for changes.
    /// </summary>
    public void Dispose()
    {
        this._watcher?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Starts raising <see cref="Changed" /> when the store's files change.
    /// </summary>
    public void EnableChangeNotifications()
    {
        if (this._watcher is not null)
        {
            return;
        }

        Directory.CreateDirectory(this.WatchDirectory);
        this._watcher = new DebouncedFileWatcher(this.WatchDirectory, this.WatchFilter, this.WatchSubdirectories,
            captureContext: false);
        this._watcher.Changed += (_, _) => this.Changed?.Invoke(this, EventArgs.Empty);

        // A watcher error (e.g. a buffer overflow) may have dropped events, so treat it as "something changed".
        this._watcher.Error += (_, _) => this.Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Parses a document. afl doesn't upgrade older documents; one from a newer afl is refused.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="origin">Where the text came from, for error messages.</param>
    /// <returns>The document.</returns>
    /// <exception cref="AflException">The JSON is invalid or from a newer afl.</exception>
    public TDocument Deserialize(string json, string origin)
    {
        try
        {
            var node = JsonNode.Parse(json) as JsonObject
                       ?? throw new AflException(AflErrorKind.Storage, $"{origin} is not a JSON object.");
            var version = ReadVersion(node);
            if (version > this.CurrentVersion)
            {
                throw new AflException(AflErrorKind.Storage,
                    $"{origin} uses schema {version}, newer than this afl supports ({this.CurrentVersion}). Update afl.");
            }

            var document = node.Deserialize<TDocument>(JsonConstants.Options)
                           ?? throw new AflException(AflErrorKind.Storage, $"{origin} is empty.");
            document.SchemaVersion = this.CurrentVersion.ToString(2);
            return document;
        }
        catch (JsonException ex)
        {
            throw new AflException(AflErrorKind.Storage, $"{origin} is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Serializes a document at the current schema version.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON text.</returns>
    public string Serialize(TDocument document)
    {
        document.SchemaVersion = this.CurrentVersion.ToString(2);
        return JsonSerializer.Serialize(document, JsonConstants.Options);
    }

    /// <summary>
    ///     Reads a document file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The document, or null when the file doesn't exist.</returns>
    protected TDocument? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return this.Deserialize(File.ReadAllText(path), path);
    }

    /// <summary>
    ///     Writes a document atomically, so readers only ever see the old file or the new one.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="document">The document.</param>
    protected void WriteFile(string path, TDocument document)
    {
        AtomicFile.WriteAllText(path, this.Serialize(document));
    }

    /// <summary>
    ///     Reads the schema version of a raw document; documents without one are 1.0.
    /// </summary>
    /// <param name="node">The raw document.</param>
    /// <returns>The version.</returns>
    private static Version ReadVersion(JsonObject node)
    {
        var raw = node[JsonConstants.SchemaVersionPropertyName]?.GetValue<string>();
        return Version.TryParse(raw, out var version) ? version : new Version(1, 0);
    }
}
