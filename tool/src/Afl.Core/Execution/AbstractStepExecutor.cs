using System.Text;
using System.Text.Json;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.Mcp;
using Afl.Core.Registration;
using Afl.Core.Terminals;

namespace Afl.Core.Execution;

/// <summary>
///     Registers an executor with the <see cref="ExecutorRegistry" />.
/// </summary>
/// <param name="kind">The agent kind it runs (<see cref="AgentDefinition.Kind" />).</param>
public class ExecutorRegistrationAttribute(string kind) : AbstractRegistrationAttribute(kind);

/// <summary>
///     Base for the things that do a step's AI work. Each subclass runs one kind of agent (Claude Code, any AI
///     command-line tool, an MCP tool, ...) and is configured by the <see cref="AgentDefinition" /> it is given; a new
///     kind of agent is one subclass with an <see cref="ExecutorRegistrationAttribute" />.
/// </summary>
/// <param name="kind">The agent kind this executor runs.</param>
public abstract class AbstractStepExecutor(string kind)
{
    /// <summary>
    ///     Gets the agent kind this executor runs.
    /// </summary>
    public string Kind { get; } = kind;

    /// <summary>
    ///     Gets or sets the agent this executor runs; a bare agent of its kind until set.
    /// </summary>
    public AgentDefinition Agent { get; set; } = new() { Id = kind, Name = kind, Kind = kind };

    /// <summary>
    ///     Gets or sets the MCP servers the agent can reach, set by <see cref="AgentStore" />; null when the executor
    ///     was created on its own (it then reads the afl home's).
    /// </summary>
    public McpServerStore? Servers { get; set; }

    /// <summary>
    ///     Gets or sets the terminal a run's terminal mode opens for agents that <see cref="CanRunInTerminal" />; null for
    ///     this platform's. Tests set one that runs the script directly.
    /// </summary>
    public AbstractTerminalLauncher? Terminal { get; set; }

    /// <summary>
    ///     Gets the name of the agent, for messages.
    /// </summary>
    public string Name => this.Agent.Id;

    /// <summary>
    ///     Gets whether this agent honours a step's <c>permissionMode</c> and <c>allowedTools</c>.
    /// </summary>
    public virtual bool SupportsPermissions => false;

    /// <summary>
    ///     Gets whether this agent can continue an earlier conversation, so a step sent back keeps its context. Agents
    ///     that can't are given the earlier answer and the feedback in a fresh prompt instead.
    /// </summary>
    public virtual bool SupportsResume => false;

    /// <summary>
    ///     Gets whether this agent can do interactive steps (<see cref="GetInteractiveLaunch" /> opens a session, or the
    ///     agent is itself the interactive session).
    /// </summary>
    public virtual bool CanRunInteractive => false;

    /// <summary>
    ///     Gets whether this agent can be given MCP servers as extra tools for a step.
    /// </summary>
    public virtual bool SupportsMcpServers => false;

    /// <summary>
    ///     Gets whether the agent only works as a run's main agent (the calling Claude session), so it's never handed a
    ///     step chosen by capability or a delegated task when another agent runs the run.
    /// </summary>
    public virtual bool OnlyAsMainAgent => false;

    /// <summary>
    ///     Gets whether the agent runs a command that a run's terminal mode can show in a window you can watch (see
    ///     <see cref="RunModeRegistry" />). Agents that don't ignore the run mode.
    /// </summary>
    public virtual bool CanRunInTerminal => false;

    /// <summary>
    ///     Gets the capabilities afl gives this agent from what its executor can do (<c>interactive</c>, <c>resume</c>,
    ///     <c>permissions</c>, <c>mcp-tools</c>), on top of the ones it declares.
    /// </summary>
    public IReadOnlyList<string> ImpliedCapabilities =>
        new[]
            {
                (this.CanRunInteractive, CapabilityCatalog.Interactive),
                (this.SupportsResume, CapabilityCatalog.Resume),
                (this.SupportsPermissions, CapabilityCatalog.Permissions),
                (this.SupportsMcpServers, CapabilityCatalog.McpTools),
            }
            .Where(c => c.Item1)
            .Select(c => c.Item2)
            .ToList();

