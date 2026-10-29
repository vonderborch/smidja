using System.Collections.ObjectModel;
using Afl.Core.Agents;
using Afl.Core.Execution;
using Afl.Core.Notifications;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     A choice in one of the Execution tab's drop-downs: the value saved and what it means.
/// </summary>
/// <param name="Value">The value saved in the settings.</param>
/// <param name="Label">The name shown.</param>
/// <param name="Description">What picking it does.</param>
public record SettingChoice(string Value, string Label, string Description)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return this.Label;
    }
}

/// <summary>
///     The Execution tab of <see cref="SettingsPageDataContext" />: the main agent for new runs, how agents' commands run,
///     how steps choose between agents, which run states notify and the user's own capabilities.
/// </summary>
public partial class SettingsPageDataContext
{
    /// <summary>
    ///     Gets the agents that can be the main agent.
    /// </summary>
    public ObservableCollection<string> MainAgentChoices { get; } = [];

    /// <summary>
    ///     Gets or sets the main agent for new runs.
    /// </summary>
    public string? DefaultAgent
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the run modes.
    /// </summary>
    public IReadOnlyList<SettingChoice> RunModes { get; } = RunModeRegistry.Instance.CreateAll()
        .Select(m => new SettingChoice(m.Name, m.DisplayName, m.Description)).OrderBy(c => c.Value).ToList();

    /// <summary>
    ///     Gets or sets the default run mode.
    /// </summary>
    public SettingChoice? DefaultRunMode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the folder at each repo's root where runs keep their working documents, e.g. <c>.scratch</c>.
    /// </summary>
    public string ScratchDirectory
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets the ways a step can pick between agents.
    /// </summary>
    public IReadOnlyList<SettingChoice> Selectors { get; } = AgentSelectorRegistry.Instance.Registrations
        .Select(r => new SettingChoice(r.Key, r.DisplayName, r.Description)).OrderBy(c => c.Value).ToList();

    /// <summary>
    ///     Gets or sets the default selector.
    /// </summary>
    public SettingChoice? DefaultSelector
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the built-in capabilities, for reference.
    /// </summary>
    public IReadOnlyList<CapabilityDefinition> BuiltInCapabilities => CapabilityCatalog.BuiltIns;

    /// <summary>
    ///     Gets the user's own capabilities.
    /// </summary>
    public ObservableCollection<CapabilityDefinition> CustomCapabilities { get; } = [];

    /// <summary>
    ///     Gets or sets the custom capability picked to remove.
    /// </summary>
    public CapabilityDefinition? SelectedCapability
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the id of a capability to add.
    /// </summary>
    public string? NewCapabilityId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the description of a capability to add (the main agent reads it when choosing).
    /// </summary>
    public string? NewCapabilityDescription
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the run states, each ticked when reaching it shows a desktop notification. Values are the states' CLI names.
    /// </summary>
    public ObservableCollection<SelectableItem<string>> NotifyStates { get; } = [];

    /// <summary>
    ///     Gets what shows notifications here, e.g. "Notifications are shown through osascript.".
    /// </summary>
    public string NotifierText => this.Shell.Driver.Engine.Notifier switch
    {
        null => "This window doesn't send notifications.",
        { Notifier: { } notifier } => $"Notifications are shown through {notifier.DisplayName}.",
        _ => "afl can't show notifications here: install notify-send (libnotify) on Linux.",
    };

    /// <summary>
    ///     Gets the last notification-settings action's outcome.
    /// </summary>
    public string? NotificationNote
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the last execution-settings action's outcome.
    /// </summary>
    public string? ExecutionNote
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Saves the main agent, run mode, selector and scratch directory.
    /// </summary>
    /// <returns>True if they were saved.</returns>
    public bool SaveExecution()
    {
        var saved = this.SaveSettings(settings =>
        {
            settings.DefaultAgent = string.IsNullOrWhiteSpace(this.DefaultAgent) ? null : this.DefaultAgent;
            settings.DefaultRunMode = this.DefaultRunMode?.Value ?? settings.DefaultRunMode;
            settings.DefaultSelector = this.DefaultSelector?.Value ?? settings.DefaultSelector;
            settings.ScratchDirectory = string.IsNullOrWhiteSpace(this.ScratchDirectory)
                ? new AflSettings().ScratchDirectory
                : ScratchArea.Normalize(this.ScratchDirectory);
        });
        this.ExecutionNote = saved ? "Saved." : null;
        return saved;
    }

