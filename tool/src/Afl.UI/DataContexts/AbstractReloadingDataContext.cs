using Avalonia.Threading;
using Niddy.Avalonia.DataContexts;

namespace Afl.UI.DataContexts;

/// <summary>
///     Base for data contexts backed by afl's stores: reloads on demand and whenever a watched store changes on disk
///     (the stores debounce, so a burst of writes from the CLI causes one reload), and runs user actions so that any failure is
///     shown in <see cref="DataContextBase.ErrorMessage" /> instead of escaping to the UI.
/// </summary>
public abstract class AbstractReloadingDataContext : DataContextBase, IDisposable
{
    /// <summary>
    ///     Unsubscribes from the watched stores.
    /// </summary>
    private readonly List<Action> _unsubscribe = [];

    /// <summary>
    ///     Stops watching the stores.
    /// </summary>
    public void Dispose()
    {
        this._unsubscribe.ForEach(unsubscribe => unsubscribe());
        this._unsubscribe.Clear();
        this.OnDisposed();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Reloads the data, showing any failure in <see cref="DataContextBase.ErrorMessage" />.
    /// </summary>
    public void Reload()
    {
        this.Try(this.Load);
    }

    /// <summary>
    ///     Runs a user action, showing any failure in <see cref="DataContextBase.ErrorMessage" />.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>True if it succeeded.</returns>
    public bool Try(Action action)
    {
        try
        {
            this.ErrorMessage = null;
            action();
            return true;
        }
        catch (Exception ex)
        {
            this.ErrorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    ///     Runs an asynchronous user action with <see cref="DataContextBase.IsBusy" /> set, showing any failure in
    ///     <see cref="DataContextBase.ErrorMessage" />.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>True if it succeeded.</returns>
    public async Task<bool> TryAsync(Func<Task> action)
    {
        var succeeded = true;
        await this.RunAsync(action, ex =>
        {
            succeeded = false;
            this.ErrorMessage = ex.Message;
        });
        return succeeded;
    }

    /// <summary>
    ///     Loads the data from the stores. Called on the UI thread.
    /// </summary>
    protected abstract void Load();

    /// <summary>
    ///     Called when the data context is disposed, after the store subscriptions are removed.
    /// </summary>
    protected virtual void OnDisposed()
    {
    }

    /// <summary>
    ///     Reloads on the UI thread whenever a store raises its (already debounced) changed event.
    /// </summary>
    /// <param name="subscribe">Adds a handler to the store's event.</param>
    /// <param name="unsubscribe">Removes the handler again.</param>
    protected void ReloadOnChange(Action<EventHandler> subscribe, Action<EventHandler> unsubscribe)
    {
        EventHandler handler = (_, _) => Dispatcher.UIThread.Post(this.Reload);
        this.OnDispose(subscribe, unsubscribe, handler);
    }

    /// <summary>
    ///     Subscribes a handler to an event and unsubscribes it when the data context is disposed.
    /// </summary>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="subscribe">Adds the handler.</param>
    /// <param name="unsubscribe">Removes the handler.</param>
    /// <param name="handler">The handler.</param>
    protected void OnDispose<THandler>(Action<THandler> subscribe, Action<THandler> unsubscribe, THandler handler)
    {
        subscribe(handler);
        this._unsubscribe.Add(() => unsubscribe(handler));
    }
}
