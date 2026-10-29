using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Cli.Output;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for agent commands: gives access to the agent store. Agents live only in the afl home, so these commands
///     never register the current repo.
/// </summary>
public abstract class AbstractAgentCommand : AbstractCommand
{
    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <summary>
    ///     Gets the agent store.
    /// </summary>
    protected AgentStore Agents => new(this.Paths);
}

/// <summary>
///     <c>afl agent list</c>.
/// </summary>
[Verb("agent-list", HelpText = "List the AI agents steps can use (built-in and yours).")]
public class AgentListCommand : AbstractAgentCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Agents;
        this.Output.Result(store.All().Select(a => AgentView.From(a, store)).ToList(), TextRendering.AgentList);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl agent show</c>.
/// </summary>
[Verb("agent-show", HelpText = "Show an agent's definition.")]
public class AgentShowCommand : AbstractAgentCommand
{
    /// <summary>
    ///     Gets or sets the agent id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The agent id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.Agents.GetRequired(this.Id), TextRendering.Agent);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl agent detect</c>.
/// </summary>
[Verb("agent-detect", HelpText = "List the agent presets and which of their tools are installed.")]
public class AgentDetectCommand : AbstractAgentCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Agents;
        var added = store.All().Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presets = AgentStore.Detect()
            .Select(p => new AgentPresetView(AgentView.From(p.Agent, store), p.Installed, added.Contains(p.Agent.Id)))
            .ToList();
        this.Output.Result(presets, TextRendering.AgentPresets);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl agent add</c>: adds an agent from a preset, a JSON file, or options; options change whatever the preset
///     or file set.
/// </summary>
[Verb("agent-add", HelpText = "Add or update an agent: afl agent add <preset> [--id x], afl agent add --file agent.json, " +
                              "or afl agent add --id x --kind cli --command tool --arg ...")]
public class AgentAddCommand : AbstractAgentCommand
{
    /// <summary>
    ///     Gets or sets the preset to start from.
    /// </summary>
    [Value(0, MetaName = "preset", HelpText = "A preset to start from (see afl agent detect), e.g. codex.")]
    public string? Preset { get; set; }

