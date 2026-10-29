using Afl.Cli.Output;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Mcp;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for MCP server commands: gives access to the server registry. Servers live only in the afl home, so these
///     commands never register the current repo.
/// </summary>
public abstract class AbstractMcpCommand : AbstractCommand
{
    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <summary>
    ///     Gets the MCP server registry.
    /// </summary>
    protected McpServerStore Servers => new(this.Paths);

    /// <summary>
    ///     Asks for the values of a server's secrets that have none, with hidden input, when the user can be asked.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="server">The server.</param>
    protected void AskForMissingSecrets(McpServerStore store, McpServerDefinition server)
    {
        foreach (var name in store.MissingSecrets(server))
        {
            if (!this.Output.IsInteractive)
            {
                this.Output.Info($"Secret {name} has no value. Set it with: afl mcp-secret {server.Id} {name}");
                continue;
            }

            if (this.Output.ReadSecret($"Value for {server.Id} secret {name} (empty to skip):") is { } value)
            {
                store.SetSecret(server.Id, name, value);
            }
        }
    }
}

/// <summary>
///     <c>afl mcp list</c>.
/// </summary>
[Verb("mcp-list", HelpText = "List the MCP servers afl can connect to (see also mcp show|add|remove|import|tools|test|secret).")]
public class McpListCommand : AbstractMcpCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Servers;
        this.Output.Result(store.All().Select(s => McpServerView.From(store, s)).ToList(), TextRendering.McpServerList);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl mcp show</c>.
/// </summary>
[Verb("mcp-show", HelpText = "Show an MCP server (secret values are never shown).")]
public class McpShowCommand : AbstractMcpCommand
{
    /// <summary>
    ///     Gets or sets the server id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The server id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Servers;
        this.Output.Result(McpServerView.From(store, store.GetRequired(this.Id)), TextRendering.McpServer);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl mcp add</c>: adds or updates a server. Environment and header values that look secret are moved to the
///     secure store; secrets with no value are asked for with hidden input.
/// </summary>
[Verb("mcp-add", HelpText = "Add or update an MCP server: afl mcp add <id> --command <cmd> [--arg ...] [--env K=V] " +
                            "or afl mcp add <id> --url <url> [--header K=V]. Use {{secret.NAME}} in values for secrets.")]
public class McpAddCommand : AbstractMcpCommand
{
    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The server id (lowercase letters, digits and dashes).")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    [Option("name", HelpText = "A display name.")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets the description.
    /// </summary>
    [Option("description", HelpText = "A one-line description.")]
    public string? Description { get; set; }

    /// <summary>
    ///     Gets or sets the command.
    /// </summary>
    [Option('c', "command", HelpText = "stdio: the command that starts the server.")]
    public string? Command { get; set; }

    /// <summary>
    ///     Gets or sets the arguments.
    /// </summary>
    [Option("arg", HelpText = "stdio: an argument (repeat it; write --arg=-x for one starting with a dash).")]
    public IEnumerable<string>? Args { get; set; }

    /// <summary>
    ///     Gets or sets environment variables.
    /// </summary>
    [Option("env", HelpText = "stdio: NAME=value set for the server (repeat it).")]
    public IEnumerable<string>? Env { get; set; }

    /// <summary>
    ///     Gets or sets the URL.
    /// </summary>
    [Option("url", HelpText = "http: the server's URL.")]
    public string? Url { get; set; }

    /// <summary>
    ///     Gets or sets headers.
    /// </summary>
    [Option("header", HelpText = "http: Name=value sent to the server (repeat it).")]
    public IEnumerable<string>? Headers { get; set; }

    /// <summary>
    ///     Gets or sets secret names to declare.
    /// </summary>
    [Option("secret", HelpText = "Declare a secret NAME (repeat it); its value is asked for with hidden input.")]
    public IEnumerable<string>? Secrets { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Servers;
        var server = store.Find(this.Id)?.Clone() ?? new McpServerDefinition { Id = this.Id };
        server.Name = this.Name ?? server.Name;
        server.Description = this.Description ?? server.Description;
        if (this.Url is not null)
        {
            server.Transport = AgentConstants.TransportHttp;
            server.Url = this.Url;
        }
        else if (this.Command is not null)
        {
            server.Transport = AgentConstants.TransportStdio;
            server.Command = this.Command;
        }

        server.Args = this.Args?.ToList() is { Count: > 0 } args ? args : server.Args;
        server.Env = Pairs(this.Env, "--env") ?? server.Env;
        server.Headers = Pairs(this.Headers, "--header") ?? server.Headers;
        foreach (var name in this.Secrets ?? [])
        {
            server.Secrets ??= [];
            if (!server.Secrets.Contains(name, StringComparer.Ordinal))
            {
                server.Secrets.Add(name);
            }
        }

        var saved = store.SaveExtractingSecrets(server);
        this.AskForMissingSecrets(store, saved);
        this.Output.Result(McpServerView.From(store, saved), (v, w) =>
        {
            TextRendering.McpServer(v, w);
            w.WriteLine();
            w.WriteLine($"Check it with: afl mcp test {v.Id}");
        });
        return Task.FromResult(ExitSuccess);
    }

