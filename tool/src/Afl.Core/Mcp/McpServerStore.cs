using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Storage;
using Afl.Core.Workflows;
using ModelContextProtocol.Client;
using Niddy.Core.Security;

namespace Afl.Core.Mcp;

/// <summary>
///     The MCP servers afl can connect to as a client (<c>mcp-servers.json</c>), and their secrets. Server details are
///     plain JSON; secret values go to the OS secure store under <c>mcp:&lt;server&gt;:&lt;NAME&gt;</c>, and an
///     <c>AFL_MCP_SECRET_&lt;SERVER&gt;_&lt;NAME&gt;</c> environment variable overrides the stored one. Secrets are put
///     into a server's environment or headers only in memory, when connecting.
/// </summary>
public partial class McpServerStore : AbstractJsonStore<McpServerDocument>
{
    /// <summary>
    ///     The secure store, created on first use so listing servers never touches the keychain.
    /// </summary>
    private readonly Lazy<ISecureStorage> _secrets;

    /// <summary>
    ///     Initializes a new instance of the <see cref="McpServerStore" /> class.
    /// </summary>
    /// <param name="paths">The afl home paths.</param>
    /// <param name="secrets">The secure store to keep secrets in; defaults to the platform's.</param>
    public McpServerStore(AflPaths paths, ISecureStorage? secrets = null) : base(paths.McpServersFile)
    {
        this._secrets = new Lazy<ISecureStorage>(() =>
            secrets ?? SecureStorageFactory.Create(paths.Root, Jira.JiraInstanceStore.SecureStorageService));
    }

    /// <summary>
    ///     Returns every server, by id.
    /// </summary>
    /// <returns>The servers.</returns>
    public IReadOnlyList<McpServerDefinition> All()
    {
        return this.Load().Servers.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     Finds a server by id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The server, or null.</returns>
    public McpServerDefinition? Find(string id)
    {
        return this.Load().Servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Finds a server by id, or fails.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The server.</returns>
    /// <exception cref="AflException">No server has that id.</exception>
    public McpServerDefinition GetRequired(string id)
    {
        return this.Find(id) ?? throw new AflException(AflErrorKind.NotFound,
            $"No MCP server '{id}'. Known: {string.Join(", ", this.All().Select(s => s.Id).DefaultIfEmpty("none"))}. " +
            "Add one with: afl mcp-add, or afl mcp-import.");
    }

    /// <summary>
    ///     Adds or replaces a server, and stores the secrets given with it. Secrets its environment or headers refer to
    ///     (<c>{{secret.NAME}}</c>) are added to <see cref="McpServerDefinition.Secrets" />.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="secrets">Secret values by name (null values delete), or null to keep the stored ones.</param>
    /// <returns>The saved server.</returns>
    /// <exception cref="AflException">The server is invalid.</exception>
    public McpServerDefinition Save(McpServerDefinition server, IReadOnlyDictionary<string, string?>? secrets = null)
    {
        var referenced = (server.Env?.Values ?? Enumerable.Empty<string>())
            .Concat(server.Headers?.Values ?? Enumerable.Empty<string>())
            .SelectMany(PromptTemplate.References)
            .Where(name => name.StartsWith(AgentConstants.SecretVariablePrefix, StringComparison.Ordinal))
            .Select(name => name[AgentConstants.SecretVariablePrefix.Length..]);
        foreach (var name in (secrets?.Keys ?? Enumerable.Empty<string>()).Concat(referenced))
        {
            server.Secrets ??= [];
            if (!server.Secrets.Contains(name, StringComparer.Ordinal))
            {
                server.Secrets.Add(name);
            }
        }

        if (string.IsNullOrWhiteSpace(server.Name))
        {
            server.Name = server.Id;
        }

        var problems = Validate(server);
        if (problems.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, $"MCP server '{server.Id}' is invalid: {string.Join(" ", problems)}");
        }

        var saved = this.Update(document =>
        {
            var existing = document.Servers.FindIndex(s => string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                document.Servers[existing] = server;
            }
            else
            {
                document.Servers.Add(server);
            }

            return server;
        });

        foreach (var (name, value) in secrets ?? new Dictionary<string, string?>())
        {
            this.SetSecret(saved.Id, name, value);
        }

        return saved;
    }

    /// <summary>
    ///     Removes a server and its stored secrets.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True if it existed.</returns>
    public bool Remove(string id)
    {
        var removed = this.Find(id);
        if (removed is null)
        {
            return false;
        }

        this.Update(document => document.Servers.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)));
        foreach (var name in removed.Secrets ?? [])
        {
            this.SetSecret(removed.Id, name, null);
        }

