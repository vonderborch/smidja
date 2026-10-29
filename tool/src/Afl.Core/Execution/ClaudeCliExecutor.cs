using Afl.Core.Agents;
using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.Permissions;

namespace Afl.Core.Execution;

/// <summary>
///     Runs AI work through headless Claude Code (<c>claude -p</c>), streaming its events. Headless runs can't answer
///     permission prompts, so tools that need approval are denied; the result lists them so the step can warn that the
///     work may be incomplete. Allow them on the step (<c>allowedTools</c> / <c>permissionMode</c>) or make it
///     interactive. An agent of this kind may set <c>command</c> (another <c>claude</c> install) and <c>args</c> (added to
///     every call).
/// </summary>
[ExecutorRegistration(ExecutorName)]
public class ClaudeCliExecutor() : AbstractProcessExecutor<ClaudeCliExecutor.Turn>(ExecutorName)
{
    /// <summary>
    ///     The executor name.
    /// </summary>
    public const string ExecutorName = "claude";

    /// <summary>
    ///     The name of the session plugin that carries a step's common skills, so they're invoked as
    ///     <c>afl-skills:&lt;id&gt;</c>.
    /// </summary>
    public const string SkillsPluginName = "afl-skills";

    /// <summary>
    ///     Gets the <c>claude</c> executable: <c>AFL_CLAUDE</c> if set, otherwise the agent's command, otherwise
    ///     <c>claude</c>.
    /// </summary>
    protected virtual string Executable =>
        Environment.GetEnvironmentVariable(EnvironmentConstants.ClaudeExecutable) ?? this.Agent.Command ?? "claude";

    /// <inheritdoc />
    public override bool SupportsPermissions => true;

    /// <inheritdoc />
    public override bool SupportsResume => true;

    /// <inheritdoc />
    public override bool CanRunInteractive => true;

    /// <inheritdoc />
    public override bool SupportsMcpServers => true;

    /// <inheritdoc />
    protected override IReadOnlyList<AbstractAgentField> KindFields =>
    [
        new TextAgentField("command", "Command", "Another claude install to run; AFL_CLAUDE overrides it."),
        new ListAgentField("args", "Extra arguments", "Added to every call, one per line."),
        new ListAgentField("mcpServers", "MCP servers", "Servers it always gets as extra tools.", AgentFieldEditors.Servers),
    ];

    /// <inheritdoc />
    public override bool IsAvailable(string searchPath)
    {
        return AgentStore.IsOnPath(this.Executable, searchPath);
    }

