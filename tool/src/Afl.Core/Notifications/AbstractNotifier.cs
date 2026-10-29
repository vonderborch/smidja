using Afl.Core.Agents;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Registration;

namespace Afl.Core.Notifications;

/// <summary>
///     Registers an <see cref="AbstractNotifier" /> with the <see cref="NotifierRegistry" />.
/// </summary>
/// <param name="name">The notifier name.</param>
public class NotifierRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     A desktop notification.
/// </summary>
/// <param name="Title">The title, e.g. "afl: needs you".</param>
/// <param name="Message">The text under it.</param>
/// <param name="Group">
///     What the notification is about (a run id), so a notifier that can replaces the last one about it rather than
///     stacking them; null when it stands alone.
/// </param>
public record Notification(string Title, string Message, string? Group = null);

/// <summary>
///     Shows desktop notifications on one kind of system, through whatever that system offers. A new way to notify is
///     one subclass with a <see cref="NotifierRegistrationAttribute" />; <see cref="NotifierRegistry.ForThisMachine" />
///     picks the available one with the highest <see cref="Priority" />.
/// </summary>
/// <param name="name">The notifier name.</param>
public abstract class AbstractNotifier(string name)
{
    /// <summary>
    ///     Gets the notifier name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets the name shown in front-ends, e.g. "notify-send".
    /// </summary>
    public abstract string DisplayName { get; }

    /// <summary>
    ///     Gets the notifier's rank among those available: the highest wins.
    /// </summary>
    public virtual int Priority => 0;

    /// <summary>
    ///     Checks whether the notifier works here: the right system, with what it needs installed.
    /// </summary>
    /// <returns>True if it does.</returns>
    public abstract bool IsAvailable();

    /// <summary>
    ///     Shows a notification.
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="AflException">The notification couldn't be shown.</exception>
    public abstract Task SendAsync(Notification notification, CancellationToken ct);
}

/// <summary>
///     Base for notifiers that run a command, with the text passed as arguments or environment variables, never
///     through a shell.
/// </summary>
/// <param name="name">The notifier name.</param>
public abstract class AbstractProcessNotifier(string name) : AbstractNotifier(name)
{
    /// <summary>
    ///     Gets the command to run, looked up on <c>PATH</c>.
    /// </summary>
    protected abstract string Command { get; }

    /// <summary>
    ///     Gets whether the notifier is meant for this operating system.
    /// </summary>
    protected abstract bool IsForThisSystem { get; }

    /// <inheritdoc />
    public override string DisplayName => this.Command;

    /// <inheritdoc />
    public override bool IsAvailable()
    {
        return this.IsForThisSystem && AgentStore.IsOnPath(this.Command, Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
    }

    /// <inheritdoc />
    public override async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(this.Command, this.ArgumentsFor(notification), ct: ct,
            environment: this.EnvironmentFor(notification)).ConfigureAwait(false);
        if (!result.Success)
        {
            var error = result.StandardError.Trim();
            throw new AflException(AflErrorKind.ExternalTool,
                $"{this.Command} couldn't show the notification (exit {result.ExitCode}){(error.Length > 0 ? $": {error}" : ".")}");
        }
    }

    /// <summary>
    ///     Builds the command's arguments.
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <returns>The arguments.</returns>
    protected abstract IReadOnlyList<string> ArgumentsFor(Notification notification);

    /// <summary>
    ///     Builds environment variables for the command, if it reads the text from them.
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <returns>The variables, or null for none.</returns>
    protected virtual IReadOnlyDictionary<string, string>? EnvironmentFor(Notification notification)
    {
        return null;
    }
}

/// <summary>
///     Finds notifiers by name, and the one to use here.
/// </summary>
public class NotifierRegistry() : TypeRegistry<AbstractNotifier, NotifierRegistrationAttribute>("notifier")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static NotifierRegistry Instance { get; } = new();

    /// <summary>
    ///     Returns the best notifier available on this machine.
    /// </summary>
    /// <returns>The notifier, or null when none works here.</returns>
    public AbstractNotifier? ForThisMachine()
    {
        return this.CreateAll().OrderByDescending(n => n.Priority).FirstOrDefault(n => n.IsAvailable());
    }
}
