using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Settings;

namespace Afl.Core.Notifications;

/// <summary>
///     Where a run stood before some work on it: enough to tell whether, afterwards, it newly needs the user, rather
///     than still waiting on the same thing.
/// </summary>
/// <param name="State">The run's state.</param>
/// <param name="StepId">The current step, or null when the run is done.</param>
/// <param name="StepStatus">The current step's status.</param>
/// <param name="Rounds">How many times the step has gone round: attempts, asks and permission rounds.</param>
public record RunAttention(RunState State, string? StepId, StepStatus? StepStatus, int Rounds)
{
    /// <summary>
    ///     Gets where a run stands.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>Where it stands.</returns>
    public static RunAttention Of(Run run)
    {
        var step = run.CurrentStep;
        return new RunAttention(RunStates.Of(run), step?.StepId, step?.Status,
            step is null ? 0 : step.History.Count + step.AskRounds.Count + step.PermissionRounds);
    }
}

/// <summary>
///     Tells the user, through the machine's desktop notifications, when a run newly needs them or a run they started
///     finishes or fails. Which states notify is <see cref="AflSettings.NotifyOn" />. <see cref="RunEngine.Notifier" />
///     calls it after each piece of work; front-ends set it, tests leave it unset (or pass their own notifier).
/// </summary>
/// <param name="notifier">Shows the notifications; null to pick the best one on this machine when first needed.</param>
/// <param name="settings">The settings, read on each notice so a change applies at once.</param>
public class RunNotifier(AbstractNotifier? notifier, SettingsStore settings)
{
    /// <summary>
    ///     The longest a notification may take to show before it is given up on.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The longest a failure's error may be in a notification.
    /// </summary>
    private const int MaxErrorLength = 160;

    /// <summary>
    ///     What the user is asked to do, by the status of the step waiting on them.
    /// </summary>
    private static readonly IReadOnlyDictionary<StepStatus, string> AskOfStatus = new Dictionary<StepStatus, string>
    {
        [StepStatus.AwaitingApproval] = "approve or reject the output",
        [StepStatus.AwaitingInput] = "answer its questions",
        [StepStatus.AwaitingPermission] = "allow or deny what the agent was refused",
        [StepStatus.AwaitingChoice] = "choose an agent's result",
        [StepStatus.AwaitingUser] = "do the step and mark it done",
        [StepStatus.ChoosingAgent] = "pick the agent",
        [StepStatus.AwaitingSession] = "do the step and submit its output",
    };

    /// <summary>
    ///     The notifier, found when first needed.
    /// </summary>
    private readonly Lazy<AbstractNotifier?> _notifier = new(() => notifier ?? NotifierRegistry.Instance.ForThisMachine());

    /// <summary>
    ///     Gets the notifier in use, or null when none works on this machine.
    /// </summary>
    public AbstractNotifier? Notifier => this._notifier.Value;

    /// <summary>
    ///     Creates the notifier for this machine, for front-ends.
    /// </summary>
    /// <param name="paths">The afl home.</param>
    /// <returns>The run notifier.</returns>
    public static RunNotifier ForThisMachine(AflPaths paths)
    {
        return new RunNotifier(null, new SettingsStore(paths));
    }

    /// <summary>
    ///     Works out the notification for a run after some work, if one is due: its state changed (or it waits on
    ///     something new), the state is one the settings notify on, and it either needs the user or is a run they
    ///     started (not a child or path run, whose parent tells them).
    /// </summary>
    /// <param name="before">Where the run stood before the work.</param>
    /// <param name="run">The run now.</param>
    /// <param name="settings">The settings.</param>
    /// <returns>The notification, or null for none.</returns>
    public static Notification? For(RunAttention before, Run run, AflSettings settings)
    {
        var after = RunAttention.Of(run);
        if (after == before || !settings.NotifyOn.Contains(after.State))
        {
            return null;
        }

        if (after.State != RunState.NeedsYou && run.ParentRunId is not null)
        {
            return null;
        }

        var title = $"afl: {RunStates.Info(after.State).Label.ToLowerInvariant()}";
        var step = run.CurrentStep;
        var message = after.State switch
        {
            RunState.NeedsYou when step is not null =>
                $"{run.DisplayTitle}: {StepName(step)}, {AskOfStatus.GetValueOrDefault(step.Status, "it's waiting on you")}.",
            RunState.Failed when step is not null =>
                $"{run.DisplayTitle}: {StepName(step)} failed{ErrorOf(step)}",
            _ => $"{run.DisplayTitle} is {RunStates.Info(after.State).Label.ToLowerInvariant()}.",
        };
        return new Notification(title, message, run.Id);
    }

    /// <summary>
    ///     Sends the notification for a run after some work, if one is due (see <see cref="For" />), and calls
    ///     <see cref="Alert" /> with it. It never fails the work: a notification that can't be shown, or an alert that
    ///     fails, is reported as a status event.
    /// </summary>
    /// <param name="before">Where the run stood before the work.</param>
    /// <param name="run">The run now.</param>
    /// <param name="progress">Receives the status when a notification can't be shown, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task NoticeAsync(RunAttention before, Run run, IProgress<ExecutorEvent>? progress, CancellationToken ct)
    {
        Notification? notification;
        try
        {
            notification = For(before, run, settings.Load());
        }
        catch (Exception ex)
        {
            progress?.Report(new ExecutorEvent(ExecutorEventKind.Status, $"Couldn't show a notification: {ex.Message}"));
            return;
        }

        if (notification is null)
        {
            return;
        }

        try
        {
            this.Alert(notification, RunStates.Of(run));
        }
        catch (Exception ex)
        {
            progress?.Report(new ExecutorEvent(ExecutorEventKind.Status, $"Couldn't ask for your attention: {ex.Message}"));
        }

        try
        {
            if (this.Notifier is { } shower)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(SendTimeout);
                await shower.SendAsync(notification, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            progress?.Report(new ExecutorEvent(ExecutorEventKind.Status, $"Couldn't show a notification: {ex.Message}"));
        }
    }

    /// <summary>
    ///     Called with each notification due, before it is shown and whether or not a notifier works here, so a
    ///     front-end can draw the user's eye in its own way too, e.g. by flashing its taskbar button. It must not block:
    ///     it runs on the engine's thread. Does nothing by default.
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <param name="state">The run's state, e.g. <see cref="RunState.NeedsYou" />, which wants a stronger alert.</param>
    protected virtual void Alert(Notification notification, RunState state)
    {
    }

    /// <summary>
    ///     Gets a step's name for a notification.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>Its name, or its id when it has none.</returns>
    private static string StepName(RunStep step)
    {
        return string.IsNullOrWhiteSpace(step.Name) ? step.StepId : step.Name;
    }

    /// <summary>
    ///     Gets the first line of a failed step's error, shortened, for a notification.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>": " and the error, or "." when there's none.</returns>
    private static string ErrorOf(RunStep step)
    {
        var line = step.Error?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            return ".";
        }

        return ": " + (line.Length > MaxErrorLength ? line[..(MaxErrorLength - 1)] + "…" : line);
    }
}