    /// <summary>
    ///     Saves which run states show a notification.
    /// </summary>
    /// <returns>True if they were saved.</returns>
    public bool SaveNotifications()
    {
        var states = RunStates.Parse(this.NotifyStates.Where(s => s.IsSelected).Select(s => s.Value));
        var saved = this.SaveSettings(settings => settings.NotifyOn = states);
        this.NotificationNote = saved ? states.Count == 0 ? "Saved; notifications are off." : "Saved." : null;
        return saved;
    }

    /// <summary>
    ///     Shows a test notification through the app's notifier.
    /// </summary>
    /// <returns>True if it was shown.</returns>
    public async Task<bool> SendTestNotificationAsync()
    {
        this.NotificationNote = null;
        if (this.Shell.Driver.Engine.Notifier?.Notifier is not { } notifier)
        {
            this.ErrorMessage = this.NotifierText;
            return false;
        }

        if (!await this.TryAsync(() => notifier.SendAsync(new Notification("afl: test", "Notifications from afl work."), CancellationToken.None)))
        {
            return false;
        }

        this.NotificationNote = "Sent. If nothing appeared, allow notifications for it in your system settings.";
        return true;
    }

    /// <summary>
    ///     Adds a custom capability.
    /// </summary>
    /// <returns>True if it was added.</returns>
    public bool AddCapability()
    {
        var id = this.NewCapabilityId?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id))
        {
            this.ErrorMessage = "Enter the capability's id.";
            return false;
        }

        if (CapabilityCatalogFor(this.Shell).Contains(id))
        {
            this.ErrorMessage = $"There's already a capability '{id}'.";
            return false;
        }

        var capability = new CapabilityDefinition { Id = id, Description = this.NewCapabilityDescription?.Trim() ?? string.Empty };
        if (!this.SaveSettings(settings => settings.Capabilities = [.. settings.Capabilities, capability]))
        {
            return false;
        }

        this.NewCapabilityId = null;
        this.NewCapabilityDescription = null;
        this.ExecutionNote = $"Added {id}.";
        return true;
    }

    /// <summary>
    ///     Removes the picked custom capability. Agents and steps that use it keep the id, which validation then flags.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool RemoveCapability()
    {
        if (this.SelectedCapability is not { } capability ||
            !this.SaveSettings(settings => settings.Capabilities = settings.Capabilities.Where(c => c.Id != capability.Id).ToList()))
        {
            return false;
        }

        this.ExecutionNote = $"Removed {capability.Id}.";
        return true;
    }

    /// <summary>
    ///     Loads the execution settings.
    /// </summary>
    private void LoadExecution()
    {
        var settings = this.Shell.Settings.Load();
        this.MainAgentChoices.Clear();
        foreach (var item in this.AgentList)
        {
            this.MainAgentChoices.Add(item.Id);
        }

        this.DefaultAgent = this.Shell.Settings.DefaultAgent();
        this.DefaultRunMode = this.RunModes.FirstOrDefault(m => m.Value == settings.DefaultRunMode) ?? this.RunModes.FirstOrDefault();
        this.DefaultSelector = this.Selectors.FirstOrDefault(s => s.Value == settings.DefaultSelector) ?? this.Selectors.FirstOrDefault();
        this.ScratchDirectory = settings.ScratchDirectory;
        this.NotifyStates.Clear();
        foreach (var state in RunStates.All)
        {
            this.NotifyStates.Add(new SelectableItem<string>(state.CliName, state.Label, settings.NotifyOn.Contains(state.State)));
        }

        this.CustomCapabilities.Clear();
        foreach (var capability in settings.Capabilities)
        {
            this.CustomCapabilities.Add(capability);
        }
    }

    /// <summary>
    ///     Changes and saves the settings, showing any problem.
    /// </summary>
    /// <param name="change">Changes the loaded settings.</param>
    /// <returns>True if they were saved.</returns>
    private bool SaveSettings(Action<AflSettings> change)
    {
        if (!this.Try(() =>
            {
                var settings = this.Shell.Settings.Load();
                change(settings);
                this.Shell.Settings.Save(settings);
            }))
        {
            return false;
        }

        this.Reload();
        return true;
    }
}
