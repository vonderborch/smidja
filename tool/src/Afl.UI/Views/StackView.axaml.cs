using Afl.UI.DataContexts;
using Avalonia.Controls;
using Niddy.Avalonia.Dialogs;

namespace Afl.UI.Views;

/// <summary>
///     What a repo is built with (languages and frameworks), shown in a dialog by <see cref="Open" /> when the repo is
///     still empty, and in repo settings.
/// </summary>
public partial class StackView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="StackView" /> class.
    /// </summary>
    public StackView()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Asks what a repo is built with.
    /// </summary>
    /// <param name="parent">A control in the window the dialog opens over.</param>
    /// <param name="stack">The languages and frameworks, edited in place.</param>
    /// <param name="description">Why it's asked.</param>
    /// <returns>True to save what's picked, false to skip.</returns>
    public static Task<bool> Open(Control parent, StackDataContext stack, string description)
    {
        return Dialog.View.Open(parent, new StackView { DataContext = stack }, "About this project", description, "Save",
            maxWidth: 520);
    }
}