    /// <summary>
    ///     Gets or sets a JSON file holding the definition.
    /// </summary>
    [Option('f', "file", HelpText = "A JSON file with the agent definition (the fields of agents.json's agents[]).")]
    public string? File { get; set; }

    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Option("id", HelpText = "The agent id (lowercase, digits and dashes; defaults to the preset's).")]
    public string? Id { get; set; }

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    [Option("name", HelpText = "A display name.")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets the kind.
    /// </summary>
    [Option('k', "kind", HelpText = "cli (any AI command-line tool) or mcp (a tool on an MCP server).")]
    public string? Kind { get; set; }

    /// <summary>
    ///     Gets or sets the command.
    /// </summary>
    [Option('c', "command", HelpText = "The command to run (the AI tool, or the MCP server for stdio).")]
    public string? Command { get; set; }

    /// <summary>
    ///     Gets or sets the arguments.
    /// </summary>
    [Option("arg", HelpText = "An argument (repeat it; write --arg=-x for one starting with a dash); may use {{prompt}}, {{promptFile}}, {{outputFile}}, {{cwd}}, {{model}}, {{session}}.")]
    public IEnumerable<string>? Args { get; set; }

    /// <summary>
    ///     Gets or sets the model.
    /// </summary>
    [Option('m', "model", HelpText = "The model used when a step doesn't set one.")]
    public string? Model { get; set; }

    /// <summary>
    ///     Gets or sets how the prompt is passed.
    /// </summary>
    [Option("prompt-via", HelpText = "cli: arg (default), stdin or file.")]
    public string? PromptVia { get; set; }

    /// <summary>
    ///     Gets or sets the model arguments.
    /// </summary>
    [Option("model-arg", HelpText = "cli: an argument added when there is a model (repeat it).")]
    public IEnumerable<string>? ModelArgs { get; set; }

    /// <summary>
    ///     Gets or sets the resume arguments.
    /// </summary>
    [Option("resume-arg", HelpText = "cli: an argument added when continuing a conversation (repeat it).")]
    public IEnumerable<string>? ResumeArgs { get; set; }

    /// <summary>
    ///     Gets or sets the interactive arguments.
    /// </summary>
    [Option("interactive-arg", HelpText = "cli: an argument that opens the tool interactively (repeat it).")]
    public IEnumerable<string>? InteractiveArgs { get; set; }

    /// <summary>
    ///     Gets or sets the output format.
    /// </summary>
    [Option("output", HelpText = "cli: text (default) or json.")]
    public string? OutputFormat { get; set; }

    /// <summary>
    ///     Gets or sets the output path.
    /// </summary>
    [Option("output-path", HelpText = "The dotted path of the answer in JSON output.")]
    public string? OutputPath { get; set; }

    /// <summary>
    ///     Gets or sets the session path.
    /// </summary>
    [Option("session-path", HelpText = "The dotted path of the conversation id in JSON output.")]
    public string? SessionPath { get; set; }

    /// <summary>
    ///     Gets or sets environment variables.
    /// </summary>
    [Option("env", HelpText = "NAME=value set for the command (repeat it).")]
    public IEnumerable<string>? Env { get; set; }

    /// <summary>
    ///     Gets or sets the MCP server.
    /// </summary>
    [Option("server", HelpText = "mcp: the MCP server id (see afl mcp-list) whose tool the agent calls.")]
    public string? Server { get; set; }

    /// <summary>
    ///     Gets or sets the MCP tool.
    /// </summary>
    [Option("tool", HelpText = "mcp: the tool to call.")]
    public string? Tool { get; set; }

    /// <summary>
    ///     Gets or sets the MCP tool arguments.
    /// </summary>
    [Option("tool-arg", HelpText = "mcp: name=value for the tool (repeat it); string values are templates, JSON values pass as they are.")]
    public IEnumerable<string>? ToolArgs { get; set; }

    /// <summary>
    ///     Gets or sets the capabilities.
    /// </summary>
    [Option("capability", HelpText = "Something the agent is good at (repeat it; see afl capability list), e.g. review. Steps that ask for capabilities pick from the agents that have them.")]
    public IEnumerable<string>? Capabilities { get; set; }

    /// <summary>
    ///     Gets or sets the MCP servers always attached.
    /// </summary>
    [Option("mcp-server", HelpText = "An MCP server (see afl mcp-list) whose tools the agent is always given (repeat it); for agents that can take servers.")]
    public IEnumerable<string>? McpServers { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var agent = this.Start();
        this.ApplyOptions(agent);
        if (string.IsNullOrWhiteSpace(agent.Id))
        {
            throw new AflException(AflErrorKind.InvalidInput, "Give the agent an --id (or start from a preset).");
        }

        var store = this.Agents;
        var saved = store.Save(agent);
        this.Output.Result(AgentView.From(saved, store), (v, w) => w.WriteLine($"Saved agent '{v.Id}' ({v.Kind}). Try it: afl agent test {v.Id}"));
        return Task.FromResult(ExitSuccess);
    }

    /// <summary>
    ///     Returns the definition to start from: the file, the preset, or a blank one.
    /// </summary>
    /// <returns>The definition.</returns>
    private AgentDefinition Start()
    {
        if (this.File is not null)
        {
            return JsonSerializer.Deserialize<AgentDefinition>(ReadTextArgument("@" + this.File.TrimStart('@')), JsonConstants.Options)
                   ?? throw new AflException(AflErrorKind.InvalidInput, $"{this.File} has no agent definition.");
        }

        return this.Preset is null ? new AgentDefinition() : AgentStore.GetPreset(this.Preset);
    }

    /// <summary>
    ///     Applies the options given on the command line over the starting definition.
    /// </summary>
    /// <param name="agent">The definition.</param>
    private void ApplyOptions(AgentDefinition agent)
    {
        agent.Id = this.Id ?? agent.Id;
        agent.Name = this.Name ?? agent.Name;
        agent.Kind = this.Kind ?? (string.IsNullOrEmpty(agent.Kind) ? AgentConstants.KindCli : agent.Kind);
        agent.Command = this.Command ?? agent.Command;
        agent.Model = this.Model ?? agent.Model;
        agent.PromptVia = this.PromptVia ?? agent.PromptVia;
        agent.Output = this.OutputFormat ?? agent.Output;
        agent.OutputPath = this.OutputPath ?? agent.OutputPath;
        agent.SessionPath = this.SessionPath ?? agent.SessionPath;
        agent.Server = this.Server ?? agent.Server;
        agent.Tool = this.Tool ?? agent.Tool;
        agent.Args = Replace(this.Args, agent.Args);
        agent.ModelArgs = Replace(this.ModelArgs, agent.ModelArgs);
        agent.ResumeArgs = Replace(this.ResumeArgs, agent.ResumeArgs);
        agent.InteractiveArgs = Replace(this.InteractiveArgs, agent.InteractiveArgs);
        agent.Capabilities = Replace(this.Capabilities?.Select(c => c.Trim().ToLowerInvariant()), agent.Capabilities);
        agent.McpServers = Replace(this.McpServers, agent.McpServers);
        if (this.Env?.Any() == true)
        {
            agent.Env = this.Env.Select(e => SplitPair(e, "--env")).ToDictionary(p => p.Name, p => p.Value);
        }

        if (this.ToolArgs?.Any() == true)
        {
            agent.Arguments = this.ToolArgs.Select(a => SplitPair(a, "--tool-arg"))
                .ToDictionary(p => p.Name, p => ParseToolValue(p.Value));
        }
    }

    /// <summary>
    ///     Returns the options' list when any were given, otherwise the existing one.
    /// </summary>
    /// <param name="given">The options given.</param>
    /// <param name="existing">The existing list.</param>
    /// <returns>The list.</returns>
    private static List<string>? Replace(IEnumerable<string>? given, List<string>? existing)
    {
        var list = given?.ToList();
        return list is { Count: > 0 } ? list : existing;
    }

    /// <summary>
    ///     Splits <c>name=value</c>.
    /// </summary>
    /// <param name="pair">The text.</param>
    /// <param name="option">The option it came from, for the error.</param>
    /// <returns>The name and value.</returns>
    private static (string Name, string Value) SplitPair(string pair, string option)
    {
        var at = pair.IndexOf('=');
        if (at <= 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, $"{option} takes name=value, not '{pair}'.");
        }

        return (pair[..at], pair[(at + 1)..]);
    }

