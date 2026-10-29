using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for format 5.0 arrows: which of a step's arrows fire (<c>follow</c>, <c>on</c>), what a step waits for
///     (<c>waitFor</c>, <c>others</c>), dead paths, and the run-wide rules (<c>retry</c>, <c>timeout</c>,
///     <c>maxSteps</c>, <c>maxParallel</c>, <c>onFailure</c>).
/// </summary>
public partial class ArrowTests : AbstractAgentPoolTest
{
    private static AiStepDefinition Ai(string id, string prompt, params ArrowDefinition[] arrows)
    {
        return new AiStepDefinition { Id = id, Name = id, Prompt = prompt, Arrows = arrows.Length == 0 ? null : [.. arrows] };
    }

    private static ArrowDefinition To(string to, string on = ArrowConstants.OnSuccess, ConditionDefinition? when = null)
    {
        return new ArrowDefinition { To = to, On = on, When = when };
    }

    private static ConditionDefinition PlanSays(string text) =>
        new() { Kind = StepConstants.ConditionOutput, Step = "plan", Contains = text };

    private static ConditionDefinition Not(ConditionDefinition condition) =>
        new() { Kind = StepConstants.ConditionNot, Conditions = [condition] };

    private static ArrowWaitRule Leaf(string step) => new() { Step = step };

    private static AiStepDefinition Gated(AiStepDefinition step)
    {
        step.Advance = StepConstants.AdvanceApprove;
        return step;
    }

    private Run Get(string id) => new RunStore(this.Paths).GetRequired(id);

    private int Asked(string prompt) => this.Fake("main").Requests.Count(r => r.Prompt == prompt);

    private void Failing(string agent) =>
        this.Fake(agent).Respond = r => new ExecutionResult(ExecutionStatus.Failed, null, r.SessionId, "boom");

    private Run Start(IEnumerable<AbstractStepDefinition> steps, int? maxParallel = null, Action<WorkflowDefinition>? setUp = null)
    {
        var workflow = new WorkflowDefinition { Id = "test", Name = "Test", Steps = [.. steps] };
        setUp?.Invoke(workflow);
        Assert.Equal([], WorkflowLibrary.Validate(workflow));
        return this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main", maxParallel: maxParallel);
    }

    [Fact]
    public void WaitRules_LeaveDeadArrowsOut()
    {
        string[] incoming = ["a", "b", "c"];
        Func<string, ArrowState> States(ArrowState a, ArrowState b, ArrowState c) =>
            s => s switch { "a" => a, "b" => b, _ => c };
        const ArrowState f = ArrowState.Fired, d = ArrowState.Dead, p = ArrowState.Pending;

        var all = new AllWaitRule();
        Assert.Equal(WaitResult.Pending, all.Evaluate(incoming, States(f, p, d)));
        Assert.Equal(WaitResult.Ready, all.Evaluate(incoming, States(f, d, d)));
        Assert.Equal(WaitResult.Dead, all.Evaluate(incoming, States(d, d, d)));

        var any = new AnyWaitRule();
        Assert.Equal(WaitResult.Ready, any.Evaluate(incoming, States(p, f, d)));
        Assert.Equal(WaitResult.Pending, any.Evaluate(incoming, States(p, d, d)));
        Assert.Equal(WaitResult.Dead, any.Evaluate(incoming, States(d, d, d)));

        var twoOf = new AtLeastWaitRule { Count = 2 };
        Assert.Equal(WaitResult.Pending, twoOf.Evaluate(incoming, States(f, d, p)));
        Assert.Equal(WaitResult.Ready, twoOf.Evaluate(incoming, States(f, p, f)));
        Assert.Equal(WaitResult.Dead, twoOf.Evaluate(incoming, States(f, d, d)));

        var nested = new AnyWaitRule { Items = [new AllWaitRule { Items = [Leaf("a"), Leaf("b")] }, Leaf("c")] };
        Assert.Equal(WaitResult.Pending, nested.Evaluate(incoming, States(f, p, d)));
        Assert.Equal(WaitResult.Ready, nested.Evaluate(incoming, States(p, p, f)));
        Assert.Equal(WaitResult.Ready, nested.Evaluate(incoming, States(f, f, p)));
        Assert.Equal(WaitResult.Ready, nested.Evaluate(incoming, States(d, f, d)));
        Assert.Equal(WaitResult.Dead, nested.Evaluate(incoming, States(d, d, d)));
        Assert.Equal("any(all(a, b), c)", nested.Describe());
    }

