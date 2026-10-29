using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for parallel paths: a step with several arrows that fire runs each target as a path run, together, and a
///     step several arrows lead into starts once its <c>waitFor</c> holds (all of them, or the first with <c>any</c>).
/// </summary>
public class ForkTests : AbstractAgentPoolTest
{
    private static AbstractStepDefinition Ai(string id, string prompt, params string[] then)
    {
        return new AiStepDefinition { Id = id, Name = id, Prompt = prompt, Arrows = then.Length == 0 ? null : [.. then.Select(t => new ArrowDefinition { To = t })] };
    }

    private static AbstractStepDefinition Gated(AbstractStepDefinition step)
    {
        step.Advance = StepConstants.AdvanceApprove;
        return step;
    }

    private Run Get(string id) => new RunStore(this.Paths).GetRequired(id);

    private int Asked(string agent, string prompt) => this.Fake(agent).Requests.Count(r => r.Prompt == prompt);

    private Run StartFork(AbstractStepDefinition a, AbstractStepDefinition b, string review = "Review {{steps.a.output}} | {{steps.b.output}}", bool sequential = false,
        Action<AbstractStepDefinition>? join = null)
    {
        var reviewStep = Ai("review", review);
        join?.Invoke(reviewStep);
        var workflow = new WorkflowDefinition { Id = "test", Name = "Test", Steps = [Ai("plan", "Plan", "a", "b"), a, b, reviewStep] };
        return this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main", maxParallel: sequential ? 1 : null);
    }

    [Fact]
    public async Task Split_RunsPathsTogether_AndTheJoinSeesBoth()
    {
        this.Fake("main").Together = 2;
        var run = this.StartFork(Ai("a", "Do A", "review"), Ai("b", "Do B", "review"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(2, this.Fake("main").MostAtOnce);
        var fork = run.StepOf("plan")!;
        Assert.Equal(StepStatus.Done, fork.Status);
        Assert.Equal(["a", "b"], fork.Paths!.Select(b => b.Branch));
        Assert.All(fork.Paths!, b => Assert.True(this.Get(b.RunId).IsPathRun));
        Assert.Equal("main did: Do A", run.StepOf("a")!.Output);
        Assert.Equal("main did: Review main did: Do A | main did: Do B", run.StepOf("review")!.Output);
        Assert.Equal("plan", run.Path[0].StepId);
        Assert.Equal("review", run.Path[^1].StepId);
        Assert.Equal(["a", "b"], run.Path.Where(v => v.Branch is not null).Select(v => v.Branch!).Order());
    }

    [Fact]
    public async Task Sequential_RunsOnePathAtATime()
    {
        this.Fake("main").Delay = TimeSpan.FromMilliseconds(50);
        var run = this.StartFork(Ai("a", "Do A", "review"), Ai("b", "Do B", "review"), sequential: true);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(1, this.Fake("main").MostAtOnce);
    }

    [Fact]
    public async Task FailedPath_FailsTheRun_AndRetryRerunsOnlyIt()
    {
        var b = Ai("b", "Do B", "review");
        b.Agent = "beta";
        this.Fake("beta").Respond = _ => new ExecutionResult(ExecutionStatus.Failed, null, null, "boom");
        var run = this.StartFork(Ai("a", "Do A", "review"), b);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        var fork = run.StepOf("plan")!;
        Assert.Equal(StepStatus.Failed, fork.Status);
        Assert.Contains("Step 'b' failed on path 'b'", fork.Error);
        Assert.Equal(StepStatus.Done, run.StepOf("a")!.Status);

        this.Fake("beta").Respond = r => new ExecutionResult(ExecutionStatus.Completed, $"beta did: {r.Prompt}", r.SessionId);
        run = await this.Engine.RetryAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(1, this.Asked("main", "Do A"));
        Assert.Equal("main did: Review main did: Do A | beta did: Do B", run.StepOf("review")!.Output);
    }

    [Fact]
    public async Task SeveralWaitingSteps_NeedAStep_AndEachCanBeApproved()
    {
        var run = this.StartFork(Gated(Ai("a", "Do A", "review")), Gated(Ai("b", "Do B", "review")));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Waiting, run.Status);
        Assert.Equal(StepStatus.AwaitingPaths, run.CurrentStep!.Status);
        var waiting = new RunStore(this.Paths).WaitingSteps(run);
        Assert.Equal(["a", "b"], waiting.Select(w => w.Step.StepId).Order());
        var error = Assert.Throws<AflException>(() => this.Engine.ResolveTarget(run.Id));
        Assert.Contains("several steps waiting", error.Message);
        Assert.Contains("--step", error.Message);
        Assert.Throws<AflException>(() => this.Engine.ResolveTarget(run.Id, "review"));

        var a = this.Engine.ResolveTarget(run.Id, "a");
        Assert.True(this.Get(a).IsPathRun);
        await this.Engine.ApproveAsync(a);
        Assert.Equal(StepStatus.AwaitingPaths, this.Get(run.Id).CurrentStep!.Status);
        Assert.Equal(this.Engine.ResolveTarget(run.Id, "b"), this.Engine.ResolveTarget(run.Id));

        await this.Engine.ApproveAsync(this.Engine.ResolveTarget(run.Id));

        run = this.Get(run.Id);
        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("a")!.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("b")!.Status);
    }

