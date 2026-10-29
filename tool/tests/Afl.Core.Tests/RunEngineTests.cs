using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for driving runs through each kind of step.
/// </summary>
public class RunEngineTests : AbstractAflTest
{
    private readonly FakeStepExecutor _executor = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor);

    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    private static UserStepDefinition User(string id, string instructions) =>
        new() { Id = id, Name = id, Instructions = instructions };

    private static AiStepDefinition Approve(string id, string prompt) =>
        new() { Id = id, Name = id, Advance = "approve", Prompt = prompt };

    private static InteractiveStepDefinition Interactive(string id, string prompt) =>
        new() { Id = id, Name = id, Prompt = prompt };

    private Run Start(WorkflowDefinition workflow, RunEngine? engine = null)
    {
        return (engine ?? this.Engine).Create(this.RegisterRepo(), workflow, SampleTicket());
    }

    [Fact]
    public async Task AiSteps_RunThroughToUserGate_WithTemplatedPrompts()
    {
        var run = this.Start(Workflow(
            Ai("understand", "Read {{ticket.key}}: {{ticket.title}}"),
            Ai("summarise", "Summarise: {{steps.understand.output}}"),
            User("pr", "Open a PR for {{ticket.url}}")));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal("pr", run.CurrentStep!.StepId);
        Assert.Equal(StepStatus.AwaitingUser, run.CurrentStep.Status);
        Assert.Equal("Open a PR for https://github.com/acme/app/issues/7", run.CurrentStep.PendingPrompt);
        Assert.Equal("Read acme/app#7: Add dark mode", this._executor.Requests[0].Prompt);
        Assert.Equal("Summarise: output for: Read acme/app#7: Add dark mode", this._executor.Requests[1].Prompt);
        Assert.Equal(this.RepoPath, this._executor.Requests[0].WorkingDirectory);
        Assert.Equal(StepConstants.DefaultPermissionMode, this._executor.Requests[0].PermissionMode);

        run = await this.Engine.CompleteAsync(run.Id, "PR #9 merged");

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("PR #9 merged", run.Steps[2].Output);
        Assert.Null(run.CurrentStep);
    }

    [Fact]
    public async Task ApproveGate_RejectResumesSameSessionWithFeedback()
    {
        var run = this.Start(Workflow(Approve("plan", "Plan {{ticket.key}}"), User("next", "go")));

        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);
        var sessionId = run.CurrentStep.SessionId;
        Assert.False(this._executor.Requests[0].Resume);

        run = await this.Engine.RejectAsync(run.Id, "Also handle the settings page.");

        var retry = this._executor.Requests[1];
        Assert.True(retry.Resume);
        Assert.Equal(sessionId, retry.SessionId);
        Assert.Contains("Also handle the settings page.", retry.Prompt);
        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);
        Assert.Equal(2, run.CurrentStep.Attempts);
        Assert.Equal(["Also handle the settings page."], run.CurrentStep.Feedback);

        run = await this.Engine.ApproveAsync(run.Id);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal("next", run.CurrentStep!.StepId);
    }

    [Fact]
    public async Task ActionsInTheWrongStateAreRefused()
    {
        var run = this.Start(Workflow(User("do", "it")));
        run = await this.Engine.DriveAsync(run.Id);

        var ex = await Assert.ThrowsAsync<AflException>(() => this.Engine.ApproveAsync(run.Id));
        Assert.Equal(AflErrorKind.InvalidState, ex.Kind);
        await Assert.ThrowsAsync<AflException>(() => this.Engine.SubmitAsync(run.Id, "x"));
        Assert.Equal(StepStatus.AwaitingUser, this.Engine.Store.GetRequired(run.Id).CurrentStep!.Status);
    }

    [Fact]
    public async Task RunsSurviveReloadAndCanBeFoundByPrefix()
    {
        var run = this.Start(Workflow(Approve("plan", "Plan"), Ai("impl", "Do {{steps.plan.output}}")));
        await this.Engine.DriveAsync(run.Id);

        var reloaded = new RunStore(this.Paths).GetRequired(run.Id[..9]);
        Assert.Equal(run.Id, new RunStore(this.Paths).GetRequired(run.Id[^4..]).Id);
        Assert.Equal(StepStatus.AwaitingApproval, reloaded.CurrentStep!.Status);
        Assert.StartsWith("output for: Plan\n\n", reloaded.CurrentStep.Output);
        Assert.Equal("test", reloaded.Workflow.Id);

        var done = await new RunEngine(new RunStore(this.Paths), this._executor).ApproveAsync(reloaded.Id);

        Assert.Equal(RunStatus.Done, done.Status);
        Assert.StartsWith("Do output for: Plan\n\n", this._executor.Requests.Last().Prompt);
        Assert.Single(new RunStore(this.Paths).List(done.RepoId));
    }

    [Fact]
    public async Task SessionExecutor_HandsTheCallerTheStepsSkills()
    {
        var engine = new RunEngine(new RunStore(this.Paths), new SessionExecutor());
        var step = Ai("understand", "Read {{ticket.key}}");
        step.Skills = ["caveman"];
        var run = this.Start(Workflow(step), engine);

        run = await engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.AwaitingSession, run.CurrentStep!.Status);
        Assert.StartsWith("Read acme/app#7\n\n## Skill: Caveman", run.CurrentStep.PendingPrompt);
    }

    [Fact]
    public async Task SessionExecutor_HandsPromptToCallerAndAcceptsSubmission()
    {
        var engine = new RunEngine(new RunStore(this.Paths), new SessionExecutor());
        var run = this.Start(Workflow(Ai("understand", "Read {{ticket.key}}"), Approve("plan", "Plan from {{steps.understand.output}}")), engine);

        run = await engine.DriveAsync(run.Id);
        Assert.Equal(StepStatus.AwaitingSession, run.CurrentStep!.Status);
        Assert.Equal("Read acme/app#7", run.CurrentStep.PendingPrompt);

        run = await engine.SubmitAsync(run.Id, "It's a CSS change.");
        Assert.Equal("plan", run.CurrentStep!.StepId);
        Assert.Equal(StepStatus.AwaitingSession, run.CurrentStep.Status);
        Assert.StartsWith("Plan from It's a CSS change.\n\n", run.CurrentStep.PendingPrompt);

        run = await engine.SubmitAsync(run.Id, "1. Edit theme.css");
        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);

        run = await engine.RejectAsync(run.Id, "More detail");
        Assert.Equal(StepStatus.AwaitingSession, run.CurrentStep!.Status);
        Assert.Contains("More detail", run.CurrentStep.PendingPrompt);

        run = await engine.SubmitAsync(run.Id, "1. Edit theme.css\n2. Add toggle");
        run = await engine.ApproveAsync(run.Id);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task FailedStepStopsRunAndCanBeRetried()
    {
        var fail = true;
        this._executor.Respond = r => fail
            ? new ExecutionResult(ExecutionStatus.Failed, null, r.SessionId, "claude exited with code 1")
            : new ExecutionResult(ExecutionStatus.Completed, "fixed", r.SessionId);
        var run = this.Start(Workflow(Ai("fix", "Fix it")));

        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("claude exited with code 1", run.CurrentStep!.Error);

        fail = false;
        run = await this.Engine.RetryAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("fixed", run.Steps[0].Output);
        Assert.NotEqual(this._executor.Requests[0].SessionId, this._executor.Requests[1].SessionId);
    }

    [Fact]
    public async Task InteractiveStep_WaitsForUserThenSummarisesSession()
    {
        this._executor.SupportsInteractive = true;
        var run = this.Start(Workflow(Interactive("implement", "Build {{ticket.title}}")));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.AwaitingUser, run.CurrentStep!.Status);
        Assert.Equal(["--session-id", run.CurrentStep.SessionId!, "Build Add dark mode"], run.CurrentStep.Interactive!.Arguments);
        Assert.Empty(this._executor.Requests);

        this._executor.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "Added a theme toggle.", r.SessionId);
        run = await this.Engine.CompleteAsync(run.Id, "Tested on macOS");

        var summary = Assert.Single(this._executor.Requests);
        Assert.True(summary.Resume);
        Assert.Equal(StepConstants.ReadOnlyPermissionMode, summary.PermissionMode);
        Assert.Equal(StepConstants.InteractiveSummaryPrompt, summary.Prompt);
        Assert.Equal("Added a theme toggle.\n\nTested on macOS", run.Steps[0].Output);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task DeniedToolsWarnButDoNotFailTheStep()
    {
        this._executor.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "partial", "s1", null,
            [new PermissionDenial { Tool = "Bash" }]);
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket(),
            permissions: PermissionConstants.PolicyStrict);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("partial", run.Steps[0].Output);
        Assert.Contains("denied permission to use Bash", Assert.Single(run.Steps[0].Notes));
    }

    [Fact]
    public async Task CancelledRunsRefuseFurtherActions()
    {
        var run = this.Start(Workflow(User("do", "it")));
        await this.Engine.DriveAsync(run.Id);

        run = this.Engine.Cancel(run.Id);

        Assert.Equal(RunStatus.Cancelled, run.Status);
        await Assert.ThrowsAsync<AflException>(() => this.Engine.CompleteAsync(run.Id));
    }

    [Fact]
    public async Task NotesAttachToCurrentStep()
    {
        var run = this.Start(Workflow(User("do", "it")));
        await this.Engine.DriveAsync(run.Id);

        run = this.Engine.AddNote(run.Id, "Handoff: halfway through.");

        Assert.Equal(["Handoff: halfway through."], run.Steps[0].Notes);
    }

    [Fact]
    public void CreateRejectsInvalidWorkflow()
    {
        Assert.Throws<AflException>(() => this.Start(Workflow(Ai("x", ""))));
    }
}
