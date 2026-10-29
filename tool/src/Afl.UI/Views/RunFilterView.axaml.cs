using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Afl.UI.Views;

/// <summary>
///     A <see cref="RunFilterDataContext" />: a search box over the runs, and a chip for each state (click to show only
///     runs in it).
/// </summary>
public partial class RunFilterView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RunFilterView" /> class.
    /// </summary>
    public RunFilterView()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Gets the filter.
    /// </summary>
    private RunFilterDataContext? Filter => this.DataContext as RunFilterDataContext;

    /// <summary>
    ///     Picks or unpicks the clicked state.
    /// </summary>
    /// <param name="sender">The state's button.</param>
    /// <param name="e">The click.</param>
    private void OnToggleState(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RunStateChoiceDataContext choice })
        {
            this.Filter?.Toggle(choice.State);
        }
    }

    /// <summary>
    ///     Empties the search box.
    /// </summary>
    /// <param name="sender">The clear button.</param>
    /// <param name="e">The click.</param>
    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        if (this.Filter is { } filter)
        {
            filter.SearchText = null;
        }
    }

    /// <summary>
    ///     Empties the search box on Escape.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">The key.</param>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && this.Filter is { SearchText.Length: > 0 } filter)
        {
            filter.SearchText = null;
            e.Handled = true;
        }
    }
}
