using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for steps whose agent is chosen by capability: the candidates, the preferred agent, the selectors and
///     picking an agent by hand.
/// </summary>
public class SelectorTests : AbstractAgentPoolTest
{
    private static AiStepDefinition Review(string? select = null, string? agent = null) => new()
    {
        Id = "review", Name = "Review", Prompt = "Review {{ticket.key}}", Capabilities = ["review"],
        Select = select, Agent = agent,
    };

    private static AiStepDefinition Ai(string id) => new() { Id = id, Name = id, Prompt = "Do " + id };

    [Fact]
    public async Task First_FollowsThePreferenceOrder()
    {
        var run = this.Start("main", Review(AgentConstants.SelectorFirst));
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal("alpha", run.Steps[0].AgentChoice!.AgentId);
        Assert.Equal(["alpha", "beta"], run.Steps[0].AgentChoice!.Candidates);
        Assert.Equal(AgentConstants.ChosenBySelector, run.Steps[0].AgentChoice!.By);
        Assert.Equal("alpha did: Review acme/app#7", run.Steps[0].Output);

        new SettingsStore(this.Paths).Save(new AflSettings { AgentPreference = ["beta"], DefaultSelector = AgentConstants.SelectorFirst });
        run = this.Start("main", Review());
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal("beta", run.Steps[0].AgentChoice!.AgentId);
        Assert.Equal(["beta", "alpha"], run.Steps[0].AgentChoice!.Candidates);
        Assert.Empty(this.Fake("main").Requests);
    }

    [Fact]
    public async Task Ai_AsksTheMainAgentAndReadsItsAnswer()
    {
        var main = this.Fake("main");
        main.Respond = r => new ExecutionResult(ExecutionStatus.Completed,
            "Beta is the reviewer.\n\n```afl-agent\n{\"agent\": \"beta\", \"reason\": \"it only reviews\"}\n```", r.SessionId);
        var run = this.Start("main", Review(AgentConstants.SelectorAi), Ai("after"));

        run = await this.Engine.DriveAsync(run.Id);

        var ask = main.Requests[0];
        Assert.Contains("Step: Review", ask.Prompt);
        Assert.Contains("Review acme/app#7", ask.Prompt);
        Assert.Contains("- alpha: The alpha agent. (capabilities: review, edit-code;", ask.Prompt);
        Assert.Contains("- beta:", ask.Prompt);
        Assert.DoesNotContain("- gamma:", ask.Prompt);
        Assert.DoesNotContain("- missing:", ask.Prompt);
        Assert.Equal(StepConstants.ReadOnlyPermissionMode, ask.PermissionMode);
        Assert.EndsWith("review.choose-agent.log", ask.LogPath);
        var choice = run.Steps[0].AgentChoice!;
        Assert.Equal(("beta", "it only reviews", AgentConstants.ChosenByMainAgent), (choice.AgentId, choice.Reason, choice.By));
        Assert.Equal("beta did: Review acme/app#7", run.Steps[0].Output);
        Assert.Null(run.Steps[1].AgentChoice);
        Assert.Equal("Do after", main.Requests[1].Prompt);
    }

    [Theory]
    [InlineData("No block here.", "no afl-agent block")]
    [InlineData("```afl-agent\n{\"agent\": \"gamma\"}\n```", "'gamma' isn't one of the agents that qualify")]
    [InlineData("```afl-agent\n{nope\n```", "can't be read")]
    public async Task Ai_FallsBackToTheFirstOnABadAnswer(string answer, string why)
    {
        this.Fake("main").Respond = r => new ExecutionResult(ExecutionStatus.Completed, answer, r.SessionId);
        var run = this.Start("main", Review(AgentConstants.SelectorAi));

        run = await this.Engine.DriveAsync(run.Id);

        var choice = run.Steps[0].AgentChoice!;
        Assert.Equal("alpha", choice.AgentId);
        Assert.Equal(AgentConstants.ChosenBySelector, choice.By);
        Assert.Contains(why, choice.Reason);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
    }

    [Fact]
    public async Task Preferred_IsUsedWhenItQualifiesElseTheStepFallsBack()
    {
        var run = this.Start("main", Review(AgentConstants.SelectorFirst, "beta"));
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(("beta", AgentConstants.ChosenByPreference), (run.Steps[0].AgentChoice!.AgentId, run.Steps[0].AgentChoice!.By));

        run = this.Start("main", Review(AgentConstants.SelectorFirst, "missing"));
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal("alpha", run.Steps[0].AgentChoice!.AgentId);
        Assert.Contains("preferred agent 'missing' isn't available on this machine", run.Steps[0].AgentChoice!.Reason);

        run = this.Start("main", Review(AgentConstants.SelectorFirst, "not-set-up"));
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal("alpha", run.Steps[0].AgentChoice!.AgentId);
        Assert.Contains("'not-set-up' isn't set up here", run.Steps[0].AgentChoice!.Reason);
    }

