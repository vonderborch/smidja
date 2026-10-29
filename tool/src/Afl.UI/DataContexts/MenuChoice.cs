using Avalonia.Controls;

namespace Afl.UI.DataContexts;

/// <summary>
///     One item of a context menu's submenu that's built from data, e.g. a tag to put on a run or take off it, or a
///     folder to move it to.
/// </summary>
/// <param name="Header">The item's text.</param>
/// <param name="Value">The value it picks; null for the item that asks for a new one (e.g. "New folder…").</param>
/// <param name="IsChecked">Whether it's ticked: the value is the run's now.</param>
/// <param name="ToggleType">How it's ticked: a check box for a set of values, a radio button for one of several.</param>
public record MenuChoice(string Header, string? Value, bool IsChecked, MenuItemToggleType ToggleType)
{
    /// <summary>
    ///     Gets whether the item asks for a new value rather than picking one.
    /// </summary>
    public bool IsNew => this.Value is null;

    /// <summary>
    ///     Creates the item that asks for a new value.
    /// </summary>
    /// <param name="header">The item's text, e.g. "New folder…".</param>
    /// <returns>The item.</returns>
    public static MenuChoice New(string header)
    {
        return new MenuChoice(header, null, false, MenuItemToggleType.None);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Header;
    }
}
