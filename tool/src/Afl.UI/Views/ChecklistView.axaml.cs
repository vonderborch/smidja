using Afl.UI.DataContexts;
using Avalonia.Controls;
using Niddy.Avalonia.Dialogs;

namespace Afl.UI.Views;

/// <summary>
///     A list of items to tick, e.g. the optional steps to include in a run, shown in a dialog by <see cref="Open" />.
///     The items are ticked or not in place.
/// </summary>
public partial class ChecklistView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ChecklistView" /> class, for the designer.
    /// </summary>
    public ChecklistView()
        : this([])
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ChecklistView" /> class.
    /// </summary>
    /// <param name="items">The items, ticked as they start; ticking one changes it.</param>
    public ChecklistView(IReadOnlyList<SelectableItem<string>> items)
    {
        this.InitializeComponent();
        this.Items = items;
        this.Checklist.ItemsSource = items;
    }

    /// <summary>
    ///     Gets the items.
    /// </summary>
    public IReadOnlyList<SelectableItem<string>> Items { get; }

    /// <summary>
    ///     Asks someone to tick any of a list of items.
    /// </summary>
    /// <param name="parent">A control in the window the dialog opens over.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">What the list is for.</param>
    /// <param name="items">The items, ticked as they start; ticking one changes it.</param>
    /// <param name="accept">The text of the button that goes on.</param>
    /// <returns>True to go on with the items as ticked, false if cancelled.</returns>
    public static Task<bool> Open(Control parent, string title, string description, IReadOnlyList<SelectableItem<string>> items,
        string accept)
    {
        return Dialog.View.Open(parent, new ChecklistView(items), title, description, accept, maxWidth: 560);
    }
}
