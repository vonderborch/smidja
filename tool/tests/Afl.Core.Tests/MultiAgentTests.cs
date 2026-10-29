using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for steps several agents do side by side, and how their results are combined.
/// </summary>
public class MultiAgentTests : AbstractAflTest
{
    private readonly Dictionary<string, FakeStepExecutor> _agents = new()
    {
        ["alpha"] = Answering("alpha"),
        ["beta"] = Answering("beta"),
        ["gamma"] = Answering("gamma"),
    };

    private RunEngine Engine => new(new RunStore(this.Paths), id => this._agents[id]);

    private static FakeStepExecutor Answering(string name)
    {
        return new FakeStepExecutor
        {
            Respond = r => new ExecutionResult(ExecutionStatus.Completed, $"{name} did: {r.Prompt}", r.SessionId),
        };
    }

    private static AiStepDefinition Review(string? combine = null, string? mergeAgent = null, string? mergePrompt = null) =>
        new()
        {
            Id = "review", Name = "Review", Prompt = "Review {{ticket.key}}", Agents = ["alpha", "beta"],
            Combine = combine, MergeAgent = mergeAgent, MergePrompt = mergePrompt,
        };

    /// <summary>
    ///     Re-reads a step from JSON as another type, as a hand edit of <c>type</c> would.
    /// </summary>
    private static AbstractStepDefinition Retyped(AbstractStepDefinition step, string type)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(step, JsonConstants.Options))!;
        node["type"] = type;
        return node.Deserialize<AbstractStepDefinition>(JsonConstants.Options)!;
    }

    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    private Run Start(params AbstractStepDefinition[] steps)
    {
        return this.Engine.Create(this.RegisterRepo(), Workflow(steps), SampleTicket(), "alpha");
    }

    [Fact]
    public async Task Choose_RunsEveryAgentInItsOwnSessionThenWaitsForAChoice()
    {
        var run = this.Start(Review(), Ai("next", "Use {{steps.review.output}} / {{steps.review.candidates.alpha}}"));

        run = await this.Engine.DriveAsync(run.Id);

        var step = run.CurrentStep!;
        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(StepStatus.AwaitingChoice, step.Status);
        Assert.Null(step.Output);
        Assert.Equal(["alpha", "beta"], step.Candidates!.Select(c => c.Agent));
        Assert.All(step.Candidates!, c => Assert.Equal(StepStatus.Done, c.Status));
        Assert.Equal("beta did: Review acme/app#7", step.Candidates![1].Output);
        var alpha = this._agents["alpha"].Requests.Single();
        var beta = this._agents["beta"].Requests.Single();
        Assert.NotEqual(alpha.SessionId, beta.SessionId);
        Assert.EndsWith("review.alpha.log", alpha.LogPath);
        Assert.EndsWith("review.beta.log", beta.LogPath);

        run = await this.Engine.ChooseAsync(run.Id, "beta");

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal("beta did: Review acme/app#7", run.Steps[0].Output);
        Assert.Contains("Used beta's result.", run.Steps[0].Notes);
        Assert.Equal("Use beta did: Review acme/app#7 / alpha did: Review acme/app#7",
            this._agents["alpha"].Requests[1].Prompt);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task Choose_TakesAnEditedResult_AndRejectsUnknownAgents()
    {
        var run = this.Start(Review());
        run = await this.Engine.DriveAsync(run.Id);

        await Assert.ThrowsAsync<AflException>(() => this.Engine.ChooseAsync(run.Id, "gamma"));
        await Assert.ThrowsAsync<AflException>(() => this.Engine.ApproveAsync(run.Id));
        run = await this.Engine.ChooseAsync(run.Id, "alpha", "My own take.");

        Assert.Equal("My own take.", run.Steps[0].Output);
        Assert.Contains("Used alpha's result, edited.", run.Steps[0].Notes);
    }

    [Fact]
    public async Task Choose_OnAGatedStep_WaitsForApprovalOfTheChoice()
    {
        var step = Review();
        step.Advance = StepConstants.AdvanceApprove;
        var run = this.Start(step);
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.ChooseAsync(run.Id, "alpha");
        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);
        Assert.StartsWith("alpha did: Review acme/app#7\n\n", run.CurrentStep.Output);

        run = await this.Engine.ApproveAsync(run.Id);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task Merge_HasTheMergeAgentCombineTheResults()
    {
        var run = this.Start(Review(StepConstants.CombineMerge, "gamma"));

        run = await this.Engine.DriveAsync(run.Id);

        var merge = this._agents["gamma"].Requests.Single();
        Assert.StartsWith("Review acme/app#7\n\n" + StepConstants.MergeInstructions, merge.Prompt);
        Assert.Contains("## alpha\n\nalpha did: Review acme/app#7", merge.Prompt);
        Assert.Contains("## beta\n\nbeta did: Review acme/app#7", merge.Prompt);
        Assert.EndsWith("review.merge.log", merge.LogPath);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.StartsWith("gamma did: Review", run.Steps[0].Output);
        Assert.Contains("Merged by gamma.", run.Steps[0].Notes);
    }

    [Fact]
    public async Task Merge_FromAChoice_UsesTheGivenAgentNoteAndMergePrompt()
    {
        var run = this.Start(Review(mergePrompt: "Pick the best of:\n{{candidates}}"));
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.MergeAsync(run.Id, "gamma", "Prefer short answers.");

        var merge = this._agents["gamma"].Requests.Single().Prompt;
        Assert.StartsWith("Pick the best of:\n## alpha\n\nalpha did:", merge);
        Assert.EndsWith(StepConstants.MergeNoteHeading + "\n\nPrefer short answers.", merge);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
    }

    [Fact]
    public async Task Merge_ThatFails_GoesBackToTheChoice()
    {
        this._agents["gamma"].Respond = _ => new ExecutionResult(ExecutionStatus.Failed, null, null, "out of tokens");
        var run = this.Start(Review(StepConstants.CombineMerge, "gamma"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.AwaitingChoice, run.CurrentStep!.Status);
        Assert.Contains("out of tokens", run.CurrentStep.Error);
        run = await this.Engine.ChooseAsync(run.Id, "alpha");
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task All_KeepsEveryResultUnderItsAgent()
    {
        var run = this.Start(Review(StepConstants.CombineAll));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("## alpha\n\nalpha did: Review acme/app#7\n\n## beta\n\nbeta did: Review acme/app#7", run.Steps[0].Output);
    }

    [Fact]
    public async Task PartialFailure_CarriesOnWithTheAgentsThatSucceeded()
    {
        this._agents["beta"].Respond = _ => new ExecutionResult(ExecutionStatus.Failed, null, null, "crashed");
        var run = this.Start(Review(StepConstants.CombineAll));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("## alpha\n\nalpha did: Review acme/app#7", run.Steps[0].Output);
        Assert.Equal("crashed", run.Steps[0].Candidates![1].Error);
        Assert.Equal(StepStatus.Failed, run.Steps[0].Candidates![1].Status);
    }

    [Fact]
    public async Task EveryAgentFailing_FailsTheStep()
    {
        this._agents["alpha"].Respond = _ => new ExecutionResult(ExecutionStatus.Failed, null, null, "no key");
        this._agents["beta"].Respond = _ => throw new AflException(AflErrorKind.ExternalTool, "not installed");
        var run = this.Start(Review());

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(StepStatus.Failed, run.CurrentStep!.Status);
        Assert.Contains("alpha: no key", run.CurrentStep.Error);
        Assert.Contains("beta: not installed", run.CurrentStep.Error);
    }

    [Fact]
    public async Task SendBack_ReworksEveryAgentInItsOwnSession()
    {
        this._agents["beta"].Resumes = false;
        var run = this.Start(Review());
        run = await this.Engine.DriveAsync(run.Id);
        var sessions = run.CurrentStep!.Candidates!.Select(c => c.SessionId).ToList();

        run = await this.Engine.RejectAsync(run.Id, "Look at the tests too.");

        var alpha = this._agents["alpha"].Requests[1];
        var beta = this._agents["beta"].Requests[1];
        Assert.True(alpha.Resume);
        Assert.Equal(sessions[0], alpha.SessionId);
        Assert.Contains("Look at the tests too.", alpha.Prompt);
        Assert.False(beta.Resume);
        Assert.Contains("beta did: Review acme/app#7", beta.Prompt);
        Assert.Contains("Look at the tests too.", beta.Prompt);
        var step = run.CurrentStep!;
        Assert.Equal(StepStatus.AwaitingChoice, step.Status);
        Assert.Equal(2, step.Attempts);
        Assert.Equal(2, step.History.Single().Candidates!.Count);
        Assert.Equal("alpha did: Review acme/app#7", step.History.Single().Candidates![0].Output);
    }

    [Fact]
    public async Task SessionCandidate_WaitsForTheSessionToSubmitIt()
    {
        this._agents["beta"].Respond = r => new ExecutionResult(ExecutionStatus.AwaitingSession, null, r.SessionId);
        var run = this.Start(Review());

        run = await this.Engine.DriveAsync(run.Id);

        var step = run.CurrentStep!;
        Assert.Equal(StepStatus.AwaitingSession, step.Status);
        Assert.Equal("Review acme/app#7", step.PendingPrompt);
        Assert.Equal(StepStatus.AwaitingSession, step.Candidates![1].Status);
        await Assert.ThrowsAsync<AflException>(() => this.Engine.SubmitAsync(run.Id, "x", agent: "alpha"));

        run = await this.Engine.SubmitAsync(run.Id, "The session's review.", agent: "beta");

        Assert.Equal(StepStatus.AwaitingChoice, run.CurrentStep!.Status);
        Assert.Equal("The session's review.", run.CurrentStep.Candidates![1].Output);
    }

    [Fact]
    public async Task SessionMerge_WaitsForTheSessionToSubmitTheMergedResult()
    {
        this._agents["gamma"].Respond = r => new ExecutionResult(ExecutionStatus.AwaitingSession, null, r.SessionId);
        var run = this.Start(Review(StepConstants.CombineMerge, "gamma"));

        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(StepStatus.AwaitingSession, run.CurrentStep!.Status);
        Assert.Contains("## beta", run.CurrentStep.PendingPrompt);

        run = await this.Engine.SubmitAsync(run.Id, "Merged by hand.");
        Assert.Equal("Merged by hand.", run.Steps[0].Output);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task Replay_ArchivesTheCandidatesAndStartsAfresh()
    {
        var run = this.Start(Review(StepConstants.CombineAll));
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.ReplayAsync(run.Id, "review");

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(2, run.Steps[0].History.Single().Candidates!.Count);
        Assert.All(this._agents["alpha"].Requests, r => Assert.False(r.Resume));
        Assert.NotEqual(this._agents["alpha"].Requests[0].SessionId, this._agents["alpha"].Requests[1].SessionId);
    }

    [Fact]
    public void Create_RejectsUnknownAgents()
    {
        var step = Review();
        step.Agents = ["alpha", "nope"];

        Assert.ThrowsAny<Exception>(() => this.Start(step));
    }

    [Theory]
    [InlineData("both", "set 'agent' or 'agents'")]
    [InlineData("one", "two or more agents")]
    [InlineData("duplicate", "more than once")]
    [InlineData("model", "'model' applies")]
    [InlineData("combine", "unknown combine 'vote'")]
    [InlineData("all-merge", "aren't used with combine 'all'")]
    [InlineData("single-combine", "only apply to a step with 'agents'")]
    [InlineData("user", "doesn't take 'agents'")]
    [InlineData("interactive", "is done by one agent")]
    [InlineData("merge-prompt", "not available in the merge prompt")]
    [InlineData("bad-id", "isn't an agent id")]
    public void Validate_RejectsBadMultiAgentSteps(string problem, string expected)
    {
        AbstractStepDefinition step = Review();
        switch (problem)
        {
            case "both": step.Agent = "alpha"; break;
            case "one": ((AiStepDefinition)step).Agents = ["alpha"]; break;
            case "duplicate": ((AiStepDefinition)step).Agents = ["alpha", "beta", "alpha"]; break;
            case "model": ((AiStepDefinition)step).Model = "big"; break;
            case "combine": ((AiStepDefinition)step).Combine = "vote"; break;
            case "all-merge": step = Review(StepConstants.CombineAll, "gamma"); break;
            case "single-combine": step = new AiStepDefinition { Id = "review", Name = "Review", Prompt = "p", Combine = StepConstants.CombineMerge }; break;
            case "user": step = Retyped(step, StepConstants.TypeUser); break;
            case "interactive": step = Retyped(step, StepConstants.TypeInteractive); break;
            case "merge-prompt": step = Review(mergePrompt: "{{steps.later.output}}"); break;
            case "bad-id": ((AiStepDefinition)step).Agents = ["alpha", "Not An Id"]; break;
        }

        Assert.Contains(WorkflowLibrary.Validate(Workflow(step)), e => e.Contains(expected));
    }

    [Fact]
    public void Validate_AcceptsCandidateVariables()
    {
        var workflow = Workflow(
            Review(StepConstants.CombineMerge, "gamma", "Merge {{candidates}} for {{ticket.key}}"),
            Ai("next", "Compare {{steps.review.candidates.alpha}} with {{steps.review.candidates.beta}}"));

        Assert.Empty(WorkflowLibrary.Validate(workflow));
    }

    [Fact]
    public async Task GatedSteps_TellEveryAgentHowToLeaveQuestions()
    {
        var review = Review();
        review.Advance = StepConstants.AdvanceApprove;
        var run = this.Start(review);

        await this.Engine.DriveAsync(run.Id);

        Assert.Contains(StepConstants.OpenQuestionsHeading, this._agents["alpha"].Requests.Single().Prompt);
        Assert.Contains(StepConstants.OpenQuestionsHeading, this._agents["beta"].Requests.Single().Prompt);
    }
}

