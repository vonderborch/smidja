namespace Afl.Core.Execution;

/// <summary>
///     Hands AI work to the Claude session that called afl (the <c>/afl</c> plugin skill). Nothing is executed here:
///     the step waits in <c>awaiting-session</c> with its rendered prompt, the calling session does the work, and then
///     submits the output with <c>afl run submit</c>.
/// </summary>
[ExecutorRegistration(ExecutorName)]
public class SessionExecutor() : AbstractStepExecutor(ExecutorName)
{
    /// <summary>
    ///     The executor name.
    /// </summary>
    public const string ExecutorName = "session";

    /// <inheritdoc />
    public override bool SupportsPermissions => true;

    /// <inheritdoc />
    public override bool SupportsResume => true;

    /// <inheritdoc />
    public override bool CanRunInteractive => true;

    /// <inheritdoc />
    public override bool OnlyAsMainAgent => true;

    /// <inheritdoc />
    public override Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        IProgress<ExecutorEvent>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ExecutorEvent(ExecutorEventKind.Status, "Waiting for the calling Claude session to do this step."));
        return Task.FromResult(new ExecutionResult(ExecutionStatus.AwaitingSession, null, request.SessionId));
    }
}
