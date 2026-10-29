using Afl.Core.Registration;
using Avalonia.Controls;

namespace Afl.UI.Attention;

/// <summary>
///     Registers an <see cref="AbstractWindowAttention" /> with the <see cref="WindowAttentionRegistry" />.
/// </summary>
/// <param name="name">The attention name.</param>
public class WindowAttentionRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     Draws the user's eye to afl's window the way one kind of system does: flashing its taskbar button, bouncing its
///     Dock icon, marking it urgent. A new way is one subclass with a <see cref="WindowAttentionRegistrationAttribute" />;
///     <see cref="WindowAttentionRegistry.ForThisMachine" /> picks the one for this system. Calls come on the UI thread.
/// </summary>
/// <param name="name">The attention name.</param>
public abstract class AbstractWindowAttention(string name)
{
    /// <summary>
    ///     Gets the attention name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets whether this is meant for the operating system afl is running on.
    /// </summary>
    public abstract bool IsForThisSystem { get; }

    /// <summary>
    ///     Asks for the user's attention to a window that isn't active.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="urgent">
    ///     True when a run needs the user, to keep at it until they come back; false for news (a run finished), to draw
    ///     the eye briefly.
    /// </param>
    public abstract void Request(Window window, bool urgent);

    /// <summary>
    ///     Stops asking once the user has come back to the window, for systems that don't stop by themselves. Does
    ///     nothing by default.
    /// </summary>
    /// <param name="window">The window.</param>
    public virtual void Clear(Window window)
    {
    }

    /// <summary>
    ///     Gets a window's native handle when it is of the kind given, e.g. <c>"HWND"</c>.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="descriptor">The kind of handle wanted.</param>
    /// <returns>The handle, or null when the window has none of that kind.</returns>
    protected static nint? HandleOf(Window window, string descriptor)
    {
        return window.TryGetPlatformHandle() is { } handle && handle.HandleDescriptor == descriptor ? handle.Handle : null;
    }
}

/// <summary>
///     Finds the window attention for this machine.
/// </summary>
public class WindowAttentionRegistry() : TypeRegistry<AbstractWindowAttention, WindowAttentionRegistrationAttribute>("window attention")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static WindowAttentionRegistry Instance { get; } = new();

    /// <summary>
    ///     Returns the window attention meant for this system.
    /// </summary>
    /// <returns>The attention, or null when afl has none for this system.</returns>
    public AbstractWindowAttention? ForThisMachine()
    {
        return this.CreateAll().FirstOrDefault(a => a.IsForThisSystem);
    }
}
