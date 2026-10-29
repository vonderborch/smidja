using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Afl.UI.Views;

/// <summary>
///     Gives a list's rows context menus: set <c>views:RowMenus.IsEnabled="True"</c> on a list (a <c>ListBox</c>, or
///     anything with <c>ItemsSource</c> and <c>SelectedItem</c>, like a <c>TableGrid</c>), and right-clicking a row (or
///     pressing the menu key on it) selects the row before its menu opens, so the menu acts on the selection like the
///     page's buttons do. No menu opens over the list's empty space.
/// </summary>
/// <remarks>
///     The menu is the list's own <c>ContextMenu</c>, or one on a control in the row's template, which is how rows of
///     different kinds get different menus. A menu in a row's template is given the list's DataContext (the page's) when
///     it opens, so it's declared with the page's DataContext type and reuses the page's handlers and <c>Can…</c>
///     properties.
/// </remarks>
public static class RowMenus
{
    /// <summary>
    ///     Defines whether a list's rows get context menus.
    /// </summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(RowMenus));

    /// <summary>
    ///     Hooks lists up as <see cref="IsEnabledProperty" /> is set on them.
    /// </summary>
    static RowMenus()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);
    }

    /// <summary>
    ///     Gets whether a list's rows get context menus.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <returns>Whether they do.</returns>
    public static bool GetIsEnabled(Control list)
    {
        return list.GetValue(IsEnabledProperty);
    }

    /// <summary>
    ///     Sets whether a list's rows get context menus.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="value">Whether they do.</param>
    public static void SetIsEnabled(Control list, bool value)
    {
        list.SetValue(IsEnabledProperty, value);
    }

    /// <summary>
    ///     Finds the row of a list a control is in: the outermost control between them showing one of the list's items.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="source">The control, e.g. the one right-clicked.</param>
    /// <returns>The row's control, or null if the control isn't in a row (it's the list's empty space or a header).</returns>
    public static Control? FindRow(Control list, Visual? source)
    {
        Control? row = null;
        for (var visual = source; visual is not null && !ReferenceEquals(visual, list); visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: { } item } control && !ReferenceEquals(item, list.DataContext))
            {
                row = control;
            }
        }

        return row is not null && IsItemOf(list, row.DataContext) ? row : null;
    }

    /// <summary>
    ///     Adds or removes the handler that selects the row a menu is asked for on.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="e">The change.</param>
    private static void OnIsEnabledChanged(Control list, AvaloniaPropertyChangedEventArgs e)
    {
        list.RemoveHandler(Control.ContextRequestedEvent, OnContextRequested);
        if (e.NewValue is true)
        {
            // Tunnelling, so the row's selected before the menu (which opens as the request bubbles back up) is.
            list.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        }
    }

    /// <summary>
    ///     Selects the row a menu is asked for on and gives its menu the list's DataContext, or stops the menu opening
    ///     when it's asked for off the rows.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">The request.</param>
    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control list)
        {
            return;
        }

        if (FindRow(list, e.Source as Visual) is not { } row)
        {
            e.Handled = true;
            return;
        }

        if (AvaloniaPropertyRegistry.Instance.FindRegistered(list, "SelectedItem") is { } selected)
        {
            list.SetCurrentValue(selected, row.DataContext);
        }

        // The menu that opens is the first one met on the way up from what was clicked.
        for (var visual = e.Source as Visual; visual is not null && !ReferenceEquals(visual, list); visual = visual.GetVisualParent())
        {
            if (visual is Control { ContextMenu: { } menu })
            {
                menu.DataContext = list.DataContext;
                return;
            }
        }

        // Clicked in the row but beside its template (e.g. in the row's padding): ask again from the template's menu's
        // control, so that menu opens as if it was clicked.
        if (list.ContextMenu is null && row.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.ContextMenu is not null) is { } owner)
        {
            e.Handled = true;
            owner.RaiseEvent(new ContextRequestedEventArgs(e));
        }
    }

    /// <summary>
    ///     Checks that a value is one of a list's items, so a control with a DataContext of its own that isn't a row
    ///     (like a pager) isn't taken for one.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="item">The value.</param>
    /// <returns>True if it's one of the list's items.</returns>
    private static bool IsItemOf(Control list, object? item)
    {
        var items = list is ItemsControl control
            ? control.Items
            : AvaloniaPropertyRegistry.Instance.FindRegistered(list, "ItemsSource") is { } property
                ? list.GetValue(property) as IEnumerable
                : null;
        return items?.Cast<object?>().Any(i => ReferenceEquals(i, item)) == true;
    }
}
