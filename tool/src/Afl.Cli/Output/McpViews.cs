using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Mcp;

namespace Afl.Cli.Output;

/// <summary>
///     An MCP server as <c>afl mcp-list</c> and <c>--json</c> show it. Secret values are never included, only whether
///     each one is set.
/// </summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Transport">stdio or http.</param>
/// <param name="Command">The command, for stdio.</param>
/// <param name="Args">The command's arguments.</param>
/// <param name="Url">The URL, for http.</param>
/// <param name="Env">The environment (secrets appear as <c>{{secret.NAME}}</c>).</param>
/// <param name="Headers">The headers (secrets appear as <c>{{secret.NAME}}</c>).</param>
/// <param name="Secrets">The secrets and whether each has a value.</param>
/// <param name="Source">Where it was imported from.</param>
public record McpServerView(
    string Id,
    string Name,
    string? Description,
    string Transport,
    string? Command,
    IReadOnlyList<string> Args,
    string? Url,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<McpSecretView> Secrets,
    string? Source)
{
    /// <summary>
    ///     Creates the view for a server.
    /// </summary>
    /// <param name="store">The store, to check its secrets.</param>
    /// <param name="server">The server.</param>
    /// <returns>The view.</returns>
    public static McpServerView From(McpServerStore store, McpServerDefinition server)
    {
        return new McpServerView(server.Id, server.Name, server.Description, server.Transport ?? AgentConstants.TransportStdio,
            server.Command, server.Args ?? [], server.Url, server.Env ?? [], server.Headers ?? [],
            (server.Secrets ?? []).Select(name => new McpSecretView(name, store.HasSecret(server.Id, name),
                McpServerStore.SecretVariable(server.Id, name))).ToList(),
            server.Source);
    }

    /// <summary>
    ///     Gets what the server runs or where it is, on one line.
    /// </summary>
    public string Target => this.Url ?? string.Join(' ', new[] { this.Command ?? string.Empty }.Concat(this.Args));
}

/// <summary>
///     One of a server's secrets: its name and whether it has a value, never the value.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Set">Whether it has a value.</param>
/// <param name="Variable">The environment variable that overrides it.</param>
public record McpSecretView(string Name, bool Set, string Variable);

/// <summary>
///     A server found in another app's configuration, as <c>afl mcp-import</c> lists it. Values that look secret are
///     masked.
/// </summary>
/// <param name="Source">The app.</param>
/// <param name="Path">The file it's in.</param>
/// <param name="Id">The id it would get.</param>
/// <param name="Name">Its name there.</param>
/// <param name="Transport">stdio or http.</param>
/// <param name="Target">What it runs or where it is.</param>
/// <param name="SecretNames">The settings that would move to the secure store.</param>
/// <param name="Exists">Whether a server with that id is already registered.</param>
public record McpCandidateView(
    string Source,
    string Path,
    string Id,
    string Name,
    string Transport,
    string Target,
    IReadOnlyList<string> SecretNames,
    bool Exists)
{
    /// <summary>
    ///     Creates the view for a candidate.
    /// </summary>
    /// <param name="store">The store, to see what's registered.</param>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The view.</returns>
    public static McpCandidateView From(McpServerStore store, McpServerCandidate candidate)
    {
        var server = candidate.Server;
        var secrets = (server.Env ?? []).Concat(server.Headers ?? [])
            .Where(e => McpServerStore.LooksSecret(e.Key, e.Value))
            .Select(e => e.Key)
            .ToList();
        return new McpCandidateView(candidate.Source, candidate.Path, server.Id, server.Name,
            server.Transport ?? AgentConstants.TransportStdio,
            server.Url ?? string.Join(' ', new[] { server.Command ?? string.Empty }.Concat(server.Args ?? [])),
            secrets, store.Find(server.Id) is not null);
    }
}

/// <summary>
///     A server's tools, as <c>afl mcp-tools</c> shows them.
/// </summary>
/// <param name="Server">The server id.</param>
/// <param name="Tools">The tools.</param>
public record McpToolsView(string Server, IReadOnlyList<McpToolView> Tools);

/// <summary>
///     One tool.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Arguments">Its argument names.</param>
/// <param name="Required">Its required arguments.</param>
/// <param name="InputSchema">Its argument schema.</param>
public record McpToolView(string Name, string? Description, IReadOnlyList<string> Arguments, IReadOnlyList<string> Required,
    JsonElement InputSchema)
{
    /// <summary>
    ///     Creates the view for a tool.
    /// </summary>
    /// <param name="tool">The tool.</param>
    /// <returns>The view.</returns>
    public static McpToolView From(McpToolInfo tool)
    {
        return new McpToolView(tool.Name, tool.Description, tool.ArgumentNames, tool.RequiredArguments, tool.InputSchema);
    }
}
