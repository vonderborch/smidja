using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Afl.UI.Views;

/// <summary>
///     A <see cref="PickListDataContext" />: the picked ids, each with Remove, then a dropdown of the rest (which can be
///     typed into) and Add. Used for the skills on a workflow or step and the skills picked when starting a run.
/// </summary>
public partial class PickListView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="PickListView" /> class.
    /// </summary>
    public PickListView()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Gets the list.
    /// </summary>
    private PickListDataContext? List => this.DataContext as PickListDataContext;

    /// <summary>
    ///     Adds what's picked or typed in the dropdown.
    /// </summary>
    /// <param name="sender">The Add button.</param>
    /// <param name="e">The event.</param>
    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        this.List?.Add();
    }

    /// <summary>
    ///     Adds what's typed in the dropdown when Enter is pressed.
    /// </summary>
    /// <param name="sender">The dropdown.</param>
    /// <param name="e">The event.</param>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && this.List?.Add() == true)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Removes the id whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string id })
        {
            this.List?.Remove(id);
        }
    }
}
