using System.Text.Json;
using System.Text.RegularExpressions;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Helpers;

namespace Afl.Core.Execution;

/// <summary>
///     Runs AI work through any AI command-line tool (codex, gemini, aider, ollama, ...), as the agent describes: the
///     arguments are templates, the prompt goes in an argument, on stdin or in a file, and the answer is the tool's
///     output (or a file it writes, or a value picked from its JSON). The command runs directly, never through a shell.
/// </summary>
[ExecutorRegistration(AgentConstants.KindCli)]
public partial class CommandLineExecutor() : AbstractProcessExecutor<CommandLineExecutor.Turn>(AgentConstants.KindCli)
{
    /// <inheritdoc />
    public override bool SupportsResume => this.Agent.ResumeArgs is { Count: > 0 };

    /// <inheritdoc />
    public override bool CanRunInteractive => this.Agent.InteractiveArgs is { Count: > 0 };

    /// <inheritdoc />
    public override bool SupportsMcpServers => this.Agent.McpArgs is { Count: > 0 };

    /// <summary>
    ///     Gets how the prompt reaches the command.
    /// </summary>
    protected string PromptVia => this.Agent.PromptVia ?? AgentConstants.PromptViaArg;

    /// <inheritdoc />
    protected override IReadOnlyList<AbstractAgentField> KindFields =>
    [
        new TextAgentField("command", "Command", "The AI tool to run, e.g. codex or /usr/local/bin/aider.") { Required = true },
        new ListAgentField("args", "Arguments",
            "One per line. May use {{prompt}}, {{promptFile}}, {{outputFile}}, {{cwd}}, {{model}} and {{session}}."),
        new TextAgentField("promptVia", "Prompt goes", "How the prompt reaches the command: as an argument, on stdin or in a file.",
            AgentFieldEditors.Choice) { Options = AgentConstants.PromptVias },
        new TextAgentField("output", "Answer is", "How the answer is read: plain text or JSON.", AgentFieldEditors.Choice)
        {
            Options = AgentConstants.Outputs,
        },
        new TextAgentField("outputPath", "Answer path", "Where the answer is in JSON output, e.g. result or messages.0.text."),
        new TextAgentField("sessionPath", "Session path", "Where the conversation id is in JSON output, so it can be continued."),
        new ListAgentField("modelArgs", "Model arguments", "Added when there's a model, e.g. --model then {{model}}."),
        new ListAgentField("resumeArgs", "Resume arguments",
            "Added when continuing a conversation, e.g. --resume then {{session}}. Without them a step sent back starts afresh."),
        new ListAgentField("interactiveArgs", "Interactive arguments",
            "Open the tool interactively for interactive steps. Without them it can't do interactive steps."),
        new ListAgentField("mcpArgs", "MCP arguments",
            "Hand the tool MCP servers, e.g. --mcp-config then {{mcpConfigFile}}. Without them it can't be given servers."),
        new ListAgentField("mcpServers", "MCP servers", "Servers it always gets as extra tools.", AgentFieldEditors.Servers),
        new MapAgentField("env", "Environment", "Variables set for the command, NAME=value per line."),
    ];

    /// <inheritdoc />
    public override bool IsAvailable(string searchPath)
    {
        return this.Agent.Command is { Length: > 0 } command && AgentStore.IsOnPath(command, searchPath);
    }

