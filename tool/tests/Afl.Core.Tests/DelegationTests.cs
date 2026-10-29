using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the AI handing sub-tasks to other agents part-way through a step (<c>afl-delegate</c> blocks).
/// </summary>
public class DelegationTests : AbstractAgentPoolTest
{
    private static AiStepDefinition Build(List<string>? helpersNeed = null) => new()
    {
        Id = "build", Name = "Build", Prompt = "Build it", Delegate = true, DelegateCapabilities = helpersNeed,
    };

    private static string Block(string tasks) => $"Started.\n\n```afl-delegate\n{{\"tasks\": [{tasks}]}}\n```";

    /// <summary>
    ///     Makes the main agent answer each prompt from a script, in order, then echo.
    /// </summary>
    private void Script(params string[] replies)
    {
        var queue = new Queue<string>(replies);
        this.Fake("main").Respond = r =>
            new ExecutionResult(ExecutionStatus.Completed, queue.Count > 0 ? queue.Dequeue() : $"main did: {r.Prompt}", r.SessionId ?? "main-session");
    }

    [Fact]
    public async Task Tasks_RunOnCapableAgentsAndTheAiCarriesOnWithTheResults()
    {
        this.Script(Block("""{"capabilities": ["review"], "prompt": "Review the plan"}, {"agent": "gamma", "prompt": "Write the code"}"""), "All done.");
        var run = this.Start("main", Build());

        run = await this.Engine.DriveAsync(run.Id);

        var main = this.Fake("main").Requests;
        Assert.Contains("```afl-delegate", main[0].Prompt);
        Assert.Contains("- alpha: The alpha agent.", main[0].Prompt);
        Assert.Contains("- gamma:", main[0].Prompt);
        Assert.DoesNotContain("- main:", main[0].Prompt);
        Assert.DoesNotContain("- session:", main[0].Prompt);
        Assert.DoesNotContain("- missing:", main[0].Prompt);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal("All done.", step.Output);
        Assert.Equal(1, step.Attempts);
        Assert.Equal(["alpha", "gamma"], step.Delegations.Select(d => d.Agent));
        Assert.All(step.Delegations, d => Assert.Equal((1, StepStatus.Done), (d.Round, d.Status)));
        Assert.Equal("alpha did: Review the plan", step.Delegations[0].Output);
        Assert.Equal(["review"], step.Delegations[0].Capabilities);
        Assert.EndsWith("build.delegate-2.log", this.Fake("gamma").Requests[0].LogPath);

        Assert.True(main[1].Resume);
        Assert.StartsWith(StepConstants.DelegationResultsHeading, main[1].Prompt);
        Assert.Contains("### Task 1: alpha (done)\n\nalpha did: Review the plan", main[1].Prompt);
        Assert.Contains("### Task 2: gamma (done)\n\ngamma did: Write the code", main[1].Prompt);
    }

    [Fact]
    public async Task NonResumableAgent_GetsThePromptAgainWithItsEarlierOutput()
    {
        this.Fake("main").Resumes = false;
        this.Script(Block("""{"agent": "beta", "prompt": "Check"}"""), "Done.");
        var run = this.Start("main", Build());

        run = await this.Engine.DriveAsync(run.Id);

        var again = this.Fake("main").Requests[1];
        Assert.False(again.Resume);
        Assert.StartsWith("Build it", again.Prompt);
        Assert.Contains($"{StepConstants.PreviousOutputHeading}\n\nStarted.", again.Prompt);
        Assert.Contains("beta did: Check", again.Prompt);
        Assert.Contains("```afl-delegate", again.Prompt);
        Assert.Equal("Done.", run.Steps[0].Output);
    }

    [Fact]
    public async Task Rounds_AreLimited()
    {
        var block = Block("""{"agent": "beta", "prompt": "Again"}""");
        this.Script(block, block, block, block);
        var run = this.Start("main", Build());

        run = await this.Engine.DriveAsync(run.Id);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(StepConstants.MaxDelegationRounds, step.Delegations.Count);
        Assert.Equal("Started.", step.Output);
        Assert.Contains(step.Notes, n => n.Contains($"already handed off work {StepConstants.MaxDelegationRounds} times"));
    }