    [Fact]
    public async Task WaitForAny_TheFirstPathWins_AndTheOthersAreCancelled()
    {
        var run = this.StartFork(Ai("a", "Do A", "review"), Gated(Ai("b", "Do B", "review")), "Review {{steps.a.output?}} | {{steps.b.output?}}",
            join: s => s.WaitFor = new AnyWaitRule());

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var branches = run.StepOf("plan")!.Paths!;
        Assert.Equal(RunStatus.Done, branches.Single(b => b.Branch == "a").Status);
        var loser = branches.Single(b => b.Branch == "b");
        Assert.Equal(RunStatus.Cancelled, loser.Status);
        Assert.Equal(StepConstants.OvertakenNote, loser.Note);
        Assert.Equal(RunStatus.Cancelled, this.Get(loser.RunId).Status);
        Assert.Equal("main did: Review main did: Do A | ", run.StepOf("review")!.Output);
    }

    [Fact]
    public async Task Cancel_CancelsThePaths()
    {
        var run = this.StartFork(Gated(Ai("a", "Do A", "review")), Gated(Ai("b", "Do B", "review")));
        run = await this.Engine.DriveAsync(run.Id);

        run = this.Engine.Cancel(run.Id);

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.All(run.StepOf("plan")!.Paths!, b => Assert.Equal(RunStatus.Cancelled, this.Get(b.RunId).Status));
    }

    [Fact]
    public async Task Replay_OnAPath_RerunsOnlyThatPath()
    {
        var run = this.StartFork(Ai("a", "Do A", "review"), Ai("b", "Do B", "review"));
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(RunStatus.Done, run.Status);

        run = await this.Engine.ReplayAsync(run.Id, "a", "again");

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(1, this.Asked("main", "Do B"));
        Assert.Equal(2, this.Fake("main").Requests.Count(r => r.Prompt.StartsWith("Review")));
        Assert.Equal(1, run.StepOf("a")!.Replays);
    }

    [Fact]
    public async Task NestedSplit_JoinsInnerThenOuter()
    {
        var steps = new List<AbstractStepDefinition>
        {
            Ai("plan", "Plan", "a", "b"),
            Ai("a", "Do A", "a1", "a2"),
            Ai("a1", "Do A1", "a3"),
            Ai("a2", "Do A2", "a3"),
            Ai("a3", "Join A {{steps.a1.output}} {{steps.a2.output}}", "review"),
            Ai("b", "Do B", "review"),
            Ai("review", "Review {{steps.a3.output}} | {{steps.b.output}}"),
        };
        var run = this.Engine.Create(this.RegisterRepo(), new WorkflowDefinition { Id = "test", Name = "Test", Steps = steps }, SampleTicket(), "main");

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("main did: Join A main did: Do A1 main did: Do A2", run.StepOf("a3")!.Output);
        Assert.Contains("main did: Do B", run.StepOf("review")!.Output);
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
    }

    [Fact]
    public async Task PathsThatOnlyEnd_EndTheRun()
    {
        var workflow = new WorkflowDefinition { Id = "test", Name = "Test", Steps = [Ai("plan", "Plan", "a", "b"), Ai("a", "Do A"), Ai("b", "Do B")] };
        var run = this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main");

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Null(run.CurrentStepId);
        Assert.Equal(StepStatus.Done, run.StepOf("a")!.Status);
        Assert.Equal(StepStatus.Done, run.StepOf("b")!.Status);
    }
}