    [Fact]
    public async Task Session_WaitsForTheChoiceThenUseAgentStartsTheStep()
    {
        var run = this.Start(SessionExecutor.ExecutorName, Review(AgentConstants.SelectorAi));

        run = await this.Engine.DriveAsync(run.Id);

        var step = run.Steps[0];
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(StepStatus.ChoosingAgent, step.Status);
        Assert.Null(step.AgentChoice!.AgentId);
        Assert.Equal(["session", "alpha", "beta"], step.AgentChoice.Candidates);
        Assert.Contains("```afl-agent", step.PendingPrompt);

        await Assert.ThrowsAsync<AflException>(() => this.Engine.UseAgentAsync(run.Id, "gamma"));
        await Assert.ThrowsAsync<AflException>(() => this.Engine.UseAgentAsync(run.Id, "beta", by: "robot"));
        run = await this.Engine.UseAgentAsync(run.Id, "beta", reason: "it only reviews", by: AgentConstants.ChosenByMainAgent);

        step = run.Steps[0];
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal("beta did: Review acme/app#7", step.Output);
        Assert.Equal(("beta", "it only reviews", AgentConstants.ChosenByMainAgent), (step.AgentChoice!.AgentId, step.AgentChoice.Reason, step.AgentChoice.By));
        Assert.Equal(["session", "alpha", "beta"], step.AgentChoice.Candidates);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task UseAgent_PicksAheadForAStepThatHasNotStarted()
    {
        var gate = new UserStepDefinition { Id = "gate", Name = "Gate", Instructions = "Look" };
        var run = this.Start("main", gate, Ai("build"));
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.UseAgentAsync(run.Id, "gamma", "build");
        Assert.Equal(AgentConstants.ChosenByUser, run.Steps[1].AgentChoice!.By);
        await Assert.ThrowsAsync<AflException>(() => this.Engine.UseAgentAsync(run.Id, "gamma", "gate"));

        run = await this.Engine.CompleteAsync(run.Id);
        Assert.Equal("gamma did: Do build", run.Steps[1].Output);
        await Assert.ThrowsAsync<AflException>(() => this.Engine.UseAgentAsync(run.Id, "alpha", "build"));
    }

    [Fact]
    public async Task Replay_ChoosesAgainButReworkKeepsTheAgent()
    {
        var choices = new Queue<string>(["alpha", "beta", "alpha"]);
        this.Fake("main").Respond = r => new ExecutionResult(ExecutionStatus.Completed,
            $"```afl-agent\n{{\"agent\": \"{choices.Dequeue()}\"}}\n```", r.SessionId);
        var review = Review(AgentConstants.SelectorAi);
        review.Advance = StepConstants.AdvanceApprove;
        var run = this.Start("main", review);
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal("alpha", run.Steps[0].AgentChoice!.AgentId);

        run = await this.Engine.RejectAsync(run.Id, "Be stricter.");
        Assert.Equal("alpha", run.Steps[0].AgentChoice!.AgentId);
        Assert.Equal(2, this.Fake("alpha").Requests.Count);

        run = await this.Engine.ReplayAsync(run.Id, "review");
        Assert.Equal("beta", run.Steps[0].AgentChoice!.AgentId);
        Assert.Equal("alpha", run.Steps[0].History.Last().AgentChoice!.AgentId);
    }

    [Fact]
    public async Task NoQualifyingAgent_FailsCreationAndLaterTheStep()
    {
        var step = Review(AgentConstants.SelectorFirst);
        step.Capabilities = ["review", "web"];
        var ex = Assert.Throws<AflException>(() => this.Start("main", step));
        Assert.Contains("no agent here has review, web", ex.Message);
        Assert.Contains("alpha lacks web", ex.Message);
        Assert.Contains("missing isn't available on this machine", ex.Message);
        Assert.Contains("session lacks web; only works as the run's agent", ex.Message);

        var run = this.Start("main", Review(AgentConstants.SelectorFirst));
        this.Agents.Remove("alpha");
        this.Agents.Remove("beta");
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(StepStatus.Failed, run.Steps[0].Status);
        Assert.Contains("no agent here has review", run.Steps[0].Error);
    }

    [Fact]
    public void Validate_ChecksCapabilitiesAndSelect()
    {
        string[] Problems(AbstractStepDefinition step) => [.. WorkflowLibrary.Validate(Workflow(step))];

        Assert.Empty(Problems(Review(AgentConstants.SelectorAi)));
        Assert.Contains(Problems(Review("random")), p => p.Contains("unknown select 'random'"));
        Assert.Contains(Problems(new AiStepDefinition { Id = "a", Name = "A", Prompt = "x", Select = "first" }),
            p => p.Contains("'select' only applies"));
        Assert.Contains(Problems(ReadStep("""{ "id": "u", "name": "U", "type": "user", "capabilities": ["review"] }""")),
            p => p.Contains("doesn't take 'capabilities'"));
        Assert.Contains(Problems(new AiStepDefinition { Id = "m", Name = "M", Prompt = "x", Capabilities = ["review"], Agents = ["a", "b"] }),
            p => p.Contains("can't be used with 'agents'"));
        Assert.Contains(Problems(new AiStepDefinition { Id = "c", Name = "C", Prompt = "x", Capabilities = ["Bad Id"] }),
            p => p.Contains("capability 'Bad Id'"));
    }
}
