using Afl.Cli.Output;
using Afl.Core.Agents;
using Afl.Core.Exceptions;
using Afl.Core.Notifications;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Workflows;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for the global settings commands: gives access to the settings and agent stores. Settings live only in the
///     afl home, so these commands never register the current repo.
/// </summary>
public abstract class AbstractSettingsCommand : AbstractCommand
{
    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <summary>
    ///     Gets the settings store.
    /// </summary>
    protected SettingsStore Settings => new(this.Paths);

    /// <summary>
    ///     Writes the settings as they now are.
    /// </summary>
    /// <param name="settings">The settings.</param>
    protected void WriteSettings(AflSettings settings)
    {
        this.Output.Result(SettingsView.From(settings, new SettingsStore(this.Paths).DefaultAgent()), TextRendering.Settings);
    }
}

/// <summary>
///     <c>afl settings show</c>.
/// </summary>
[Verb("settings-show", HelpText = "Show afl's global settings: the main agent, run mode, how steps choose agents, and custom capabilities.")]
public class SettingsShowCommand : AbstractSettingsCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.WriteSettings(this.Settings.Load());
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl settings set</c>: changes one global setting.
/// </summary>
[Verb("settings-set", HelpText = "Change a global setting: default-agent <id>, default-run-mode background|terminal, " +
                                 "default-selector ai|first, default-permissions ask|strict, scratch-directory <dir>, agent-preference <id,id,...>, or notify-on " +
                                 "<state,state,...> (needs-you, done, failed, running, waiting, cancelled; none for no notifications). An empty value resets it.")]
public class SettingsSetCommand : AbstractSettingsCommand
{
    /// <summary>
    ///     The settings that can be changed, each with how it's applied: the store's settings, the value (null to reset)
    ///     and the agent store, for checking ids.
    /// </summary>
    private static readonly Dictionary<string, Action<AflSettings, string?, AgentStore>> Setters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["default-agent"] = (settings, value, agents) =>
                settings.DefaultAgent = value is null ? null : agents.GetRequired(value).Id,
            ["default-run-mode"] = (settings, value, _) =>
                settings.DefaultRunMode = value ?? new AflSettings().DefaultRunMode,
            ["default-selector"] = (settings, value, _) =>
                settings.DefaultSelector = value ?? new AflSettings().DefaultSelector,
            ["default-permissions"] = (settings, value, _) =>
                settings.DefaultPermissionPolicy = value is null
                    ? new AflSettings().DefaultPermissionPolicy
                    : PermissionPolicyRegistry.Instance.Require(value, true).Name,
            ["scratch-directory"] = (settings, value, _) =>
                settings.ScratchDirectory = value is null ? new AflSettings().ScratchDirectory : ScratchArea.Normalize(value),
            ["notify-on"] = (settings, value, _) =>
                settings.NotifyOn = value is null
                    ? [..AflSettings.DefaultNotifyOn]
                    : string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? [] : RunStates.Parse(value.Split(',')),
            ["agent-preference"] = (settings, value, agents) =>
                settings.AgentPreference = value is null
                    ? []
                    : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(id => agents.GetRequired(id).Id).ToList(),
        };

    /// <summary>
    ///     Gets or sets the setting's name.
    /// </summary>
    [Value(0, MetaName = "key", Required = true, HelpText = "default-agent, default-run-mode, default-selector, default-permissions, scratch-directory, agent-preference or notify-on.")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the new value.
    /// </summary>
    [Value(1, MetaName = "value", HelpText = "The new value; leave it out (or give an empty one) to reset the setting.")]
    public string? Value { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!Setters.TryGetValue(this.Key, out var set))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"There's no setting '{this.Key}'. Use one of {string.Join(", ", Setters.Keys)}.");
        }

        var store = this.Settings;
        var settings = store.Load();
        set(settings, string.IsNullOrWhiteSpace(this.Value) ? null : this.Value.Trim(), new AgentStore(this.Paths));
        this.WriteSettings(store.Save(settings));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl capability list</c>.
/// </summary>
[Verb("capability-list", HelpText = "List the capabilities agents can declare and steps can ask for: the built-in ones and yours.")]
public class CapabilityListCommand : AbstractSettingsCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var views = CapabilityCatalog.For(this.Settings).All.Select(CapabilityView.From).ToList();
        this.Output.Result(views, TextRendering.CapabilityList);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl capability add</c>: adds or redescribes one of your own capabilities.
/// </summary>
[Verb("capability-add", HelpText = "Add your own capability (or change its description); the main agent reads the description when choosing.")]
public class CapabilityAddCommand : AbstractSettingsCommand
{
    /// <summary>
    ///     Gets or sets the capability id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The capability id (lowercase, digits and dashes).")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the description.
    /// </summary>
    [Option('d', "description", HelpText = "What an agent with it is good at.")]
    public string? Description { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Settings;
        var settings = store.Load();
        var id = this.Id.Trim().ToLowerInvariant();
        settings.Capabilities =
        [
            .. settings.Capabilities.Where(c => c.Id != id),
            new CapabilityDefinition { Id = id, Description = this.Description?.Trim() ?? string.Empty },
        ];
        this.WriteSettings(store.Save(settings));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl capability remove</c>.
/// </summary>
[Verb("capability-remove", HelpText = "Remove one of your capabilities. Agents and steps that use it keep the id, which validation then flags.")]
public class CapabilityRemoveCommand : AbstractSettingsCommand
{
    /// <summary>
    ///     Gets or sets the capability id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The capability id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Settings;
        var settings = store.Load();
        if (settings.Capabilities.RemoveAll(c => string.Equals(c.Id, this.Id, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            throw new AflException(AflErrorKind.NotFound,
                CapabilityCatalog.BuiltIns.Any(c => c.Id == this.Id)
                    ? $"'{this.Id}' is built in and can't be removed."
                    : $"You have no capability '{this.Id}'.");
        }

        this.WriteSettings(store.Save(settings));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl notify-test</c>: shows a test desktop notification, to check notifications work on this machine.
/// </summary>
[Verb("notify-test", HelpText = "Show a test desktop notification, the kind afl shows when a run needs you or finishes (see settings-set notify-on).")]
public class NotifyTestCommand : AbstractSettingsCommand
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var notifier = NotifierRegistry.Instance.ForThisMachine() ?? throw new AflException(AflErrorKind.InvalidState,
            "afl can't show notifications here: install notify-send (libnotify) on Linux; macOS and Windows need nothing extra.");
        var notification = new Notification("afl: test", "Notifications from afl work.");
        await notifier.SendAsync(notification, ct);
        var states = this.Settings.Load().NotifyOn.Select(s => RunStates.Info(s).CliName).ToList();
        this.Output.Result(new { notifier = notifier.Name, notifyOn = states }, (r, w) =>
        {
            w.WriteLine($"Sent a test notification through {notifier.DisplayName}.");
            w.WriteLine(r.notifyOn.Count == 0
                ? "Notifications are off (afl settings-set notify-on needs-you,done,failed turns them on)."
                : $"afl notifies when a run is: {string.Join(", ", r.notifyOn)}.");
        });
        return ExitSuccess;
    }
}
