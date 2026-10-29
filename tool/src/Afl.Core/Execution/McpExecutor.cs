using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Afl.Core.Execution;

/// <summary>
///     Runs AI work through a tool on an MCP server (e.g. <c>codex mcp-server</c>'s <c>codex</c> tool): afl connects as
///     an MCP client to the agent's <see cref="AgentDefinition.Server" /> (from the server registry), and calls its tool
///     with its arguments rendered for the step. The answer is the tool's text, or a value picked from its structured
///     result.
/// </summary>
[ExecutorRegistration(AgentConstants.KindMcp)]
public class McpExecutor() : AbstractStepExecutor(AgentConstants.KindMcp)
{
    /// <inheritdoc />
    public override bool SupportsResume => !string.IsNullOrWhiteSpace(this.Agent.ResumeTool);

    /// <inheritdoc />
    protected override IReadOnlyList<AbstractAgentField> KindFields =>
    [
        new TextAgentField("server", "MCP server", "The registered MCP server whose tool it calls.", AgentFieldEditors.Server)
        {
            Required = true,
        },
        new TextAgentField("tool", "Tool", "The tool to call.", AgentFieldEditors.Tool) { Required = true, DependsOn = "server" },
        new JsonAgentField("arguments", "Arguments",
            "The tool's arguments. Strings may use {{prompt}}, {{cwd}}, {{model}} and {{session}}; empty ones are left out."),
        new TextAgentField("resumeTool", "Resume tool", "The tool that continues a conversation, if the server has one.",
            AgentFieldEditors.Tool) { DependsOn = "server" },
        new JsonAgentField("resumeArguments", "Resume arguments", "The resume tool's arguments."),
        new TextAgentField("outputPath", "Answer path", "Where the answer is in the tool's structured result; defaults to its text."),
        new TextAgentField("sessionPath", "Session path", "Where the conversation id is in the tool's structured result."),
    ];

    /// <inheritdoc />
    public override bool IsAvailable(string searchPath)
    {
        if (string.IsNullOrWhiteSpace(this.Agent.Server))
        {
            return false;
        }

        var server = (this.Servers ?? new McpServerStore(AflPaths.FromEnvironment())).Find(this.Agent.Server);
        return server is not null && (server.Transport == AgentConstants.TransportHttp ||
                                      (server.Command is { Length: > 0 } command && AgentStore.IsOnPath(command, searchPath)));
    }

    /// <inheritdoc />
    public override IEnumerable<string> Validate(AgentDefinition agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Server))
        {
            yield return "an MCP agent needs the 'server' (an MCP server id; see afl mcp-list) whose tool it calls.";
        }
        else if (this.Servers is { } servers && servers.Find(agent.Server) is null)
        {
            yield return $"there's no MCP server '{agent.Server}'. Add it with afl mcp-add or afl mcp-import.";
        }

        if (string.IsNullOrWhiteSpace(agent.Tool))
        {
            yield return "an MCP agent needs the 'tool' to call.";
        }

        var problems = ValidateTemplates("arguments", StringValues(agent.Arguments))
            .Concat(ValidateTemplates("resumeArguments", StringValues(agent.ResumeArguments)));
        foreach (var problem in problems)
        {
            yield return problem;
        }
    }

    /// <inheritdoc />
    public override async Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct)
    {
        var agent = this.Agent;
        var resume = request.Resume && this.SupportsResume;
        var tool = resume ? agent.ResumeTool! : agent.Tool!;
        var arguments = RenderArguments(resume ? agent.ResumeArguments : agent.Arguments, TemplateValues(request));
        AppendLog(request, $"--- {DateTimeOffset.Now:u} {agent.Id}: {tool} {JsonSerializer.Serialize(arguments)}");

        CallToolResult result;
        try
        {
            await using var client = await this.ConnectAsync(request, ct);
            var reporter = progress is null
                ? null
                : new Progress<ProgressNotificationValue>(p =>
                {
                    if (!string.IsNullOrWhiteSpace(p.Message))
                    {
                        progress.Report(new ExecutorEvent(ExecutorEventKind.Status, p.Message));
                    }
                });
            result = await client.CallToolAsync(tool, arguments, reporter, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AflException)
        {
            AppendLog(request, ex.ToString());
            throw new AflException(AflErrorKind.ExternalTool, $"MCP agent '{agent.Id}' failed: {ex.Message}", ex);
        }

        var text = string.Join("\n\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text)).Trim();
        AppendLog(request, text);
        JsonElement? structured = result.StructuredContent;
        var sessionId = agent.SessionPath is not null && structured is { } forSession
            ? SelectJson(forSession, agent.SessionPath)
            : null;
        sessionId ??= resume ? request.SessionId : null;

        if (result.IsError == true)
        {
            return new ExecutionResult(ExecutionStatus.Failed, null, sessionId ?? request.SessionId,
                text.Length > 0 ? text : $"MCP tool '{tool}' reported an error.");
        }

        var output = agent.OutputPath is not null && structured is { } forOutput
            ? SelectJson(forOutput, agent.OutputPath)
            : text;
        return output is null
            ? new ExecutionResult(ExecutionStatus.Failed, null, sessionId ?? request.SessionId,
                $"MCP tool '{tool}' returned no '{agent.OutputPath}'.")
            : new ExecutionResult(ExecutionStatus.Completed, output, sessionId ?? request.SessionId);
    }

    /// <summary>
    ///     Connects to the agent's MCP server through the server registry. Tests override this to connect to an
    ///     in-process server.
    /// </summary>
    /// <param name="request">The request (for the working directory).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The connected client; the caller disposes it.</returns>
    /// <exception cref="AflException">The server isn't registered or a secret has no value.</exception>
    protected virtual async Task<McpClient> ConnectAsync(ExecutionRequest request, CancellationToken ct)
    {
        var servers = this.Servers ?? new McpServerStore(AflPaths.FromEnvironment());
        var server = servers.GetRequired(this.Agent.Server ?? string.Empty);
        return await servers.ConnectAsync(server, request.WorkingDirectory, ct);
    }

    /// <summary>
    ///     Renders a tool's arguments: string values are templates, and those that render empty are left out; other
    ///     values are passed as they are.
    /// </summary>
    /// <param name="templates">The argument templates.</param>
    /// <param name="values">The template values.</param>
    /// <returns>The arguments.</returns>
    protected static Dictionary<string, object?> RenderArguments(Dictionary<string, JsonNode?>? templates, Dictionary<string, string> values)
    {
        var arguments = new Dictionary<string, object?>();
        foreach (var (name, node) in templates ?? [])
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var template))
            {
                var rendered = PromptTemplate.Render(template, values);
                if (rendered.Length > 0)
                {
                    arguments[name] = rendered;
                }
            }
            else
            {
                arguments[name] = node is null ? null : ToElement(node);
            }
        }

        return arguments;
    }

    /// <summary>
    ///     Returns the string values among a tool's argument templates.
    /// </summary>
    /// <param name="templates">The templates.</param>
    /// <returns>The strings.</returns>
    private static IEnumerable<string> StringValues(Dictionary<string, JsonNode?>? templates)
    {
        return (templates ?? []).Values
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .OfType<string>();
    }

    /// <summary>
    ///     Converts JSON to a detached element.
    /// </summary>
    /// <param name="node">The JSON.</param>
    /// <returns>The element.</returns>
    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
