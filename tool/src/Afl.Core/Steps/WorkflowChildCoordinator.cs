using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     Drives a <c>workflow</c> step's child runs: together with <see cref="WorkflowStepDefinition.ChildrenTogether" /> (unless the run
///     is <see cref="Run.Sequential" />), each in the step's <see cref="AbstractStepDefinition.Workspace" />, with the output of
///     its <see cref="WorkflowStepDefinition.ChildOutput" /> step.
/// </summary>
public class WorkflowChildCoordinator : AbstractChildRunCoordinator
{
    /// <summary>
    ///     Gets the shared coordinator.
    /// </summary>
    public static WorkflowChildCoordinator Instance { get; } = new();

    /// <inheritdoc />
    public override List<ChildRun> ChildrenOf(StepContext context)
    {
        return context.State.ChildRuns;
    }

    /// <inheritdoc />
    protected override bool DrivesTogether(StepContext context)
    {
        return context.Definition is WorkflowStepDefinition { ChildrenTogether: true } && !context.Run.Sequential;
    }

    /// <inheritdoc />
    protected override string? OutputStepOf(StepContext context)
    {
        return (context.Definition as WorkflowStepDefinition)?.ChildOutput;
    }
}
