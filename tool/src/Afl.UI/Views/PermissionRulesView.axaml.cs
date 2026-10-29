using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Afl.UI.Views;

/// <summary>
///     An <see cref="AbstractPermissionRuleListDataContext" />: the rules, with Copy, Remove and "Change to allow/deny" on
///     right-click, a box to add one, and Reset where the list has defaults.
/// </summary>
public partial class PermissionRulesView : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="PermissionRulesView" /> class.
    /// </summary>
    public PermissionRulesView()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Gets the list.
    /// </summary>
    private AbstractPermissionRuleListDataContext? List => this.DataContext as AbstractPermissionRuleListDataContext;

    /// <summary>
    ///     Adds the typed rule.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The click.</param>
    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        this.List?.Add();
    }

    /// <summary>
    ///     Removes the picked rule.
    /// </summary>
    /// <param name="sender">The button or menu item.</param>
    /// <param name="e">The click.</param>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        this.List?.Remove();
    }

    /// <summary>
    ///     Turns the picked rule from allow into deny, or back.
    /// </summary>
    /// <param name="sender">The menu item.</param>
    /// <param name="e">The click.</param>
    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        this.List?.Toggle();
    }

    /// <summary>
    ///     Puts the list back to afl's defaults.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The click.</param>
    private void OnReset(object? sender, RoutedEventArgs e)
    {
        this.List?.Reset();
    }

    /// <summary>
    ///     Copies the picked rule.
    /// </summary>
    /// <param name="sender">The menu item.</param>
    /// <param name="e">The click.</param>
    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (this.List?.Selected is { } rule && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(rule.Rule);
        }
    }
}
