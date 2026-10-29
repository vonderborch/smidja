using System.Collections.ObjectModel;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Execution;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An agent in the Settings list: the agent, whether it can run on this machine, and its capabilities.
/// </summary>
/// <param name="Agent">The agent.</param>
/// <param name="IsAvailable">Whether it can run here (its command is installed, its server registered).</param>
/// <param name="Capabilities">Its capabilities, declared and implied.</param>
public record AgentListItem(AgentDefinition Agent, bool IsAvailable, IReadOnlyList<string> Capabilities)
{
    /// <summary>
    ///     Gets the agent id.
    /// </summary>
    public string Id => this.Agent.Id;

    /// <summary>
    ///     Gets the agent name.
    /// </summary>
    public string Name => this.Agent.Name;

    /// <summary>
    ///     Gets the agent kind.
    /// </summary>
    public string Kind => this.Agent.Kind;

    /// <summary>
    ///     Gets a value indicating whether it can't run here.
    /// </summary>
    public bool IsMissing => !this.IsAvailable;
}

/// <summary>
///     The AI agents tab of <see cref="SettingsPageDataContext" />: the agents, a form generated from each kind's fields
///     (or its raw JSON), presets, a test and the order agents are preferred in.
/// </summary>
public partial class SettingsPageDataContext
{
    /// <summary>
    ///     Gets the agents.
    /// </summary>
    public ObservableCollection<AgentListItem> AgentList { get; } = [];

    /// <summary>
    ///     Gets the presets, installed ones first.
    /// </summary>
    public ObservableCollection<AgentPreset> AgentPresets { get; } = [];

