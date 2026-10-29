using System.Collections.ObjectModel;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Mcp;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     One secret of an MCP server in the Settings form. The stored value is never read back; the form only says
///     whether one is set, and a value typed into <see cref="Value" /> replaces it on save.
/// </summary>
/// <param name="name">The secret's name, as <c>{{secret.NAME}}</c> uses it.</param>
/// <param name="isSet">Whether a value is stored (or comes from the environment).</param>
public class McpSecretDataContext(string name, bool isSet) : ReactiveObject
{
    /// <summary>
    ///     Gets the secret's name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets a value indicating whether a value is stored.
    /// </summary>
    public bool IsSet { get; } = isSet;

    /// <summary>
    ///     Gets what the form says about the stored value.
    /// </summary>
    public string Status => this.IsSet ? "set" : "not set";

    /// <summary>
    ///     Gets or sets a new value, saved with the server; empty keeps the stored one.
    /// </summary>
    public string? Value
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>
///     The MCP servers tab of <see cref="SettingsPageDataContext" />: the registered servers, a form for one (its secrets
///     masked), importing servers other apps have configured, listing a server's tools and turning a tool into an agent.
/// </summary>
public partial class SettingsPageDataContext
{
    /// <summary>
    ///     The tools each server was last seen to have, by server id.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _toolCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the registered servers.
    /// </summary>
    public ObservableCollection<McpServerDefinition> ServerList { get; } = [];

