using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Settings;
using Afl.Core.Terminals;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for run modes: command-line agents running in a terminal you can watch (through a stand-in terminal that
///     runs the wrapper script directly), and the run's mode reaching each step.
/// </summary>
public class RunModeTests : AbstractAgentPoolTest
{
    private string LogPath => Path.Combine(this.Root, "run", "build.log");

    /// <summary>
    ///     Writes a stand-in agent command.
    /// </summary>
    /// <param name="body">The script's body.</param>
    /// <returns>The script's path.</returns>
    private string Stub(string body)
    {
        var path = Path.Combine(this.Root, "agent-stub-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private ExecutionRequest Request(string mode = AgentConstants.RunModeTerminal) => new()
    {
        Prompt = "Do it", WorkingDirectory = this.RepoPath, SessionId = "s1", PermissionMode = StepConstants.DefaultPermissionMode,
        LogPath = this.LogPath, RunMode = mode,
    };

    /// <summary>
    ///     Creates an executor for an agent that opens the stand-in terminal.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="terminal">The terminal, or null for one that runs the script.</param>
    /// <returns>The executor.</returns>
    private static AbstractStepExecutor Executor(AgentDefinition agent, AbstractTerminalLauncher? terminal = null)
    {
        var executor = AgentStore.CreateExecutor(agent);
        executor.Terminal = terminal ?? new ScriptTerminal();
        return executor;
    }

    [Fact]
    public async Task Cli_RunsInTheTerminalAndItsOutputIsReadBack()
    {
        var agent = new AgentDefinition
        {
            Id = "tool", Kind = AgentConstants.KindCli, PromptVia = AgentConstants.PromptViaStdin, Args = ["--flag", "it's"],
            Command = this.Stub("read line; echo \"got $line\"; echo \"args $*\"; echo 'warming up' >&2"),
        };
        var lines = new List<string>();

        var result = await Executor(agent).ExecuteAsync(this.Request(), new SyncProgress(e => lines.Add(e.Text)), CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal("got Do it\nargs --flag it's", result.Output);
        Assert.Equal(["got Do it", "args --flag it's"], lines);
        var log = File.ReadAllText(this.LogPath);
        Assert.Contains("(running in terminal mode)", log);
        Assert.Contains("got Do it", log);
    }

    [Fact]
    public async Task Cli_FailureInTheTerminalReportsTheExitCodeAndErrors()
    {
        var agent = new AgentDefinition { Id = "tool", Kind = AgentConstants.KindCli, Command = this.Stub("echo 'it broke' >&2; exit 3") };

        var result = await Executor(agent).ExecuteAsync(this.Request(), null, CancellationToken.None);

        Assert.Equal((ExecutionStatus.Failed, "it broke"), (result.Status, result.Error));

        agent.Command = this.Stub("exit 4");
        result = await Executor(agent).ExecuteAsync(this.Request(), null, CancellationToken.None);
        Assert.Contains("exited with code 4", result.Error);
    }

    [Fact]
    public async Task Claude_PrintsPlainTextWhenWatched()
    {
        var agent = new AgentDefinition
        {
            Id = "claude-stub", Kind = ClaudeCliExecutor.ExecutorName,
            Command = this.Stub("echo \"$*\" > '" + Path.Combine(this.Root, "claude-args") + "'; echo 'Plain answer.'"),
        };

        var result = await Executor(agent).ExecuteAsync(this.Request(), null, CancellationToken.None);

        Assert.Equal((ExecutionStatus.Completed, "Plain answer.", "s1"), (result.Status, result.Output, result.SessionId));
        var arguments = File.ReadAllText(Path.Combine(this.Root, "claude-args"));
        Assert.Contains("--output-format text", arguments);
        Assert.DoesNotContain("stream-json", arguments);

        result = await Executor(agent).ExecuteAsync(this.Request(AgentConstants.RunModeBackground), null, CancellationToken.None);
        Assert.Contains("stream-json", File.ReadAllText(Path.Combine(this.Root, "claude-args")));
        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Claude_ReadsDenialsAndPassesDenyRules()
    {
        var argsFile = Path.Combine(this.Root, "claude-args");
        var result = "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s1\",\"result\":\"Partly done.\"," +
                     "\"permission_denials\":[{\"tool_name\":\"Bash\",\"tool_use_id\":\"t1\",\"tool_input\":{\"command\":\"dotnet test x\"}}," +
                     "{\"tool_name\":\"WebFetch\",\"tool_use_id\":\"t2\",\"tool_input\":{\"url\":\"https://example.com/a\"}}]}";
        var agent = new AgentDefinition
        {
            Id = "claude-stub", Kind = ClaudeCliExecutor.ExecutorName,
            Command = this.Stub($"echo \"$*\" > '{argsFile}'; echo '{result}'"),
        };
        var request = this.Request(AgentConstants.RunModeBackground);
        request.DisallowedTools = ["Bash(git push:*)", "Read(~/.ssh/**)"];

        var outcome = await Executor(agent).ExecuteAsync(request, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, outcome.Status);
        Assert.Equal(["Bash: dotnet test x", "WebFetch: https://example.com/a"], outcome.Denials!.Select(d => d.Display));
        Assert.Equal("Bash(dotnet test x)", outcome.Denials![0].SuggestedRules[0]);
        Assert.Equal("WebFetch(domain:example.com)", outcome.Denials[1].SuggestedRules[0]);
        Assert.Contains("--disallowedTools Bash(git push:*),Read(~/.ssh/**)", File.ReadAllText(argsFile));
    }

    [Fact]
    public async Task Cancelling_StopsTheCommand()
    {
        var marker = Path.Combine(this.Root, "finished");
        var agent = new AgentDefinition
        {
            Id = "tool", Kind = AgentConstants.KindCli, Command = this.Stub($"echo started; sleep 20; touch '{marker}'"),
        };
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(e =>
        {
            if (e.Text == "started")
            {
                cancel.Cancel();
            }
        });
        var started = DateTimeOffset.Now;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(agent).ExecuteAsync(this.Request(), progress, cancel.Token));

        Assert.True(DateTimeOffset.Now - started < TimeSpan.FromSeconds(15));
        await Task.Delay(500);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ClosingTheWindow_FailsTheTurn()
    {
        var grace = TerminalRunMode.ExitGrace;
        TerminalRunMode.ExitGrace = TimeSpan.FromMilliseconds(300);
        try
        {
            var agent = new AgentDefinition { Id = "tool", Kind = AgentConstants.KindCli, Command = this.Stub("echo never") };

            var result = await Executor(agent, new ClosedTerminal()).ExecuteAsync(this.Request(), null, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Contains("terminal window was closed", result.Error);
        }
        finally
        {
            TerminalRunMode.ExitGrace = grace;
        }
    }

    [Fact]
    public async Task RunMode_ComesFromTheRunOrTheSettingsAndReachesEachStep()
    {
        var step = new AiStepDefinition { Id = "build", Name = "Build", Prompt = "Build it" };

        var run = this.Engine.Create(this.RegisterRepo(), Workflow(step), SampleTicket(), "main", AgentConstants.RunModeTerminal);
        Assert.Equal(AgentConstants.RunModeTerminal, run.Mode);
        await this.Engine.DriveAsync(run.Id);
        Assert.Equal(AgentConstants.RunModeTerminal, this.Fake("main").Requests[0].RunMode);

        Assert.Equal(AgentConstants.RunModeBackground, this.Start("main", step).Mode);
        new SettingsStore(this.Paths).Save(new AflSettings { DefaultRunMode = AgentConstants.RunModeTerminal });
        Assert.Equal(AgentConstants.RunModeTerminal, this.Start("main", step).Mode);

        var ex = Assert.Throws<AflException>(() => this.Engine.Create(this.RegisterRepo(), Workflow(step), SampleTicket(), "main", "window"));
        Assert.Contains("no run mode 'window'", ex.Message);
        Assert.Contains(SettingsStore.Validate(new AflSettings { DefaultRunMode = "window" }), p => p.Contains("defaultRunMode"));
    }

    [Fact]
    public void OnlyAgentsThatRunACommand_CanRunInTerminal()
    {
        Assert.True(AgentStore.CreateExecutor(new AgentDefinition { Id = "c", Kind = ClaudeCliExecutor.ExecutorName }).CanRunInTerminal);
        Assert.True(AgentStore.CreateExecutor(new AgentDefinition { Id = "t", Kind = AgentConstants.KindCli, Command = "x" }).CanRunInTerminal);
        Assert.False(AgentStore.CreateExecutor(new AgentDefinition { Id = "m", Kind = AgentConstants.KindMcp, Server = "s", Tool = "t" }).CanRunInTerminal);
        Assert.False(AgentStore.CreateExecutor(new AgentDefinition { Id = "s", Kind = SessionExecutor.ExecutorName }).CanRunInTerminal);
        Assert.Equal([AgentConstants.RunModeBackground, AgentConstants.RunModeTerminal], RunModeRegistry.Instance.Keys.Order());
        Assert.Equal(AgentConstants.RunModeBackground, RunModeRegistry.Instance.Resolve("from-the-future").Name);
    }

    /// <summary>
    ///     A stand-in terminal that runs the wrapper script directly, out of sight.
    /// </summary>
    private sealed class ScriptTerminal() : AbstractUnixTerminalLauncher("script-test")
    {
        /// <inheritdoc />
        public override bool IsSupported => false;

        /// <inheritdoc />
        protected override (string Executable, IReadOnlyList<string> Arguments) BuildTerminalCommand(string scriptPath, string workingDirectory)
        {
            return ("/bin/sh", [scriptPath]);
        }
    }

    /// <summary>
    ///     A stand-in terminal that's closed straight away: it records a process id that's gone and never runs the command.
    /// </summary>
    private sealed class ClosedTerminal() : AbstractUnixTerminalLauncher("closed-test")
    {
        /// <inheritdoc />
        public override bool IsSupported => false;

        /// <inheritdoc />
        protected override (string Executable, IReadOnlyList<string> Arguments) BuildTerminalCommand(string scriptPath, string workingDirectory)
        {
            var filesBase = Path.ChangeExtension(scriptPath, null);
            return ("/bin/sh", ["-c", $"echo $$ > {Quote(filesBase + StepConstants.TerminalProcessExtension)}"]);
        }
    }

    /// <summary>
    ///     Reports progress on the calling thread, so the test sees every event before the turn ends.
    /// </summary>
    /// <param name="onEvent">Receives each event.</param>
    private sealed class SyncProgress(Action<ExecutorEvent> onEvent) : IProgress<ExecutorEvent>
    {
        /// <inheritdoc />
        public void Report(ExecutorEvent value)
        {
            onEvent(value);
        }
    }
}
