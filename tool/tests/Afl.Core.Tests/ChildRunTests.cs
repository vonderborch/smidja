using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for <c>workflow</c> steps: child runs, fan-out over tickets and lines, waiting on children, and
///     <c>requires</c>.
/// </summary>
public class ChildRunTests : AbstractAgentPoolTest
{
    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    private static WorkflowStepDefinition Child(string id, string workflow, ChildSourceDefinition? forEach = null)
    {
        return new WorkflowStepDefinition { Id = id, Name = id, Workflow = workflow, ForEach = forEach };
    }

    private static ChildSourceDefinition Lines(string step) => new() { Kind = StepConstants.ChildSourceLines, Step = step };

    private void SaveWorkflow(string id, params AbstractStepDefinition[] steps)
    {
        new WorkflowLibrary(this.Paths).Save(new WorkflowDefinition { Id = id, Name = id, Steps = steps.ToList() }.Chain(), WorkflowScope.Global);
    }

    private void SaveChildWorkflow(string id = "child", string? advance = null)
    {
        var work = Ai("work", "Work on {{ticket.key}} {{params.item}}");
        work.Advance = advance;
        new WorkflowLibrary(this.Paths).Save(new WorkflowDefinition
        {
            Id = id, Name = id, Steps = [work], Parameters = [new ParameterDefinition { Id = "item", Default = "" }],
        }.Chain(), WorkflowScope.Global);
    }

    private void RespondWith(string agent, string output)
    {
        this.Fake(agent).Respond = r => new ExecutionResult(ExecutionStatus.Completed, output, r.SessionId);
    }

    private Run Get(string id) => new RunStore(this.Paths).GetRequired(id);