    /// <summary>
    ///     Gets or sets the server being edited; null for a new one.
    /// </summary>
    public McpServerDefinition? SelectedServer
    {
        get;
        set
        {
            var changed = value?.Id != field?.Id;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.HasSelectedServer));
            if (changed && !this._loading)
            {
                this.FillServerForm(value);
            }
        }
    }

    /// <summary>
    ///     Gets a value indicating whether an existing server is selected.
    /// </summary>
    public bool HasSelectedServer => this.SelectedServer is not null;

    /// <summary>
    ///     Gets the transports.
    /// </summary>
    public IReadOnlyList<string> Transports => AgentConstants.Transports;

    /// <summary>
    ///     Gets or sets the server id.
    /// </summary>
    public string ServerId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the server name.
    /// </summary>
    public string ServerName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets what the server is for.
    /// </summary>
    public string? ServerDescription
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets how afl talks to the server.
    /// </summary>
    public string ServerTransport
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? AgentConstants.TransportStdio);
            this.RaisePropertyChanged(nameof(this.IsStdio));
            this.RaisePropertyChanged(nameof(this.IsHttp));
        }
    } = AgentConstants.TransportStdio;

    /// <summary>
    ///     Gets a value indicating whether afl starts the server as a command.
    /// </summary>
    public bool IsStdio => this.ServerTransport == AgentConstants.TransportStdio;

    /// <summary>
    ///     Gets a value indicating whether afl reaches the server over HTTP.
    /// </summary>
    public bool IsHttp => !this.IsStdio;

    /// <summary>
    ///     Gets or sets the command that starts the server.
    /// </summary>
    public string? ServerCommand
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the command's arguments, one per line.
    /// </summary>
    public string? ServerArgs
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the command's environment, NAME=value per line (values may use <c>{{secret.NAME}}</c>).
    /// </summary>
    public string? ServerEnv
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the server's URL.
    /// </summary>
    public string? ServerUrl
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the HTTP headers, Name=value per line (values may use <c>{{secret.NAME}}</c>).
    /// </summary>
    public string? ServerHeaders
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the server's secrets.
    /// </summary>
    public ObservableCollection<McpSecretDataContext> ServerSecrets { get; } = [];

    /// <summary>
    ///     Gets or sets the name of a secret to add.
    /// </summary>
    public string? NewSecretName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the tools of the selected server, once it's been tested.
    /// </summary>
    public ObservableCollection<McpToolInfo> ServerTools { get; } = [];

    /// <summary>
    ///     Gets or sets the tool picked to make an agent from.
    /// </summary>
    public McpToolInfo? SelectedTool
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the last server action's outcome.
    /// </summary>
    public string? ServerStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the import list is open.
    /// </summary>
    public bool IsImporting
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the servers other apps have configured, ticked to import.
    /// </summary>
    public ObservableCollection<SelectableItem<McpServerCandidate>> ImportCandidates { get; } = [];

    /// <summary>
    ///     Gets or sets the home directory to look for other apps' configuration under; the user's by default.
    /// </summary>
    public string? ImportHome { get; set; }

    /// <summary>
    ///     Gets the known tools of each server, for the agent form's tool fields.
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> ToolCache => this._toolCache;

    /// <summary>
    ///     Starts a new server.
    /// </summary>
    public void NewServer()
    {
        this.SelectedServer = null;
        this.FillServerForm(null);
    }

    /// <summary>
    ///     Adds a secret row to the form.
    /// </summary>
    public void AddSecret()
    {
        var name = this.NewSecretName?.Trim();
        if (string.IsNullOrEmpty(name) || this.ServerSecrets.Any(s => s.Name == name))
        {
            return;
        }

        this.ServerSecrets.Add(new McpSecretDataContext(name, false));
        this.NewSecretName = null;
    }

    /// <summary>
    ///     Saves the server in the form. Secret values typed into the form, and inline values that look secret, go to
    ///     the secure store; the JSON keeps only <c>{{secret.NAME}}</c> references.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public bool SaveServer()
    {
        McpServerDefinition? saved = null;
        if (!this.Try(() =>
            {
                var server = this.FormServer();
                var secrets = this.ServerSecrets.Where(s => !string.IsNullOrEmpty(s.Value))
                    .ToDictionary(s => s.Name, s => (string?)s.Value);
                server.Secrets = this.ServerSecrets.Select(s => s.Name).ToList();
                saved = this.Shell.Servers.SaveExtractingSecrets(server, secrets);
            }))
        {
            return false;
        }

        this.ServerStatus = $"Saved {saved!.Id}.";
        this.Reload();
        this.SelectedServer = this.ServerList.FirstOrDefault(s => s.Id == saved.Id);
        this.FillServerForm(this.SelectedServer);
        return true;
    }

    /// <summary>
    ///     Removes the selected server and its secrets.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool RemoveServer()
    {
        if (this.SelectedServer is not { } server || !this.Try(() => this.Shell.Servers.Remove(server.Id)))
        {
            return false;
        }

        this.ServerStatus = $"Removed {server.Id}.";
        this.NewServer();
        this.Reload();
        return true;
    }

    /// <summary>
    ///     Connects to the selected server and lists its tools.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task TestServerAsync()
    {
        if (this.SelectedServer is not { } server)
        {
            return;
        }

        this.ServerStatus = $"Connecting to {server.Id}…";
        var tools = await this.LoadToolsAsync(server.Id);
        this.ServerTools.Clear();
        foreach (var tool in tools ?? [])
        {
            this.ServerTools.Add(tool);
        }

        this.SelectedTool = this.ServerTools.FirstOrDefault();
        this.ServerStatus = tools is null ? null : $"{server.Id}: {tools.Count} tool{(tools.Count == 1 ? string.Empty : "s")}.";
    }

    /// <summary>
    ///     Starts a new <c>mcp</c> agent that calls the picked tool, its arguments guessed from the tool's schema, and
    ///     shows it in the agent form to check and save.
    /// </summary>
    /// <returns>True if the agent form now holds it.</returns>
    public bool CreateAgentFromTool()
    {
        if (this.SelectedServer is not { } server || this.SelectedTool is not { } tool)
        {
            return false;
        }

        var agent = AgentStore.AgentForTool(server, tool, [.. this.ServerTools]);
        var baseId = agent.Id;
        for (var number = 2; this.AgentList.Any(a => string.Equals(a.Id, agent.Id, StringComparison.OrdinalIgnoreCase)); number++)
        {
            agent.Id = $"{baseId}-{number}";
        }

        this.SelectedAgent = null;
        this.FillAgentForm(agent);
        this.AgentStatus = $"Drafted {agent.Id} from {server.Id}'s {tool.Name} tool. Check its arguments, then save.";
        return true;
    }

    /// <summary>
    ///     Opens the import list with the servers other apps have configured (the selected repo's project files included).
    /// </summary>
    public void FindImports()
    {
        var problems = new List<string>();
        var candidates = McpConfigSourceRegistry.Instance.FindAll(this.Repo?.Path, home: this.ImportHome, problems: problems);
        this.ImportCandidates.Clear();
        foreach (var candidate in candidates)
        {
            var server = candidate.Server;
            var target = server.Transport == AgentConstants.TransportHttp ? server.Url : server.Command;
            var taken = this.ServerList.Any(s => string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));
            var label = $"{server.Id} — {target} ({candidate.Source}{(taken ? ", replaces the registered one" : string.Empty)})";
            this.ImportCandidates.Add(new SelectableItem<McpServerCandidate>(candidate, label, !taken));
        }

        this.ErrorMessage = problems.Count > 0 ? string.Join(" ", problems) : null;
        this.ServerStatus = candidates.Count == 0 ? "No MCP servers found in Claude Code, Claude Desktop, Cursor or Codex." : null;
        this.IsImporting = candidates.Count > 0;
    }

    /// <summary>
    ///     Imports the ticked servers, moving their secrets to the secure store.
    /// </summary>
    /// <returns>The number imported, or -1 if the import failed.</returns>
    public int ImportServers()
    {
        var picked = this.ImportCandidates.Where(c => c.IsSelected).Select(c => c.Value).ToList();
        IReadOnlyList<McpServerDefinition> imported = [];
        if (!this.Try(() => imported = this.Shell.Servers.Import(picked)))
        {
            return -1;
        }

        this.IsImporting = false;
        this.ImportCandidates.Clear();
        this.ServerStatus = imported.Count == 0 ? "Nothing imported." : $"Imported {string.Join(", ", imported.Select(s => s.Id))}.";
        this.Reload();
        this.SelectedServer = this.ServerList.FirstOrDefault(s => imported.Any(i => i.Id == s.Id));
        return imported.Count;
    }

    /// <summary>
    ///     Closes the import list without importing.
    /// </summary>
    public void CancelImport()
    {
        this.IsImporting = false;
        this.ImportCandidates.Clear();
    }

    /// <summary>
    ///     Loads the registered servers.
    /// </summary>
    private void LoadServers()
    {
        var selectedId = this.SelectedServer?.Id;
        this.ServerList.Clear();
        foreach (var server in this.Shell.Servers.All())
        {
            this.ServerList.Add(server);
        }

        this.SelectedServer = this.ServerList.FirstOrDefault(s => s.Id == selectedId);
        if (this.SelectedServer is null && selectedId is not null)
        {
            this.FillServerForm(null);
        }
    }

    /// <summary>
    ///     Lists a server's tools, remembering them for the agent form.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <returns>The tools, or null when the server couldn't be reached (the error is shown).</returns>
    private async Task<IReadOnlyList<McpToolInfo>?> LoadToolsAsync(string serverId)
    {
        IReadOnlyList<McpToolInfo>? tools = null;
        var directory = this.Repo?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!await this.TryAsync(async () =>
                tools = await this.Shell.Servers.ListToolsAsync(this.Shell.Servers.GetRequired(serverId), directory)))
        {
            return null;
        }

        this._toolCache[serverId] = tools!.Select(t => t.Name).ToList();
        this.AgentForm?.SetTools(this.ToolCache);
        return tools;
    }

    /// <summary>
    ///     Shows a server in the form.
    /// </summary>
    /// <param name="server">The server, or null for a new one.</param>
    private void FillServerForm(McpServerDefinition? server)
    {
        this.ServerId = server?.Id ?? string.Empty;
        this.ServerName = server?.Name ?? string.Empty;
        this.ServerDescription = server?.Description;
        this.ServerTransport = server?.Transport ?? AgentConstants.TransportStdio;
        this.ServerCommand = server?.Command;
        this.ServerArgs = server?.Args is { } args ? string.Join('\n', args) : null;
        this.ServerEnv = server?.Env is { } env ? string.Join('\n', env.Select(e => $"{e.Key}={e.Value}")) : null;
        this.ServerUrl = server?.Url;
        this.ServerHeaders = server?.Headers is { } headers ? string.Join('\n', headers.Select(h => $"{h.Key}={h.Value}")) : null;
        this.ServerSecrets.Clear();
        foreach (var name in server?.Secrets ?? [])
        {
            this.ServerSecrets.Add(new McpSecretDataContext(name, this.Shell.Servers.HasSecret(server!.Id, name)));
        }

        this.ServerTools.Clear();
        this.SelectedTool = null;
    }

    /// <summary>
    ///     Builds the server in the form.
    /// </summary>
    /// <returns>The server.</returns>
    /// <exception cref="FormatException">An environment or header line isn't NAME=value.</exception>
    private McpServerDefinition FormServer()
    {
        var stdio = this.IsStdio;
        return new McpServerDefinition
        {
            Id = this.ServerId.Trim(),
            Name = this.ServerName.Trim(),
            Description = string.IsNullOrWhiteSpace(this.ServerDescription) ? null : this.ServerDescription.Trim(),
            Transport = this.ServerTransport,
            Command = stdio && !string.IsNullOrWhiteSpace(this.ServerCommand) ? this.ServerCommand.Trim() : null,
            Args = stdio ? Lines(this.ServerArgs) is { Count: > 0 } args ? args : null : null,
            Env = stdio ? Pairs(this.ServerEnv, "Environment") : null,
            Url = stdio || string.IsNullOrWhiteSpace(this.ServerUrl) ? null : this.ServerUrl.Trim(),
            Headers = stdio ? null : Pairs(this.ServerHeaders, "Headers"),
            Source = this.SelectedServer?.Id == this.ServerId.Trim() ? this.SelectedServer.Source : null,
        };

        static List<string> Lines(string? text)
        {
            return (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        static Dictionary<string, string>? Pairs(string? text, string label)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Lines(text))
            {
                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    throw new FormatException($"{label}: '{line[..Math.Min(line.Length, 20)]}…' should be NAME=value.");
                }

                pairs[line[..equals].Trim()] = line[(equals + 1)..];
            }

            return pairs.Count > 0 ? pairs : null;
        }
    }
}
