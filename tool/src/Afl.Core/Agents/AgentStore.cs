using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Mcp;
using Afl.Core.Storage;
using Afl.Core.Workflows;

namespace Afl.Core.Agents;

/// <summary>
///     The AI agents afl can hand step work to (<c>agents.json</c>), plus the built-in <c>claude</c> and
///     <c>session</c> agents. Agents live only in the afl home, never in a repo, so a cloned repo can't bring commands
///     for afl to run; workflows refer to agents by id.
/// </summary>
/// <param name="paths">The afl home paths.</param>
/// <param name="servers">The MCP server registry; defaults to the afl home's.</param>
public class AgentStore(AflPaths paths, McpServerStore? servers = null) : AbstractJsonStore<AgentDocument>(paths.AgentsFile)
{
    /// <summary>
    ///     The presets, read once from the embedded <c>agents/presets.json</c>.
    /// </summary>
    private static readonly Lazy<AgentPresetDocument> PresetDocument = new(ReadPresets);

    /// <summary>
    ///     The question <see cref="TestAsync" /> asks by default.
    /// </summary>
    public const string TestPrompt = "Reply with the single word: ready";

    /// <summary>
    ///     What the built-in Claude agents are declared to be able to do.
    /// </summary>
    private static readonly string[] ClaudeCapabilities = ["edit-code", "run-commands", "review", "plan", "test", "long-context"];

    /// <summary>
    ///     Gets the agents afl defines itself.
    /// </summary>
    public static IReadOnlyList<AgentDefinition> BuiltIns { get; } =
    [
        new()
        {
            Id = ClaudeCliExecutor.ExecutorName, Name = "Claude Code", Kind = ClaudeCliExecutor.ExecutorName, BuiltIn = true,
            Description = "Headless Claude Code (claude -p); AFL_CLAUDE overrides the executable.",
            Capabilities = [.. ClaudeCapabilities],
        },
        new()
        {
            Id = SessionExecutor.ExecutorName, Name = "Calling Claude session", Kind = SessionExecutor.ExecutorName, BuiltIn = true,
            Description = "The Claude session that called afl (the /afl skill) does the work and submits it.",
            Capabilities = [.. ClaudeCapabilities],
        },
    ];

    /// <summary>
    ///     Gets the ready-made definitions for well-known AI tools.
    /// </summary>
    public static IReadOnlyList<AgentDefinition> Presets => PresetDocument.Value.Agents;

    /// <summary>
    ///     Gets the ready-made MCP servers the presets use.
    /// </summary>
    public static IReadOnlyList<McpServerDefinition> PresetServers => PresetDocument.Value.Servers;

    /// <summary>
    ///     Gets the MCP server registry the agents' servers come from.
    /// </summary>
    public McpServerStore Servers { get; } = servers ?? new McpServerStore(paths);

    /// <summary>
    ///     Gets or sets the directories commands are looked for in (<see cref="IsAvailable" />), separated like
    ///     <c>PATH</c>; null means <c>PATH</c>.
    /// </summary>
    public string? SearchPath { get; set; }

