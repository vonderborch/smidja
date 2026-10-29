using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Registration;
using Tomlyn;
using Tomlyn.Model;

namespace Afl.Core.Mcp;

/// <summary>
///     Registers an MCP configuration source with the <see cref="McpConfigSourceRegistry" />.
/// </summary>
/// <param name="name">The source's name, used with <c>afl mcp-import --from</c>.</param>
public class McpConfigSourceRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     Base for the other apps whose MCP server configuration afl can import (Claude Code, Cursor, ...). A source
///     names the files to look in and reads the servers from one; it only ever reads them. A new app is one subclass
///     with an <see cref="McpConfigSourceRegistrationAttribute" />.
/// </summary>
/// <param name="name">The source's name.</param>
/// <param name="displayName">The app's name, for people.</param>
public abstract class AbstractMcpConfigSource(string name, string displayName)
{
    /// <summary>
    ///     Gets the source's name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets the app's name, for people.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    ///     The user's home directory.
    /// </summary>
    public static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    ///     Gets or sets the home directory the app's files are under; the user's by default (tests point it elsewhere).
    /// </summary>
    public string Home { get; set; } = UserHome;

    /// <summary>
    ///     Returns the files this app may keep MCP servers in, whether or not they exist.
    /// </summary>
    /// <param name="repoPath">The repo whose project configuration to include, if any.</param>
    /// <returns>The paths.</returns>
    public abstract IEnumerable<string> ConfigPaths(string? repoPath);

    /// <summary>
    ///     Reads the servers from one of this app's files.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="repoPath">The repo being imported for, if any.</param>
    /// <returns>The servers, with secrets still inline.</returns>
    public abstract IReadOnlyList<McpServerDefinition> Read(string path, string? repoPath);

    /// <summary>
    ///     Reads every server this app has configured. Files that don't exist are skipped; files that can't be read are
    ///     reported in <paramref name="problems" />.
    /// </summary>
    /// <param name="repoPath">The repo whose project configuration to include, if any.</param>
    /// <param name="problems">Receives a message for each file that couldn't be read.</param>
    /// <returns>The servers found.</returns>
    public IReadOnlyList<McpServerCandidate> Find(string? repoPath, ICollection<string>? problems = null)
    {
        var found = new List<McpServerCandidate>();
        foreach (var path in this.ConfigPaths(repoPath).Distinct().Where(File.Exists))
        {
            try
            {
                found.AddRange(this.Read(path, repoPath).Select(server => new McpServerCandidate(this.Name, path, server)));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException
                                           or InvalidCastException or TomlException)
            {
                problems?.Add($"{this.DisplayName}: couldn't read {path}: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>
    ///     Reads a Claude-format <c>mcpServers</c> object (<c>{"name": {"command", "args", "env"} | {"type", "url", "headers"}}</c>).
    /// </summary>
    /// <param name="servers">The object.</param>
    /// <returns>The servers.</returns>
    protected static IReadOnlyList<McpServerDefinition> ReadServersObject(JsonNode? servers)
    {
        if (servers is not JsonObject entries)
        {
            return [];
        }

        var result = new List<McpServerDefinition>();
        foreach (var (name, node) in entries)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var url = entry["url"]?.GetValue<string>() ?? entry["serverUrl"]?.GetValue<string>();
            result.Add(new McpServerDefinition
            {
                Id = McpServerStore.IdFrom(name),
                Name = name,
                Transport = url is null ? AgentConstants.TransportStdio : AgentConstants.TransportHttp,
                Command = entry["command"]?.GetValue<string>(),
                Args = entry["args"]?.Deserialize<List<string>>(JsonConstants.Options),
                Env = entry["env"]?.Deserialize<Dictionary<string, string>>(JsonConstants.Options),
                Url = url,
                Headers = entry["headers"]?.Deserialize<Dictionary<string, string>>(JsonConstants.Options),
            });
        }

        return result;
    }

    /// <summary>
    ///     Reads a JSON file that may contain comments and trailing commas.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The JSON.</returns>
    protected static JsonNode? ReadJson(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        });
    }
}

/// <summary>
///     The sources whose configuration has a top-level <c>mcpServers</c> object, like <c>.mcp.json</c>.
/// </summary>
/// <param name="name">The source's name.</param>
/// <param name="displayName">The app's name, for people.</param>
public abstract class AbstractJsonMcpConfigSource(string name, string displayName) : AbstractMcpConfigSource(name, displayName)
{
    /// <inheritdoc />
    public override IReadOnlyList<McpServerDefinition> Read(string path, string? repoPath)
    {
        return ReadServersObject(ReadJson(path)?["mcpServers"]);
    }
}

/// <summary>
///     Claude Code: user and per-project servers in <c>~/.claude.json</c>, and the repo's shared <c>.mcp.json</c>.
/// </summary>
[McpConfigSourceRegistration(SourceName)]
public class ClaudeCodeMcpConfigSource() : AbstractJsonMcpConfigSource(SourceName, "Claude Code")
{
    /// <summary>
    ///     The source's name.
    /// </summary>
    public const string SourceName = "claude-code";

    /// <inheritdoc />
    public override IEnumerable<string> ConfigPaths(string? repoPath)
    {
        yield return Path.Combine(this.Home, ".claude.json");
        if (repoPath is not null)
        {
            yield return Path.Combine(repoPath, ".mcp.json");
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<McpServerDefinition> Read(string path, string? repoPath)
    {
        var root = ReadJson(path);
        var servers = ReadServersObject(root?["mcpServers"]).ToList();
        if (repoPath is not null && root?["projects"] is JsonObject projects)
        {
            var full = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar);
            var project = projects.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Key).TrimEnd(Path.DirectorySeparatorChar),
                full, StringComparison.Ordinal)).Value;
            servers.AddRange(ReadServersObject(project?["mcpServers"]).Where(s => servers.All(e => e.Id != s.Id)));
        }

