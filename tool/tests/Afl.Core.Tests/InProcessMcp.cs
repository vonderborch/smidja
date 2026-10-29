using System.IO.Pipelines;
using System.Text.Json;
using Afl.Core.Helpers;
using Afl.Core.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Niddy.Core.Security;

namespace Afl.Core.Tests;

/// <summary>
///     An MCP server running in the test process, connected over pipes.
/// </summary>
public static class InProcessMcp
{
    /// <summary>
    ///     Starts a server with the given tools and connects a client to it.
    /// </summary>
    /// <param name="tools">The tools it lists.</param>
    /// <param name="answer">Answers a tool call.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The connected client.</returns>
    public static async Task<McpClient> ConnectAsync(IList<Tool> tools,
        Func<string, Dictionary<string, JsonElement>, CallToolResult> answer, CancellationToken ct)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "test", Version = "1.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }),
                CallToolHandler = (context, _) => ValueTask.FromResult(answer(context.Params!.Name,
                    context.Params.Arguments?.ToDictionary(a => a.Key, a => a.Value) ?? [])),
            },
        };
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        _ = server.RunAsync(CancellationToken.None);
        return await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: ct);
    }
}

/// <summary>
///     A server registry whose servers all run in the test process, recording the resolved definition each connection
///     used.
/// </summary>
/// <param name="paths">The afl home paths.</param>
/// <param name="secrets">The secure store.</param>
/// <param name="tools">The tools every server lists.</param>
/// <param name="answer">Answers a tool call.</param>
public class InProcessMcpServerStore(AflPaths paths, ISecureStorage secrets, IList<Tool> tools,
    Func<string, Dictionary<string, JsonElement>, CallToolResult> answer) : McpServerStore(paths, secrets)
{
    /// <summary>
    ///     Gets the resolved servers connected to.
    /// </summary>
    public List<McpServerDefinition> Connected { get; } = [];

    /// <inheritdoc />
    public override Task<McpClient> ConnectAsync(McpServerDefinition server, string workingDirectory, CancellationToken ct)
    {
        this.Connected.Add(this.Resolve(server));
        return InProcessMcp.ConnectAsync(tools, answer, ct);
    }
}
