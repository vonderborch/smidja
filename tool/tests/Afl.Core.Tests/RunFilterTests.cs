using Afl.Core.Exceptions;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for what lists show a run as doing, and for searching and filtering runs.
/// </summary>
public class RunFilterTests : AbstractAflTest
{
    private RunStore Store => new(this.Paths);

    private RunEngine Engine => new(this.Store, new FakeStepExecutor());

    [Theory]
    [InlineData(RunStatus.Running, StepStatus.Running, RunState.Running)]
    [InlineData(RunStatus.Waiting, StepStatus.AwaitingApproval, RunState.NeedsYou)]
    [InlineData(RunStatus.Waiting, StepStatus.AwaitingSession, RunState.Waiting)]
    [InlineData(RunStatus.Failed, StepStatus.Failed, RunState.Failed)]
    [InlineData(RunStatus.Done, StepStatus.Done, RunState.Done)]
    [InlineData(RunStatus.Cancelled, StepStatus.AwaitingApproval, RunState.Cancelled)]
    public void State_SplitsWaitingOnYouOutOfWaiting(RunStatus status, StepStatus step, RunState expected)
    {
        var run = this.CreateRun();
        run.Status = status;
        run.CurrentStepId = run.Steps[0].StepId;
        run.Steps[0].Status = step;

        Assert.Equal(expected, run.State);
    }

    [Fact]
    public void Parse_ReadsStatesHoweverTheyAreWritten()
    {
        Assert.Equal([RunState.NeedsYou, RunState.Failed], RunStates.Parse(["needs-you", " Failed", "NeedsYou", ""]));
        Assert.Equal(["needs-you", "running", "waiting", "failed", "done", "cancelled"], RunStates.All.Select(s => s.CliName));
        var ex = Assert.Throws<AflException>(() => RunStates.Parse(["stuck"]));
        Assert.Equal(AflErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("needs-you, running", ex.Message);
    }

    [Fact]
    public void Matches_NeedsEverySearchWordAStateAndEveryTag()
    {
        var run = this.CreateRun();
        run = this.Engine.Rename(run.Id, "Billing export");
        run = this.Engine.SetTags(run.Id, ["q4", "urgent"]);
        run = this.Engine.MoveToFolder(run.Id, "finance/reports");

        Assert.True(new RunFilter().IsEmpty);
        Assert.True(new RunFilter { Search = "  " }.IsEmpty);
        Assert.True(new RunFilter { Search = "billing #Q4 reports" }.Matches(run));
        Assert.True(new RunFilter { Search = run.Id[..4] }.Matches(run));
        Assert.True(new RunFilter { Search = "dark mode" }.Matches(run));
        Assert.True(new RunFilter { Search = RunStates.Info(run.State).Label.ToUpperInvariant() }.Matches(run));
        Assert.False(new RunFilter { Search = "billing payroll" }.Matches(run));

        Assert.True(new RunFilter { States = [run.State, RunState.Failed] }.Matches(run));
        Assert.False(new RunFilter { States = [RunState.Failed] }.Matches(run));
        Assert.True(new RunFilter { States = [RunState.Failed], Search = "billing" }.WithoutStates().Matches(run));

        Assert.True(new RunFilter { Tags = ["URGENT"] }.Matches(run));
        Assert.False(new RunFilter { Tags = ["urgent", "later"] }.Matches(run));
    }

    /// <summary>
    ///     Creates a run of a one-step workflow.
    /// </summary>
    /// <returns>The run.</returns>
    private Run CreateRun()
    {
        var step = (UserStepDefinition)StepHandlerRegistry.Instance.CreateDefinition("user");
        step.Id = "u";
        step.Name = "u";
        step.Advance = "approve";
        step.Instructions = "You do u";
        return this.Engine.Create(this.RegisterRepo(), Workflow(step), SampleTicket());
    }
}
