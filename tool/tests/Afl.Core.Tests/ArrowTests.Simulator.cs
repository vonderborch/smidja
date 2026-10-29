using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for <see cref="FlowSimulator" />: it routes a workflow the way a run does, so each test runs the same
///     workflow for real and checks the simulation gives the same steps.
/// </summary>
public partial class ArrowTests
{
    private static WorkflowDefinition Workflow(IEnumerable<AbstractStepDefinition> steps, Action<WorkflowDefinition>? setUp = null)
    {
        var workflow = new WorkflowDefinition { Id = "test", Name = "Test", Steps = [.. steps] };
        setUp?.Invoke(workflow);
        return workflow;
    }

    private static string[] Ran(Run run) =>
        [.. run.Steps.Where(s => s.Status is StepStatus.Done or StepStatus.Failed or StepStatus.Skipped).Select(s => s.StepId).Order()];

    private static string[] Ran(FlowSimulation simulation) => [.. simulation.Visits.Select(v => v.StepId).Distinct().Order()];

    [Theory]
    [InlineData("skip")]
    [InlineData("go")]
    public async Task Simulate_GoesRoundAConditionalStep_LikeARun(string plan)
    {
        AbstractStepDefinition[] steps =
        [
            Ai("plan", plan, To("risky", when: Not(PlanSays("skip"))), To("ship", when: PlanSays("skip"))),
            Ai("risky", "Risky", To("ship")),
            Ai("ship", "Ship {{steps.risky.output?}}"),
        ];
        var skip = plan == "skip";
        var arrows = ((AiStepDefinition)steps[0]).Arrows!;
        var simulator = new FlowSimulator(taken: new Dictionary<string, bool>
        {
            [FlowMachine.KeyOf("plan", arrows[0])] = !skip,
            [FlowMachine.KeyOf("plan", arrows[1])] = skip,
        });

        var simulation = await simulator.SimulateAsync(Workflow(steps));
        var run = await this.Engine.DriveAsync(this.Start(steps).Id);

        Assert.False(simulation.Failed);
        Assert.Equal(Ran(run), Ran(simulation));
        Assert.Equal(1, simulation.RunsOf("ship"));
        Assert.Equal(skip, simulation.Dead.Contains(FlowMachine.KeyOf("plan", arrows[0])));
        Assert.Equal(skip, simulation.Dead.Contains(FlowMachine.KeyOf("risky", ((AiStepDefinition)steps[1]).Arrows![0])));
    }

    [Fact]
    public async Task Simulate_StartsAStepOnceOneSideOfItsWaitHolds_AndCancelsTheRest()
    {
        var review = Ai("review", "Review");
        review.WaitFor = new AnyWaitRule { Items = [new AllWaitRule { Items = [Leaf("a"), Leaf("b")] }, Leaf("c")] };
        AbstractStepDefinition[] steps =
            [Ai("plan", "Plan", To("a"), To("b"), To("c")), Ai("a", "Do A", To("review")), Ai("b", "Do B", To("review")), Ai("c", "Do C", To("review")), review];

        var simulation = await new FlowSimulator().SimulateAsync(Workflow(steps));

        Assert.False(simulation.Failed);
        Assert.Equal(["plan", "a", "b", "review"], simulation.Visits.Select(v => v.StepId));
        Assert.Equal(["c"], simulation.Overtaken);
    }

    [Fact]
    public async Task Simulate_WithFinish_LetsTheOtherPathsRun()
    {
        var review = Ai("review", "Review");
        review.WaitFor = new AnyWaitRule();
        review.Others = ArrowConstants.OthersFinish;
        AbstractStepDefinition[] steps = [Ai("plan", "Plan", To("a"), To("b")), Ai("a", "Do A", To("review")), Ai("b", "Do B", To("review")), review];

        var simulation = await new FlowSimulator().SimulateAsync(Workflow(steps));
        var run = await this.Engine.DriveAsync(this.Start(steps).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(Ran(run), Ran(simulation));
        Assert.Equal(1, simulation.RunsOf("review"));
        Assert.Empty(simulation.Overtaken);
    }

    [Fact]
    public async Task Simulate_GoesRoundALoopUpToItsLimit_LikeARun()
    {
        var never = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "never" };
        var loop = new ArrowDefinition { To = "a", Until = never, Max = 3, OnLimit = StepConstants.OnLimitContinue };
        AbstractStepDefinition[] steps = [Ai("a", "A", loop, To("b")), Ai("b", "B")];

        var notTaken = await new FlowSimulator().SimulateAsync(Workflow(steps));
        var taken = await new FlowSimulator(taken: new Dictionary<string, bool> { [FlowMachine.KeyOf("a", loop)] = true }).SimulateAsync(Workflow(steps));
        var run = await this.Engine.DriveAsync(this.Start(steps).Id);

        Assert.Equal(1, notTaken.RunsOf("a"));
        Assert.Equal(this.Asked("A"), taken.RunsOf("a"));
        Assert.Equal(1, taken.RunsOf("b"));
        Assert.Contains(FlowMachine.KeyOf("a", loop), taken.Fired);
        Assert.False(taken.Failed);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task Simulate_StopsAtMaxSteps()
    {
        var never = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "never" };
        var aLoop = new ArrowDefinition { To = "a", Until = never, Max = 3, OnLimit = StepConstants.OnLimitContinue };
        var bLoop = new ArrowDefinition { To = "a", Until = never, Max = 50 };
        var simulator = new FlowSimulator(taken: new Dictionary<string, bool> { [FlowMachine.KeyOf("a", aLoop)] = true, [FlowMachine.KeyOf("b", bLoop)] = true });

        var simulation = await simulator.SimulateAsync(Workflow([Ai("a", "A", aLoop, To("b")), Ai("b", "B", bLoop)], w => w.MaxSteps = 10));

        Assert.True(simulation.Failed);
        Assert.Equal(10, simulation.Visits.Count);
        Assert.StartsWith("The run has done 10 step runs", simulation.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Simulate_FailsWhereAStepFails_AndRunsTheCatchStep(bool catchStep)
    {
        AbstractStepDefinition[] steps = [Ai("build", "Build", To("ship")), Ai("ship", "Ship"), Ai("cleanup", "Clean up")];
        var outcomes = new Dictionary<string, FlowTrigger> { ["build"] = FlowTrigger.Failed };

        var simulation = await new FlowSimulator(outcomes).SimulateAsync(Workflow(steps, w => w.OnFailure = catchStep ? "cleanup" : null));

        Assert.True(simulation.Failed);
        Assert.Equal("build", simulation.FailedAt);
        Assert.Equal(0, simulation.RunsOf("ship"));
        Assert.Equal(catchStep ? 1 : 0, simulation.RunsOf("cleanup"));
    }

    [Fact]
    public async Task Simulate_TakesAFailureArrow_AndTheRunCarriesOn()
    {
        AbstractStepDefinition[] steps = [Ai("build", "Build", To("ship"), To("notify", ArrowConstants.OnFailure)), Ai("ship", "Ship"), Ai("notify", "Notify")];

        var simulation = await new FlowSimulator(new Dictionary<string, FlowTrigger> { ["build"] = FlowTrigger.Failed }).SimulateAsync(Workflow(steps));

        Assert.False(simulation.Failed);
        Assert.Equal(["build", "notify"], simulation.Visits.Select(v => v.StepId));
        Assert.Contains(FlowMachine.KeyOf("build", ((AiStepDefinition)steps[0]).Arrows![0]), simulation.Dead);
    }
}
