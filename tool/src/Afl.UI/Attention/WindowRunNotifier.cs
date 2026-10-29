using Afl.Core.Helpers;
using Afl.Core.Notifications;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Afl.UI.Attention;

/// <summary>
///     The desktop app's run notifier: besides the desktop notification, it draws the user's eye to afl's window
///     (taskbar flash, Dock bounce, urgency hint) when the window isn't active, for the same runs and states.
/// </summary>
/// <param name="notifier">Shows the notifications; null to pick the best one on this machine when first needed.</param>
/// <param name="settings">The settings.</param>
/// <param name="attention">Draws the eye to the window; null for nothing.</param>
public class WindowRunNotifier(AbstractNotifier? notifier, SettingsStore settings, AbstractWindowAttention? attention)
    : RunNotifier(notifier, settings)
{
    /// <summary>
    ///     The windows asked about that haven't been come back to yet.
    /// </summary>
    private readonly HashSet<Window> _waiting = [];

    /// <summary>
    ///     Gets what draws the eye to the window, or null for nothing.
    /// </summary>
    public AbstractWindowAttention? Attention { get; } = attention;

    /// <summary>
    ///     Creates the notifier for this machine: its best notifier and its window attention.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <returns>The run notifier.</returns>
    public static new WindowRunNotifier ForThisMachine(AflPaths paths)
    {
        return new WindowRunNotifier(null, new SettingsStore(paths), WindowAttentionRegistry.Instance.ForThisMachine());
    }

    /// <summary>
    ///     Asks for the user's attention to a window, unless it is active; and once, when they come back to it, stops.
    ///     Call on the UI thread.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="urgent">Whether a run needs the user.</param>
    /// <returns>Whether attention was asked for.</returns>
    public bool RequestAttention(Window window, bool urgent)
    {
        if (this.Attention is not { } attention || window.IsActive)
        {
            return false;
        }

        attention.Request(window, urgent);
        if (this._waiting.Add(window))
        {
            window.Activated += this.OnCameBack;
        }

        return true;
    }

    /// <inheritdoc />
    protected override void Alert(Notification notification, RunState state)
    {
        if (this.Attention is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                this.RequestAttention(window, state == RunState.NeedsYou);
            }
        });
    }

    /// <summary>
    ///     Stops asking for attention to a window the user has come back to.
    /// </summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">Unused.</param>
    private void OnCameBack(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Activated -= this.OnCameBack;
        this._waiting.Remove(window);
        this.Attention?.Clear(window);
    }
}