    /// <summary>
    ///     Gets or sets the agent being edited; null for a new one.
    /// </summary>
    public AgentListItem? SelectedAgent
    {
        get;
        set
        {
            var changed = value?.Id != field?.Id;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.CanEditAgent));
            this.RaisePropertyChanged(nameof(this.HasSelectedAgent));
            if (changed && !this._loading)
            {
                this.FillAgentForm(value?.Agent);
            }
        }
    }

    /// <summary>
    ///     Gets a value indicating whether an existing agent is selected.
    /// </summary>
    public bool HasSelectedAgent => this.SelectedAgent is not null;

    /// <summary>
    ///     Gets or sets the preset to add.
    /// </summary>
    public AgentPreset? SelectedPreset
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the form for the agent being edited, if any.
    /// </summary>
    public AgentFormDataContext? AgentForm
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the agent is edited as raw JSON. Switching back with JSON that doesn't
    ///     parse shows the problem and stays on the JSON.
    /// </summary>
    public bool IsAgentJson
    {
        get => this.AgentForm?.IsJson ?? false;
        set
        {
            if (this.AgentForm is { } form)
            {
                this.Try(() => form.ShowJson(value));
            }

            this.RaisePropertyChanged();
        }
    }

    /// <summary>
    ///     Gets a value indicating whether the selected agent can be changed (built-in agents can't).
    /// </summary>
    public bool CanEditAgent => this.SelectedAgent is not { Agent.BuiltIn: true };

    /// <summary>
    ///     Gets the last agent action's outcome.
    /// </summary>
    public string? AgentStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the order agents are preferred in when several fit a step (ids; agents not listed come after, in list order).
    /// </summary>
    public ObservableCollection<string> AgentPreference { get; } = [];

    /// <summary>
    ///     Gets or sets the agent picked in <see cref="AgentPreference" />.
    /// </summary>
    public string? SelectedPreference
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Starts a new command-line agent.
    /// </summary>
    public void NewAgent()
    {
        this.SelectedAgent = null;
        this.FillAgentForm(new AgentDefinition
        {
            Id = "my-agent", Name = "My agent", Kind = AgentConstants.KindCli, Command = "my-ai-cli",
            Args = ["{{prompt}}"],
        });
    }

    /// <summary>
    ///     Adds the selected preset, numbering the id if it's taken.
    /// </summary>
    /// <returns>True if it was added.</returns>
    public bool AddPresetAgent()
    {
        if (this.SelectedPreset is not { } preset)
        {
            return false;
        }

        var agent = preset.Agent.Clone();
        var baseId = agent.Id;
        for (var number = 2; this.AgentList.Any(a => string.Equals(a.Id, agent.Id, StringComparison.OrdinalIgnoreCase)); number++)
        {
            agent.Id = $"{baseId}-{number}";
        }

        if (!string.IsNullOrWhiteSpace(agent.Server) && this.Shell.Servers.Find(agent.Server) is null &&
            AgentStore.FindPresetServer(agent.Server) is { } server && !this.Try(() => this.Shell.Servers.Save(server)))
        {
            return false;
        }

        return this.SaveAgent(agent);
    }

    /// <summary>
    ///     Saves the agent in the form.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public bool SaveAgent()
    {
        AgentDefinition? agent = null;
        return this.Try(() => agent = this.FormAgent()) && this.SaveAgent(agent!);
    }

    /// <summary>
    ///     Removes the selected agent.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool RemoveAgent()
    {
        if (this.SelectedAgent is not { } item || !this.Try(() => this.Shell.Agents.Remove(item.Id)))
        {
            return false;
        }

        this.AgentStatus = $"Removed {item.Id}.";
        this.SelectedAgent = null;
        this.Reload();
        this.SelectedAgent = this.AgentList.FirstOrDefault();
        return true;
    }

    /// <summary>
    ///     Asks the agent in the form a small question to check that it works.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task TestAgentAsync()
    {
        AgentDefinition? agent = null;
        if (!this.Try(() => agent = this.FormAgent(validate: false)))
        {
            return;
        }

        this.AgentStatus = $"Testing {agent!.Id}…";
        ExecutionResult? result = null;
        var directory = this.Repo?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ran = await this.TryAsync(async () => result = await this.Shell.Agents.TestAsync(agent, directory));
        this.AgentStatus = !ran
            ? null
            : result!.Status == ExecutionStatus.Completed
                ? $"{agent.Id} answered: {result.Output?.Trim()}"
                : $"{agent.Id} failed: {result.Error ?? result.Status.ToString()}";
    }

    /// <summary>
    ///     Lists the tools of the server the form's agent calls, so its tool fields offer them.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task LoadFormToolsAsync()
    {
        if (this.AgentForm?.ToolServer is not { } serverId)
        {
            this.ErrorMessage = "Pick the agent's MCP server first.";
            return;
        }

        await this.LoadToolsAsync(serverId);
    }

    /// <summary>
    ///     Moves an agent in the preference order and saves it.
    /// </summary>
    /// <param name="by">-1 for up, 1 for down.</param>
    /// <returns>True if it moved.</returns>
    public bool MovePreference(int by)
    {
        if (this.SelectedPreference is not { } id)
        {
            return false;
        }

        var index = this.AgentPreference.IndexOf(id);
        var target = index + by;
        if (index < 0 || target < 0 || target >= this.AgentPreference.Count)
        {
            return false;
        }

        this.AgentPreference.Move(index, target);
        var saved = this.SaveSettings(settings => settings.AgentPreference = [.. this.AgentPreference]);
        this.SelectedPreference = id;
        return saved;
    }

    /// <summary>
    ///     Loads the agents, presets and preference order.
    /// </summary>
    private void LoadAgents()
    {
        var selectedId = this.SelectedAgent?.Id;
        var hadForm = this.AgentForm is not null;
        this.AgentList.Clear();
        foreach (var agent in this.Shell.Agents.All())
        {
            this.AgentList.Add(new AgentListItem(agent, this.Shell.Agents.IsAvailable(agent), this.Shell.Agents.CapabilitiesOf(agent)));
        }

        if (this.AgentPresets.Count == 0)
        {
            foreach (var preset in AgentStore.Detect().OrderByDescending(p => p.Installed).ThenBy(p => p.Agent.Id))
            {
                this.AgentPresets.Add(preset);
            }

            this.SelectedPreset = this.AgentPresets.FirstOrDefault();
        }

        var preferred = this.SelectedPreference;
        var preference = this.Shell.Settings.Load().AgentPreference;
        this.AgentPreference.Clear();
        foreach (var id in preference.Where(id => this.AgentList.Any(a => a.Id == id))
                     .Concat(this.AgentList.Select(a => a.Id).Where(id => !preference.Contains(id))))
        {
            this.AgentPreference.Add(id);
        }

        this.SelectedPreference = preferred;
        this.SelectedAgent = this.AgentList.FirstOrDefault(a => a.Id == selectedId);
        if (selectedId is null && !hadForm)
        {
            this.SelectedAgent = this.AgentList.FirstOrDefault();
            this.FillAgentForm(this.SelectedAgent?.Agent);
        }
    }

    /// <summary>
    ///     Saves an agent and selects it.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>True if it was saved.</returns>
    private bool SaveAgent(AgentDefinition agent)
    {
        if (!this.Try(() => this.Shell.Agents.Save(agent)))
        {
            return false;
        }

        this.AgentStatus = $"Saved {agent.Id}.";
        this.Reload();
        this.SelectedAgent = this.AgentList.FirstOrDefault(a => a.Id == agent.Id);
        this.FillAgentForm(this.SelectedAgent?.Agent);
        return true;
    }

    /// <summary>
    ///     Shows an agent in the form.
    /// </summary>
    /// <param name="agent">The agent, or null to clear the form.</param>
    private void FillAgentForm(AgentDefinition? agent)
    {
        this.AgentForm = agent is null
            ? null
            : new AgentFormDataContext(agent, CapabilityCatalogFor(this.Shell).All, this.ServerList.Select(s => s.Id).ToList(), this.ToolCache);
        this.RaisePropertyChanged(nameof(this.IsAgentJson));
    }

    /// <summary>
    ///     Builds the agent in the form and checks it.
    /// </summary>
    /// <param name="validate">Whether to check it.</param>
    /// <returns>The agent.</returns>
    private AgentDefinition FormAgent(bool validate = true)
    {
        if (this.AgentForm is not { } form)
        {
            throw new InvalidOperationException("Pick an agent, or start a new one.");
        }

        var agent = form.ToAgent();
        var problems = validate ? AgentStore.Validate(agent, this.Shell.Servers) : [];
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Agent '{agent.Id}' is invalid: {string.Join(" ", problems)}");
        }

        return agent;
    }

    /// <summary>
    ///     Returns the capabilities on offer: the built-in ones and the shell's custom ones.
    /// </summary>
    /// <param name="shell">The shell.</param>
    /// <returns>The catalogue.</returns>
    private static CapabilityCatalog CapabilityCatalogFor(ShellDataContext shell)
    {
        return CapabilityCatalog.For(shell.Settings);
    }
}
