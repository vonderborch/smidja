using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Niddy.Avalonia.Dialogs;

namespace Afl.UI.Views;

/// <summary>
///     An <see cref="AuthoringSessionDataContext" /> beside a Library page's list: the agent's progress, its questions
///     and permission requests, and the review card.
/// </summary>
public partial class AuthoringSessionView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AuthoringSessionView" /> class.
    /// </summary>
    public AuthoringSessionView()
    {
        this.InitializeComponent();

        // Keep the agent's output scrolled to the newest line.
        this.OutputScroller.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Extent")
            {
                Dispatcher.UIThread.Post(this.OutputScroller.ScrollToEnd);
            }
        };
    }

    /// <summary>
    ///     Raised when the user asks to open the draft in the editor; the page confirms replacing what's there.
    /// </summary>
    public event EventHandler? OpenInEditorRequested;

    /// <summary>
    ///     Gets the draft shown.
    /// </summary>
    private AuthoringSessionDataContext? Session => this.DataContext as AuthoringSessionDataContext;

    /// <summary>
    ///     Closes the draft; its work carries on.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        this.Session?.Close();
    }

    /// <summary>
    ///     Stops the agent.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (this.Session is { } session)
        {
            await session.StopAsync();
        }
    }

    /// <summary>
    ///     Discards the draft, once confirmed.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnDiscard(object? sender, RoutedEventArgs e)
    {
        if (this.Session is not { } session)
        {
            return;
        }

        var saved = session.IsSaved ? $"The saved {session.Noun} stays." : "Nothing has been saved.";
        if (await Dialog.Warning.Open(this, "Discard AI draft", $"Delete this draft and its run? {saved}", yesButtonText: "Discard",
                noButtonText: "Cancel"))
        {
            await session.DiscardAsync();
        }
    }

    /// <summary>
    ///     Tries the agent's work again.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        this.Session?.Retry();
    }

    /// <summary>
    ///     Drives an idle draft on.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnResume(object? sender, RoutedEventArgs e)
    {
        this.Session?.Resume();
    }

    /// <summary>
    ///     Gives the agent what was allowed.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnPermit(object? sender, RoutedEventArgs e)
    {
        this.Session?.Permit();
    }

    /// <summary>
    ///     Takes the draft as it is, allowing nothing.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAcceptOutput(object? sender, RoutedEventArgs e)
    {
        this.Session?.AcceptOutput();
    }

    /// <summary>
    ///     Answers the agent's questions.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAnswer(object? sender, RoutedEventArgs e)
    {
        this.Session?.Answer();
    }

    /// <summary>
    ///     Approves the draft into the library.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnApprove(object? sender, RoutedEventArgs e)
    {
        this.Session?.Approve();
    }

    /// <summary>
    ///     Opens the draft in the editor.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnOpenInEditor(object? sender, RoutedEventArgs e)
    {
        this.OpenInEditorRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Sends the draft back to the agent with the feedback.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRequestChanges(object? sender, RoutedEventArgs e)
    {
        this.Session?.RequestChanges();
    }
}