    /// <inheritdoc />
    protected override Task PrepareAsync(Turn turn, CancellationToken ct)
    {
        var request = turn.Request;
        turn.SessionId = request.SessionId;
        turn.Plain = turn.Mode.IsWatched;
        var mcpConfig = this.WriteMcpConfig(request, Path.Combine(turn.ScratchDirectory, "mcp.json"));
        var plugin = WriteSkillsPlugin(request, Path.Combine(turn.ScratchDirectory, SkillsPluginName));

        // Someone watching gets claude's own text rather than its event stream; afl already knows the session id.
        List<string> arguments = turn.Plain
            ? ["-p", request.Prompt, "--output-format", "text"]
            : ["-p", request.Prompt, "--output-format", "stream-json", "--verbose"];
        arguments.AddRange(this.BuildSessionArguments(request, mcpConfig, plugin));
        turn.Launch = new ProcessLaunch { Executable = this.Executable, Arguments = arguments, WorkingDirectory = request.WorkingDirectory };

        AppendLog(request, $"--- {DateTimeOffset.Now:u} {(request.Resume ? "resume" : "start")} {request.SessionId}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void OnOutputLine(Turn turn, string line)
    {
        if (turn.Plain)
        {
            turn.Progress?.Report(new ExecutorEvent(ExecutorEventKind.Text, line));
        }
        else
        {
            this.HandleEvent(turn, line);
        }
    }

    /// <inheritdoc />
    protected override Task<ExecutionResult> FinishAsync(Turn turn, ProcessResult result, CancellationToken ct)
    {
        if (turn.Plain && result.Success)
        {
            turn.Output = result.StandardOutput.Trim();
        }

        // The same call can be refused more than once in a turn; the user decides on it once.
        var denied = turn.Denied.DistinctBy(d => d.Display).ToList();
        if (turn.Error is not null || !result.Success || turn.Output is null)
        {
            var reason = turn.Error ?? (string.IsNullOrWhiteSpace(result.StandardError)
                ? $"claude exited with code {result.ExitCode}"
                : result.StandardError.Trim());
            return Task.FromResult(new ExecutionResult(ExecutionStatus.Failed, turn.Output, turn.SessionId, reason, denied));
        }

        return Task.FromResult(new ExecutionResult(ExecutionStatus.Completed, turn.Output, turn.SessionId, null, denied));
    }

    /// <inheritdoc />
    public override InteractiveLaunch? GetInteractiveLaunch(ExecutionRequest request)
    {
        var mcpConfigPath = request.McpConfigPath ?? TurnMcpConfigPath(request);
        var mcpConfig = this.WriteMcpConfig(request, mcpConfigPath);
        var plugin = WriteSkillsPlugin(request, Path.ChangeExtension(mcpConfigPath, null) + "-" + SkillsPluginName);
        var arguments = this.BuildSessionArguments(request, mcpConfig, plugin).ToList();
        arguments.Add(request.Prompt);
        return new InteractiveLaunch
        {
            Executable = this.Executable, Arguments = arguments, WorkingDirectory = request.WorkingDirectory,
        };
    }

    /// <summary>
    ///     Builds the session, permission, tool and model arguments shared by headless and interactive runs.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="mcpConfig">The MCP client configuration file to pass, if any.</param>
    /// <param name="skillsPlugin">The session plugin holding the request's skills, if any.</param>
    /// <returns>The arguments.</returns>
    protected virtual IEnumerable<string> BuildSessionArguments(ExecutionRequest request, string? mcpConfig, string? skillsPlugin = null)
    {
        foreach (var argument in this.Agent.Args ?? [])
        {
            yield return argument;
        }

        yield return request.Resume ? "--resume" : "--session-id";
        yield return request.SessionId;
        yield return "--permission-mode";
        yield return request.PermissionMode;
        if (request.AllowedTools.Count > 0)
        {
            yield return "--allowedTools";
            yield return string.Join(",", request.AllowedTools);
        }

        if (request.DisallowedTools.Count > 0)
        {
            yield return "--disallowedTools";
            yield return string.Join(",", request.DisallowedTools);
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            yield return "--model";
            yield return request.Model;
        }

        foreach (var directory in request.AdditionalDirectories)
        {
            yield return "--add-dir";
            yield return directory;
        }

        if (skillsPlugin is not null)
        {
            yield return "--plugin-dir";
            yield return skillsPlugin;
        }

        if (mcpConfig is not null)
        {
            yield return "--mcp-config";
            yield return mcpConfig;
            if (request.McpServers.Count > 0)
            {
                // The step chose its servers, so Claude gets those (and the agent's own), not whatever else it's set up with.
                yield return "--strict-mcp-config";
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Claude loads the skills itself, from a session plugin written when it's launched
    ///     (<see cref="WriteSkillsPlugin" />); a new conversation is told to invoke them.
    /// </remarks>
    public override void PrepareSkills(ExecutionRequest request)
    {
        if (request.Skills.Count == 0 || request.Resume)
        {
            return;
        }

        var names = string.Join(", ", request.Skills.Select(s => $"`{SkillsPluginName}:{s.Id}`"));
        request.Prompt = $"{request.Prompt.TrimEnd()}\n\nInvoke the {names} skill{(request.Skills.Count == 1 ? string.Empty : "s")} before you start.";
    }

    /// <summary>
    ///     Writes a request's skills as a Claude Code plugin that lasts for one session: a manifest naming it
    ///     <see cref="SkillsPluginName" />, and a <c>skills/&lt;id&gt;/SKILL.md</c> folder per skill.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="directory">The plugin's folder; it is replaced.</param>
    /// <returns>The folder, or null when the request has no skills.</returns>
    protected static string? WriteSkillsPlugin(ExecutionRequest request, string directory)
    {
        if (request.Skills.Count == 0)
        {
            return null;
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        var manifest = Path.Combine(directory, ".claude-plugin", "plugin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = SkillsPluginName,
            ["description"] = "Common skills afl gives this step's agent.",
            ["version"] = "1.0.0",
        }, JsonConstants.Options));
        foreach (var skill in request.Skills)
        {
            skill.WriteFolder(Path.Combine(directory, PathConstants.SkillsDirectoryName, skill.Id));
        }

        return directory;
    }

    /// <summary>
    ///     Reads one line of claude's event stream, keeping the session id, answer, error and denied tools in the turn.
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="line">The line.</param>
    protected virtual void HandleEvent(Turn turn, string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("session_id", out var id) && id.GetString() is { Length: > 0 } reported)
            {
                turn.SessionId = reported;
            }

            switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "assistant":
                    ReportAssistantMessage(root, turn.Progress);
                    break;
                case "result":
                    turn.Output = root.TryGetProperty("result", out var result) ? result.GetString() : null;
                    if (root.TryGetProperty("is_error", out var isError) && isError.GetBoolean())
                    {
                        turn.Error = turn.Output ?? root.GetProperty("subtype").GetString();
                    }

                    if (root.TryGetProperty("permission_denials", out var denials))
                    {
                        turn.Denied.AddRange(denials.EnumerateArray()
                            .Select(d => ToolRuleSuggesterRegistry.Instance.Describe(
                                d.TryGetProperty("tool_name", out var n) ? n.GetString() ?? "?" : "?",
                                d.TryGetProperty("tool_use_id", out var id) ? id.GetString() : null,
                                d.TryGetProperty("tool_input", out var input) ? input : default,
                                turn.Request.WorkingDirectory,
                                turn.Request.DisallowedTools)));
                    }

                    break;
            }
        }
    }

    /// <summary>
    ///     Reports the text and tool calls in an assistant message.
    /// </summary>
    /// <param name="root">The event.</param>
    /// <param name="progress">Receives live events.</param>
    private static void ReportAssistantMessage(JsonElement root, IProgress<ExecutorEvent>? progress)
    {
        if (progress is null || !root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    progress.Report(new ExecutorEvent(ExecutorEventKind.Text, block.GetProperty("text").GetString() ?? string.Empty));
                    break;
                case "tool_use":
                    progress.Report(new ExecutorEvent(ExecutorEventKind.ToolUse, DescribeToolUse(block)));
                    break;
            }
        }
    }

    /// <summary>
    ///     Summarises a tool call in one line, e.g. <c>Read src/app.cs</c>.
    /// </summary>
    /// <param name="block">The tool_use block.</param>
    /// <returns>The summary.</returns>
    private static string DescribeToolUse(JsonElement block)
    {
        var name = block.GetProperty("name").GetString() ?? "tool";
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return name;
        }

        foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "description" })
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()!.ReplaceLineEndings(" ");
                return $"{name} {(text.Length > 120 ? text[..117] + "..." : text)}";
            }
        }

        return name;
    }

    /// <summary>
    ///     What one <c>claude -p</c> turn has learned so far.
    /// </summary>
    public class Turn : ProcessTurn
    {
        /// <summary>
        ///     Gets or sets a value indicating whether claude prints plain text (someone is watching) rather than its
        ///     event stream.
        /// </summary>
        public bool Plain { get; set; }

        /// <summary>
        ///     Gets or sets the conversation id claude reported (the request's until it reports one).
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets the answer, once there is one.
        /// </summary>
        public string? Output { get; set; }

        /// <summary>
        ///     Gets or sets the error claude reported, if any.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        ///     Gets the tools claude was refused.
        /// </summary>
        public List<PermissionDenial> Denied { get; } = [];
    }
}