    /// <summary>
    ///     Parses <c>name=value</c> options.
    /// </summary>
    /// <param name="pairs">The options given.</param>
    /// <param name="option">The option name, for errors.</param>
    /// <returns>The map, or null when none were given.</returns>
    private static Dictionary<string, string>? Pairs(IEnumerable<string>? pairs, string option)
    {
        var list = pairs?.ToList();
        if (list is not { Count: > 0 })
        {
            return null;
        }

        return list.ToDictionary(pair =>
        {
            var at = pair.IndexOf('=');
            return at > 0 ? pair[..at] : throw new AflException(AflErrorKind.InvalidInput, $"{option} takes name=value, not '{pair}'.");
        }, pair => pair[(pair.IndexOf('=') + 1)..]);
    }
}

/// <summary>
///     <c>afl mcp remove</c>.
/// </summary>
[Verb("mcp-remove", HelpText = "Remove an MCP server and its stored secrets.")]
public class McpRemoveCommand : AbstractMcpCommand
{
    /// <summary>
    ///     Gets or sets the server id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The server id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var removed = this.Servers.Remove(this.Id);
        this.Output.Result(new { id = this.Id, removed },
            (r, w) => w.WriteLine(r.removed ? $"Removed MCP server '{r.id}'." : $"No MCP server '{r.id}'."));
        return Task.FromResult(removed ? ExitSuccess : ExitError);
    }
}

/// <summary>
///     <c>afl mcp import</c>: finds the MCP servers configured in Claude Code, Claude Desktop, Cursor and Codex (only
///     reading their files) and imports the ones picked. Values that look secret go to the secure store.
/// </summary>
[Verb("mcp-import", HelpText = "Import MCP servers from other apps' config. Without --server or --all it lists what it found " +
                               "and asks which to import.")]
public class McpImportCommand : AbstractMcpCommand
{
    /// <summary>
    ///     Gets or sets the source to read.
    /// </summary>
    [Option("from", HelpText = "One source: claude-code, claude-desktop, cursor or codex (default: all).")]
    public string? From { get; set; }

    /// <summary>
    ///     Gets or sets the servers to import.
    /// </summary>
    [Option("server", HelpText = "A server id to import, or source/id when several apps have it (repeat it).")]
    public IEnumerable<string>? Ids { get; set; }

    /// <summary>
    ///     Gets or sets whether to import everything found.
    /// </summary>
    [Option("all", HelpText = "Import every server found that isn't registered yet.")]
    public bool All { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Servers;
        var problems = new List<string>();
        var found = McpConfigSourceRegistry.Instance.FindAll(Environment.CurrentDirectory, this.From, problems: problems);
        foreach (var problem in problems)
        {
            this.Output.Info(problem);
        }

        var picked = this.Pick(store, found);
        if (picked is null)
        {
            this.Output.Result(found.Select(c => McpCandidateView.From(store, c)).ToList(), TextRendering.McpCandidates);
            return Task.FromResult(ExitSuccess);
        }

        var saved = store.Import(picked);
        foreach (var server in saved)
        {
            this.AskForMissingSecrets(store, server);
        }

        this.Output.Result(saved.Select(s => McpServerView.From(store, s)).ToList(), (views, w) =>
        {
            w.WriteLine(views.Count == 0 ? "Nothing imported." : $"Imported {views.Count} MCP server(s):");
            TextRendering.McpServerList(views, w);
        });
        return Task.FromResult(ExitSuccess);
    }

