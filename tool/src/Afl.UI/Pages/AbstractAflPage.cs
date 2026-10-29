using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Niddy.Avalonia.Dialogs;
using Niddy.Avalonia.PageSystem;
using Niddy.Avalonia.Toasts;

namespace Afl.UI.Pages;

/// <summary>
///     Base for afl pages. Niddy hands every page the <see cref="ShellDataContext" /> as its app data context; the page
///     builds its own data context from it, which Niddy sets as the page's data context and disposes when the page
///     closes. Also holds the dialog and toast helpers the pages' code-behind uses.
/// </summary>
/// <typeparam name="TDataContext">The page's data context.</typeparam>
public abstract class AbstractAflPage<TDataContext> : Page<ShellDataContext, TDataContext>
    where TDataContext : AbstractPageDataContext
{
    /// <summary>
    ///     Creates the page's data context from the shell.
    /// </summary>
    /// <returns>The data context.</returns>
    protected abstract override TDataContext CreatePageDataContext();

    /// <summary>
    ///     Shows a toast. Niddy falls back to the app's toast host, so this works even from a page that has just
    ///     navigated away (e.g. Issues after starting a run).
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="type">The style.</param>
    protected void ShowToast(string message, ToastType type = ToastType.Success)
    {
        Toast.Show(this, message, type);
    }

    /// <summary>
    ///     Asks the user to confirm a destructive or irreversible action. The title is highlighted and
    ///     Enter doesn't confirm, so it can't be accepted by accident.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="description">What will happen.</param>
    /// <param name="proceed">The confirming button's text.</param>
    /// <returns>True if they confirmed.</returns>
    protected Task<bool> WarnAsync(string title, string description, string proceed)
    {
        return Dialog.Warning.Open(this, title, description, yesButtonText: proceed, noButtonText: "Cancel");
    }

    /// <summary>
    ///     Asks the user for a line of text.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="description">What to enter.</param>
    /// <param name="defaultValue">The initial text.</param>
    /// <param name="placeholder">The placeholder.</param>
    /// <returns>The text, or null if they cancelled.</returns>
    protected Task<string?> AskAsync(string title, string description, string defaultValue = "", string placeholder = "")
    {
        return Dialog.Input.Open(this, title, description, defaultValue, placeholder);
    }

    /// <summary>
    ///     Copies text to the clipboard, e.g. from a context menu's Copy item, and says so.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="what">What it is, for the toast (e.g. "run id").</param>
    protected void CopyText(string text, string what)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(text);
            this.ShowToast($"Copied the {what}.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Opens a web page in the browser, e.g. a ticket from a context menu.
    /// </summary>
    /// <param name="url">The page's URL; nothing happens if it's blank or not an absolute URL.</param>
    protected void OpenUrl(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            _ = launcher.LaunchUriAsync(uri);
        }
    }
}
