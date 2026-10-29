using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Afl.UI.Attention;

/// <summary>
///     Windows: flashes the window's taskbar button with <c>FlashWindowEx</c>. When a run needs the user it flashes until
///     they come back to the window; otherwise it flashes a few times and stays highlighted.
/// </summary>
[WindowAttentionRegistration("windows-flash")]
public class WindowsFlashAttention() : AbstractWindowAttention("windows-flash")
{
    /// <summary>
    ///     Flash the window's caption and taskbar button.
    /// </summary>
    private const uint FlashAll = 0x3;

    /// <summary>
    ///     Flash the taskbar button only.
    /// </summary>
    private const uint FlashTray = 0x2;

    /// <summary>
    ///     Keep flashing until the window comes to the foreground.
    /// </summary>
    private const uint FlashUntilForeground = 0xC;

    /// <summary>
    ///     How many times to flash for news.
    /// </summary>
    private const uint NewsFlashes = 3;

    /// <inheritdoc />
    public override bool IsForThisSystem => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public override void Request(Window window, bool urgent)
    {
        if (HandleOf(window, "HWND") is not { } handle)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = urgent ? FlashAll | FlashUntilForeground : FlashTray,
            Count = urgent ? 0 : NewsFlashes,
        };
        FlashWindowEx(ref info);
    }

    /// <summary>
    ///     Flashes a window (user32).
    /// </summary>
    /// <param name="info">What to flash, and how.</param>
    /// <returns>Whether the window was active before the call.</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>
    ///     <c>FLASHWINFO</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        /// <summary>
        ///     The struct's size in bytes.
        /// </summary>
        public uint Size;

        /// <summary>
        ///     The window to flash.
        /// </summary>
        public nint Window;

        /// <summary>
        ///     The <c>FLASHW_*</c> flags.
        /// </summary>
        public uint Flags;

        /// <summary>
        ///     How many times to flash.
        /// </summary>
        public uint Count;

        /// <summary>
        ///     The flash rate in milliseconds, 0 for the cursor blink rate.
        /// </summary>
        public uint Timeout;
    }
}

/// <summary>
///     macOS: bounces afl's Dock icon with <c>[NSApp requestUserAttention:]</c>. When a run needs the user it bounces
///     until they switch to afl; otherwise it bounces once. macOS does nothing while afl is the active app.
/// </summary>
[WindowAttentionRegistration("mac-dock")]
public class MacDockAttention() : AbstractWindowAttention("mac-dock")
{
    /// <summary>
    ///     The Objective-C runtime.
    /// </summary>
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>
    ///     <c>NSCriticalRequest</c>: bounce until the app is activated.
    /// </summary>
    private const nint CriticalRequest = 0;

    /// <summary>
    ///     <c>NSInformationalRequest</c>: bounce once.
    /// </summary>
    private const nint InformationalRequest = 10;

    /// <inheritdoc />
    public override bool IsForThisSystem => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    public override void Request(Window window, bool urgent)
    {
        var application = SendPointer(GetClass("NSApplication"), RegisterSelector("sharedApplication"));
        if (application != 0)
        {
            SendPointer(application, RegisterSelector("requestUserAttention:"), urgent ? CriticalRequest : InformationalRequest);
        }
    }

    /// <summary>
    ///     Looks up an Objective-C class.
    /// </summary>
    /// <param name="name">The class name.</param>
    /// <returns>The class, or 0.</returns>
    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);

    /// <summary>
    ///     Looks up an Objective-C selector.
    /// </summary>
    /// <param name="name">The selector name.</param>
    /// <returns>The selector.</returns>
    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint RegisterSelector(string name);

    /// <summary>
    ///     Sends a message with no arguments that returns an object.
    /// </summary>
    /// <param name="receiver">The receiver.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The result.</returns>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint receiver, nint selector);

    /// <summary>
    ///     Sends a message with one integer argument that returns an integer.
    /// </summary>
    /// <param name="receiver">The receiver.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">The argument.</param>
    /// <returns>The result.</returns>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint receiver, nint selector, nint argument);
}

