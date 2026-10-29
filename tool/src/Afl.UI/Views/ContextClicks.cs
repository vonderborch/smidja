using Avalonia.Input;
using Avalonia.Interactivity;

namespace Afl.UI.Views;

/// <summary>
///     Makes Control+click a right-click on macOS, the platform's usual way to open a context menu from a trackpad or
///     mouse without a second button. <see cref="Install" /> (called by the app) takes the press before anything under it
///     sees it, so a Control+click doesn't also click a button or start a drag, and asks for the context menu on the
///     release, as a right-click does. Every context menu, declared or built in code from <c>ContextRequested</c>, opens
///     without knowing about it; code that checks the right button itself checks <see cref="IsContextPress" /> and
///     <see cref="IsContextRelease" /> instead.
/// </summary>
/// <remarks>
///     It's only on for macOS: elsewhere Control+click adds to a list's selection.
/// </remarks>
public static class ContextClicks
{
    /// <summary>
    ///     The pointer whose last press was a Control+click, or null when its last press was an ordinary one.
    /// </summary>
    private static IPointer? _contextPointer;

    /// <summary>
    ///     Whether <see cref="Install" /> has run.
    /// </summary>
    private static bool _installed;

    /// <summary>
    ///     Gets or sets a value indicating whether Control+click is a right-click. On for macOS.
    /// </summary>
    public static bool IsEnabled { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>
    ///     Adds the handlers that turn Control+clicks into right-clicks, once.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        InputElement.PointerPressedEvent.AddClassHandler<InputElement>(OnPressed, RoutingStrategies.Tunnel);
        InputElement.PointerReleasedEvent.AddClassHandler<InputElement>(OnReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>
    ///     Checks whether a press asks for a context menu: the right button, or a Control+click.
    /// </summary>
    /// <param name="e">The press.</param>
    /// <returns>True if it does.</returns>
    public static bool IsContextPress(PointerPressedEventArgs e)
    {
        return e.GetCurrentPoint(null).Properties.IsRightButtonPressed || IsControlClick(e);
    }

    /// <summary>
    ///     Checks whether a release ends a press that asked for a context menu.
    /// </summary>
    /// <param name="e">The release.</param>
    /// <returns>True if it does.</returns>
    public static bool IsContextRelease(PointerReleasedEventArgs e)
    {
        return e.InitialPressMouseButton == MouseButton.Right
               || (e.InitialPressMouseButton == MouseButton.Left && ReferenceEquals(e.Pointer, _contextPointer));
    }

    /// <summary>
    ///     Checks whether a press is a Control+click that stands for a right-click.
    /// </summary>
    /// <param name="e">The press.</param>
    /// <returns>True if it is.</returns>
    private static bool IsControlClick(PointerPressedEventArgs e)
    {
        return IsEnabled && e.Pointer.Type == PointerType.Mouse && e.KeyModifiers.HasFlag(KeyModifiers.Control)
               && e.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
    }

    /// <summary>
    ///     Takes a Control+click's press, at the top of its route, so nothing under it acts on it as a left click.
    /// </summary>
    /// <param name="sender">The element the press is passing.</param>
    /// <param name="e">The press.</param>
    private static void OnPressed(InputElement sender, PointerPressedEventArgs e)
    {
        if (!IsControlClick(e))
        {
            _contextPointer = null;
            return;
        }

        _contextPointer = e.Pointer;
        e.Handled = true;
    }

    /// <summary>
    ///     Asks for a context menu where a Control+click is let go, as a right-click's release does.
    /// </summary>
    /// <param name="sender">The element the release is passing.</param>
    /// <param name="e">The release.</param>
    private static void OnReleased(InputElement sender, PointerReleasedEventArgs e)
    {
        if (!IsContextRelease(e) || e.InitialPressMouseButton != MouseButton.Left || e.Source is not InputElement source)
        {
            return;
        }

        e.Handled = true;
        source.RaiseEvent(new ContextRequestedEventArgs(e));
    }
}
