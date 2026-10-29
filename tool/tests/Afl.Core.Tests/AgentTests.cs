using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Afl.Core.Workflows;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for agents: the store, the command-line and MCP executors, and picking an agent per step.
/// </summary>
public class AgentTests : AbstractAflTest
{
    private static AgentDefinition Shell(string script, params string[] extra) => new()
    {
        Id = "script", Name = "script", Kind = AgentConstants.KindCli, Command = "/bin/sh",
        Args = ["-c", script, "sh", .. extra],
    };

    private ExecutionRequest Request(string prompt = "hello world") => new()
    {
        Prompt = prompt, WorkingDirectory = this.RepoPath, SessionId = "sess-1", PermissionMode = "default",
        LogPath = Path.Combine(this.Root, "agent.log"),
    };

    private static Task<ExecutionResult> RunAsync(AgentDefinition agent, ExecutionRequest request)
    {
        return AgentStore.CreateExecutor(agent).ExecuteAsync(request, null, CancellationToken.None);
    }

    [Fact]
    public async Task CliAgent_PassesPromptAsLastArgument()
    {
        var result = await RunAsync(Shell("echo \"arg:$1\""), this.Request());

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal("arg:hello world", result.Output);
        Assert.Contains("arg:hello world", File.ReadAllText(Path.Combine(this.Root, "agent.log")));
    }

    [Fact]
    public async Task CliAgent_PassesPromptOnStdin()
    {
        var agent = Shell("read line; echo \"stdin:$line\"");
        agent.PromptVia = AgentConstants.PromptViaStdin;

        var result = await RunAsync(agent, this.Request());

        Assert.Equal("stdin:hello world", result.Output);
    }

    [Fact]
    public async Task CliAgent_PassesPromptInAFile()
    {
        var agent = Shell("cat \"$1\"");
        agent.PromptVia = AgentConstants.PromptViaFile;

        var result = await RunAsync(agent, this.Request("from a file"));

        Assert.Equal("from a file", result.Output);
    }

    [Fact]
    public async Task CliAgent_PicksAnswerAndSessionFromJsonLines()
    {
        var agent = Shell("echo '{\"type\":\"start\",\"session\":\"s-9\"}'; echo 'not json'; echo '{\"result\":\"done\",\"items\":[{\"t\":1}]}'");
        agent.Output = AgentConstants.OutputJson;
        agent.OutputPath = "result";
        agent.SessionPath = "session";

        var result = await RunAsync(agent, this.Request());

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal("done", result.Output);
        Assert.Equal("s-9", result.SessionId);

        agent.OutputPath = "items.0";
        Assert.Equal("{\"t\":1}", (await RunAsync(agent, this.Request())).Output);

        agent.OutputPath = "missing";
        Assert.Equal(ExecutionStatus.Failed, (await RunAsync(agent, this.Request())).Status);
    }

    [Fact]
    public async Task CliAgent_ReadsTheOutputFile()
    {
        var result = await RunAsync(Shell("printf 'from file' > \"$1\"; echo noise", "{{outputFile}}"), this.Request());

        Assert.Equal("from file", result.Output);
    }

    [Fact]
    public async Task CliAgent_AddsModelAndResumeArguments()
    {
        var agent = Shell("echo \"$@\"");
        agent.ModelArgs = ["--model", "{{model}}"];
        agent.ResumeArgs = ["--resume", "{{session}}"];
        agent.Env = new Dictionary<string, string> { ["AFL_TEST_CWD"] = "{{cwd}}" };
        var request = this.Request("go");
        request.Model = "m1";
        request.Resume = true;

        var result = await RunAsync(agent, request);

        Assert.Equal("--model m1 --resume sess-1 go", result.Output);
        Assert.True(AgentStore.CreateExecutor(agent).SupportsResume);

        agent.Args = ["-c", "echo \"$AFL_TEST_CWD\"", "sh"];
        Assert.Equal(this.RepoPath, (await RunAsync(agent, this.Request())).Output);
    }

    [Fact]
    public async Task CliAgent_FailsOnNonZeroExit()
    {
        var result = await RunAsync(Shell("echo oops >&2; exit 3"), this.Request());

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("oops", result.Error);
    }

    [Fact]
    public void CliAgent_InteractiveLaunchUsesInteractiveArgs()
    {
        var agent = Shell("x");
        Assert.Null(AgentStore.CreateExecutor(agent).GetInteractiveLaunch(this.Request()));

        agent.InteractiveArgs = ["-i", "{{prompt}}"];
        agent.Env = new Dictionary<string, string> { ["MODE"] = "chat" };
        var launch = AgentStore.CreateExecutor(agent).GetInteractiveLaunch(this.Request());

        Assert.NotNull(launch);
        Assert.Equal("/bin/sh", launch.Executable);
        Assert.Equal(["-i", "hello world"], launch.Arguments);
        Assert.Equal("chat", launch.Environment!["MODE"]);
    }