    /// <summary>
    ///     Gets every setting of an agent of this kind, as the agent form shows them: the ones all agents share, then
    ///     <see cref="KindFields" />.
    /// </summary>
    public IReadOnlyList<AbstractAgentField> Fields =>
    [
        new TextAgentField("id", "Id", "Used by steps (agent) and on the command line (--agent).") { Required = true },
        new TextAgentField("name", "Name", "The display name."),
        new TextAgentField("description", "Description",
            "What it's good at, in a line. The main agent reads this when it picks an agent for a step."),
        new TextAgentField("model", "Model", "The model used when a step doesn't set one."),
        new ListAgentField("capabilities", "Capabilities",
            "What it can do or is good at; steps that need these can use it.", AgentFieldEditors.Capabilities),
        .. this.KindFields,
    ];

    /// <summary>
    ///     Gets the settings this kind reads, beyond the shared ones.
    /// </summary>
    protected virtual IReadOnlyList<AbstractAgentField> KindFields => [];

    /// <summary>
    ///     Returns whether this agent can run on this machine: its command is installed, its server is registered, ...
    /// </summary>
    /// <param name="searchPath">The directories to look for commands in, separated like <c>PATH</c>.</param>
    /// <returns>True if it's usable here.</returns>
    public virtual bool IsAvailable(string searchPath)
    {
        return true;
    }

    /// <summary>
    ///     Checks an agent definition of this kind, returning what's wrong with it.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The problems; empty when it's usable.</returns>
    public virtual IEnumerable<string> Validate(AgentDefinition agent)
    {
        return [];
    }