    /// <summary>
    ///     Works out which servers to import: those named, everything new with <c>--all</c>, or the user's pick.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="found">The servers found.</param>
    /// <returns>The servers to import, or null to only list them.</returns>
    /// <exception cref="AflException">A named server wasn't found.</exception>
    private IReadOnlyList<McpServerCandidate>? Pick(McpServerStore store, IReadOnlyList<McpServerCandidate> found)
    {
        var ids = this.Ids?.ToList() ?? [];
        if (this.All)
        {
            return found.Where(c => store.Find(c.Server.Id) is null).DistinctBy(c => c.Server.Id).ToList();
        }

        if (ids.Count == 0)
        {
            if (!this.Output.IsInteractive || found.Count == 0)
            {
                return null;
            }

            TextRendering.McpCandidates(found.Select(c => McpCandidateView.From(store, c)).ToList(), Console.Out);
            var answer = this.Output.Ask("Import which? (ids or source/id, comma-separated; empty for none)");
            if (answer is null)
            {
                return [];
            }

            ids = answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        return ids.Select(id =>
        {
            var slash = id.IndexOf('/');
            var match = slash > 0
                ? found.FirstOrDefault(c => c.Source == id[..slash] && c.Server.Id == id[(slash + 1)..])
                : found.FirstOrDefault(c => c.Server.Id == id);
            return match ?? throw new AflException(AflErrorKind.NotFound,
                $"No server '{id}' found. Found: {string.Join(", ", found.Select(c => $"{c.Source}/{c.Server.Id}").DefaultIfEmpty("none"))}.");
        }).ToList();
    }
}

/// <summary>
///     <c>afl mcp tools</c>.
/// </summary>
[Verb("mcp-tools", HelpText = "Connect to an MCP server and list its tools with their arguments.")]
public class McpToolsCommand : AbstractMcpCommand
{
    /// <summary>
    ///     Gets or sets the server id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The server id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Servers;
        var tools = await store.ListToolsAsync(store.GetRequired(this.Id), Environment.CurrentDirectory, ct);
        this.Output.Result(new McpToolsView(this.Id, tools.Select(McpToolView.From).ToList()), TextRendering.McpTools);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl mcp test</c>.
/// </summary>
[Verb("mcp-test", HelpText = "Check that afl can connect to an MCP server.")]
public class McpTestCommand : AbstractMcpCommand
{
    /// <summary>
    ///     Gets or sets the server id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The server id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Servers;
        var server = store.GetRequired(this.Id);
        var missing = store.MissingSecrets(server);
        if (missing.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"MCP server '{server.Id}' has no value for {string.Join(", ", missing)}. Set it with: afl mcp-secret {server.Id} <NAME>");
        }

        this.Output.Info($"Connecting to {server.Id}...");
        var tools = await store.ListToolsAsync(server, Environment.CurrentDirectory, ct);
        this.Output.Result(new { server = server.Id, ok = true, tools = tools.Select(t => t.Name).ToList() },
            (r, w) => w.WriteLine($"Connected to '{r.server}': {r.tools.Count} tool(s): {string.Join(", ", r.tools)}"));
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl mcp secret</c>: sets one of a server's secrets from hidden input (or the first line of stdin); the value
///     is never taken as an argument.
/// </summary>
[Verb("mcp-secret", HelpText = "Set (or --clear) an MCP server's secret. The value is read from a hidden prompt or the first line of stdin.")]
public class McpSecretCommand : AbstractMcpCommand
{
    /// <summary>
    ///     Gets or sets the server id.
    /// </summary>
    [Value(0, MetaName = "server", Required = true, HelpText = "The server id.")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the secret name.
    /// </summary>
    [Value(1, MetaName = "name", Required = true, HelpText = "The secret's name, e.g. GITHUB_TOKEN.")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether to delete the value.
    /// </summary>
    [Option("clear", HelpText = "Delete the stored value.")]
    public bool Clear { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Servers;
        var server = store.GetRequired(this.Id).Clone();
        if (this.Clear)
        {
            store.SetSecret(server.Id, this.Name, null);
        }
        else
        {
            var value = this.Output.ReadSecret($"Value for {server.Id} secret {this.Name}:")
                        ?? throw new AflException(AflErrorKind.InvalidInput, "No value entered; nothing changed.");
            store.Save(server, new Dictionary<string, string?> { [this.Name] = value });
        }

        this.Output.Result(new McpSecretView(this.Name, store.HasSecret(server.Id, this.Name), McpServerStore.SecretVariable(server.Id, this.Name)),
            (v, w) => w.WriteLine(v.Set ? $"Secret {v.Name} is set for '{this.Id}'." : $"Secret {v.Name} is cleared for '{this.Id}'."));
        return Task.FromResult(ExitSuccess);
    }
}
