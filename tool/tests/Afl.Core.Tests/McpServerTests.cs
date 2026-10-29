using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Mcp;
using ModelContextProtocol.Protocol;
using Niddy.Core.Security;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the MCP server registry: secrets, importing other apps' configuration, and agents that use servers.
/// </summary>
public class McpServerTests : AbstractAflTest
{
    private ISecureStorage Secrets => new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests");

    private McpServerStore Store() => new(this.Paths, this.Secrets);

    private InProcessMcpServerStore InProcess(Func<string, Dictionary<string, JsonElement>, CallToolResult>? answer = null) =>
        new(this.Paths, this.Secrets,
        [
            new Tool
            {
                Name = "ask", Description = "Asks.",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object", properties = new { prompt = new { type = "string" }, cwd = new { type = "string" } },
                    required = new[] { "prompt" },
                }),
            },
        ],
        answer ?? ((tool, arguments) => new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"{tool}: {arguments["prompt"].GetString()}" }],
        }));

    private static McpServerDefinition Stdio(string id = "gh") => new()
    {
        Id = id, Command = "gh-mcp", Args = ["serve"],
        Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "{{secret.TOKEN}}", ["DEBUG"] = "1" },
    };

    [Fact]
    public void Secrets_GoToTheSecureStoreNotTheFile()
    {
        var store = this.Store();
        var saved = store.Save(Stdio(), new Dictionary<string, string?> { ["TOKEN"] = "ghp_secret" });

        Assert.Equal(["TOKEN"], saved.Secrets);
        Assert.DoesNotContain("ghp_secret", File.ReadAllText(this.Paths.McpServersFile));
        Assert.True(store.HasSecret("gh", "TOKEN"));
        Assert.Empty(store.MissingSecrets(saved));

        var resolved = store.Resolve(saved);
        Assert.Equal("ghp_secret", resolved.Env!["GITHUB_TOKEN"]);
        Assert.Equal("{{secret.TOKEN}}", store.GetRequired("gh").Env!["GITHUB_TOKEN"]);

        Assert.True(store.Remove("gh"));
        Assert.False(store.HasSecret("gh", "TOKEN"));
    }

    [Fact]
    public void Secrets_EnvironmentOverridesAndMissingOnesFail()
    {
        var store = this.Store();
        var saved = store.Save(Stdio("env-test"));
        Assert.Equal(["TOKEN"], saved.Secrets);
        Assert.Equal(["TOKEN"], store.MissingSecrets(saved));
        var problem = Assert.Throws<AflException>(() => store.Resolve(saved));
        Assert.Contains("afl mcp-secret env-test TOKEN", problem.Message);

        var variable = McpServerStore.SecretVariable("env-test", "TOKEN");
        Assert.Equal("AFL_MCP_SECRET_ENV_TEST_TOKEN", variable);
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            Assert.Equal("from-env", store.Resolve(saved).Env!["GITHUB_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void SaveExtractingSecrets_MovesSecretLookingValues()
    {
        var store = this.Store();
        var saved = store.SaveExtractingSecrets(new McpServerDefinition
        {
            Id = "remote", Transport = AgentConstants.TransportHttp, Url = "https://mcp.example.com/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc123", ["X-Team"] = "core" },
        });

        Assert.Equal("Bearer {{secret.AUTHORIZATION}}", saved.Headers!["Authorization"]);
        Assert.Equal("core", saved.Headers["X-Team"]);
        Assert.DoesNotContain("abc123", File.ReadAllText(this.Paths.McpServersFile));
        Assert.Equal("Bearer abc123", store.Resolve(saved).Headers!["Authorization"]);

        Assert.True(McpServerStore.LooksSecret("API_KEY", "x"));
        Assert.False(McpServerStore.LooksSecret("API_KEY", "${API_KEY}"));
        Assert.False(McpServerStore.LooksSecret("DEBUG", "1"));
    }

    [Fact]
    public void Validate_ReportsProblems()
    {
        Assert.Contains(McpServerStore.Validate(new McpServerDefinition { Id = "Bad Id", Command = "x" }), p => p.Contains("server id"));
        Assert.Contains(McpServerStore.Validate(new McpServerDefinition { Id = "x" }), p => p.Contains("'command'"));
        Assert.Contains(McpServerStore.Validate(new McpServerDefinition { Id = "x", Transport = "http" }), p => p.Contains("'url'"));
        Assert.Contains(McpServerStore.Validate(new McpServerDefinition
        {
            Id = "x", Command = "x", Env = new Dictionary<string, string> { ["A"] = "{{prompt}}" },
        }), p => p.Contains("isn't a secret"));
        var valid = Stdio();
        valid.Secrets = ["TOKEN"];
        Assert.Empty(McpServerStore.Validate(valid));
        Assert.Equal("github-server", McpServerStore.IdFrom("GitHub Server"));
    }

    [Fact]
    public async Task ListTools_ReturnsNamesAndArguments()
    {
        var store = this.InProcess();
        var server = store.Save(new McpServerDefinition { Id = "local", Command = "x" });

        var tool = Assert.Single(await store.ListToolsAsync(server, this.RepoPath));

        Assert.Equal("ask", tool.Name);
        Assert.Equal(["prompt", "cwd"], tool.ArgumentNames);
        Assert.Equal(["prompt"], tool.RequiredArguments);
    }

    [Fact]
    public async Task McpAgent_ConnectsThroughTheRegistryWithSecretsResolved()
    {
        var servers = this.InProcess();
        servers.Save(Stdio(), new Dictionary<string, string?> { ["TOKEN"] = "ghp_secret" });
        var agents = new AgentStore(this.Paths, servers);
        agents.Save(new AgentDefinition
        {
            Id = "gh-agent", Kind = AgentConstants.KindMcp, Server = "gh", Tool = "ask",
            Arguments = new Dictionary<string, JsonNode?> { ["prompt"] = "{{prompt}}" },
        });

        var result = await agents.CreateExecutor("gh-agent").ExecuteAsync(new ExecutionRequest
        {
            Prompt = "hi", WorkingDirectory = this.RepoPath, SessionId = "s", PermissionMode = "default",
        }, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal("ask: hi", result.Output);
        Assert.Equal("ghp_secret", Assert.Single(servers.Connected).Env!["GITHUB_TOKEN"]);

        var problem = Assert.Throws<AflException>(() => agents.Save(new AgentDefinition
        {
            Id = "lost", Kind = AgentConstants.KindMcp, Server = "nowhere", Tool = "ask",
        }));
        Assert.Contains("no MCP server 'nowhere'", problem.Message);
    }

    [Fact]
    public void Presets_AddTheirServer()
    {
        var agents = new AgentStore(this.Paths, this.Store());
        agents.Save(AgentStore.GetPreset("codex-mcp"));

        var server = agents.Servers.GetRequired("codex");
        Assert.Equal("codex", server.Command);
        Assert.Equal(["mcp-server"], server.Args);
    }

    [Fact]
    public void WriteClientConfig_WritesResolvedServersPrivately()
    {
        var store = this.Store();
        var server = store.Save(Stdio(), new Dictionary<string, string?> { ["TOKEN"] = "ghp_secret" });
        var path = Path.Combine(this.Root, "mcp.json");

        store.WriteClientConfig([server], path);

        var config = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("gh-mcp", config["mcpServers"]!["gh"]!["command"]!.GetValue<string>());
        Assert.Equal("ghp_secret", config["mcpServers"]!["gh"]!["env"]!["GITHUB_TOKEN"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void ConfigSources_FindServersInEachApp()
    {
        var home = Path.Combine(this.Root, "home");
        var repo = this.RepoPath;
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Write(Path.Combine(home, ".claude.json"), $$"""
            {
              "mcpServers": { "user-one": { "command": "one", "args": ["a"] } },
              "projects": { {{JsonSerializer.Serialize(repo)}}: { "mcpServers": { "proj": { "type": "http", "url": "https://p.example/mcp" } } } }
            }
            """);
        Write(Path.Combine(repo, ".mcp.json"), """{ "mcpServers": { "shared": { "command": "shared", "env": { "KEY": "k" } } } }""");
        var desktop = new ClaudeDesktopMcpConfigSource { Home = home };
        Write(desktop.ConfigPaths(null).Single(), """
            // comments are allowed
            { "mcpServers": { "Desk Top": { "command": "desk", }, }, }
            """);
        Write(Path.Combine(home, ".cursor", "mcp.json"), """{ "mcpServers": { "cur": { "url": "https://c.example/mcp", "headers": { "Authorization": "Bearer zz" } } } }""");
        Write(Path.Combine(home, ".codex", "config.toml"), """
            model = "gpt-5"

            [mcp_servers.docs]
            command = "npx"
            args = ["-y", "docs-mcp"]

            [mcp_servers.docs.env]
            DOCS_TOKEN = "dt"

            [mcp_servers.remote]
            url = "https://r.example/mcp"
            bearer_token_env_var = "REMOTE_TOKEN"
            """);

        var problems = new List<string>();
        var found = McpConfigSourceRegistry.Instance.FindAll(repo, home: home, problems: problems);

        Assert.Empty(problems);
        var byId = found.ToDictionary(c => $"{c.Source}/{c.Server.Id}");
        Assert.Equal(["a"], byId["claude-code/user-one"].Server.Args);
        Assert.Equal(AgentConstants.TransportHttp, byId["claude-code/proj"].Server.Transport);
        Assert.Equal("shared", byId["claude-code/shared"].Server.Command);
        Assert.Equal("desk", byId["claude-desktop/desk-top"].Server.Command);
        Assert.Equal("Desk Top", byId["claude-desktop/desk-top"].Server.Name);
        Assert.Equal("Bearer zz", byId["cursor/cur"].Server.Headers!["Authorization"]);
        Assert.Equal(["-y", "docs-mcp"], byId["codex/docs"].Server.Args);
        Assert.Equal("dt", byId["codex/docs"].Server.Env!["DOCS_TOKEN"]);
        Assert.Equal("Bearer ${REMOTE_TOKEN}", byId["codex/remote"].Server.Headers!["Authorization"]);

        Assert.Single(McpConfigSourceRegistry.Instance.FindAll(repo, CursorMcpConfigSource.SourceName, home));

        var store = this.Store();
        var imported = store.Import([byId["codex/docs"], byId["codex/remote"], byId["cursor/cur"]]);
        Assert.Equal("{{secret.DOCS_TOKEN}}", imported[0].Env!["DOCS_TOKEN"]);
        Assert.Equal("Bearer ${REMOTE_TOKEN}", imported[1].Headers!["Authorization"]);
        Assert.Equal("codex", imported[0].Source);
        Assert.Equal("Bearer {{secret.AUTHORIZATION}}", imported[2].Headers!["Authorization"]);
        var text = File.ReadAllText(this.Paths.McpServersFile);
        Assert.DoesNotContain("\"dt\"", text);
        Assert.DoesNotContain("zz", text);
    }

    [Fact]
    public void ConfigSources_ReportUnreadableFiles()
    {
        var home = Path.Combine(this.Root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".cursor"));
        File.WriteAllText(Path.Combine(home, ".cursor", "mcp.json"), "{ not json");
        var problems = new List<string>();

        Assert.Empty(McpConfigSourceRegistry.Instance.FindAll(null, CursorMcpConfigSource.SourceName, home, problems));
        Assert.Contains("Cursor", Assert.Single(problems));
    }
}