    /// <summary>
    ///     Runs one AI turn.
    /// </summary>
    /// <param name="request">What to do.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The outcome.</returns>
    public abstract Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct);

    /// <summary>
    ///     Returns the command that opens an interactive session for a request, or null when this executor can't open
    ///     one (the step's interactive work then happens wherever <see cref="ExecuteAsync" /> sends it).
    /// </summary>
    /// <param name="request">What the session is for.</param>
    /// <returns>The launch command, or null.</returns>
    public virtual InteractiveLaunch? GetInteractiveLaunch(ExecutionRequest request)
    {
        return null;
    }

    /// <summary>
    ///     Gives the agent a request's <see cref="ExecutionRequest.Skills" />, by default by adding their instructions to
    ///     the prompt of a new conversation (one that's resumed already has them). Executors whose agent can load skills
    ///     itself override this, and take the skills from the request when they launch it.
    /// </summary>
    /// <param name="request">The request, whose prompt is changed.</param>
    public virtual void PrepareSkills(ExecutionRequest request)
    {
        if (request.Skills.Count == 0 || request.Resume)
        {
            return;
        }

        var builder = new StringBuilder(request.Prompt.TrimEnd());
        foreach (var skill in request.Skills)
        {
            builder.Append("\n\n## Skill: ").Append(skill.Name).Append("\n\n").Append(skill.Content.Trim());
        }

        request.Prompt = builder.ToString();
    }

    /// <summary>
    ///     Returns the MCP servers the agent gets for a request: the step's, its optional ones, then its own; none when it
    ///     can't take them.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The server ids.</returns>
    public IReadOnlyList<string> McpServerIds(ExecutionRequest request)
    {
        return this.SupportsMcpServers
            ? request.McpServers.Concat(request.OptionalMcpServers).Concat(this.Agent.McpServers ?? [])
                .Distinct(StringComparer.Ordinal).ToList()
            : [];
    }

    /// <summary>
    ///     Returns which of some MCP servers this agent can be given: those that are set up, if it can take servers at all.
    /// </summary>
    /// <param name="ids">The server ids.</param>
    /// <returns>The ids it can be given, in order.</returns>
    public IReadOnlyList<string> UsableMcpServers(IEnumerable<string> ids)
    {
        if (!this.SupportsMcpServers)
        {
            return [];
        }

        var servers = this.Servers ?? new McpServerStore(AflPaths.FromEnvironment());
        return ids.Where(id => servers.Find(id) is not null).ToList();
    }

    /// <summary>
    ///     Writes the MCP client configuration for a request's servers (secrets filled in, readable only by the user),
    ///     if it has any. The caller deletes it with <see cref="DeleteMcpConfig" /> when the agent has finished.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="path">The file to write.</param>
    /// <returns>The file, or null when the agent gets no servers.</returns>
    /// <exception cref="Exceptions.AflException">A server isn't registered or a secret has no value.</exception>
    protected string? WriteMcpConfig(ExecutionRequest request, string path)
    {
        var ids = this.McpServerIds(request);
        if (ids.Count == 0)
        {
            return null;
        }

        var servers = this.Servers ?? new McpServerStore(AflPaths.FromEnvironment());
        servers.WriteClientConfig(ids.Select(servers.GetRequired).ToList(), path);
        return path;
    }

    /// <summary>
    ///     Returns a fresh file for one headless turn's MCP client configuration, beside the step's own.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The path.</returns>
    protected static string TurnMcpConfigPath(ExecutionRequest request)
    {
        var directory = Path.GetDirectoryName(request.McpConfigPath ?? request.LogPath);
        return Path.Combine(string.IsNullOrEmpty(directory) ? Path.GetTempPath() : directory,
            $"afl-{Guid.NewGuid():N}{StepConstants.McpConfigExtension}");
    }

    /// <summary>
    ///     Deletes an MCP client configuration file, if there is one.
    /// </summary>
    /// <param name="path">The file, or null.</param>
    public static void DeleteMcpConfig(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Already gone or in use; there's nothing more to do.
        }
    }

    /// <summary>
    ///     Appends text to a request's log file, if it has one.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="text">The text.</param>
    protected static void AppendLog(ExecutionRequest request, string text)
    {
        if (request.LogPath is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath)!);
        File.AppendAllText(request.LogPath, text + Environment.NewLine);
    }

    /// <summary>
    ///     Returns the values an agent's argument templates can use for a request: <c>prompt</c>, <c>cwd</c>,
    ///     <c>model</c> and <c>session</c> (executors add file paths when they make them).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The values by name.</returns>
    protected static Dictionary<string, string> TemplateValues(ExecutionRequest request)
    {
        return new Dictionary<string, string>
        {
            [AgentConstants.PromptVariable] = request.Prompt,
            [AgentConstants.CwdVariable] = request.WorkingDirectory,
            [AgentConstants.ModelVariable] = request.Model ?? string.Empty,
            [AgentConstants.SessionVariable] = request.SessionId,
        };
    }

    /// <summary>
    ///     Returns the problems with an agent's templates: placeholders that aren't agent variables.
    /// </summary>
    /// <param name="field">The field name, for messages.</param>
    /// <param name="templates">The templates.</param>
    /// <returns>The problems.</returns>
    protected static IEnumerable<string> ValidateTemplates(string field, IEnumerable<string?> templates)
    {
        return templates
            .SelectMany(PromptTemplate.References)
            .Where(name => !AgentConstants.Variables.Contains(name))
            .Distinct()
            .Select(name => $"'{field}' uses '{{{{{name}}}}}', which isn't one of {string.Join(", ", AgentConstants.Variables)}.");
    }

    /// <summary>
    ///     Picks a value out of JSON by a dotted path such as <c>result</c> or <c>messages.0.text</c>.
    /// </summary>
    /// <param name="element">The JSON.</param>
    /// <param name="path">The dotted path; array items are picked by index.</param>
    /// <returns>The value as text (strings as they are, anything else as JSON), or null when the path isn't there.</returns>
    protected static string? SelectJson(JsonElement element, string path)
    {
        var current = element;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var child))
            {
                current = child;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) &&
                     index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => current.GetRawText(),
        };
    }
}

/// <summary>
///     The executors afl knows about, discovered from <see cref="ExecutorRegistrationAttribute" />.
/// </summary>
public class ExecutorRegistry() : TypeRegistry<AbstractStepExecutor, ExecutorRegistrationAttribute>("executor")
{
    /// <summary>
    ///     The executor used when none is chosen.
    /// </summary>
    public const string DefaultExecutor = ClaudeCliExecutor.ExecutorName;

    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static ExecutorRegistry Instance { get; } = new();
}