    /// <summary>
    ///     Reads an MCP tool argument: JSON numbers, booleans, objects and arrays as they are; anything else as a
    ///     template string.
    /// </summary>
    /// <param name="value">The value as typed.</param>
    /// <returns>The JSON value.</returns>
    private static JsonNode? ParseToolValue(string value)
    {
        try
        {
            var node = JsonNode.Parse(value);
            return node is JsonValue { } scalar && scalar.TryGetValue<string>(out _) ? JsonValue.Create(value) : node;
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }
}

/// <summary>
///     <c>afl agent remove</c>.
/// </summary>
[Verb("agent-remove", HelpText = "Remove one of your agents.")]
public class AgentRemoveCommand : AbstractAgentCommand
{
    /// <summary>
    ///     Gets or sets the agent id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The agent id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!this.Agents.Remove(this.Id))
        {
            throw new AflException(AflErrorKind.NotFound, $"No agent '{this.Id}'.");
        }

        this.Output.Result(new { removed = this.Id }, (r, w) => w.WriteLine($"Removed agent '{r.removed}'."));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl agent test</c>: sends an agent a prompt in the current directory and shows its answer.
/// </summary>
[Verb("agent-test", HelpText = "Send an agent a prompt (read-only where it supports that) and show its answer.")]
public class AgentTestCommand : AbstractAgentCommand
{
    /// <summary>
    ///     Gets or sets the agent id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The agent id.")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the prompt.
    /// </summary>
    [Option('p', "prompt", Default = AgentStore.TestPrompt, HelpText = "The prompt (@file or - for stdin).")]
    public string Prompt { get; set; } = AgentStore.TestPrompt;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var agent = this.Agents.GetRequired(this.Id);
        this.Output.Info($"Testing agent '{agent.Name}'...");
        var result = await this.Agents.TestAsync(agent, Environment.CurrentDirectory, ReadTextArgument(this.Prompt), ct);
        var ok = result.Status == ExecutionStatus.Completed;
        this.Output.Result(new { agent = this.Id, ok, output = result.Output, error = result.Error }, (r, w) =>
        {
            w.WriteLine(r.ok ? $"Agent '{r.agent}' answered:" : $"Agent '{r.agent}' failed: {r.error}");
            if (r.output is not null)
            {
                w.WriteLine(r.output);
            }
        });
        return ok ? ExitSuccess : ExitError;
    }
}