    [Fact]
    public void Validate_ReportsProblems()
    {
        var problems = AgentStore.Validate(new AgentDefinition
        {
            Id = "Bad Id", Kind = AgentConstants.KindCli, Args = ["{{nope}}"], Output = AgentConstants.OutputJson,
            Env = new Dictionary<string, string> { ["1X"] = "y" },
        });

        Assert.Contains(problems, p => p.Contains("agent id"));
        Assert.Contains(problems, p => p.Contains("'command'"));
        Assert.Contains(problems, p => p.Contains("nope"));
        Assert.Contains(problems, p => p.Contains("outputPath"));
        Assert.Contains(problems, p => p.Contains("1X"));

        Assert.Contains(AgentStore.Validate(new AgentDefinition { Id = "x", Kind = "robot" }), p => p.Contains("kind"));
        Assert.Contains(AgentStore.Validate(new AgentDefinition { Id = "x", Kind = AgentConstants.KindMcp, Tool = "t" }),
            p => p.Contains("'server'"));
        Assert.Empty(AgentStore.Validate(Shell("echo")));
    }

    [Fact]
    public void Store_KeepsBuiltInsAndSavesAgents()
    {
        var store = new AgentStore(this.Paths);
        Assert.Equal(["claude", "session"], store.All().Select(a => a.Id));

        store.Save(Shell("echo"));
        Assert.Equal("script", store.GetRequired("SCRIPT").Id);
        Assert.Throws<AflException>(() => store.Save(new AgentDefinition { Id = "claude", Kind = AgentConstants.KindCli, Command = "x" }));
        Assert.Throws<AflException>(() => store.Save(new AgentDefinition { Id = "x", Kind = AgentConstants.KindCli }));
        Assert.Throws<AflException>(() => store.Remove("session"));
        Assert.Throws<AflException>(() => store.GetRequired("nope"));
        Assert.IsType<CommandLineExecutor>(store.CreateExecutor("script"));

        Assert.True(store.Remove("script"));
        Assert.Equal(2, store.All().Count);
    }

    [Fact]
    public void Presets_AreValidAndDetectedOnPath()
    {
        Assert.Contains(AgentStore.Presets, p => p.Id == "codex");
        Assert.All(AgentStore.Presets, p => Assert.Empty(AgentStore.Validate(p)));

        var bin = Path.Combine(this.Root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "gemini"), "#!/bin/sh\n");

        var detected = AgentStore.Detect(bin);
        Assert.True(detected.Single(p => p.Agent.Id == "gemini").Installed);
        Assert.False(detected.Single(p => p.Agent.Id == "codex").Installed);