    [Theory]
    [InlineData("""{"agent": "main", "prompt": "Me"}""", "names agent 'main'")]
    [InlineData("""{"agent": "missing", "prompt": "X"}""", "names agent 'missing'")]
    [InlineData("""{"capabilities": ["web"], "prompt": "Search"}""", "needs web")]
    [InlineData("""{"agent": "beta"}""", "task 1 has no prompt")]
    [InlineData("", "has no tasks")]
    public async Task UnusableBlock_FinishesTheStepWithANote(string tasks, string why)
    {
        this.Script(Block(tasks));
        var run = this.Start("main", Build());

        run = await this.Engine.DriveAsync(run.Id);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal("Started.", step.Output);
        Assert.Empty(step.Delegations);
        Assert.Contains(step.Notes, n => n.Contains("tried to hand off work") && n.Contains(why));
    }

    [Fact]
    public async Task DelegateCapabilities_NarrowTheHelpers()
    {
        this.Script(Block("""{"agent": "gamma", "prompt": "Code"}"""));
        var run = this.Start("main", Build(["review"]));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.DoesNotContain("- gamma:", this.Fake("main").Requests[0].Prompt);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("names agent 'gamma'"));
    }

    [Fact]
    public async Task FailedTask_IsReportedToTheAi()
    {
        this.Fake("beta").Respond = _ => new ExecutionResult(ExecutionStatus.Failed, null, null, "boom");
        this.Script(Block("""{"agent": "beta", "prompt": "Try"}"""), "Coped.");
        var run = this.Start("main", Build());

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.Failed, run.Steps[0].Delegations[0].Status);
        Assert.Equal("boom", run.Steps[0].Delegations[0].Error);
        Assert.Contains("### Task 1: beta (failed)\n\nboom", this.Fake("main").Requests[1].Prompt);
        Assert.Equal("Coped.", run.Steps[0].Output);
    }

    [Fact]
    public async Task Replay_ArchivesTheDelegations()
    {
        this.Script(Block("""{"agent": "beta", "prompt": "Check"}"""), "Done.");
        var run = this.Start("main", Build());
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.ReplayAsync(run.Id, "build");

        Assert.Single(run.Steps[0].History.Last().Delegations!);
        Assert.Empty(run.Steps[0].Delegations);
    }

    [Fact]
    public async Task Session_RunsTheTasksWhenItSubmits()
    {
        var run = this.Start(SessionExecutor.ExecutorName, Build());
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(StepStatus.AwaitingSession, run.Steps[0].Status);
        Assert.Contains("- beta:", run.Steps[0].PendingPrompt);

        run = await this.Engine.SubmitAsync(run.Id, Block("""{"agent": "beta", "prompt": "Check"}"""));

        var step = run.Steps[0];
        Assert.Equal(StepStatus.AwaitingSession, step.Status);
        Assert.Equal(StepStatus.Done, step.Delegations[0].Status);
        Assert.Contains("beta did: Check", step.PendingPrompt);

        run = await this.Engine.SubmitAsync(run.Id, "Finished.");
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal("Finished.", run.Steps[0].Output);
    }

    [Fact]
    public void Validate_ChecksDelegateSettings()
    {
        string[] Problems(AbstractStepDefinition step) => [.. WorkflowLibrary.Validate(Workflow(step))];

        Assert.Empty(Problems(Build(["review"])));
        Assert.Contains(Problems(ReadStep("""{ "id": "u", "name": "U", "type": "user", "delegate": true }""")),
            p => p.Contains("doesn't take 'delegate'"));
        Assert.Contains(Problems(new AiStepDefinition { Id = "a", Name = "A", Prompt = "x", DelegateCapabilities = ["review"] }),
            p => p.Contains("only applies with 'delegate'"));
        Assert.Contains(Problems(Build(["Bad"])), p => p.Contains("delegate capability 'Bad'"));
        Assert.Equal([StepConstants.AskBlockTag, StepConstants.DelegateBlockTag, StepConstants.TicketBlockTag], ReplyBlockRegistry.Instance.All.Select(b => b.Tag));
    }
}
