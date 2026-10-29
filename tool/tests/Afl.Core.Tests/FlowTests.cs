using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for branching (routes) and loops (repeat) between steps.
/// </summary>
public class FlowTests : AbstractAflTest
{
    private readonly FakeStepExecutor _worker = new();

    private readonly FakeStepExecutor _judge = new()
    {
        Respond = r => new ExecutionResult(ExecutionStatus.Completed, "YES, it's done.", r.SessionId),
    };

    private RunEngine Engine => new(new RunStore(this.Paths), id => id switch
    {
        "judge" => this._judge,
        "session" => new SessionExecutor(),
        _ => this._worker,
    });

    private static AiStepDefinition Ai(string id, string prompt = "Do {{ticket.key}}") =>
        new() { Id = id, Name = id, Prompt = prompt };

    private static UserStepDefinition Ask(string id) =>
        new()
        {
            Id = id, Name = id, Instructions = "Decide.",
            Inputs = [new StepInput { Id = "big", Label = "Is it big?", Type = StepConstants.InputConfirm }],
        };

    private static ConditionDefinition OutputContains(string text, string? step = null) =>
        new() { Kind = StepConstants.ConditionOutput, Contains = text, Step = step };

    private Run Start(params AbstractStepDefinition[] steps)
    {
        return this.Engine.Create(this.RegisterRepo(), Workflow(steps), SampleTicket(), "worker");
    }

    [Theory]
    [InlineData("yes", StepStatus.Pending, StepStatus.Done)]
    [InlineData("n", StepStatus.Done, StepStatus.Pending)]
    public async Task Arrows_BranchOnAnInputAnswer(string answer, StepStatus small, StepStatus big)
    {
        var ask = Ask("ask");
        ask.Follow = ArrowConstants.FollowFirst;
        ask.Arrows =
        [
            new ArrowDefinition { To = "big", When = new ConditionDefinition { Kind = "input", Input = "big", EqualTo = "true" } },
            new ArrowDefinition { To = "small" },
        ];
        var smallStep = Ai("small");
        smallStep.Arrows = [new ArrowDefinition { To = "wrap" }];
        var run = this.Start(ask, smallStep, Ai("big"), Ai("wrap", "Wrap {{steps.big.output?}}"));
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.CompleteAsync(run.Id, answers: new Dictionary<string, string> { ["big"] = answer });

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(small, run.Steps[1].Status);
        Assert.Equal(big, run.Steps[2].Status);
        Assert.Equal(StepStatus.Done, run.Steps[3].Status);
        Assert.Equal(["ask", small == StepStatus.Done ? "small" : "big", "wrap"], run.Path.Select(v => v.StepId));
        if (big == StepStatus.Pending)
        {
            Assert.Equal("Wrap ", this._worker.Requests.Last().Prompt);
        }
    }