    /// <summary>
    ///     Returns every agent: the built-ins, then the user's by id.
    /// </summary>
    /// <returns>The agents.</returns>
    public IReadOnlyList<AgentDefinition> All()
    {
        return BuiltIns.Select(a => a.Clone())
            .Concat(this.Load().Agents.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    ///     Finds an agent by id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The agent, or null.</returns>
    public AgentDefinition? Find(string id)
    {
        return this.All().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Finds an agent by id, or fails.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The agent.</returns>
    /// <exception cref="AflException">No agent has that id.</exception>
    public AgentDefinition GetRequired(string id)
    {
        return this.Find(id) ?? throw new AflException(AflErrorKind.NotFound,
            $"No agent '{id}'. Known: {string.Join(", ", this.All().Select(a => a.Id))}. Add one with: afl agent add.");
    }

    /// <summary>
    ///     Adds or replaces one of the user's agents.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The saved agent.</returns>
    /// <exception cref="AflException">The agent is invalid or its id is a built-in's.</exception>
    public AgentDefinition Save(AgentDefinition agent)
    {
        if (BuiltIns.Any(b => string.Equals(b.Id, agent.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"'{agent.Id}' is a built-in agent; pick another id.");
        }

        this.AddPresetServer(agent);
        var problems = Validate(agent, this.Servers);
        if (problems.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, $"Agent '{agent.Id}' is invalid: {string.Join(" ", problems)}");
        }

        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            agent.Name = agent.Id;
        }

        return this.Update(document =>
        {
            var existing = document.Agents.FindIndex(a => string.Equals(a.Id, agent.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                document.Agents[existing] = agent;
            }
            else
            {
                document.Agents.Add(agent);
            }

            return agent;
        });
    }

    /// <summary>
    ///     Removes one of the user's agents.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True if it existed.</returns>
    /// <exception cref="AflException">The agent is a built-in.</exception>
    public bool Remove(string id)
    {
        if (BuiltIns.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"'{id}' is a built-in agent and can't be removed.");
        }

        return this.Update(document =>
            document.Agents.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    /// <summary>
    ///     Creates the executor that runs an agent.
    /// </summary>
    /// <param name="id">The agent id.</param>
    /// <returns>The executor, configured with the agent.</returns>
    /// <exception cref="AflException">No agent has that id.</exception>
    public AbstractStepExecutor CreateExecutor(string id)
    {
        return CreateExecutor(this.GetRequired(id), this.Servers);
    }

    /// <summary>
    ///     Creates the executor that runs an agent definition (e.g. to test one before saving it).
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="servers">The MCP server registry; null to use the afl home's.</param>
    /// <returns>The executor, configured with the agent.</returns>
    /// <exception cref="AflException">The agent's kind is unknown.</exception>
    public static AbstractStepExecutor CreateExecutor(AgentDefinition agent, McpServerStore? servers = null)
    {
        var executor = ExecutorRegistry.Instance.Create(agent.Kind);
        executor.Agent = agent;
        executor.Servers = servers;
        return executor;
    }

    /// <summary>
    ///     Returns an agent's capabilities: the ones it declares, then the ones its kind implies.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The capability ids.</returns>
    public IReadOnlyList<string> CapabilitiesOf(AgentDefinition agent)
    {
        var implied = ExecutorRegistry.Instance.Contains(agent.Kind)
            ? CreateExecutor(agent, this.Servers).ImpliedCapabilities
            : [];
        return (agent.Capabilities ?? []).Concat(implied).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Returns whether an agent can run on this machine: its command is installed or its MCP server is registered.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>True if it's usable here.</returns>
    public bool IsAvailable(AgentDefinition agent)
    {
        return ExecutorRegistry.Instance.Contains(agent.Kind) &&
               CreateExecutor(agent, this.Servers).IsAvailable(this.SearchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
    }

    /// <summary>
    ///     Returns the settings an agent of a kind has, as the agent form shows them.
    /// </summary>
    /// <param name="kind">The agent kind.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="AflException">The kind is unknown.</exception>
    public static IReadOnlyList<AbstractAgentField> FieldsOf(string kind)
    {
        return ExecutorRegistry.Instance.Create(kind).Fields;
    }

    /// <summary>
    ///     Asks an agent a small read-only question to check that it works.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="workingDirectory">The directory to run it in.</param>
    /// <param name="prompt">The question; defaults to <see cref="TestPrompt" />.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The agent's answer.</returns>
    /// <exception cref="AflException">The agent is the session agent, which can't be called from afl.</exception>
    public Task<ExecutionResult> TestAsync(AgentDefinition agent, string workingDirectory, string? prompt = null,
        CancellationToken ct = default)
    {
        var executor = CreateExecutor(agent, this.Servers);
        if (executor.Kind == SessionExecutor.ExecutorName)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                "The session agent is the Claude session that calls afl; there's nothing to test.");
        }

        var request = new ExecutionRequest
        {
            Prompt = prompt ?? TestPrompt,
            WorkingDirectory = workingDirectory,
            SessionId = Guid.NewGuid().ToString(),
            PermissionMode = StepConstants.ReadOnlyPermissionMode,
            Model = agent.Model,
        };
        return executor.ExecuteAsync(request, null, ct);
    }

    /// <summary>
    ///     Checks an agent: its id, its kind, and whatever its kind's executor needs.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="servers">The MCP server registry to check server references against; null to skip that.</param>
    /// <returns>The problems; empty when it's usable.</returns>
    public static IReadOnlyList<string> Validate(AgentDefinition agent, McpServerStore? servers = null)
    {
        var problems = new List<string>();
        if (!WorkflowLibrary.IsValidId(agent.Id))
        {
            problems.Add($"'{agent.Id}' isn't a usable agent id. Use lowercase letters, digits and dashes.");
        }

        if (!ExecutorRegistry.Instance.Contains(agent.Kind))
        {
            problems.Add($"kind must be one of {string.Join(", ", ExecutorRegistry.Instance.Keys.Order())}.");
            return problems;
        }

        foreach (var capability in (agent.Capabilities ?? []).Where(c => !WorkflowLibrary.IsValidId(c)))
        {
            problems.Add($"'{capability}' isn't a usable capability id. Use lowercase letters, digits and dashes.");
        }

        var executor = ExecutorRegistry.Instance.Create(agent.Kind);
        executor.Servers = servers;
        executor.Agent = agent;
        if (agent.McpServers is { Count: > 0 } && !executor.SupportsMcpServers)
        {
            problems.Add($"this {agent.Kind} agent can't be given MCP servers (a cli agent needs mcpArgs).");
        }

        foreach (var server in (agent.McpServers ?? []).Where(s => servers is not null && servers.Find(s) is null))
        {
            problems.Add($"there's no MCP server '{server}'. Add it with afl mcp-add or afl mcp-import.");
        }

        problems.AddRange(executor.Validate(agent));
        return problems;
    }

    /// <summary>
    ///     Finds a preset by id.
    /// </summary>
    /// <param name="id">The preset id, e.g. <c>codex</c>.</param>
    /// <returns>A copy of the preset.</returns>
    /// <exception cref="AflException">There is no such preset.</exception>
    public static AgentDefinition GetPreset(string id)
    {
        var preset = Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                     ?? throw new AflException(AflErrorKind.NotFound,
                         $"No preset '{id}'. Presets: {string.Join(", ", Presets.Select(p => p.Id))}.");
        return preset.Clone();
    }

    /// <summary>
    ///     Lists the presets, saying which ones' commands are installed.
    /// </summary>
    /// <param name="searchPath">The directories to search, separated like <c>PATH</c>; defaults to <c>PATH</c>.</param>
    /// <returns>The presets.</returns>
    public static IReadOnlyList<AgentPreset> Detect(string? searchPath = null)
    {
        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return Presets.Select(p => new AgentPreset(p.Clone(), CommandOf(p) is { } command && IsOnPath(command, path))).ToList();
    }

    /// <summary>
    ///     What afl guesses each tool argument should get, by the argument's name (any case): the prompt, the working
    ///     directory, the model or the conversation to continue.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ToolArgumentGuesses =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = "{{prompt}}", ["message"] = "{{prompt}}", ["query"] = "{{prompt}}", ["input"] = "{{prompt}}",
            ["cwd"] = "{{cwd}}", ["workdir"] = "{{cwd}}", ["directory"] = "{{cwd}}",
            ["model"] = "{{model}}",
            ["threadId"] = "{{session}}", ["sessionId"] = "{{session}}", ["conversationId"] = "{{session}}",
        };

    /// <summary>
    ///     Drafts an <c>mcp</c> agent that calls a server's tool, guessing its arguments from their names
    ///     (<see cref="ToolArgumentGuesses" />). A tool that takes a conversation id is used to continue instead; when
    ///     the server has another one that takes a prompt and a conversation id, it becomes the resume tool.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="tool">The tool to call.</param>
    /// <param name="tools">All the server's tools, to find a resume tool among.</param>
    /// <returns>The agent, not yet saved.</returns>
    public static AgentDefinition AgentForTool(McpServerDefinition server, McpToolInfo tool, IReadOnlyList<McpToolInfo> tools)
    {
        var resume = tools.FirstOrDefault(t => t.Name != tool.Name && Guesses(t).ContainsValue("{{session}}") &&
                                               Guesses(t).ContainsValue("{{prompt}}"));
        var arguments = Guesses(tool);
        arguments.Remove(arguments.FirstOrDefault(a => a.Value == "{{session}}").Key ?? string.Empty);
        return new AgentDefinition
        {
            Id = McpServerStore.IdFrom($"{server.Id}-{tool.Name}"),
            Name = $"{server.Name}: {tool.Name}",
            Kind = AgentConstants.KindMcp,
            Description = tool.Description?.Split('\n')[0].Trim(),
            Server = server.Id,
            Tool = tool.Name,
            Arguments = arguments.Count > 0 ? arguments.ToDictionary(a => a.Key, a => (JsonNode?)JsonValue.Create(a.Value)) : null,
            ResumeTool = resume?.Name,
            ResumeArguments = resume is null ? null : Guesses(resume).ToDictionary(a => a.Key, a => (JsonNode?)JsonValue.Create(a.Value)),
        };

        static Dictionary<string, string> Guesses(McpToolInfo tool)
        {
            return tool.ArgumentNames.Where(ToolArgumentGuesses.ContainsKey).ToDictionary(n => n, n => ToolArgumentGuesses[n]);
        }
    }

    /// <summary>
    ///     Finds a preset MCP server by id.
    /// </summary>
    /// <param name="id">The server id, e.g. <c>codex</c>.</param>
    /// <returns>A copy of the server, or null.</returns>
    public static McpServerDefinition? FindPresetServer(string id)
    {
        return PresetServers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();
    }

    /// <summary>
    ///     Returns whether a command can be found: a path that exists, or a name in one of the search directories.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="searchPath">The directories to search, separated like <c>PATH</c>.</param>
    /// <returns>True if found.</returns>
    public static bool IsOnPath(string command, string searchPath)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command);
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { string.Empty }.Concat((Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';'))
            : [string.Empty];
        return searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => extensions.Select(extension => Path.Combine(directory, command + extension)))
            .Any(File.Exists);
    }