        return servers;
    }
}

/// <summary>
///     Claude Desktop: <c>claude_desktop_config.json</c> in the app's settings folder.
/// </summary>
[McpConfigSourceRegistration(SourceName)]
public class ClaudeDesktopMcpConfigSource() : AbstractJsonMcpConfigSource(SourceName, "Claude Desktop")
{
    /// <summary>
    ///     The source's name.
    /// </summary>
    public const string SourceName = "claude-desktop";

    /// <inheritdoc />
    public override IEnumerable<string> ConfigPaths(string? repoPath)
    {
        var folder = OperatingSystem.IsMacOS() ? Path.Combine(this.Home, "Library", "Application Support")
            : OperatingSystem.IsWindows() ? Path.Combine(this.Home, "AppData", "Roaming")
            : Path.Combine(this.Home, ".config");
        yield return Path.Combine(folder, "Claude", "claude_desktop_config.json");
    }
}

/// <summary>
///     Cursor: <c>~/.cursor/mcp.json</c> and the repo's <c>.cursor/mcp.json</c>.
/// </summary>
[McpConfigSourceRegistration(SourceName)]
public class CursorMcpConfigSource() : AbstractJsonMcpConfigSource(SourceName, "Cursor")
{
    /// <summary>
    ///     The source's name.
    /// </summary>
    public const string SourceName = "cursor";

    /// <inheritdoc />
    public override IEnumerable<string> ConfigPaths(string? repoPath)
    {
        yield return Path.Combine(this.Home, ".cursor", "mcp.json");
        if (repoPath is not null)
        {
            yield return Path.Combine(repoPath, ".cursor", "mcp.json");
        }
    }
}

/// <summary>
///     OpenAI Codex: the <c>[mcp_servers.&lt;name&gt;]</c> tables of <c>~/.codex/config.toml</c>.
/// </summary>
[McpConfigSourceRegistration(SourceName)]
public class CodexMcpConfigSource() : AbstractMcpConfigSource(SourceName, "OpenAI Codex")
{
    /// <summary>
    ///     The source's name.
    /// </summary>
    public const string SourceName = "codex";

    /// <inheritdoc />
    public override IEnumerable<string> ConfigPaths(string? repoPath)
    {
        yield return Path.Combine(this.Home == UserHome && Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome
            ? codexHome
            : Path.Combine(this.Home, ".codex"), "config.toml");
    }

    /// <inheritdoc />
    public override IReadOnlyList<McpServerDefinition> Read(string path, string? repoPath)
    {
        var model = Toml.ToModel(File.ReadAllText(path), path);
        if (!model.TryGetValue("mcp_servers", out var serversValue) || serversValue is not TomlTable servers)
        {
            return [];
        }

        var result = new List<McpServerDefinition>();
        foreach (var (name, value) in servers)
        {
            if (value is not TomlTable entry)
            {
                continue;
            }

            var url = Text(entry, "url");
            var headers = Map(entry, "http_headers");
            if (Text(entry, "bearer_token_env_var") is { Length: > 0 } tokenVariable)
            {
                headers ??= [];
                headers["Authorization"] = $"Bearer ${{{tokenVariable}}}";
            }

            result.Add(new McpServerDefinition
            {
                Id = McpServerStore.IdFrom(name),
                Name = name,
                Transport = url is null ? AgentConstants.TransportStdio : AgentConstants.TransportHttp,
                Command = Text(entry, "command"),
                Args = entry.TryGetValue("args", out var args) && args is TomlArray list
                    ? list.Select(a => a?.ToString() ?? string.Empty).ToList()
                    : null,
                Env = Map(entry, "env"),
                Url = url,
                Headers = headers,
            });
        }

        return result;
    }

    /// <summary>
    ///     Reads a string value from a table.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null.</returns>
    private static string? Text(TomlTable table, string key)
    {
        return table.TryGetValue(key, out var value) ? value as string : null;
    }

    /// <summary>
    ///     Reads a table of strings from a table.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="key">The key.</param>
    /// <returns>The map, or null.</returns>
    private static Dictionary<string, string>? Map(TomlTable table, string key)
    {
        return table.TryGetValue(key, out var value) && value is TomlTable map
            ? map.ToDictionary(e => e.Key, e => e.Value?.ToString() ?? string.Empty)
            : null;
    }
}

/// <summary>
///     The registered MCP configuration sources.
/// </summary>
public class McpConfigSourceRegistry() : TypeRegistry<AbstractMcpConfigSource, McpConfigSourceRegistrationAttribute>("MCP config source")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static McpConfigSourceRegistry Instance { get; } = new();

    /// <summary>
    ///     Finds the servers configured in other apps.
    /// </summary>
    /// <param name="repoPath">The repo whose project configuration to include, if any.</param>
    /// <param name="sourceName">One source to read, or null for all of them.</param>
    /// <param name="home">The home directory to look under; the user's by default.</param>
    /// <param name="problems">Receives a message for each file that couldn't be read.</param>
    /// <returns>The servers found, by source.</returns>
    public IReadOnlyList<McpServerCandidate> FindAll(string? repoPath, string? sourceName = null, string? home = null,
        ICollection<string>? problems = null)
    {
        var sources = sourceName is null ? this.CreateAll() : [this.Create(sourceName)];
        return sources.SelectMany(source =>
        {
            if (home is not null)
            {
                source.Home = home;
            }

            return source.Find(repoPath, problems);
        }).ToList();
    }
}