    /// <inheritdoc />
    public override IEnumerable<string> Validate(AgentDefinition agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Command))
        {
            yield return "a cli agent needs a 'command'.";
        }

        if (agent.PromptVia is not null && !AgentConstants.PromptVias.Contains(agent.PromptVia))
        {
            yield return $"promptVia must be one of {string.Join(", ", AgentConstants.PromptVias)}.";
        }

        if (agent.Output is not null && !AgentConstants.Outputs.Contains(agent.Output))
        {
            yield return $"output must be one of {string.Join(", ", AgentConstants.Outputs)}.";
        }

        if (agent.Output == AgentConstants.OutputJson && string.IsNullOrWhiteSpace(agent.OutputPath) &&
            !UsesVariable(agent.Args, AgentConstants.OutputFileVariable))
        {
            yield return "JSON output needs an 'outputPath' saying where the answer is.";
        }

        foreach (var name in (agent.Env ?? []).Keys.Where(n => !EnvironmentNameRegex().IsMatch(n)))
        {
            yield return $"'{name}' isn't a usable environment variable name.";
        }

        var problems = ValidateTemplates("args", agent.Args ?? [])
            .Concat(ValidateTemplates("modelArgs", agent.ModelArgs ?? []))
            .Concat(ValidateTemplates("resumeArgs", agent.ResumeArgs ?? []))
            .Concat(ValidateTemplates("interactiveArgs", agent.InteractiveArgs ?? []))
            .Concat(ValidateTemplates("mcpArgs", agent.McpArgs ?? []))
            .Concat(ValidateTemplates("env", (agent.Env ?? []).Values));
        foreach (var problem in problems)
        {
            yield return problem;
        }
    }

    /// <inheritdoc />
    protected override async Task PrepareAsync(Turn turn, CancellationToken ct)
    {
        var agent = this.Agent;
        var request = turn.Request;
        var values = TemplateValues(request);
        values[AgentConstants.PromptFileVariable] = Path.Combine(turn.ScratchDirectory, "prompt.md");
        values[AgentConstants.OutputFileVariable] = Path.Combine(turn.ScratchDirectory, "output.txt");
        var mcpConfig = this.WriteMcpConfig(request, Path.Combine(turn.ScratchDirectory, "mcp.json"));
        values[AgentConstants.McpConfigFileVariable] = mcpConfig ?? string.Empty;

        var templates = this.ArgumentTemplates(agent.Args, request, mcpConfig is not null).ToList();
        var arguments = templates.Select(t => PromptTemplate.Render(t, values)).ToList();
        if (this.PromptVia == AgentConstants.PromptViaFile || UsesVariable(templates, AgentConstants.PromptFileVariable))
        {
            await File.WriteAllTextAsync(values[AgentConstants.PromptFileVariable], request.Prompt, ct);
        }

        this.AppendPrompt(arguments, templates, values);
        turn.Values = values;
        turn.Templates = templates;
        turn.Launch = new ProcessLaunch
        {
            Executable = agent.Command!,
            Arguments = arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = this.RenderEnvironment(values),
            StandardInput = this.PromptVia == AgentConstants.PromptViaStdin ? request.Prompt : null,
        };
        AppendLog(request, $"--- {DateTimeOffset.Now:u} {agent.Id}: {agent.Command} {string.Join(" ", arguments)}");
    }

    /// <inheritdoc />
    protected override void OnOutputLine(Turn turn, string line)
    {
        if (this.Agent.Output != AgentConstants.OutputJson)
        {
            turn.Progress?.Report(new ExecutorEvent(ExecutorEventKind.Text, line));
        }
    }

    /// <inheritdoc />
    protected override async Task<ExecutionResult> FinishAsync(Turn turn, ProcessResult result, CancellationToken ct)
    {
        var agent = this.Agent;
        var request = turn.Request;
        if (!result.Success)
        {
            var reason = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"{agent.Command} exited with code {result.ExitCode}."
                : result.StandardError.Trim();
            AppendLog(request, reason);
            return new ExecutionResult(ExecutionStatus.Failed, null, request.SessionId, reason);
        }

        var sessionId = agent.SessionPath is null ? null : PickFromJson(result.StandardOutput, agent.SessionPath);
        var output = await this.ReadOutputAsync(result.StandardOutput, turn.Values, turn.Templates, ct);
        if (output is null)
        {
            return new ExecutionResult(ExecutionStatus.Failed, null, sessionId ?? request.SessionId,
                $"{agent.Command}'s output had no '{agent.OutputPath}'.");
        }

        return new ExecutionResult(ExecutionStatus.Completed, output, sessionId ?? request.SessionId);
    }

    /// <inheritdoc />
    public override InteractiveLaunch? GetInteractiveLaunch(ExecutionRequest request)
    {
        if (!this.CanRunInteractive)
        {
            return null;
        }

        var values = TemplateValues(request);
        var promptFile = Path.Combine(Path.GetTempPath(), $"afl-{request.SessionId}.prompt.md");
        values[AgentConstants.PromptFileVariable] = promptFile;
        values[AgentConstants.OutputFileVariable] = string.Empty;
        var mcpConfig = this.WriteMcpConfig(request, request.McpConfigPath ?? TurnMcpConfigPath(request));
        values[AgentConstants.McpConfigFileVariable] = mcpConfig ?? string.Empty;
        var templates = this.ArgumentTemplates(this.Agent.InteractiveArgs, request, mcpConfig is not null).ToList();
        if (UsesVariable(templates, AgentConstants.PromptFileVariable))
        {
            File.WriteAllText(promptFile, request.Prompt);
        }

        return new InteractiveLaunch
        {
            Executable = this.Agent.Command!,
            Arguments = templates.Select(t => PromptTemplate.Render(t, values)).ToList(),
            WorkingDirectory = request.WorkingDirectory,
            Environment = this.RenderEnvironment(values),
        };
    }

    /// <summary>
    ///     Returns the argument templates for a call: the base ones, then the model ones when there is a model, then the
    ///     resume ones when continuing a conversation, then the MCP ones when it's given servers.
    /// </summary>
    /// <param name="baseArguments">The agent's arguments for this kind of call.</param>
    /// <param name="request">The request.</param>
    /// <param name="withMcpServers">Whether an MCP client configuration was written for the call.</param>
    /// <returns>The templates.</returns>
    protected virtual IEnumerable<string> ArgumentTemplates(List<string>? baseArguments, ExecutionRequest request, bool withMcpServers)
    {
        var templates = new List<string>(baseArguments ?? []);
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            templates.AddRange(this.Agent.ModelArgs ?? []);
        }

        if (request.Resume)
        {
            templates.AddRange(this.Agent.ResumeArgs ?? []);
        }

        if (withMcpServers)
        {
            templates.AddRange(this.Agent.McpArgs ?? []);
        }

        return templates;
    }

    /// <summary>
    ///     Adds the prompt (or its file) as the last argument when it goes in the arguments and no template placed it.
    /// </summary>
    /// <param name="arguments">The rendered arguments.</param>
    /// <param name="templates">The templates they came from.</param>
    /// <param name="values">The template values.</param>
    protected virtual void AppendPrompt(List<string> arguments, List<string> templates, Dictionary<string, string> values)
    {
        if (this.PromptVia == AgentConstants.PromptViaArg && !UsesVariable(templates, AgentConstants.PromptVariable))
        {
            arguments.Add(values[AgentConstants.PromptVariable]);
        }
        else if (this.PromptVia == AgentConstants.PromptViaFile && !UsesVariable(templates, AgentConstants.PromptFileVariable))
        {
            arguments.Add(values[AgentConstants.PromptFileVariable]);
        }
    }

    /// <summary>
    ///     Reads the answer: the output file when the arguments asked the tool to write one, otherwise the value at
    ///     <see cref="AgentDefinition.OutputPath" /> for JSON output, otherwise standard output as it is.
    /// </summary>
    /// <param name="standardOutput">What the tool wrote to stdout.</param>
    /// <param name="values">The template values.</param>
    /// <param name="templates">The argument templates.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The answer, or null when the JSON didn't have it.</returns>
    protected virtual async Task<string?> ReadOutputAsync(
        string standardOutput,
        Dictionary<string, string> values,
        List<string> templates,
        CancellationToken ct)
    {
        var outputFile = values[AgentConstants.OutputFileVariable];
        var text = UsesVariable(templates, AgentConstants.OutputFileVariable) && File.Exists(outputFile)
            ? await File.ReadAllTextAsync(outputFile, ct)
            : standardOutput;
        if (this.Agent.Output == AgentConstants.OutputJson && !string.IsNullOrWhiteSpace(this.Agent.OutputPath))
        {
            return PickFromJson(text, this.Agent.OutputPath);
        }

        return text.Trim();
    }

    /// <summary>
    ///     Renders the agent's environment variables.
    /// </summary>
    /// <param name="values">The template values.</param>
    /// <returns>The variables, or null when there are none.</returns>
    protected Dictionary<string, string>? RenderEnvironment(Dictionary<string, string> values)
    {
        return this.Agent.Env is { Count: > 0 } env
            ? env.ToDictionary(e => e.Key, e => PromptTemplate.Render(e.Value, values))
            : null;
    }

    /// <summary>
    ///     Picks a value from JSON output: the whole text as one document, or failing that, each line as JSON (the last
    ///     line that has the value wins).
    /// </summary>
    /// <param name="text">The output.</param>
    /// <param name="path">The dotted path.</param>
    /// <returns>The value, or null.</returns>
    protected static string? PickFromJson(string text, string path)
    {
        var whole = ParseJson(text);
        if (whole is { } document)
        {
            return SelectJson(document, path);
        }

        string? found = null;
        foreach (var line in text.Split('\n'))
        {
            if (ParseJson(line) is { } element && SelectJson(element, path) is { } value)
            {
                found = value;
            }
        }

        return found;
    }

    /// <summary>
    ///     Returns whether any template uses a variable.
    /// </summary>
    /// <param name="templates">The templates.</param>
    /// <param name="variable">The variable name.</param>
    /// <returns>True if one does.</returns>
    protected static bool UsesVariable(IEnumerable<string>? templates, string variable)
    {
        return (templates ?? []).Any(t => PromptTemplate.References(t).Contains(variable));
    }

    /// <summary>
    ///     Parses text as JSON.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The root element, or null when it isn't JSON.</returns>
    private static JsonElement? ParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     One turn's rendered template values and argument templates, which say where its answer is.
    /// </summary>
    public class Turn : ProcessTurn
    {
        /// <summary>
        ///     Gets or sets the template values (prompt, files, model, session and so on).
        /// </summary>
        public Dictionary<string, string> Values { get; set; } = [];

        /// <summary>
        ///     Gets or sets the argument templates the command ran with.
        /// </summary>
        public List<string> Templates { get; set; } = [];
    }

    /// <summary>
    ///     Matches a valid environment variable name.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentNameRegex();
}