    [Fact]
    public async Task Arrows_EndTheRunWhenTheOutputMatches()
    {
        this._worker.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "Tests: ALL PASS", r.SessionId);
        var check = Ai("check");
        check.Follow = ArrowConstants.FollowFirst;
        check.Arrows =
        [
            new ArrowDefinition { To = ArrowConstants.End, When = new ConditionDefinition { Kind = "output", Matches = @"^Tests: ALL\s+PASS$" } },
            new ArrowDefinition { To = "fix" },
        ];
        var run = this.Start(check, Ai("fix"), Ai("ship"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal([StepStatus.Done, StepStatus.Pending, StepStatus.Pending], run.Steps.Select(s => s.Status));
        Assert.False(run.Steps[1].HasRun);
        Assert.Single(this._worker.Requests);
    }

    [Fact]
    public async Task Loop_RunsUntilTheConditionHolds_WithTheIterationAndPreviousAttempt()
    {
        this._worker.Respond = r => new ExecutionResult(ExecutionStatus.Completed,
            r.Prompt.StartsWith("Try 3") ? "GREEN" : $"red after {r.Prompt}", r.SessionId);
        var fix = Ai("fix", "Try {{loop.iteration}}; last time: {{steps.fix.previous}}");
        fix.Arrows = [new ArrowDefinition { To = "fix", Until = OutputContains("green") }, new ArrowDefinition { To = "after" }];
        var run = this.Start(fix, Ai("after", "After {{steps.fix.output}}"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var prompts = this._worker.Requests.Select(r => r.Prompt).ToList();
        Assert.Equal("Try 1; last time: ", prompts[0]);
        Assert.Equal("Try 2; last time: red after Try 1; last time: ", prompts[1]);
        Assert.StartsWith("Try 3; last time: red after Try 2", prompts[2]);
        Assert.Equal("After GREEN", prompts[3]);
        var step = run.Steps[0];
        Assert.Equal(3, step.Runs);
        Assert.Equal(["Loop iteration 2", "Loop iteration 3"], step.History.Select(a => a.Reason));
        Assert.Equal([("fix", 1), ("fix", 2), ("fix", 3), ("after", 1)], run.Path.Select(v => (v.StepId, v.Iteration)));
        Assert.NotEqual(this._worker.Requests[0].SessionId, this._worker.Requests[1].SessionId);
    }

    [Fact]
    public async Task Templates_SeeTheLoopLimitAndWhereTheStepWasReachedFrom()
    {
        var fix = Ai("fix", "{{loop.iteration}}/{{loop.max}} from [{{arrived.from}}]");
        fix.Arrows = [new ArrowDefinition { To = "fix", Until = OutputContains("1/3"), Max = 3 }, new ArrowDefinition { To = "after" }];
        fix.Name = "Fix it";
        var after = Ai("after", "{{loop.max}} from {{arrived.from}} ({{arrived.fromName}})");
        this._worker.Respond = r => new ExecutionResult(ExecutionStatus.Completed, r.Prompt, r.SessionId);
        var run = this.Start(fix, after);

        Assert.Empty(WorkflowLibrary.Validate(run.Workflow));
        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(["1/3 from []", " from fix (Fix it)"], this._worker.Requests.Select(r => r.Prompt));
    }

    [Fact]
    public async Task Loop_FailsAtItsLimit_OrMovesOnWhenAskedTo()
    {
        var fix = Ai("fix");
        var loop = new ArrowDefinition { To = "fix", Until = OutputContains("never"), Max = 2 };
        fix.Arrows = [loop, new ArrowDefinition { To = "after" }];
        var run = await this.Engine.DriveAsync(this.Start(fix, Ai("after")).Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(StepStatus.Failed, run.Steps[0].Status);
        Assert.StartsWith("Loop limit reached (2)", run.Steps[0].Error);
        Assert.Equal(2, this._worker.Requests.Count);

        loop.OnLimit = StepConstants.OnLimitContinue;
        run = await this.Engine.DriveAsync(this.Start(fix, Ai("after")).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains("Loop limit reached (2); moved on.", run.Steps[0].Notes);
    }

    [Fact]
    public async Task Loop_BackToAnEarlierStep_RedoesEveryStepInTheLoop()
    {
        var tries = 0;
        this._worker.Respond = r => new ExecutionResult(ExecutionStatus.Completed,
            r.Prompt == "Test" ? (++tries == 2 ? "PASS" : "FAIL") : "fixed", r.SessionId);
        var fix = Ai("fix", "Fix {{steps.test.output}}");
        fix.Arrows = [new ArrowDefinition { To = "test", Until = OutputContains("pass", "test") }];
        var test = Ai("test", "Test");
        test.Follow = ArrowConstants.FollowFirst;
        test.Arrows = [new ArrowDefinition { To = ArrowConstants.End, When = OutputContains("pass") }, new ArrowDefinition { To = "fix" }];

        var run = await this.Engine.DriveAsync(this.Start(test, fix).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(["test", "fix", "test"], run.Path.Select(v => v.StepId));
        Assert.Equal(StepStatus.Pending, run.Steps[1].Status);
        Assert.Equal("Loop iteration 2", run.Steps[1].History.Single().Reason);
        Assert.Equal("FAIL", run.Steps[0].History.Single().Output);
    }

    [Fact]
    public async Task Loop_BackToAnEarlierStep_UpToItsMax()
    {
        var test = Ai("test", "Test");
        test.Follow = ArrowConstants.FollowFirst;
        test.Arrows = [new ArrowDefinition { To = ArrowConstants.End, When = OutputContains("PASS") }, new ArrowDefinition { To = "fix" }];
        var fix = Ai("fix", "Fix {{steps.test.output}}");
        fix.Arrows = [new ArrowDefinition { To = "test", Max = 2 }];

        var run = await this.Engine.DriveAsync(this.Start(test, fix).Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("fix", run.CurrentStep!.StepId);
        Assert.StartsWith("Loop limit reached (2): step 'test'", run.CurrentStep.Error);
        Assert.Equal(["test", "fix", "test", "fix"], run.Path.Select(v => v.StepId));
        Assert.Equal("Loop iteration 2", run.Steps[0].History.Single().Reason);
        Assert.Equal(2, run.Steps[0].Runs);
    }

    [Fact]
    public async Task Loop_BackPastTheLimit_MovesOnWhenAskedTo()
    {
        var test = Ai("test", "Test");
        var fix = Ai("fix", "Fix {{steps.test.output}}");
        fix.Arrows =
        [
            new ArrowDefinition { To = "test", Max = 2, OnLimit = StepConstants.OnLimitContinue },
            new ArrowDefinition { To = "after" },
        ];

        var run = await this.Engine.DriveAsync(this.Start(test, fix, Ai("after")).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(["test", "fix", "test", "fix", "after"], run.Path.Select(v => v.StepId));
        Assert.Contains("Loop limit reached (2); moved on.", run.Steps[1].Notes);
    }

    [Fact]
    public async Task Loop_SelfAndBackToAnEarlierStep_CountSeparately()
    {
        var polishes = 0;
        this._worker.Respond = r => new ExecutionResult(ExecutionStatus.Completed,
            r.Prompt == "Polish" ? (++polishes % 2 == 0 ? "shiny" : "dull") : "ok", r.SessionId);
        var test = Ai("test", "Test");
        var polish = Ai("polish", "Polish");
        polish.Arrows =
        [
            new ArrowDefinition { To = "polish", Until = OutputContains("shiny"), Max = 2 },
            new ArrowDefinition { To = "test", Max = 2, OnLimit = StepConstants.OnLimitContinue },
            new ArrowDefinition { To = "after" },
        ];

        var run = await this.Engine.DriveAsync(this.Start(test, polish, Ai("after")).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(["test", "polish", "polish", "test", "polish", "polish", "after"], run.Path.Select(v => v.StepId));
    }

    [Fact]
    public async Task AiCondition_AsksTheJudgeReadOnly()
    {
        var review = Ai("review");
        review.Follow = ArrowConstants.FollowFirst;
        review.Arrows =
        [
            new ArrowDefinition
            {
                To = ArrowConstants.End,
                When = new ConditionDefinition { Kind = "ai", Question = "Is {{ticket.key}} finished?", Agent = "judge" },
            },
            new ArrowDefinition { To = "more" },
        ];

        var run = await this.Engine.DriveAsync(this.Start(review, Ai("more")).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepStatus.Pending, run.Steps[1].Status);
        var request = this._judge.Requests.Single();
        Assert.Equal(StepConstants.ReadOnlyPermissionMode, request.PermissionMode);
        Assert.Contains("Question: Is acme/app#7 finished?", request.Prompt);
        Assert.Contains("output for: Do acme/app#7", request.Prompt);
        Assert.EndsWith("review.condition.log", request.LogPath);
        Assert.Contains("judge answered yes to: Is acme/app#7 finished?", run.Steps[0].Notes);
    }

    [Fact]
    public async Task AiCondition_FailsTheStepWhenItCantBeAnswered()
    {
        this._judge.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "Maybe?", r.SessionId);
        var review = Ai("review");
        var loop = new ArrowDefinition { To = "review", Until = new ConditionDefinition { Kind = "ai", Question = "Done?", Agent = "judge" } };
        review.Arrows = [loop];

        var run = await this.Engine.DriveAsync(this.Start(review).Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("didn't answer yes or no", run.Steps[0].Error);
        Assert.Equal(0, run.Steps[0].Runs);

        loop.Until.Agent = "session";
        run = await this.Engine.DriveAsync(this.Start(review).Id);

        Assert.Contains("can't answer an ai condition", run.Steps[0].Error);

        this._judge.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "no.", r.SessionId);
        loop.Until = new ConditionDefinition
        {
            Kind = "not", Conditions = [new ConditionDefinition { Kind = "ai", Question = "Done?", Agent = "judge" }],
        };
        run = await this.Engine.DriveAsync(this.Start(review).Id);

        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task Replay_AfterALoop_ResetsItsCountAndRunsAgain()
    {
        var fix = Ai("fix");
        fix.Arrows =
        [
            new ArrowDefinition { To = "fix", Until = OutputContains("never"), Max = 2, OnLimit = StepConstants.OnLimitContinue },
            new ArrowDefinition { To = "after" },
        ];
        var run = await this.Engine.DriveAsync(this.Start(fix, Ai("after")).Id);
        Assert.Equal(4 - 1, this._worker.Requests.Count);

        run = await this.Engine.ReplayAsync(run.Id, "fix");

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(2, run.Steps[0].Runs);
        Assert.Equal(6, this._worker.Requests.Count);
        Assert.Equal([AttemptCause.Loop, AttemptCause.Replay, AttemptCause.Loop], run.Steps[0].History.Select(a => a.Cause));
        Assert.Equal(1, run.Steps[0].Replays);
    }

    [Fact]
    public async Task Replay_RefusesASkippedStep()
    {
        var check = Ai("check");
        check.Arrows = [];
        var run = await this.Engine.DriveAsync(this.Start(check, Ai("later")).Id);

        var ex = await Assert.ThrowsAsync<AflException>(() => this.Engine.ReplayAsync(run.Id, "later"));
        Assert.Contains("skipped", ex.Message);
    }

    [Fact]
    public void LimitOf_IsTheLimitOfTheLoopThatCanBringAStepBack()
    {
        var plan = Ai("plan");
        var test = Ai("test");
        var fix = Ai("fix");
        fix.Arrows = [new ArrowDefinition { To = "test", When = OutputContains("x"), Max = 3 }, new ArrowDefinition { To = "polish" }];
        var polish = Ai("polish");
        polish.Arrows = [new ArrowDefinition { To = "polish", Until = OutputContains("shiny"), Max = 4 }, new ArrowDefinition { To = "ship" }];
        var workflow = Workflow(plan, test, fix, polish, Ai("ship"));

        Assert.Equal([null, 3, 3, 4, null], workflow.Steps.Select(s => FlowRouter.Instance.LimitOf(workflow, s)));
    }

    [Fact]
    public void Validate_ChecksArrowsLoopsAndConditions()
    {
        var ask = Ask("ask");
        ask.Arrows =
        [
            new ArrowDefinition { To = "nowhere" },
            new ArrowDefinition { To = ArrowConstants.End, When = new ConditionDefinition { Kind = "input", Input = "big", EqualTo = "maybe" }, OnLimit = "shrug" },
            new ArrowDefinition { To = "work", On = "sometimes", When = OutputContains("a"), Until = OutputContains("b") },
            new ArrowDefinition { To = "work", When = OutputContains("c") },
        ];
        ask.Follow = "whichever";
        var work = Ai("work", "Again {{steps.later.previous}} #{{loop.iteration}}");
        work.Arrows =
        [
            new ArrowDefinition
            {
                To = "work", Max = 0, OnLimit = "explode",
                Until = new ConditionDefinition
                {
                    Kind = "any",
                    Conditions =
                    [
                        new ConditionDefinition { Kind = "output", Matches = "(" },
                        new ConditionDefinition { Kind = "input", Input = "size", EqualTo = "x", Step = "ask" },
                        new ConditionDefinition { Kind = "ai", Question = "Is {{steps.later.output}} ok?" },
                        new ConditionDefinition { Kind = "output", Contains = "x", Step = "later" },
                        new ConditionDefinition { Kind = "sometimes" },
                        new ConditionDefinition { Kind = "not" },
                    ],
                },
            },
            new ArrowDefinition { To = "later" },
        ];
        var errors = WorkflowLibrary.Validate(Workflow(ask, work, Ai("later")));

        string[] expected =
        [
            "Step 'ask': an arrow goes to 'nowhere', which isn't a step.",
            "Step 'ask': follow must be 'all' or 'first'.",
            "Step 'ask': the arrow to end: onLimit must be 'fail' or 'continue'.",
            "Step 'ask': the arrow to end: max and onLimit only apply to an arrow that goes back.",
            "Step 'ask': the arrow to end: 'equals' can't be an answer to input 'big'",
            "Step 'ask': the arrow to 'work': on must be 'success' or 'failure' or 'timeout' or 'always'.",
            "Step 'ask': the arrow to 'work' has both when and until; use one.",
            "Step 'work': the arrow to 'work': onLimit must be 'fail' or 'continue'.",
            "Step 'work': the arrow to 'work': max must be at least 1.",
            "Step 'work': the arrow to 'work': 'matches' isn't a valid regular expression",
            "Step 'work': the arrow to 'work': step 'ask' has no input 'size'.",
            "Step 'work': the arrow to 'work': condition step 'later' must be this step or one that leads to it.",
            "Step 'work': the arrow to 'work': unknown condition kind 'sometimes'.",
            "Step 'work': the arrow to 'work': a not condition needs exactly one condition in 'conditions'.",
            "Step 'work': '{{steps.later.output}}' is not available in a condition.",
        ];
        Assert.All(expected, e => Assert.Contains(errors, error => error.StartsWith(e, StringComparison.Ordinal)));
        Assert.Equal(expected.Length, errors.Count);
    }
}
