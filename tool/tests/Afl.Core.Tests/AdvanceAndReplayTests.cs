using Afl.Core.Steps;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for a step's <c>advance</c> setting (moving on by itself or waiting for approval, whatever its type), sending
///     steps back, and replaying steps that have run.
/// </summary>
public class AdvanceAndReplayTests : AbstractAflTest
{
    private readonly FakeStepExecutor _executor = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor);

    private static AbstractStepDefinition Step(string id, string type, string? advance = null,
        List<StepInput>? inputs = null)
    {
        var step = StepHandlerRegistry.Instance.CreateDefinition(type);
        step.Id = id;
        step.Name = id;
        step.Advance = advance;
        step.Inputs = inputs;
        switch (step)
        {
            case AbstractAgentStepDefinition agentStep:
                agentStep.Prompt = $"Do {id}";
                break;
            case UserStepDefinition userStep:
                userStep.Instructions = $"You do {id}";
                break;
        }

        return step;
    }

    private static StepInput Confirm(string id) =>
        new() { Id = id, Label = id, Type = StepConstants.InputConfirm };

    private Run Start(params AbstractStepDefinition[] steps)
    {
        return this.Engine.Create(this.RegisterRepo(), Workflow(steps), SampleTicket());
    }

    [Fact]
    public async Task AiStep_WithApprove_WaitsForApproval()
    {
        var run = this.Start(Step("a", "ai", advance: "approve"), Step("b", "ai"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);
        Assert.StartsWith("output for: Do a\n\n", run.CurrentStep.Output);

        run = await this.Engine.ApproveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.StartsWith("output for: Do a\n\n", run.Steps[0].Output);
    }

    [Fact]
    public async Task AiStep_WithAuto_MovesOnByItself()
    {
        var run = this.Start(Step("a", "ai", "auto"), Step("b", "ai"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task UserStep_WithApprove_TakesAnswersOnlyWhenApproved()
    {
        var run = this.Start(Step("u", "user", advance: "approve", inputs: [Confirm("ok")]), Step("b", "ai"));
        run = await this.Engine.DriveAsync(run.Id);

        var ex = await Assert.ThrowsAsync<AflException>(() =>
            this.Engine.CompleteAsync(run.Id, "did it", new Dictionary<string, string> { ["ok"] = "y" }));
        Assert.Equal(AflErrorKind.InvalidInput, ex.Kind);

        run = await this.Engine.CompleteAsync(run.Id, "did it");
        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);
        Assert.Equal("did it", run.CurrentStep.Output);

        run = await this.Engine.ApproveAsync(run.Id, new Dictionary<string, string> { ["ok"] = "y" });

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal($"did it\n\nok: {StepConstants.Yes}", run.Steps[0].Output);
    }

    [Fact]
    public async Task InteractiveStep_WithApprove_WaitsAfterMarkedDone()
    {
        this._executor.SupportsInteractive = true;
        var run = this.Start(Step("i", "interactive", "approve"));
        run = await this.Engine.DriveAsync(run.Id);
        Assert.NotNull(run.CurrentStep!.Interactive);

        run = await this.Engine.CompleteAsync(run.Id, "all good");

        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);
        Assert.Contains("all good", run.CurrentStep.Output);
    }

    [Fact]
    public void Validation_RejectsUnknownAdvanceAndAllowsInputsOnGatedAiSteps()
    {
        Assert.Contains(WorkflowLibrary.Validate(Workflow(Step("a", "ai", advance: "later"))),
            e => e.Contains("advance", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(WorkflowLibrary.Validate(Workflow(Step("a", "ai", advance: "approve", inputs: [Confirm("ok")]))));
        Assert.NotEmpty(WorkflowLibrary.Validate(Workflow(Step("a", "ai", inputs: [Confirm("ok")]))));
    }

    [Fact]
    public async Task SendBack_UserStep_ReopensWithFeedback()
    {
        var run = this.Start(Step("u", "user", advance: "approve"));
        run = await this.Engine.DriveAsync(run.Id);
        run = await this.Engine.CompleteAsync(run.Id, "first try");

        run = await this.Engine.RejectAsync(run.Id, "Check the logs too.");

        Assert.Equal(StepStatus.AwaitingUser, run.CurrentStep!.Status);
        Assert.Contains("You do u", run.CurrentStep.PendingPrompt);
        Assert.Contains("Check the logs too.", run.CurrentStep.PendingPrompt);
        var attempt = Assert.Single(run.CurrentStep.History);
        Assert.Equal("first try", attempt.Output);
        Assert.Equal("Check the logs too.", attempt.Reason);
    }

    [Fact]
    public async Task SendBack_NeedsFeedbackAndAStepAwaitingApproval()
    {
        var run = this.Start(Step("u", "user"));
        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(AflErrorKind.InvalidInput,
            (await Assert.ThrowsAsync<AflException>(() => this.Engine.RejectAsync(run.Id, " "))).Kind);
        Assert.Equal(AflErrorKind.InvalidState,
            (await Assert.ThrowsAsync<AflException>(() => this.Engine.RejectAsync(run.Id, "x"))).Kind);
    }

    [Fact]
    public async Task Replay_DoneRun_RewindsAndRedoesLaterSteps()
    {
        var run = this.Start(Step("a", "ai"), Step("b", "ai"), Step("c", "user"));
        run = await this.Engine.DriveAsync(run.Id);
        run = await this.Engine.CompleteAsync(run.Id, "shipped");
        Assert.Equal(RunStatus.Done, run.Status);
        var firstSession = run.Steps[1].SessionId;

        run = await this.Engine.ReplayAsync(run.Id, "b");

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal("c", run.CurrentStep!.StepId);
        Assert.Equal(StepStatus.AwaitingUser, run.CurrentStep.Status);
        Assert.Empty(run.Steps[0].History);
        var replayed = Assert.Single(run.Steps[1].History);
        Assert.Null(replayed.Reason);
        Assert.Equal(StepStatus.Done, run.Steps[1].Status);
        Assert.NotEqual(firstSession, run.Steps[1].SessionId);
        Assert.False(this._executor.Requests.Last().Resume);
        Assert.Equal("shipped", Assert.Single(run.Steps[2].History).Output);
    }

    [Fact]
    public async Task Replay_WithFeedback_ResumesTheAiConversation()
    {
        var run = this.Start(Step("a", "ai"), Step("u", "user"));
        run = await this.Engine.DriveAsync(run.Id);
        var session = run.Steps[0].SessionId;

        run = await this.Engine.ReplayAsync(run.Id, "a", "Be shorter.");

        var request = this._executor.Requests.Last();
        Assert.True(request.Resume);
        Assert.Equal(session, request.SessionId);
        Assert.Contains("Be shorter.", request.Prompt);
        Assert.Equal(["Be shorter."], run.Steps[0].Feedback);
        Assert.Equal("Be shorter.", Assert.Single(run.Steps[0].History).Reason);
        Assert.Equal("u", run.CurrentStep!.StepId);
    }

    [Fact]
    public async Task Replay_UserStep_ReopensIt()
    {
        var run = this.Start(Step("u", "user"), Step("b", "ai"));
        run = await this.Engine.DriveAsync(run.Id);
        run = await this.Engine.CompleteAsync(run.Id, "first");

        run = await this.Engine.ReplayAsync(run.Id, "u", "Use the staging config.");

        Assert.Equal("u", run.CurrentStep!.StepId);
        Assert.Equal(StepStatus.AwaitingUser, run.CurrentStep.Status);
        Assert.Contains("Use the staging config.", run.CurrentStep.PendingPrompt);
        Assert.Single(run.Steps[1].History);
    }

    [Fact]
    public async Task Replay_RefusesStepsThatHaveNotRunAndUnknownSteps()
    {
        var run = this.Start(Step("u", "user"), Step("b", "ai"));
        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(AflErrorKind.InvalidState,
            (await Assert.ThrowsAsync<AflException>(() => this.Engine.ReplayAsync(run.Id, "b"))).Kind);
        Assert.Equal(AflErrorKind.NotFound,
            (await Assert.ThrowsAsync<AflException>(() => this.Engine.ReplayAsync(run.Id, "nope"))).Kind);
    }
}
