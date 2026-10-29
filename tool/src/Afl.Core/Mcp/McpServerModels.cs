using System.Text.Json;
using Afl.Core.Storage;

namespace Afl.Core.Mcp;

/// <summary>
///     The MCP servers afl can connect to (<c>mcp-servers.json</c>).
/// </summary>
[DocumentVersion(1, 0)]
public class McpServerDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the servers.
    /// </summary>
    public List<McpServerDefinition> Servers { get; set; } = [];
}

/// <summary>
///     How afl reaches an MCP server, as a client: a command it starts (<c>stdio</c>) or a URL (<c>http</c>). A server
///     is used as an agent (an <c>mcp</c> agent calls one of its tools to do a step) or given to an agent as extra tools.
///     <see cref="Env" /> and <see cref="Headers" /> values may use <c>{{secret.NAME}}</c>; secret values live in the
///     secure store, never in this file.
/// </summary>
public class McpServerDefinition
{
    /// <summary>
    ///     Gets or sets the id, used by agents (<c>server</c>) and steps (<c>mcpServers</c>).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a one-line description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Gets or sets how afl reaches the server: <c>stdio</c> (default) or <c>http</c>.
    /// </summary>
    public string? Transport { get; set; }

    /// <summary>
    ///     Gets or sets the command that starts the server (<c>stdio</c>).
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    ///     Gets or sets the command's arguments.
    /// </summary>
    public List<string>? Args { get; set; }

    /// <summary>
    ///     Gets or sets environment variables for the command; values may use <c>{{secret.NAME}}</c>.
    /// </summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>
    ///     Gets or sets the server's URL (<c>http</c>).
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    ///     Gets or sets HTTP headers sent to the server; values may use <c>{{secret.NAME}}</c>.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    ///     Gets or sets the names of the server's secrets (their values are in the secure store).
    /// </summary>
    public List<string>? Secrets { get; set; }

    /// <summary>
    ///     Gets or sets where the server was imported from, e.g. <c>claude-code</c>; null when added by hand.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    ///     Makes a deep copy, so edits don't touch the original.
    /// </summary>
    /// <returns>The copy.</returns>
    public McpServerDefinition Clone()
    {
        var json = JsonSerializer.Serialize(this, Constants.JsonConstants.Options);
        return JsonSerializer.Deserialize<McpServerDefinition>(json, Constants.JsonConstants.Options)!;
    }
}

/// <summary>
///     One tool an MCP server offers.
/// </summary>
/// <param name="Name">The tool name.</param>
/// <param name="Description">What it does.</param>
/// <param name="InputSchema">The JSON schema of its arguments.</param>
public record McpToolInfo(string Name, string? Description, JsonElement InputSchema)
{
    /// <summary>
    ///     Gets the names of the tool's arguments.
    /// </summary>
    public IReadOnlyList<string> ArgumentNames =>
        this.InputSchema.ValueKind == JsonValueKind.Object && this.InputSchema.TryGetProperty("properties", out var properties) &&
        properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    /// <summary>
    ///     Gets the names of the tool's required arguments.
    /// </summary>
    public IReadOnlyList<string> RequiredArguments =>
        this.InputSchema.ValueKind == JsonValueKind.Object && this.InputSchema.TryGetProperty("required", out var required) &&
        required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(r => r.GetString() ?? string.Empty).ToList()
            : [];
}

/// <summary>
///     An MCP server found in another app's configuration, ready to import.
/// </summary>
/// <param name="Source">The config source's name, e.g. <c>claude-desktop</c>.</param>
/// <param name="Path">The file it was found in.</param>
/// <param name="Server">The server, with secrets still inline (never saved or shown as they are).</param>
public record McpServerCandidate(string Source, string Path, McpServerDefinition Server);
