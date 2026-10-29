using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Notifications;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the desktop notifications runs send when they need the user or finish.
/// </summary>
public class NotificationTests : AbstractAflTest
{
    private readonly FakeStepExecutor _executor = new();

    private readonly RecordingNotifier _notifier = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor)
    {
        Notifier = new RunNotifier(this._notifier, new SettingsStore(this.Paths)),
    };

    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    private static AiStepDefinition Approve(string id, string prompt) =>
        new() { Id = id, Name = id, Advance = "approve", Prompt = prompt };

    private Run Start(WorkflowDefinition workflow)
    {
        return this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket());
    }

    [Fact]
    public async Task ARunThatStopsForApproval_NotifiesThatItNeedsYou()
    {
        var run = this.Start(Workflow(Ai("read", "Read"), Approve("plan", "Plan")));

        run = await this.Engine.DriveAsync(run.Id);

        var notification = Assert.Single(this._notifier.Sent);
        Assert.Equal("afl: needs you", notification.Title);
        Assert.Equal("Add dark mode: plan, approve or reject the output.", notification.Message);
        Assert.Equal(run.Id, notification.Group);
    }

    [Fact]
    public async Task DrivingARunStillWaitingOnTheSameThing_DoesNotNotifyAgain()
    {
        var run = this.Start(Workflow(Approve("plan", "Plan")));
        await this.Engine.DriveAsync(run.Id);

        await this.Engine.DriveAsync(run.Id);

        Assert.Single(this._notifier.Sent);
    }

    [Fact]
    public async Task SendingAStepBack_NotifiesAgainWhenItWaitsOnceMore()
    {
        var run = this.Start(Workflow(Approve("plan", "Plan"), Ai("impl", "Do it")));
        await this.Engine.DriveAsync(run.Id);

        await this.Engine.RejectAsync(run.Id, "Also the settings page.");
        run = await this.Engine.ApproveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(["afl: needs you", "afl: needs you", "afl: done"], this._notifier.Sent.Select(n => n.Title));
        Assert.Equal("Add dark mode is done.", this._notifier.Sent[^1].Message);
    }

    [Fact]
    public async Task AFailedStep_NotifiesWithItsError()
    {
        this._executor.Respond = r => new ExecutionResult(ExecutionStatus.Failed, null, r.SessionId, "boom\nmore detail");
        var run = this.Start(Workflow(Ai("impl", "Do it")));

        await this.Engine.DriveAsync(run.Id);

        var notification = Assert.Single(this._notifier.Sent);
        Assert.Equal("afl: failed", notification.Title);
        Assert.Equal("Add dark mode: impl failed: boom", notification.Message);
    }

    [Fact]
    public async Task OnlyTheStatesInTheSettingsNotify()
    {
        var settings = new SettingsStore(this.Paths);
        var saved = settings.Load();
        saved.NotifyOn = [RunState.Done];
        settings.Save(saved);
        var run = this.Start(Workflow(Approve("plan", "Plan")));

        await this.Engine.DriveAsync(run.Id);
        Assert.Empty(this._notifier.Sent);

        await this.Engine.ApproveAsync(run.Id);
        Assert.Equal("afl: done", Assert.Single(this._notifier.Sent).Title);

        Assert.Equal([RunState.Done], new SettingsStore(this.Paths).Load().NotifyOn);
        Assert.Equal(AflSettings.DefaultNotifyOn, new AflSettings().NotifyOn);
    }

    [Fact]
    public void ChildRuns_NotifyOnlyWhenTheyNeedYou()
    {
        var run = this.Start(Workflow(Approve("plan", "Plan")));
        var before = RunAttention.Of(run);
        run.ParentRunId = "parent";
        var settings = new AflSettings();

        run.Status = RunStatus.Done;
        run.CurrentStepId = null;
        Assert.Null(RunNotifier.For(before, run, settings));

        run.Status = RunStatus.Waiting;
        run.CurrentStepId = "plan";
        run.Steps[0].Status = StepStatus.AwaitingApproval;
        Assert.Equal("afl: needs you", RunNotifier.For(before, run, settings)?.Title);
    }

    [Fact]
    public async Task ANotificationThatCantBeShown_DoesNotFailTheRun()
    {
        this._notifier.Failure = new AflException(AflErrorKind.ExternalTool, "no notification centre");
        var events = new List<ExecutorEvent>();
        var run = this.Start(Workflow(Approve("plan", "Plan")));

        run = await this.Engine.DriveAsync(run.Id, new SyncProgress(events.Add));

        Assert.Equal(StepStatus.AwaitingApproval, run.CurrentStep!.Status);
        Assert.Contains(events, e => e.Kind == ExecutorEventKind.Status && e.Text.Contains("no notification centre"));
    }

    [Fact]
    public async Task AnEngineWithoutANotifier_SendsNothing()
    {
        var engine = new RunEngine(new RunStore(this.Paths), this._executor);
        var run = engine.Create(this.RegisterRepo(), Workflow(Approve("plan", "Plan")), SampleTicket());

        await engine.DriveAsync(run.Id);

        Assert.Null(engine.Notifier);
        Assert.Empty(this._notifier.Sent);
    }

    [Fact]
    public async Task EachNotificationDue_IsAlsoPassedToTheAlertHook_EvenWithoutANotifier()
    {
        var alerting = new AlertingRunNotifier(new SettingsStore(this.Paths));
        var engine = new RunEngine(new RunStore(this.Paths), this._executor) { Notifier = alerting };
        var run = engine.Create(this.RegisterRepo(), Workflow(Approve("plan", "Plan")), SampleTicket());

        await engine.DriveAsync(run.Id);
        await engine.DriveAsync(run.Id);
        await engine.ApproveAsync(run.Id);

        Assert.Equal([("afl: needs you", RunState.NeedsYou), ("afl: done", RunState.Done)], alerting.Alerts);
    }

    /// <summary>
    ///     A run notifier with a notifier that never works, keeping the alerts it is asked for.
    /// </summary>
    /// <param name="settings">The settings.</param>
    private sealed class AlertingRunNotifier(SettingsStore settings) : RunNotifier(new RecordingNotifier
    {
        Failure = new AflException(AflErrorKind.ExternalTool, "no notification centre"),
    }, settings)
    {
        /// <summary>
        ///     Gets the alerts asked for: the notification's title and the run's state.
        /// </summary>
        public List<(string Title, RunState State)> Alerts { get; } = [];

        /// <inheritdoc />
        protected override void Alert(Notification notification, RunState state)
        {
            this.Alerts.Add((notification.Title, state));
        }
    }

    /// <summary>
    ///     A notifier that keeps what it is asked to show.
    /// </summary>
    private sealed class RecordingNotifier() : AbstractNotifier("recording")
    {
        /// <summary>
        ///     Gets the notifications shown.
        /// </summary>
        public List<Notification> Sent { get; } = [];

        /// <summary>
        ///     Gets or sets the error to fail with instead, if any.
        /// </summary>
        public Exception? Failure { get; set; }

        /// <inheritdoc />
        public override string DisplayName => "Recording";

        /// <inheritdoc />
        public override bool IsAvailable()
        {
            return true;
        }

        /// <inheritdoc />
        public override Task SendAsync(Notification notification, CancellationToken ct)
        {
            if (this.Failure is not null)
            {
                throw this.Failure;
            }

            this.Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Reports progress straight away, on the reporting thread.
    /// </summary>
    /// <param name="report">Called for each report.</param>
    private sealed class SyncProgress(Action<ExecutorEvent> report) : IProgress<ExecutorEvent>
    {
        /// <inheritdoc />
        public void Report(ExecutorEvent value)
        {
            report(value);
        }
    }
}