    [Theory]
    [InlineData("\"any\"")]
    [InlineData("{\"all\":[\"a\",{\"any\":[\"b\",\"c\"]}]}")]
    [InlineData("{\"atLeast\":2,\"of\":[\"a\",\"b\",\"c\"]}")]
    [InlineData("{\"atLeast\":1}")]
    public void WaitRules_RoundTripTheirJson(string json)
    {
        var rule = WaitRuleRegistry.Instance.Read(JsonNode.Parse(json), true);

        Assert.Equal(json, rule.ToJson(true).ToJsonString());
    }

    [Theory]
    [InlineData("skip", StepStatus.Pending)]
    [InlineData("go", StepStatus.Done)]
    public async Task ConditionalStep_IsGoneRoundByItsBypass_AndTheStepAfterStillStarts(string plan, StepStatus risky)
    {
        var run = this.Start(
        [
            Ai("plan", plan, To("risky", when: Not(PlanSays("skip"))), To("ship", when: PlanSays("skip"))),
            Ai("risky", "Risky", To("ship")),
            Ai("ship", "Ship {{steps.risky.output?}}"),
        ]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(risky, run.StepOf("risky")!.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("ship")!.Status);
        Assert.Equal(risky == StepStatus.Done ? "main did: Ship main did: Risky" : "main did: Ship ", run.StepOf("ship")!.Output);
    }

    [Theory]
    [InlineData(ArrowConstants.FollowAll, "a,b")]
    [InlineData(ArrowConstants.FollowFirst, "a")]
    public async Task Follow_TakesEveryArrowThatHolds_OrOnlyTheFirst(string follow, string ran)
    {
        var plan = Ai("plan", "Plan x", To("a", when: PlanSays("x")), To("b", when: PlanSays("x")), To("c", when: PlanSays("nope")));
        plan.Follow = follow;
        var run = this.Start([plan, Ai("a", "Do A"), Ai("b", "Do B"), Ai("c", "Do C")]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(ran.Split(','), run.Steps.Where(s => s.StepId != "plan" && s.Status == StepStatus.Done).Select(s => s.StepId));
        Assert.Equal(StepStatus.Pending, run.StepOf("c")!.Status);
    }

    [Fact]
    public async Task FollowFirst_FallsBackToTheUnconditionalArrows()
    {
        var plan = Ai("plan", "Plan", To("a", when: PlanSays("nope")), To("b"), To("c"));
        plan.Follow = ArrowConstants.FollowFirst;
        var run = this.Start([plan, Ai("a", "Do A"), Ai("b", "Do B"), Ai("c", "Do C")]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepStatus.Pending, run.StepOf("a")!.Status);
        Assert.Equal(1, this.Asked("Do B"));
        Assert.Equal(1, this.Asked("Do C"));
    }

    [Fact]
    public async Task FailureArrow_HandlesTheFailure_AndTheRunEndsDone()
    {
        var build = Ai("build", "Build", To("ship"), To("fix", ArrowConstants.OnFailure));
        build.Agent = "beta";
        this.Failing("beta");
        var run = this.Start([build, Ai("ship", "Ship"), Ai("fix", "Fix {{arrived.from}}")]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepStatus.Failed, run.StepOf("build")!.Status);
        Assert.Equal(StepStatus.Pending, run.StepOf("ship")!.Status);
        Assert.Equal("main did: Fix build", run.StepOf("fix")!.Output);
    }

    [Fact]
    public async Task AlwaysArrow_RunsLikeFinally_AndTheRunStillFails()
    {
        var build = Ai("build", "Build", To("notify", ArrowConstants.OnAlways));
        build.Agent = "beta";
        this.Failing("beta");
        var run = this.Start([build, Ai("notify", "Notify")]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("notify")!.Status);
    }

    [Fact]
    public async Task UnhandledFailure_FailsTheRun_AndRetryRunsTheStepAgain()
    {
        var build = Ai("build", "Build", To("ship"));
        build.Agent = "beta";
        this.Failing("beta");
        var run = this.Start([build, Ai("ship", "Ship")]);

        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(StepStatus.Pending, run.StepOf("ship")!.Status);

        this.Fake("beta").Respond = r => new ExecutionResult(ExecutionStatus.Completed, "built", r.SessionId);
        run = await this.Engine.RetryAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(1, this.Asked("Ship"));
    }

    [Fact]
    public async Task Retry_RunsTheStepAgain_BeforeItsFailureArrowFires()
    {
        var build = Ai("build", "Build", To("fix", ArrowConstants.OnFailure));
        build.Agent = "beta";
        build.Retry = new RetryDefinition { Max = 2 };
        this.Failing("beta");
        var run = this.Start([build, Ai("fix", "Fix")]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(3, this.Fake("beta").Requests.Count);
        Assert.Equal(2, run.StepOf("build")!.History.Count(a => a.Cause == AttemptCause.Retry));
        Assert.Equal(1, this.Asked("Fix"));
    }

    [Fact]
    public async Task GivingUp_FailsTheStep_FollowsItsFailureArrows_AndIsNotRetried()
    {
        var check = new UserStepDefinition
        {
            Id = "check", Name = "Check", Instructions = "Check it",
            Arrows = [To("ship"), To("notify", ArrowConstants.OnFailure)],
            Retry = new RetryDefinition { Max = 2 },
        };
        var run = this.Start([check, Ai("ship", "Ship"), Ai("notify", "Notify {{arrived.from}}")]);

        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(StepStatus.AwaitingUser, run.StepOf("check")!.Status);
        await Assert.ThrowsAsync<AflException>(() => this.Engine.GiveUpAsync(run.Id, " "));

        run = await this.Engine.GiveUpAsync(run.Id, "The staging box is down.");

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepStatus.Failed, run.StepOf("check")!.Status);
        Assert.Equal(StepConstants.FailureGivenUp, run.StepOf("check")!.FailureKind);
        Assert.Equal("The staging box is down.", run.StepOf("check")!.Error);
        Assert.DoesNotContain(run.StepOf("check")!.History, a => a.Cause == AttemptCause.Retry);
        Assert.Equal(StepStatus.Pending, run.StepOf("ship")!.Status);
        Assert.Equal(1, this.Asked("Notify check"));
    }

    [Theory]
    [InlineData(true, "timed-out")]
    [InlineData(false, "failed")]
    public async Task Timeout_FailsTheStep_AndFiresTimeoutArrowsBeforeFailureOnes(bool hasTimeoutArrow, string goesTo)
    {
        ArrowDefinition[] arrows = hasTimeoutArrow
            ? [To("timed-out", ArrowConstants.OnTimeout), To("failed", ArrowConstants.OnFailure)]
            : [To("failed", ArrowConstants.OnFailure)];
        var slow = Ai("slow", "Slow", arrows);
        slow.Agent = "beta";
        slow.Timeout = "0.2s";
        this.Fake("beta").Delay = TimeSpan.FromSeconds(10);
        var run = this.Start([slow, Ai("timed-out", "Timed out"), Ai("failed", "Failed")]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepConstants.FailureTimeout, run.StepOf("slow")!.FailureKind);
        Assert.Contains("ran over its timeout", run.StepOf("slow")!.Error);
        Assert.Equal(StepStatus.Done, run.StepOf(goesTo)!.Status);
        Assert.Single(run.Steps, s => s.StepId != "slow" && s.Status == StepStatus.Done);
    }

    [Fact]
    public async Task WaitForAny_WithFinish_StartsEarly_AndTheRunWaitsForTheOtherPath()
    {
        var review = Ai("review", "Review");
        review.WaitFor = new AnyWaitRule();
        review.Others = ArrowConstants.OthersFinish;
        var run = this.Start([Ai("plan", "Plan", To("a"), To("b")), Ai("a", "Do A", To("review")), Gated(Ai("b", "Do B", To("review"))), review]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("review")!.Status);

        await this.Engine.ApproveAsync(this.Engine.ResolveTarget(run.Id, "b"));

        run = this.Get(run.Id);
        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("b")!.Status);
        Assert.Equal(1, this.Asked("Review"));
    }

    [Fact]
    public async Task WaitForGroups_StartOnceOneSideHolds_AndCancelTheRest()
    {
        var review = Ai("review", "Review");
        review.WaitFor = new AnyWaitRule { Items = [new AllWaitRule { Items = [Leaf("a"), Leaf("b")] }, Leaf("c")] };
        var run = this.Start(
        [
            Ai("plan", "Plan", To("a"), To("b"), To("c")),
            Gated(Ai("a", "Do A", To("review"))),
            Gated(Ai("b", "Do B", To("review"))),
            Ai("c", "Do C", To("review")),
            review,
        ]);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("review")!.Status);
        var paths = run.StepOf("plan")!.Paths!;
        Assert.All(paths.Where(p => p.Branch != "c"), p => Assert.Equal(RunStatus.Cancelled, p.Status));
        Assert.All(paths.Where(p => p.Branch != "c"), p => Assert.Equal(StepConstants.OvertakenNote, p.Note));
    }

