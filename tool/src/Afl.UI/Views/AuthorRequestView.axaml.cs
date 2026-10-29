using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Threading;
using Niddy.Avalonia.Dialogs;

namespace Afl.UI.Views;

/// <summary>
///     The "New with AI…" and "Improve with AI…" dialog: what to draft and the agent that drafts it, shown by
///     <see cref="Open" />.
/// </summary>
public partial class AuthorRequestView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AuthorRequestView" /> class.
    /// </summary>
    public AuthorRequestView()
    {
        this.InitializeComponent();
        this.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => this.DescriptionBox.Focus());
    }

    /// <summary>
    ///     Asks what to draft; Start waits until the request can start.
    /// </summary>
    /// <param name="parent">A control in the window the dialog opens over.</param>
    /// <param name="request">The request, filled in place.</param>
    /// <returns>True to start the draft, false if cancelled.</returns>
    public static Task<bool> Open(Control parent, AuthorRequestDataContext request)
    {
        return Dialog.View.Open(parent, new AuthorRequestView { DataContext = request }, request.Title, null, "Start",
            canAccept: () => request.CanStart, maxWidth: 560);
    }
}
