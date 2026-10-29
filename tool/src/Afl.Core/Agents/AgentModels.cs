using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Afl.Core.Storage;

namespace Afl.Core.Agents;

/// <summary>
///     The agents the user has set up (<c>agents.json</c>).
/// </summary>
[DocumentVersion(2, 0)]
public class AgentDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the agents.
    /// </summary>
    public List<AgentDefinition> Agents { get; set; } = [];
}

/// <summary>
///     The embedded presets (<c>agents/presets.json</c>): ready-made agents and the MCP servers they use.
/// </summary>
[DocumentVersion(2, 0)]
public class AgentPresetDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the preset agents.
    /// </summary>
    public List<AgentDefinition> Agents { get; set; } = [];

    /// <summary>
    ///     Gets or sets the preset MCP servers.
    /// </summary>
    public List<Mcp.McpServerDefinition> Servers { get; set; } = [];
}

/// <summary>
///     An AI system afl can hand step work to: Claude Code, any AI command-line tool, or a tool on an MCP server.
///     Which fields apply depends on <see cref="Kind" />; the executor registered for the kind checks them.
///     Argument and MCP-argument templates can use <c>{{prompt}}</c>, <c>{{promptFile}}</c>, <c>{{outputFile}}</c>,
///     <c>{{cwd}}</c>, <c>{{model}}</c> and <c>{{session}}</c>.
/// </summary>
public class AgentDefinition
{
    /// <summary>
    ///     Gets or sets the id, used by steps (<c>agent</c>) and on the command line (<c>--agent</c>).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the kind of agent, which picks its executor: <c>claude</c>, <c>session</c>, <c>cli</c> or
    ///     <c>mcp</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a one-line description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Gets or sets the model used when a step doesn't set one.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    ///     Gets or sets what the agent can do or is good at (capability ids, see <c>afl capability-list</c>). afl adds the
    ///     ones its kind implies (<see cref="Execution.AbstractStepExecutor.ImpliedCapabilities" />).
    /// </summary>
    public List<string>? Capabilities { get; set; }

    /// <summary>
    ///     Gets or sets the command to run: the AI tool (<c>cli</c>) or the Claude Code executable (<c>claude</c>).
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    ///     Gets or sets the command's arguments (templates).
    /// </summary>
    public List<string>? Args { get; set; }

    /// <summary>
    ///     Gets or sets environment variables set for the command.
    /// </summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>
    ///     Gets or sets how the prompt reaches a <c>cli</c> agent: <c>arg</c> (default), <c>stdin</c> or <c>file</c>.
    /// </summary>
    public string? PromptVia { get; set; }

    /// <summary>
    ///     Gets or sets arguments added when there is a model, e.g. <c>["--model", "{{model}}"]</c>.
    /// </summary>
    public List<string>? ModelArgs { get; set; }

    /// <summary>
    ///     Gets or sets arguments added when continuing a conversation, e.g. <c>["--resume", "{{session}}"]</c>. Without
    ///     them, redoing a step starts afresh with the earlier answer and the feedback in the prompt.
    /// </summary>
    public List<string>? ResumeArgs { get; set; }

    /// <summary>
    ///     Gets or sets the arguments that open the tool interactively for interactive steps. Without them, interactive
    ///     steps can't use this agent.
    /// </summary>
    public List<string>? InteractiveArgs { get; set; }

    /// <summary>
    ///     Gets or sets how a <c>cli</c> agent's answer is read: <c>text</c> (default) or <c>json</c>.
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    ///     Gets or sets the dotted path of the answer in JSON output (e.g. <c>result</c> or <c>messages.0.text</c>). For
    ///     JSON lines, the last line that has it wins.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    ///     Gets or sets the dotted path of the conversation id in JSON output, so it can be continued with
    ///     <see cref="ResumeArgs" /> or <see cref="ResumeTool" />.
    /// </summary>
    public string? SessionPath { get; set; }

    /// <summary>
    ///     Gets or sets the MCP server (an id in <c>mcp-servers.json</c>) an <c>mcp</c> agent calls <see cref="Tool" /> on.
    /// </summary>
    public string? Server { get; set; }

    /// <summary>
    ///     Gets or sets MCP servers (ids in <c>mcp-servers.json</c>) given to this agent as extra tools on every step,
    ///     on top of the step's <c>mcpServers</c>. Only agents that can take servers use them.
    /// </summary>
    public List<string>? McpServers { get; set; }

    /// <summary>
    ///     Gets or sets the arguments that hand a <c>cli</c> agent an MCP client configuration file when it's given
    ///     servers, e.g. <c>["--mcp-config", "{{mcpConfigFile}}"]</c>. Without them it can't be given servers.
    /// </summary>
    public List<string>? McpArgs { get; set; }

    /// <summary>
    ///     Gets or sets the MCP tool to call.
    /// </summary>
    public string? Tool { get; set; }

    /// <summary>
    ///     Gets or sets the MCP tool's arguments. String values are templates; a value that renders empty is left out.
    /// </summary>
    public Dictionary<string, JsonNode?>? Arguments { get; set; }

    /// <summary>
    ///     Gets or sets the MCP tool that continues a conversation, if the server has one.
    /// </summary>
    public string? ResumeTool { get; set; }

    /// <summary>
    ///     Gets or sets the arguments of <see cref="ResumeTool" />.
    /// </summary>
    public Dictionary<string, JsonNode?>? ResumeArguments { get; set; }

    /// <summary>
    ///     Gets or sets whether afl defines this agent itself, so it can't be changed or removed.
    /// </summary>
    [JsonIgnore]
    public bool BuiltIn { get; set; }

    /// <summary>
    ///     Makes a deep copy, so edits don't touch the original.
    /// </summary>
    /// <returns>The copy.</returns>
    public AgentDefinition Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Constants.JsonConstants.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<AgentDefinition>(json, Constants.JsonConstants.Options)!;
        copy.BuiltIn = this.BuiltIn;
        return copy;
    }
}

/// <summary>
///     A ready-made agent definition for a well-known AI tool, and whether that tool was found on this machine.
/// </summary>
/// <param name="Agent">The definition to add.</param>
/// <param name="Installed">Whether its command is on <c>PATH</c>.</param>
public record AgentPreset(AgentDefinition Agent, bool Installed);
