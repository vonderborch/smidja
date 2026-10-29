using System.Text.Json;

namespace Afl.UI.DataContexts;

/// <summary>
///     The undo and redo history of a draft: snapshots of it, serialised to JSON (with their runtime type, so a
///     subclass keeps its own fields), at most <see cref="Capacity" /> of them, the oldest dropped first. Recording a
///     snapshot after undoing drops the ones that could have been redone.
/// </summary>
/// <typeparam name="T">The draft's type.</typeparam>
public class DraftHistory<T>
    where T : class
{
    /// <summary>
    ///     The JSON options the snapshots are written and read with.
    /// </summary>
    private readonly JsonSerializerOptions _options;

    /// <summary>
    ///     The snapshots, oldest first.
    /// </summary>
    private readonly List<(Type Type, string Json)> _snapshots = [];

    /// <summary>
    ///     The index of the snapshot the draft is at, or -1 when there are none.
    /// </summary>
    private int _index = -1;

    /// <summary>
    ///     The index of the snapshot that was last saved, or -1 when that one is gone.
    /// </summary>
    private int _saved = -1;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DraftHistory{T}" /> class.
    /// </summary>
    /// <param name="options">The JSON options to write and read snapshots with.</param>
    /// <param name="capacity">How many snapshots to keep.</param>
    public DraftHistory(JsonSerializerOptions options, int capacity = 100)
    {
        this._options = options;
        this.Capacity = Math.Max(2, capacity);
    }

    /// <summary>
    ///     Gets how many snapshots are kept.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    ///     Gets whether there's an earlier snapshot to go back to.
    /// </summary>
    public bool CanUndo => this._index > 0;

    /// <summary>
    ///     Gets whether there's an undone snapshot to go forward to.
    /// </summary>
    public bool CanRedo => this._index < this._snapshots.Count - 1;

    /// <summary>
    ///     Gets whether the draft is at the snapshot last marked saved.
    /// </summary>
    public bool IsAtSaved => this._index >= 0 && this._index == this._saved;

    /// <summary>
    ///     Starts the history again from a draft, which counts as saved.
    /// </summary>
    /// <param name="draft">The draft.</param>
    public void Reset(T draft)
    {
        this._snapshots.Clear();
        this._snapshots.Add(this.Snapshot(draft));
        this._index = 0;
        this._saved = 0;
    }

    /// <summary>
    ///     Clears the history.
    /// </summary>
    public void Clear()
    {
        this._snapshots.Clear();
        this._index = -1;
        this._saved = -1;
    }

    /// <summary>
    ///     Records the draft as it is now, unless it's the same as the snapshot it's at.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <returns>True if a snapshot was recorded.</returns>
    public bool Record(T draft)
    {
        var snapshot = this.Snapshot(draft);
        if (this._index >= 0 && this._snapshots[this._index] == snapshot)
        {
            return false;
        }

        this._snapshots.RemoveRange(this._index + 1, this._snapshots.Count - this._index - 1);
        if (this._saved > this._index)
        {
            this._saved = -1;
        }

        this._snapshots.Add(snapshot);
        this._index = this._snapshots.Count - 1;
        if (this._snapshots.Count > this.Capacity)
        {
            this._snapshots.RemoveAt(0);
            this._index--;
            this._saved--;
        }

        return true;
    }

    /// <summary>
    ///     Marks the snapshot the draft is at as the saved one.
    /// </summary>
    public void MarkSaved()
    {
        this._saved = this._index;
    }

    /// <summary>
    ///     Marks the draft as never saved, e.g. a new one, so no snapshot counts as saved.
    /// </summary>
    public void MarkUnsaved()
    {
        this._saved = -1;
    }

    /// <summary>
    ///     Goes back one snapshot.
    /// </summary>
    /// <returns>A copy of the draft as it was, or null if there's nothing to undo.</returns>
    public T? Undo()
    {
        return this.CanUndo ? this.Restore(--this._index) : null;
    }

    /// <summary>
    ///     Goes forward one snapshot.
    /// </summary>
    /// <returns>A copy of the draft as it was, or null if there's nothing to redo.</returns>
    public T? Redo()
    {
        return this.CanRedo ? this.Restore(++this._index) : null;
    }

    /// <summary>
    ///     Serialises a draft.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <returns>Its runtime type and JSON.</returns>
    private (Type Type, string Json) Snapshot(T draft)
    {
        var type = draft.GetType();
        return (type, JsonSerializer.Serialize(draft, type, this._options));
    }

    /// <summary>
    ///     Reads a snapshot back.
    /// </summary>
    /// <param name="index">The snapshot's index.</param>
    /// <returns>A fresh copy of the draft.</returns>
    private T Restore(int index)
    {
        var (type, json) = this._snapshots[index];
        return (T)JsonSerializer.Deserialize(json, type, this._options)!;
    }
}