        return true;
    }

    /// <summary>
    ///     Stores, or with null deletes, one of a server's secrets.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <param name="name">The secret's name.</param>
    /// <param name="value">The value.</param>
    public void SetSecret(string serverId, string name, string? value)
    {
        var key = SecretKey(serverId, name);
        if (value is null && !this._secrets.Value.TokenExists(key))
        {
            return;
        }

        this._secrets.Value.SetToken(key, value);
    }

    /// <summary>
    ///     Returns whether one of a server's secrets has a value, from the environment or the secure store.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <param name="name">The secret's name.</param>
    /// <returns>True if it has one.</returns>
    public bool HasSecret(string serverId, string name)
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SecretVariable(serverId, name))) ||
               this._secrets.Value.TokenExists(SecretKey(serverId, name));
    }

    /// <summary>
    ///     Returns the names of a server's secrets that have no value.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <returns>The names.</returns>
    public IReadOnlyList<string> MissingSecrets(McpServerDefinition server)
    {
        return (server.Secrets ?? []).Where(name => !this.HasSecret(server.Id, name)).ToList();
    }

    /// <summary>
    ///     Returns one of a server's secrets: the environment override if set, otherwise the stored one.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <param name="name">The secret's name.</param>
    /// <returns>The value.</returns>
    /// <exception cref="AflException">The secret has no value.</exception>
    public string ResolveSecret(string serverId, string name)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(SecretVariable(serverId, name));
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        return this._secrets.Value.GetToken(SecretKey(serverId, name)) ?? throw new AflException(AflErrorKind.InvalidState,
            $"MCP server '{serverId}' has no value for its secret {name}. Set it with: afl mcp-secret {serverId} {name} " +
            $"(or in Settings, or set {SecretVariable(serverId, name)}).");
    }

    /// <summary>
    ///     Returns a server with its secrets and <c>${VAR}</c> references filled in, ready to connect with. The result
    ///     holds secret values: keep it in memory only.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <returns>The resolved copy.</returns>
    /// <exception cref="AflException">A secret has no value.</exception>
    public McpServerDefinition Resolve(McpServerDefinition server)
    {
        var values = (server.Secrets ?? []).ToDictionary(
            name => AgentConstants.SecretVariablePrefix + name, name => this.ResolveSecret(server.Id, name));
        var copy = server.Clone();
        copy.Command = ExpandEnvironment(copy.Command);
        copy.Url = ExpandEnvironment(copy.Url);
        copy.Args = copy.Args?.Select(a => ExpandEnvironment(a)!).ToList();
        copy.Env = copy.Env?.ToDictionary(e => e.Key, e => ExpandEnvironment(PromptTemplate.Render(e.Value, values))!);
        copy.Headers = copy.Headers?.ToDictionary(h => h.Key, h => ExpandEnvironment(PromptTemplate.Render(h.Value, values))!);
        return copy;
    }

    /// <summary>
    ///     Connects to a server. Tests override this to connect to an in-process server.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="workingDirectory">The directory to start a stdio server in.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The connected client; the caller disposes it.</returns>
    /// <exception cref="AflException">A secret has no value.</exception>
    public virtual async Task<McpClient> ConnectAsync(McpServerDefinition server, string workingDirectory, CancellationToken ct)
    {
        var resolved = this.Resolve(server);
        IClientTransport transport;
        if ((resolved.Transport ?? AgentConstants.TransportStdio) == AgentConstants.TransportHttp)
        {
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(resolved.Url!), Name = resolved.Id, AdditionalHeaders = resolved.Headers,
            });
        }
        else
        {
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = resolved.Command!,
                Arguments = resolved.Args ?? [],
                Name = resolved.Id,
                WorkingDirectory = workingDirectory,
                EnvironmentVariables = resolved.Env?.ToDictionary(e => e.Key, string? (e) => e.Value),
            });
        }

        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    /// <summary>
    ///     Connects to a server and lists its tools.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="workingDirectory">The directory to start a stdio server in.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The tools, by name.</returns>
    /// <exception cref="AflException">The server couldn't be reached.</exception>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(McpServerDefinition server, string workingDirectory,
        CancellationToken ct = default)
    {
        try
        {
            await using var client = await this.ConnectAsync(server, workingDirectory, ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return tools.Select(t => new McpToolInfo(t.Name, t.Description, t.ProtocolTool.InputSchema.Clone()))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AflException)
        {
            throw new AflException(AflErrorKind.ExternalTool, $"Couldn't reach MCP server '{server.Id}': {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Adds or replaces a server whose environment and headers may hold secrets inline: values that look secret (by
    ///     their name, or a <c>Bearer</c> prefix) go to the secure store and are replaced with <c>{{secret.NAME}}</c>.
    /// </summary>
    /// <param name="server">The server; it isn't changed.</param>
    /// <param name="secrets">Secret values given separately (name to value), saved too.</param>
    /// <returns>The saved server.</returns>
    /// <exception cref="AflException">The server is invalid.</exception>
    public McpServerDefinition SaveExtractingSecrets(McpServerDefinition server, IReadOnlyDictionary<string, string?>? secrets = null)
    {
        var copy = server.Clone();
        var found = new Dictionary<string, string?>(secrets ?? new Dictionary<string, string?>());
        copy.Env = ExtractSecrets(copy.Env, found);
        copy.Headers = ExtractSecrets(copy.Headers, found);
        return this.Save(copy, found);
    }

    /// <summary>
    ///     Imports servers found in other apps' configuration, moving their secrets to the secure store
    ///     (<see cref="SaveExtractingSecrets" />).
    /// </summary>
    /// <param name="candidates">The servers to import.</param>
    /// <returns>The saved servers.</returns>
    /// <exception cref="AflException">A server is invalid.</exception>
    public IReadOnlyList<McpServerDefinition> Import(IEnumerable<McpServerCandidate> candidates)
    {
        return candidates.Select(candidate =>
        {
            var server = candidate.Server.Clone();
            server.Source = candidate.Source;
            return this.SaveExtractingSecrets(server);
        }).ToList();
    }

    /// <summary>
    ///     Writes a Claude-format MCP client configuration (<c>{"mcpServers": {...}}</c>) for servers, with their secrets
    ///     filled in, readable only by the user. The caller deletes it when the agent has finished.
    /// </summary>
    /// <param name="servers">The servers.</param>
    /// <param name="path">The file to write.</param>
    /// <exception cref="AflException">A secret has no value.</exception>
    public void WriteClientConfig(IEnumerable<McpServerDefinition> servers, string path)
    {
        var config = new JsonObject();
        foreach (var server in servers.Select(this.Resolve))
        {
            var entry = new JsonObject();
            if ((server.Transport ?? AgentConstants.TransportStdio) == AgentConstants.TransportHttp)
            {
                entry["type"] = "http";
                entry["url"] = server.Url;
                if (server.Headers is { Count: > 0 })
                {
                    entry["headers"] = JsonSerializer.SerializeToNode(server.Headers);
                }
            }
            else
            {
                entry["type"] = "stdio";
                entry["command"] = server.Command;
                entry["args"] = JsonSerializer.SerializeToNode(server.Args ?? []);
                if (server.Env is { Count: > 0 })
                {
                    entry["env"] = JsonSerializer.SerializeToNode(server.Env);
                }
            }

            config[server.Id] = entry;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(new JsonObject { ["mcpServers"] = config }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    ///     Checks a server's fields, returning what's wrong.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <returns>The problems; empty when it's usable.</returns>
    public static IReadOnlyList<string> Validate(McpServerDefinition server)
    {
        var problems = new List<string>();
        if (!WorkflowLibrary.IsValidId(server.Id))
        {
            problems.Add($"'{server.Id}' isn't a usable server id. Use lowercase letters, digits and dashes.");
        }

        var transport = server.Transport ?? AgentConstants.TransportStdio;
        if (!AgentConstants.Transports.Contains(transport))
        {
            problems.Add($"transport must be one of {string.Join(", ", AgentConstants.Transports)}.");
        }
        else if (transport == AgentConstants.TransportStdio && string.IsNullOrWhiteSpace(server.Command))
        {
            problems.Add("a stdio server needs its 'command'.");
        }
        else if (transport == AgentConstants.TransportHttp &&
                 (!Uri.TryCreate(ExpandEnvironment(server.Url), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            problems.Add("an http server needs its 'url'.");
        }

        var declared = server.Secrets ?? [];
        var used = (server.Env?.Values ?? Enumerable.Empty<string>())
            .Concat(server.Headers?.Values ?? Enumerable.Empty<string>())
            .SelectMany(PromptTemplate.References)
            .Distinct()
            .ToList();
        foreach (var name in used)
        {
            if (!name.StartsWith(AgentConstants.SecretVariablePrefix, StringComparison.Ordinal))
            {
                problems.Add($"'{{{{{name}}}}}' isn't a secret; use {{{{secret.NAME}}}}.");
            }
            else if (!declared.Contains(name[AgentConstants.SecretVariablePrefix.Length..], StringComparer.Ordinal))
            {
                problems.Add($"'{{{{{name}}}}}' refers to a secret the server doesn't list in 'secrets'.");
            }
        }

        foreach (var name in declared.Where(n => !SecretNameRegex().IsMatch(n)))
        {
            problems.Add($"'{name}' isn't a usable secret name. Use letters, digits and '_'.");
        }

        return problems;
    }

    /// <summary>
    ///     Returns a usable server id made from a name, e.g. <c>GitHub Server</c> → <c>github-server</c>.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The id.</returns>
    public static string IdFrom(string name)
    {
        var id = NonIdRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return id.Length == 0 ? "server" : id;
    }

    /// <summary>
    ///     Returns whether a setting's name or value looks like a secret.
    /// </summary>
    /// <param name="name">The environment variable or header name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True if it should go to the secure store.</returns>
    public static bool LooksSecret(string name, string value)
    {
        return !value.Contains("{{", StringComparison.Ordinal) && !value.Contains("${", StringComparison.Ordinal) &&
               (SecretNameHintRegex().IsMatch(name) || value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Returns the environment variable that overrides one of a server's secrets.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <param name="name">The secret's name.</param>
    /// <returns>The variable name, e.g. <c>AFL_MCP_SECRET_GITHUB_TOKEN</c>.</returns>
    public static string SecretVariable(string serverId, string name)
    {
        return EnvironmentConstants.McpSecretPrefix + NonIdentifierRegex().Replace($"{serverId}_{name}".ToUpperInvariant(), "_");
    }

    /// <summary>
    ///     Moves the secret-looking values of a map into <paramref name="secrets" />, leaving references behind.
    /// </summary>
    /// <param name="values">The map (environment or headers).</param>
    /// <param name="secrets">Receives the secret values by name.</param>
    /// <returns>The map with references in place of secrets.</returns>
    private static Dictionary<string, string>? ExtractSecrets(Dictionary<string, string>? values,
        Dictionary<string, string?> secrets)
    {
        if (values is null)
        {
            return null;
        }

        var result = new Dictionary<string, string>();
        foreach (var (key, value) in values)
        {
            if (!LooksSecret(key, value))
            {
                result[key] = value;
                continue;
            }

            var name = NonIdentifierRegex().Replace(key.ToUpperInvariant(), "_");
            var bearer = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            secrets[name] = bearer ? value["Bearer ".Length..].Trim() : value;
            result[key] = (bearer ? "Bearer " : string.Empty) + "{{" + AgentConstants.SecretVariablePrefix + name + "}}";
        }

        return result;
    }

    /// <summary>
    ///     Fills in <c>${VAR}</c> and <c>${VAR:-default}</c> from the environment, as the apps afl imports from do.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The expanded value.</returns>
    private static string? ExpandEnvironment(string? value)
    {
        return value is null
            ? null
            : EnvironmentReferenceRegex().Replace(value, m =>
                Environment.GetEnvironmentVariable(m.Groups["name"].Value) is { Length: > 0 } found
                    ? found
                    : m.Groups["default"].Value);
    }

    /// <summary>
    ///     Returns the secure-store key for one of a server's secrets.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <param name="name">The secret's name.</param>
    /// <returns>The key.</returns>
    private static string SecretKey(string serverId, string name)
    {
        return $"mcp:{serverId.ToLowerInvariant()}:{name}";
    }

    /// <summary>
    ///     Matches names that suggest a secret.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("token|key|secret|password|passwd|auth|bearer|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNameHintRegex();

    /// <summary>
    ///     Matches a valid secret name.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SecretNameRegex();

    /// <summary>
    ///     Matches runs of characters that can't appear in an id.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonIdRegex();

    /// <summary>
    ///     Matches characters that can't appear in an environment variable name.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("[^A-Z0-9_]")]
    private static partial Regex NonIdentifierRegex();

    /// <summary>
    ///     Matches <c>${VAR}</c> or <c>${VAR:-default}</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<default>[^}]*))?\}")]
    private static partial Regex EnvironmentReferenceRegex();
}