    [Fact]
    public async Task SingleChild_RunsToDone_AndItsOutputBecomesTheSteps()
    {
        this.SaveChildWorkflow();
        var run = this.Start("main", Child("build", "child"), Ai("after", "After {{steps.build.output}}"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var state = run.Steps[0];
        var child = Assert.Single(state.ChildRuns);
        Assert.Equal(RunStatus.Done, child.Status);
        Assert.Equal("main did: Work on acme/app#7 ", state.Output);
        Assert.Equal($"main did: After {state.Output}", run.Steps[1].Output);
        var childRun = this.Get(child.RunId);
        Assert.Equal(run.Id, childRun.ParentRunId);
        Assert.Equal("build", childRun.ParentStepId);
        Assert.Equal(1, childRun.Depth);
        Assert.Equal("child", childRun.WorkflowId);
    }

    [Fact]
    public async Task ChildWaitingOnAPerson_LeavesTheParentWaiting_AndApprovingItWakesTheParent()
    {
        this.SaveChildWorkflow(advance: StepConstants.AdvanceApprove);
        var run = this.Start("main", Child("build", "child"), Ai("after", "After"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.AwaitingChildren, run.CurrentStep!.Status);
        Assert.Equal(RunStatus.Waiting, run.Status);
        var childId = run.Steps[0].ChildRuns[0].RunId;
        Assert.Equal(StepStatus.AwaitingApproval, this.Get(childId).CurrentStep!.Status);

        await this.Engine.ApproveAsync(childId);

        run = this.Get(run.Id);
        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("main did: After", run.Steps[1].Output);
    }

    [Fact]
    public async Task Lines_StartOneChildPerLine_WithTheItem()
    {
        this.SaveChildWorkflow();
        var plan = Ai("plan", "Plan");
        plan.Agent = "beta";
        this.RespondWith("beta", "- auth\n- billing\n");
        var build = Child("build", "child", Lines("plan"));
        build.With = new Dictionary<string, string> { ["item"] = "{{item}}" };
        var run = this.Start("main", plan, build);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var children = run.Steps[1].ChildRuns;
        Assert.Equal(["auth", "billing"], children.Select(c => c.Item));
        Assert.Contains("Work on acme/app#7 auth", run.Steps[1].Output);
        Assert.Contains("Work on acme/app#7 billing", run.Steps[1].Output);
        Assert.Contains($"({children[0].RunId})", run.Steps[1].Output);
    }

    [Fact]
    public async Task Parallel_RunsEveryChild()
    {
        this.SaveChildWorkflow();
        var plan = Ai("plan", "Plan");
        plan.Agent = "beta";
        this.RespondWith("beta", "a\nb\nc");
        var build = Child("build", "child", Lines("plan"));
        build.ChildrenTogether = true;
        var run = this.Start("main", plan, build);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(3, run.Steps[1].ChildRuns.Count);
        Assert.All(run.Steps[1].ChildRuns, c => Assert.Equal(RunStatus.Done, this.Get(c.RunId).Status));
    }

    [Fact]
    public async Task WaitNone_FinishesOnceTheChildrenHaveStarted()
    {
        this.SaveChildWorkflow(advance: StepConstants.AdvanceApprove);
        var build = Child("build", "child");
        build.Children = StepConstants.ChildrenStart;
        var run = this.Start("main", build);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains("Started 1 child run", run.Steps[0].Output);
        Assert.Equal(StepStatus.AwaitingApproval, this.Get(run.Steps[0].ChildRuns[0].RunId).CurrentStep!.Status);
    }

    [Fact]
    public async Task Tickets_StartOneChildPerTicket()
    {
        this.SaveChildWorkflow();
        var run = this.Start("main", Child("build", "child", new ChildSourceDefinition
        {
            Kind = StepConstants.ChildSourceTickets, Tickets = ["PROJ-1", "PROJ-2"],
        }));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(["PROJ-1", "PROJ-2"], run.Steps[0].ChildRuns.Select(c => c.TicketKey));
        Assert.Equal("PROJ-2", this.Get(run.Steps[0].ChildRuns[1].RunId).Ticket.Key);
    }

    [Fact]
    public async Task CreatedTickets_StartOneChildPerFiledTicket()
    {
        this.SaveChildWorkflow();
        var breakdown = Ai("breakdown", "Break it down");
        breakdown.CreateTickets = true;
        breakdown.Advance = StepConstants.AdvanceApprove;
        var run = this.Start("main", breakdown, Child("build", "child", new ChildSourceDefinition
        {
            Kind = StepConstants.ChildSourceCreatedTickets, Step = "breakdown",
        }));
        run = await this.Engine.DriveAsync(run.Id);
        var store = new RunStore(this.Paths);
        run = store.GetRequired(run.Id);
        run.Steps[0].CreatedTickets =
        [
            new CreatedTicket { Round = 1, Title = "Part one", Key = "PROJ-3" },
            new CreatedTicket { Round = 1, Title = "Failed one", Error = "nope" },
        ];
        store.Save(run);

        run = await this.Engine.ApproveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var child = Assert.Single(run.Steps[1].ChildRuns);
        Assert.Equal("PROJ-3", child.TicketKey);
        Assert.Equal("Part one", this.Get(child.RunId).Ticket.Title);
    }

    [Fact]
    public async Task FailedChild_FailsTheParent_AndRetryingTheParentRetriesTheChild()
    {
        var work = Ai("work", "Work");
        work.Agent = "beta";
        this.SaveWorkflow("child", work);
        this.Fake("beta").Respond = _ => new ExecutionResult(ExecutionStatus.Failed, null, null, "boom");
        var run = this.Start("main", Child("build", "child"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        var childId = run.Steps[0].ChildRuns[0].RunId;
        Assert.Contains(childId, run.Steps[0].Error);

        this.RespondWith("beta", "fixed");
        run = await this.Engine.RetryAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(childId, Assert.Single(run.Steps[0].ChildRuns).RunId);
        Assert.Equal("fixed", run.Steps[0].Output);
    }

    [Fact]
    public async Task CancellingTheParent_CancelsItsChildren()
    {
        this.SaveChildWorkflow(advance: StepConstants.AdvanceApprove);
        var run = this.Start("main", Child("build", "child"));
        run = await this.Engine.DriveAsync(run.Id);

        this.Engine.Cancel(run.Id);

        Assert.Equal(RunStatus.Cancelled, this.Get(run.Steps[0].ChildRuns[0].RunId).Status);
    }

    [Fact]
    public async Task Delete_RemovesTheRunItsChildrenAndTheirLogs()
    {
        this.SaveChildWorkflow(advance: StepConstants.AdvanceApprove);
        var run = this.Start("main", Child("build", "child"));
        run = await this.Engine.DriveAsync(run.Id);
        var child = this.Get(run.Steps[0].ChildRuns[0].RunId);
        var logs = Path.GetDirectoryName(this.Engine.Store.LogPath(run, "build"))!;
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "build.log"), "output");

        Assert.Throws<AflException>(() => this.Engine.Delete(run.Id));
        var refused = Assert.Throws<AflException>(() => this.Engine.Delete(child.Id));
        Assert.Contains(run.Id, refused.Message);

        this.Engine.Cancel(run.Id);
        var deleted = this.Engine.Delete(run.Id);

        Assert.Equal([child.Id, run.Id], deleted);
        Assert.Null(this.Engine.Store.Get(run.Id));
        Assert.Null(this.Engine.Store.Get(child.Id));
        Assert.False(Directory.Exists(logs));
        Assert.False(File.Exists(this.Engine.Store.LockPath(run)));
    }

    [Fact]
    public void CreateChild_RefusesToNestTooDeep()
    {
        this.SaveChildWorkflow();
        var run = this.Start("main", Child("build", "child"));
        run.Depth = StepConstants.MaxRunDepth;

        var ex = Assert.Throws<AflException>(() =>
            this.Engine.CreateChild(run, "build", new WorkflowLibrary(this.Paths).GetRequired("child"), SampleTicket(), "main", null));

        Assert.Contains("at most", ex.Message);
    }

    [Fact]
    public async Task TooManyItems_FailsTheStep()
    {
        this.SaveChildWorkflow();
        var plan = Ai("plan", "Plan");
        plan.Agent = "beta";
        this.RespondWith("beta", string.Join("\n", Enumerable.Range(1, StepConstants.MaxChildRuns + 1)));
        var run = this.Start("main", plan, Child("build", "child", Lines("plan")));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains($"at most {StepConstants.MaxChildRuns}", run.Steps[1].Error);
        Assert.Empty(run.Steps[1].ChildRuns);
    }

    [Fact]
    public async Task Reuse_UsesADoneRunOfTheWorkflowOnTheTicket()
    {
        this.SaveChildWorkflow();
        var repo = this.RegisterRepo();
        var earlier = this.Engine.Create(repo, new WorkflowLibrary(this.Paths).GetRequired("child"), SampleTicket(), "main");
        earlier = await this.Engine.DriveAsync(earlier.Id);
        var calls = this.Fake("main").Requests.Count;
        var build = Child("build", "child");
        build.Reuse = true;

        var run = await this.Engine.DriveAsync(this.Engine.Create(repo, Workflow(build), SampleTicket(), "main").Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var child = Assert.Single(run.Steps[0].ChildRuns);
        Assert.Equal(earlier.Id, child.RunId);
        Assert.True(child.Reused);
        Assert.Equal(earlier.Steps[0].Output, run.Steps[0].Output);
        Assert.Equal(calls, this.Fake("main").Requests.Count);
    }

    [Fact]
    public async Task Requires_StartsTheRequiredWorkflowOnce_AndReusesItAfterwards()
    {
        this.SaveChildWorkflow("prep");
        var repo = this.RegisterRepo();
        var workflow = Workflow(Ai("a", "A"));
        workflow.Requires = [new WorkflowRequirement { Workflow = "prep" }];

        var first = await this.Engine.DriveAsync(this.Engine.Create(repo, workflow, SampleTicket(), "main").Id);
        var second = await this.Engine.DriveAsync(this.Engine.Create(repo, workflow, SampleTicket(), "main").Id);

        Assert.Equal(RunStatus.Done, first.Status);
        Assert.Equal(RunStatus.Done, second.Status);
        var started = Assert.Single(first.Steps[0].ChildRuns);
        Assert.NotEqual(true, started.Reused);
        var reused = Assert.Single(second.Steps[0].ChildRuns);
        Assert.True(reused.Reused);
        Assert.Equal(started.RunId, reused.RunId);
    }

    [Fact]
    public void Validate_RejectsAChildWorkflowThatRunsItself()
    {
        var errors = new WorkflowComposer(new WorkflowLibrary(this.Paths)).Validate(
            new WorkflowDefinition { Id = "loop", Name = "loop", Steps = [Child("again", "loop")] }.Chain());

        Assert.Contains(errors, e => e.Contains("refers back to itself"));
    }
}
