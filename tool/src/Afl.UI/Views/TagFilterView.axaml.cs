using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Afl.UI.Views;

/// <summary>
///     A <see cref="TagFilterDataContext" />: the picked tags as chips (click to remove), a search box over every other
///     tag (pick one from its list, or type it and press Enter), and the most common tags as one-click suggestions.
/// </summary>
public partial class TagFilterView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="TagFilterView" /> class.
    /// </summary>
    public TagFilterView()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Gets the filter.
    /// </summary>
    private TagFilterDataContext? Filter => this.DataContext as TagFilterDataContext;

    /// <summary>
    ///     Removes the clicked picked tag.
    /// </summary>
    /// <param name="sender">The tag's button.</param>
    /// <param name="e">The click.</param>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string tag })
        {
            this.Filter?.Remove(tag);
        }
    }

    /// <summary>
    ///     Picks the clicked suggestion.
    /// </summary>
    /// <param name="sender">The suggestion's button.</param>
    /// <param name="e">The click.</param>
    private void OnAddSuggestion(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TagChoice choice })
        {
            this.Filter?.Add(choice.Tag);
        }
    }

    /// <summary>
    ///     Opens the search box's list as soon as it's focused, so every tag can be browsed without typing.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">The focus change.</param>
    private void OnSearchFocused(object? sender, FocusChangedEventArgs e)
    {
        this.SearchBox.IsDropDownOpen = true;
    }

    /// <summary>
    ///     Picks the tag chosen from the search box's list.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">The close.</param>
    private void OnSearchClosed(object? sender, EventArgs e)
    {
        if (this.SearchBox.SelectedItem is TagChoice choice)
        {
            this.Pick(choice.Tag);
        }
    }

    /// <summary>
    ///     Picks the typed tag on Enter, and clears the box on Escape.
    /// </summary>
    /// <param name="sender">The search box.</param>
    /// <param name="e">The key.</param>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && this.Filter?.AddTyped(this.SearchBox.Text) == true)
        {
            this.ClearSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            this.ClearSearch();
        }
    }

    /// <summary>
    ///     Picks a tag and empties the search box.
    /// </summary>
    /// <param name="tag">The tag.</param>
    private void Pick(string tag)
    {
        this.Filter?.Add(tag);
        this.ClearSearch();
    }

    /// <summary>
    ///     Empties the search box once it has finished handling the current input.
    /// </summary>
    private void ClearSearch()
    {
        Dispatcher.UIThread.Post(() =>
        {
            this.SearchBox.SelectedItem = null;
            this.SearchBox.Text = string.Empty;
        });
    }
}