/// <summary>
///     Linux and other X11 desktops: sets the window's urgency hint, which the window manager shows its own way (a
///     highlighted taskbar entry, a bouncing icon), and clears it when the user comes back. Under Wayland without
///     XWayland, windows have no X11 handle and nothing happens.
/// </summary>
[WindowAttentionRegistration("x11-urgency")]
public class X11UrgencyAttention() : AbstractWindowAttention("x11-urgency")
{
    /// <summary>
    ///     Xlib.
    /// </summary>
    private const string Xlib = "libX11.so.6";

    /// <summary>
    ///     <c>XUrgencyHint</c>, in <c>XWMHints.flags</c>.
    /// </summary>
    private const long UrgencyHint = 1L << 8;

    /// <summary>
    ///     The size of <c>XWMHints</c> on 64-bit systems: flags, input and initial state, icon pixmap and window, icon
    ///     position, icon mask and window group.
    /// </summary>
    private const int HintsSize = 56;

    /// <inheritdoc />
    public override bool IsForThisSystem => OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD();

    /// <inheritdoc />
    public override void Request(Window window, bool urgent)
    {
        this.SetUrgency(window, true);
    }

    /// <inheritdoc />
    public override void Clear(Window window)
    {
        this.SetUrgency(window, false);
    }

    /// <summary>
    ///     Sets or clears a window's urgency hint, over a connection of its own to the X server.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="urgent">Whether to set it.</param>
    private void SetUrgency(Window window, bool urgent)
    {
        if (HandleOf(window, "XID") is not { } handle || OpenDisplay(0) is var display && display == 0)
        {
            return;
        }

        try
        {
            var hints = GetWMHints(display, handle);
            var owned = hints == 0;
            if (owned)
            {
                hints = Marshal.AllocHGlobal(HintsSize);
                Marshal.Copy(new byte[HintsSize], 0, hints, HintsSize);
            }

            try
            {
                var flags = Marshal.ReadInt64(hints);
                Marshal.WriteInt64(hints, urgent ? flags | UrgencyHint : flags & ~UrgencyHint);
                SetWMHints(display, handle, hints);
                Flush(display);
            }
            finally
            {
                if (owned)
                {
                    Marshal.FreeHGlobal(hints);
                }
                else
                {
                    Free(hints);
                }
            }
        }
        finally
        {
            CloseDisplay(display);
        }
    }

    /// <summary>
    ///     Opens a connection to the X server.
    /// </summary>
    /// <param name="name">The display name, or 0 for <c>DISPLAY</c>.</param>
    /// <returns>The display, or 0.</returns>
    [DllImport(Xlib, EntryPoint = "XOpenDisplay")]
    private static extern nint OpenDisplay(nint name);

    /// <summary>
    ///     Closes a connection to the X server.
    /// </summary>
    /// <param name="display">The display.</param>
    /// <returns>Zero.</returns>
    [DllImport(Xlib, EntryPoint = "XCloseDisplay")]
    private static extern int CloseDisplay(nint display);

    /// <summary>
    ///     Gets a window's hints, allocated by Xlib.
    /// </summary>
    /// <param name="display">The display.</param>
    /// <param name="window">The window.</param>
    /// <returns>The hints, or 0 when it has none.</returns>
    [DllImport(Xlib, EntryPoint = "XGetWMHints")]
    private static extern nint GetWMHints(nint display, nint window);

    /// <summary>
    ///     Sets a window's hints.
    /// </summary>
    /// <param name="display">The display.</param>
    /// <param name="window">The window.</param>
    /// <param name="hints">The hints.</param>
    /// <returns>Unused.</returns>
    [DllImport(Xlib, EntryPoint = "XSetWMHints")]
    private static extern int SetWMHints(nint display, nint window, nint hints);

    /// <summary>
    ///     Sends what's queued to the X server.
    /// </summary>
    /// <param name="display">The display.</param>
    /// <returns>Unused.</returns>
    [DllImport(Xlib, EntryPoint = "XFlush")]
    private static extern int Flush(nint display);

    /// <summary>
    ///     Frees memory Xlib allocated.
    /// </summary>
    /// <param name="data">The memory.</param>
    /// <returns>Unused.</returns>
    [DllImport(Xlib, EntryPoint = "XFree")]
    private static extern int Free(nint data);
}