    /// <summary>
    ///     Adds the preset server an agent refers to, if it isn't registered yet (e.g. when adding the
    ///     <c>codex-mcp</c> preset).
    /// </summary>
    /// <param name="agent">The agent.</param>
    private void AddPresetServer(AgentDefinition agent)
    {
        if (agent.Server is { Length: > 0 } id && this.Servers.Find(id) is null && FindPresetServer(id) is { } preset)
        {
            this.Servers.Save(preset);
        }
    }

    /// <summary>
    ///     Returns the command a preset needs installed: its own, or its MCP server's.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The command, or null.</returns>
    private static string? CommandOf(AgentDefinition preset)
    {
        return preset.Command ?? (preset.Server is { } server ? FindPresetServer(server)?.Command : null);
    }

    /// <summary>
    ///     Reads the presets embedded from the smidja repo's <c>agents/presets.json</c>.
    /// </summary>
    /// <returns>The presets.</returns>
    private static AgentPresetDocument ReadPresets()
    {
        using var stream = typeof(AgentStore).Assembly.GetManifestResourceStream(PathConstants.AgentPresetsResourceName);
        if (stream is null)
        {
            return new AgentPresetDocument();
        }

        return JsonSerializer.Deserialize<AgentPresetDocument>(stream, JsonConstants.Options) ?? new AgentPresetDocument();
    }
}