    [Fact]
    public async Task MaxParallel_CapsHowManyPathsRunAtOnce()
    {
        this.Fake("main").Together = 2;
        var run = this.Start(
            [Ai("plan", "Plan", To("a"), To("b"), To("c")), Ai("a", "Do A", To("end")), Ai("b", "Do B", To("end")), Ai("c", "Do C", To("end"))],
            2);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(2, this.Fake("main").MostAtOnce);
    }

    [Fact]
    public async Task MaxSteps_StopsARunThatGoesRoundSeveralLoops()
    {
        var never = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "never" };
        var run = this.Start(
        [
            Ai("a", "A", new ArrowDefinition { To = "a", Until = never, Max = 3, OnLimit = StepConstants.OnLimitContinue }, To("b")),
            Ai("b", "B", new ArrowDefinition { To = "a", Until = never, Max = 50 }),
        ], setUp: w => w.MaxSteps = 10);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(10, this.Fake("main").Requests.Count);
        Assert.Contains(run.Steps, s => s.Error?.StartsWith("The run has done 10 step runs, its maxSteps") == true);
    }

    [Fact]
    public async Task Loop_ForgetsTheDeadPathsDownstream_SoAStepWaitsForThemNextRound()
    {
        var never = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "never" };
        var secondRound = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Step = "a", Contains = "A2" };
        var run = this.Start(
        [
            Ai("a", "A{{loop.iteration}}", To("b"), To("c", when: secondRound)),
            Ai("b", "B", new ArrowDefinition { To = "a", Until = never, Max = 2, OnLimit = StepConstants.OnLimitContinue }, To("e")),
            Ai("c", "C", To("e")),
            Ai("e", "E after {{steps.c.output?}}"),
        ], maxParallel: 1);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains("main did: C", run.StepOf("e")!.Output);
        Assert.Equal(1, this.Asked("E after main did: C"));
    }

    [Fact]
    public async Task CatchStep_RunsWhenNoArrowHandlesAFailure_AndTheRunStillFails()
    {
        var build = Ai("build", "Build", To("ship"));
        build.Agent = "beta";
        this.Failing("beta");
        var run = this.Start([build, Ai("ship", "Ship"), Ai("cleanup", "Clean up after {{arrived.from}}")], setUp: w => w.OnFailure = "cleanup");

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(StepStatus.Pending, run.StepOf("ship")!.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("cleanup")!.Status);
    }
}
