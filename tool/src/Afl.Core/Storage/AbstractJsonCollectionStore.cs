using Afl.Core.Constants;

namespace Afl.Core.Storage;

/// <summary>
///     A store holding one JSON file per item, such as the workflow library or run state.
/// </summary>
/// <typeparam name="TItem">The item type.</typeparam>
/// <typeparam name="TKey">The item's key type.</typeparam>
/// <param name="directory">The directory holding the item files.</param>
public abstract class AbstractJsonCollectionStore<TItem, TKey>(string directory) : AbstractJsonPersistence<TItem>
    where TItem : AbstractVersionedDocument
    where TKey : notnull
{
    /// <summary>
    ///     Gets the directory holding the item files.
    /// </summary>
    public string Directory { get; } = directory;

    /// <inheritdoc />
    protected override string WatchDirectory => this.Directory;

    /// <summary>
    ///     Gets an item by key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The item, or null when it doesn't exist.</returns>
    public virtual TItem? Get(TKey key)
    {
        var path = this.FindPath(key);
        return path is null ? null : this.ReadFile(path);
    }

    /// <summary>
    ///     Gets every item in the store.
    /// </summary>
    /// <returns>The items.</returns>
    public virtual IReadOnlyList<TItem> All()
    {
        return this.EnumerateFiles().Select(path => this.ReadFile(path)!).ToList();
    }

    /// <summary>
    ///     Creates or replaces an item.
    /// </summary>
    /// <param name="item">The item.</param>
    public virtual void Save(TItem item)
    {
        this.WriteFile(this.GetPath(item), item);
    }

    /// <summary>
    ///     Deletes an item.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True if the item existed.</returns>
    public virtual bool Delete(TKey key)
    {
        var path = this.FindPath(key);
        if (path is null)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    ///     Returns an item's key.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The key.</returns>
    public abstract TKey GetKey(TItem item);

    /// <summary>
    ///     Returns the file path an item is saved to.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The path.</returns>
    protected virtual string GetPath(TItem item)
    {
        return Path.Combine(this.Directory, this.GetKey(item) + PathConstants.JsonExtension);
    }

    /// <summary>
    ///     Finds the file for a key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The path, or null when there's no such item.</returns>
    protected virtual string? FindPath(TKey key)
    {
        var path = Path.Combine(this.Directory, key + PathConstants.JsonExtension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    ///     Lists the item files.
    /// </summary>
    /// <returns>The paths.</returns>
    protected virtual IEnumerable<string> EnumerateFiles()
    {
        if (!System.IO.Directory.Exists(this.Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(this.Directory, "*" + PathConstants.JsonExtension,
            this.WatchSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order();
    }
}