        var preset = AgentStore.GetPreset("gemini");
        preset.Model = "changed";
        Assert.NotEqual("changed", AgentStore.GetPreset("gemini").Model);
    }

    [Fact]
    public async Task Engine_PicksEachStepsAgent()
    {
        var main = new FakeStepExecutor();
        var other = new FakeStepExecutor { Respond = r => new ExecutionResult(ExecutionStatus.Completed, "other", r.SessionId) };
        other.Agent = new AgentDefinition { Id = "other", Kind = "fake", Model = "big" };
        var engine = new RunEngine(new RunStore(this.Paths), id => id switch
        {
            "main" => main,
            "other" => other,
            _ => throw new AflException(AflErrorKind.NotFound, id),
        });
        var workflow = Workflow(
            new AiStepDefinition { Id = "a", Name = "a", Prompt = "A" },
            new AiStepDefinition { Id = "b", Name = "b", Prompt = "B {{steps.a.output}}", Agent = "other" });

        Assert.Throws<AflException>(() => engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "unknown"));
        var run = engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main");
        run = await engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("A", Assert.Single(main.Requests).Prompt);
        Assert.Equal("B output for: A", Assert.Single(other.Requests).Prompt);
        Assert.Equal("big", other.Requests[0].Model);
        Assert.Equal("other", run.Steps[1].Output);
    }

    [Fact]
    public async Task Engine_RunsACommandLineAgentFromTheStore()
    {
        var store = new AgentStore(this.Paths);
        var agent = Shell("echo \"done: $1\"");
        agent.Id = "echoer";
        store.Save(agent);
        var engine = new RunEngine(this.Paths);
        var interactive = Workflow(new InteractiveStepDefinition { Id = "x", Name = "x", Prompt = "P" });

        var problem = Assert.Throws<AflException>(() => engine.Create(this.RegisterRepo(), interactive, SampleTicket(), "echoer"));
        Assert.Contains("interactive", problem.Message);

        var workflow = Workflow(new AiStepDefinition
        {
            Id = "a", Name = "a", Prompt = "{{ticket.key}}", PermissionMode = StepConstants.ReadOnlyPermissionMode,
        });
        var run = await engine.DriveAsync(engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "echoer").Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("done: acme/app#7", run.Steps[0].Output);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("permissionMode"));
        Assert.Contains("done: acme/app#7", File.ReadAllText(engine.Store.LogPath(run, "a")));
    }

    [Fact]
    public async Task Rework_WithoutResume_RepeatsPromptWithPreviousAnswer()
    {
        var executor = new FakeStepExecutor { Resumes = false };
        var engine = new RunEngine(new RunStore(this.Paths), executor);
        var run = engine.Create(this.RegisterRepo(),
            Workflow(new AiStepDefinition { Id = "plan", Name = "plan", Advance = "approve", Prompt = "Plan it" }),
            SampleTicket());
        await engine.DriveAsync(run.Id);

        await engine.RejectAsync(run.Id, "More detail");

        var rework = executor.Requests.Last();
        Assert.False(rework.Resume);
        Assert.StartsWith("Plan it", rework.Prompt);
        Assert.Contains($"{StepConstants.PreviousOutputHeading}\n\noutput for: Plan it", rework.Prompt);
        Assert.Contains($"{StepConstants.ReworkHeading}\n\nMore detail\n\n", rework.Prompt);

        executor.Resumes = true;
        await engine.RejectAsync(run.Id, "Shorter");
        Assert.True(executor.Requests.Last().Resume);
    }

    [Fact]
    public async Task McpAgent_CallsTheToolAndResumes()
    {
        var calls = new List<(string Tool, Dictionary<string, JsonElement> Arguments)>();
        var agent = new AgentDefinition
        {
            Id = "mcp", Kind = AgentConstants.KindMcp, Server = "unused", Tool = "ask",
            Arguments = new Dictionary<string, JsonNode?>
            {
                ["prompt"] = "{{prompt}}", ["cwd"] = "{{cwd}}", ["model"] = "{{model}}", ["effort"] = 3,
            },
            ResumeTool = "reply",
            ResumeArguments = new Dictionary<string, JsonNode?> { ["prompt"] = "{{prompt}}", ["threadId"] = "{{session}}" },
            SessionPath = "threadId",
        };
        var executor = new InProcessMcpExecutor((tool, arguments) =>
        {
            calls.Add((tool, arguments));
            var text = tool == "fail" ? "broken" : $"{tool}: {arguments["prompt"].GetString()}";
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }],
                StructuredContent = JsonSerializer.SerializeToElement(new { threadId = "t-1", answer = "structured" }),
                IsError = tool == "fail",
            };
        }) { Agent = agent };

        var first = await executor.ExecuteAsync(this.Request("hi"), null, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Completed, first.Status);
        Assert.Equal("ask: hi", first.Output);
        Assert.Equal("t-1", first.SessionId);
        Assert.Equal(this.RepoPath, calls[0].Arguments["cwd"].GetString());
        Assert.Equal(3, calls[0].Arguments["effort"].GetInt32());
        Assert.False(calls[0].Arguments.ContainsKey("model"));

        var resume = this.Request("again");
        resume.Resume = true;
        resume.SessionId = first.SessionId!;
        var second = await executor.ExecuteAsync(resume, null, CancellationToken.None);
        Assert.Equal("reply: again", second.Output);
        Assert.Equal("t-1", calls[1].Arguments["threadId"].GetString());

        agent.OutputPath = "answer";
        Assert.Equal("structured", (await executor.ExecuteAsync(this.Request(), null, CancellationToken.None)).Output);

        agent.Tool = "fail";
        var failed = await executor.ExecuteAsync(this.Request(), null, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Failed, failed.Status);
        Assert.Equal("broken", failed.Error);
    }

    /// <summary>
    ///     An MCP executor connected to an in-process server whose tools answer from a function.
    /// </summary>
    /// <param name="answer">Answers a tool call.</param>
    private sealed class InProcessMcpExecutor(Func<string, Dictionary<string, JsonElement>, CallToolResult> answer) : McpExecutor
    {
        /// <inheritdoc />
        protected override Task<McpClient> ConnectAsync(ExecutionRequest request, CancellationToken ct)
        {
            return InProcessMcp.ConnectAsync([], answer, ct);
        }
    }
}
