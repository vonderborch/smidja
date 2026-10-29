using System.Text;

namespace Afl.Core.Notifications;

/// <summary>
///     macOS notifications through <c>terminal-notifier</c> (Homebrew), which shows them as its own app and replaces
///     the last one about the same run. Preferred over <see cref="AppleScriptNotifier" /> when installed.
/// </summary>
[NotifierRegistration("terminal-notifier")]
public class TerminalNotifierNotifier() : AbstractProcessNotifier("terminal-notifier")
{
    /// <inheritdoc />
    public override int Priority => 10;

    /// <inheritdoc />
    protected override string Command => "terminal-notifier";

    /// <inheritdoc />
    protected override bool IsForThisSystem => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    protected override IReadOnlyList<string> ArgumentsFor(Notification notification)
    {
        var arguments = new List<string> { "-title", notification.Title, "-message", notification.Message };
        if (notification.Group is not null)
        {
            arguments.AddRange(["-group", notification.Group]);
        }

        return arguments;
    }
}

/// <summary>
///     macOS notifications through AppleScript's <c>display notification</c>, which every Mac has. The text goes in as
///     the script's arguments, so nothing needs escaping. macOS shows them as coming from Script Editor.
/// </summary>
[NotifierRegistration("osascript")]
public class AppleScriptNotifier() : AbstractProcessNotifier("osascript")
{
    /// <inheritdoc />
    protected override string Command => "osascript";

    /// <inheritdoc />
    protected override bool IsForThisSystem => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    protected override IReadOnlyList<string> ArgumentsFor(Notification notification)
    {
        return
        [
            "-e", "on run argv",
            "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
            "-e", "end run",
            notification.Title, notification.Message,
        ];
    }
}

/// <summary>
///     Linux (and other freedesktop) notifications through <c>notify-send</c> (libnotify).
/// </summary>
[NotifierRegistration("notify-send")]
public class NotifySendNotifier() : AbstractProcessNotifier("notify-send")
{
    /// <inheritdoc />
    protected override string Command => "notify-send";

    /// <inheritdoc />
    protected override bool IsForThisSystem => OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD();

    /// <inheritdoc />
    protected override IReadOnlyList<string> ArgumentsFor(Notification notification)
    {
        return ["--app-name=afl", "--", notification.Title, notification.Message];
    }
}

/// <summary>
///     Windows toast notifications through Windows PowerShell's WinRT access, shown as coming from PowerShell. The
///     script is passed encoded and the text in environment variables, so nothing typed into a run reaches PowerShell
///     as code.
/// </summary>
[NotifierRegistration("windows-toast")]
public class WindowsToastNotifier() : AbstractProcessNotifier("windows-toast")
{
    /// <summary>
    ///     The environment variable holding the title.
    /// </summary>
    private const string TitleVariable = "AFL_NOTIFY_TITLE";

    /// <summary>
    ///     The environment variable holding the message.
    /// </summary>
    private const string MessageVariable = "AFL_NOTIFY_MESSAGE";

    /// <summary>
    ///     The environment variable holding the group, used as the toast's tag.
    /// </summary>
    private const string GroupVariable = "AFL_NOTIFY_GROUP";

    /// <summary>
    ///     The script that shows the toast, reading its text from the environment.
    /// </summary>
    private static readonly string Script = string.Join("\n",
        "$ErrorActionPreference = 'Stop'",
        "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null",
        "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null",
        $"$title = [Security.SecurityElement]::Escape($env:{TitleVariable})",
        $"$message = [Security.SecurityElement]::Escape($env:{MessageVariable})",
        "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument",
        "$xml.LoadXml(\"<toast><visual><binding template=`\"ToastGeneric`\"><text>$title</text><text>$message</text></binding></visual></toast>\")",
        "$toast = New-Object Windows.UI.Notifications.ToastNotification $xml",
        $"if ($env:{GroupVariable}) {{ $toast.Tag = $env:{GroupVariable} }}",
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show($toast)");

    /// <inheritdoc />
    public override string DisplayName => "Windows notifications";

    /// <inheritdoc />
    protected override string Command => "powershell.exe";

    /// <inheritdoc />
    protected override bool IsForThisSystem => OperatingSystem.IsWindows();

    /// <inheritdoc />
    protected override IReadOnlyList<string> ArgumentsFor(Notification notification)
    {
        return ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))];
    }

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> EnvironmentFor(Notification notification)
    {
        var environment = new Dictionary<string, string>
        {
            [TitleVariable] = notification.Title,
            [MessageVariable] = notification.Message,
        };
        if (notification.Group is not null)
        {
            // Windows caps a toast's tag at 64 characters.
            environment[GroupVariable] = notification.Group.Length > 64 ? notification.Group[..64] : notification.Group;
        }

        return environment;
    }
}
