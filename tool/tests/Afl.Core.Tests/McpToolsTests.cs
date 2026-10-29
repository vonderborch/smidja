using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Mcp;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for giving agents MCP servers as extra tools: the client configuration they're handed, its clean-up, and
///     which agents can do a step that lists servers.
/// </summary>
public class McpToolsTests : AbstractAgentPoolTest
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="McpToolsTests" /> class, registering a stdio server with a secret
    ///     (<c>gh</c>) and an http one (<c>docs</c>).
    /// </summary>
    public McpToolsTests()
    {
        this.Agents.Servers.Save(new McpServerDefinition
            {
                Id = "gh", Command = "gh-mcp", Args = ["serve"],
                Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "{{secret.TOKEN}}" },
            },
            new Dictionary<string, string?> { ["TOKEN"] = "ghp_secret" });
        this.Agents.Servers.Save(new McpServerDefinition { Id = "docs", Transport = AgentConstants.TransportHttp, Url = "https://docs.test/mcp" });
    }

    private string Capture => Path.Combine(this.Root, "capture");

    private string RunDirectory => Path.Combine(this.Root, "run");

    /// <summary>
    ///     Writes a stand-in agent command that records its arguments and the MCP configuration it's handed (with the
    ///     file's permissions), then prints an answer.
    /// </summary>
    /// <param name="answer">What it prints.</param>
    /// <returns>The script's path.</returns>
    private string Stub(string answer)
    {
        Directory.CreateDirectory(this.Capture);
        var path = Path.Combine(this.Root, "agent-stub");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            : > '{{this.Capture}}/args'
            prev=""
            for a in "$@"; do
              printf '%s\n' "$a" >> '{{this.Capture}}/args'
              if [ "$prev" = "--mcp-config" ]; then
                cat "$a" > '{{this.Capture}}/config'
                ls -l "$a" | cut -c1-10 > '{{this.Capture}}/mode'
              fi
              prev="$a"
            done
            echo '{{answer}}'
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private ExecutionRequest Request(params string[] servers) => new()
    {
        Prompt = "Do it", WorkingDirectory = this.RepoPath, SessionId = "s1", PermissionMode = StepConstants.DefaultPermissionMode,
        LogPath = Path.Combine(this.RunDirectory, "build.log"), McpServers = servers,
        McpConfigPath = Path.Combine(this.RunDirectory, "build" + StepConstants.McpConfigExtension),
    };

    private string[] Arguments => File.ReadAllLines(Path.Combine(this.Capture, "args"));

    private string? ArgumentAfter(string name)
    {
        var arguments = this.Arguments;
        var index = Array.IndexOf(arguments, name);
        return index < 0 ? null : arguments[index + 1];
    }

    [Fact]
    public async Task Claude_GetsItsServersInAPrivateFileDeletedAfterwards()
    {
        var agent = new AgentDefinition
        {
            Id = "claude-stub", Kind = ClaudeCliExecutor.ExecutorName, McpServers = ["docs"],
            Command = this.Stub("""{"type":"result","result":"done","session_id":"s1"}"""),
        };
        var executor = AgentStore.CreateExecutor(agent, this.Agents.Servers);

        var result = await executor.ExecuteAsync(this.Request("gh"), null, CancellationToken.None);

        Assert.Equal((ExecutionStatus.Completed, "done"), (result.Status, result.Output));
        var file = this.ArgumentAfter("--mcp-config")!;
        Assert.NotEqual(this.RunDirectory, Path.GetDirectoryName(file));
        Assert.Contains("--strict-mcp-config", this.Arguments);
        Assert.False(File.Exists(file));
        Assert.Equal("-rw-------", File.ReadAllText(Path.Combine(this.Capture, "mode")).Trim());

        var servers = JsonNode.Parse(File.ReadAllText(Path.Combine(this.Capture, "config")))!["mcpServers"]!;
        Assert.Equal("ghp_secret", (string?)servers["gh"]!["env"]!["GITHUB_TOKEN"]);
        Assert.Equal(("http", "https://docs.test/mcp"), ((string?)servers["docs"]!["type"], (string?)servers["docs"]!["url"]));
        Assert.DoesNotContain("ghp_secret", File.ReadAllText(Path.Combine(this.RunDirectory, "build.log")));

        await executor.ExecuteAsync(this.Request(), null, CancellationToken.None);
        Assert.NotNull(this.ArgumentAfter("--mcp-config"));
        Assert.DoesNotContain("--strict-mcp-config", this.Arguments);

        agent.McpServers = null;
        await executor.ExecuteAsync(this.Request(), null, CancellationToken.None);
        Assert.DoesNotContain("--mcp-config", this.Arguments);
        Assert.Empty(Directory.GetFiles(this.RunDirectory, "*" + StepConstants.McpConfigExtension));
    }

    [Fact]
    public void Claude_InteractiveSessionKeepsItsFileUntilTheStepIsDone()
    {
        var executor = AgentStore.CreateExecutor(new AgentDefinition { Id = "c", Kind = ClaudeCliExecutor.ExecutorName }, this.Agents.Servers);
        var request = this.Request("gh");

        var launch = executor.GetInteractiveLaunch(request)!;

        var index = launch.Arguments.IndexOf("--mcp-config");
        Assert.Equal(request.McpConfigPath, launch.Arguments[index + 1]);
        Assert.True(File.Exists(request.McpConfigPath));
        AbstractStepExecutor.DeleteMcpConfig(request.McpConfigPath);
        Assert.False(File.Exists(request.McpConfigPath));
    }

    [Fact]
    public async Task Cli_UsesItsMcpArgsOnlyWhenItGetsServers()
    {
        var agent = new AgentDefinition
        {
            Id = "tool", Kind = AgentConstants.KindCli, Command = this.Stub("answer"), Args = ["run"],
            McpArgs = ["--mcp-config", "{{mcpConfigFile}}"],
        };
        var executor = AgentStore.CreateExecutor(agent, this.Agents.Servers);
        Assert.True(executor.SupportsMcpServers);
        Assert.Contains(CapabilityCatalog.McpTools, this.Agents.CapabilitiesOf(agent));

        var result = await executor.ExecuteAsync(this.Request("gh"), null, CancellationToken.None);

        Assert.Equal("answer", result.Output);
        var file = this.ArgumentAfter("--mcp-config")!;
        Assert.False(File.Exists(file));
        Assert.Contains("\"gh\"", File.ReadAllText(Path.Combine(this.Capture, "config")));

        await executor.ExecuteAsync(this.Request(), null, CancellationToken.None);
        Assert.DoesNotContain("--mcp-config", this.Arguments);

        agent.McpArgs = null;
        Assert.False(executor.SupportsMcpServers);
        Assert.DoesNotContain(CapabilityCatalog.McpTools, this.Agents.CapabilitiesOf(agent));
    }

    [Fact]
    public async Task Steps_WithServersGoOnlyToAgentsThatCanTakeThem()
    {
        var build = new AiStepDefinition { Id = "build", Name = "Build", Prompt = "Build", McpServers = ["gh"] };
        var ex = Assert.Throws<AflException>(() => this.Start("main", build));
        Assert.Contains("can't be given MCP servers (gh)", ex.Message);

        this.Fake("main").TakesMcpServers = true;
        var run = await this.Engine.DriveAsync(this.Start("main", build).Id);
        var request = this.Fake("main").Requests.Single();
        Assert.Equal(["gh"], request.McpServers);
        Assert.EndsWith("build" + StepConstants.McpConfigExtension, request.McpConfigPath);
        Assert.Equal("main did: Build", run.Steps[0].Output);

        this.Fake("beta").TakesMcpServers = true;
        var review = new AiStepDefinition
        {
            Id = "review", Name = "Review", Prompt = "Review", Capabilities = ["review"],
            Select = AgentConstants.SelectorFirst, McpServers = ["docs"],
        };
        run = await this.Engine.DriveAsync(this.Start("main", review).Id);
        Assert.Equal(["beta"], run.Steps[0].AgentChoice!.Candidates);

        var unknown = new AiStepDefinition { Id = "x", Name = "X", Prompt = "x", McpServers = ["nope"] };
        Assert.Contains("there's no MCP server 'nope'", Assert.Throws<AflException>(() => this.Start("main", unknown)).Message);
    }

    [Fact]
    public async Task OptionalServers_AreGivenWhenUsableAndOtherwiseSkippedWithANote()
    {
        var build = new AiStepDefinition { Id = "build", Name = "Build", Prompt = "Build", OptionalMcpServers = ["docs", "nope"] };
        var main = this.Fake("main");
        main.Servers = this.Agents.Servers;

        var run = await this.Engine.DriveAsync(this.Start("main", build).Id);
        Assert.Empty(main.Requests.Single().OptionalMcpServers);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Contains(string.Format(StepConstants.OptionalMcpServerSkippedFormat, "docs", "main"), run.Steps[0].Notes);

        main.TakesMcpServers = true;
        run = await this.Engine.DriveAsync(this.Start("main", build).Id);
        var request = main.Requests.Last();
        Assert.Equal(["docs"], request.OptionalMcpServers);
        Assert.Empty(request.McpServers);
        Assert.Equal(["docs"], main.McpServerIds(request));
        Assert.Equal([string.Format(StepConstants.OptionalMcpServerSkippedFormat, "nope", "main")], run.Steps[0].Notes);
    }

    [Fact]
    public void Validate_ChecksServerSettings()
    {
        string[] Problems(AbstractStepDefinition step) => [.. WorkflowLibrary.Validate(Workflow(step))];

        Assert.Empty(Problems(new AiStepDefinition { Id = "a", Name = "A", Prompt = "x", McpServers = ["gh"] }));
        Assert.Contains(Problems(ReadStep("""{ "id": "u", "name": "U", "type": "user", "mcpServers": ["gh"] }""")),
            p => p.Contains("doesn't take 'mcpServers'"));
        Assert.Contains(Problems(new AiStepDefinition { Id = "a", Name = "A", Prompt = "x", McpServers = ["Bad Id"] }),
            p => p.Contains("MCP server 'Bad Id'"));

        var agent = new AgentDefinition { Id = "t", Kind = AgentConstants.KindCli, Command = "t", McpServers = ["gh", "nope"] };
        var problems = AgentStore.Validate(agent, this.Agents.Servers);
        Assert.Contains(problems, p => p.Contains("needs mcpArgs"));
        Assert.Contains(problems, p => p.Contains("no MCP server 'nope'"));
        agent.McpArgs = ["--mcp-config", "{{mcpConfigFile}}"];
        agent.McpServers = ["gh"];
        Assert.Empty(AgentStore.Validate(agent, this.Agents.Servers));
    }
}
