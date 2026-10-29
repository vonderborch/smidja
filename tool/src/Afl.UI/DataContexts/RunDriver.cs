using System.Text;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Avalonia.Threading;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     A run the desktop app is working on, or has worked on this session: the output log of everything the engine
///     reported, and the in-flight engine call, if any.
/// </summary>
/// <param name="runId">The run id.</param>
public class LiveRun(string runId) : ReactiveObject
{
    /// <summary>
    ///     The most output kept, so a long session doesn't grow without limit.
    /// </summary>
    private const int MaxOutputLength = 200_000;

    /// <summary>
    ///     The output log.
    /// </summary>
    private readonly StringBuilder _output = new();

    /// <summary>
    ///     Cancels the in-flight engine call.
    /// </summary>
    private CancellationTokenSource? _cancellation;

    /// <summary>
    ///     The in-flight engine call itself, without the UI-thread bookkeeping that follows it.
    /// </summary>
    private Task _work = Task.CompletedTask;

    /// <summary>
    ///     Gets the run id.
    /// </summary>
    public string RunId { get; } = runId;

    /// <summary>
    ///     Gets the output log.
    /// </summary>
    public string Output => this._output.ToString();

    /// <summary>
    ///     Gets what the engine is doing, e.g. "Approve", while a call is in flight.
    /// </summary>
    public string? Activity
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsActive));
        }
    }

    /// <summary>
    ///     Gets whether an engine call is in flight.
    /// </summary>
    public bool IsActive => this.Activity is not null;

    /// <summary>
    ///     Gets the in-flight call, or a completed task.
    /// </summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>
    ///     Starts an engine call. Progress is reported on the UI thread.
    /// </summary>
    /// <param name="activity">What the call does.</param>
    /// <param name="work">The call.</param>
    /// <param name="finished">Called on the UI thread when it ends.</param>
    internal void Start(string activity, Func<IProgress<ExecutorEvent>, CancellationToken, Task<Run>> work, Action<LiveRun> finished)
    {
        this._cancellation = new CancellationTokenSource();
        this.Activity = activity;
        this.Append($"── {activity} · {DateTime.Now:HH:mm:ss} ──");

        // Created on the UI thread, so Report posts back to it.
        var progress = new Progress<ExecutorEvent>(this.Append);
        var token = this._cancellation.Token;
        this._work = Task.Run(() => work(progress, token), token);
        this.Completion = this._work.ContinueWith(task =>
        {
            if (task.IsCanceled || task.Exception?.InnerException is OperationCanceledException)
            {
                this.Append("■ Stopped.");
            }
            else if (task.Exception?.InnerException is { } error)
            {
                this.Append("✗ " + (error is AflException ? error.Message : error.ToString()));
            }

            this._cancellation?.Dispose();
            this._cancellation = null;
            this.Activity = null;
            finished(this);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    ///     Asks the in-flight call to stop and waits for it.
    /// </summary>
    /// <returns>A task that completes when the call has ended.</returns>
    internal Task StopAsync()
    {
        this._cancellation?.Cancel();
        return this.Completion;
    }

    /// <summary>
    ///     Asks the in-flight call to stop and blocks until the engine has recorded the interruption. Safe to call on the
    ///     UI thread because it doesn't wait for the UI-thread bookkeeping.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    internal void Stop(TimeSpan timeout)
    {
        this._cancellation?.Cancel();
        try
        {
            this._work.Wait(timeout);
        }
        catch (AggregateException)
        {
            // The failure is already in the run.
        }
    }

    /// <summary>
    ///     Adds an engine event to the log.
    /// </summary>
    /// <param name="value">The event.</param>
    private void Append(ExecutorEvent value)
    {
        this.Append(value.Kind switch
        {
            ExecutorEventKind.ToolUse => "→ " + value.Text,
            ExecutorEventKind.Status => "· " + value.Text,
            ExecutorEventKind.Error => "✗ " + value.Text,
            _ => value.Text,
        });
    }

    /// <summary>
    ///     Adds a line to the log.
    /// </summary>
    /// <param name="line">The line.</param>
    private void Append(string line)
    {
        this._output.AppendLine(line);
        if (this._output.Length > MaxOutputLength)
        {
            this._output.Remove(0, this._output.Length - MaxOutputLength);
        }

        this.RaisePropertyChanged(nameof(this.Output));
    }
}

/// <summary>
///     Runs the engine in-process on background threads for the desktop app, one call per run at a time, and keeps
///     each run's live output for the session so it survives page navigation. The run's own file lock still stops the
///     CLI and the app acting on the same run at once.
/// </summary>
/// <param name="engine">The engine.</param>
public class RunDriver(RunEngine engine)
{
    /// <summary>
    ///     The runs worked on this session, by id.
    /// </summary>
    private readonly Dictionary<string, LiveRun> _runs = [];

    /// <summary>
    ///     Raised on the UI thread when an engine call ends, successfully or not.
    /// </summary>
    public event EventHandler<LiveRun>? Finished;

    /// <summary>
    ///     Gets the engine.
    /// </summary>
    public RunEngine Engine { get; } = engine;

    /// <summary>
    ///     Returns the live state of a run, or null if the app hasn't worked on it this session.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <returns>The live run, or null.</returns>
    public LiveRun? Find(string runId)
    {
        return this._runs.GetValueOrDefault(runId);
    }

    /// <summary>
    ///     Starts an engine call on a background thread. Must be called on the UI thread.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <param name="activity">What the call does, for the log.</param>
    /// <param name="work">The call.</param>
    /// <returns>The live run.</returns>
    /// <exception cref="AflException">The app is already working on the run.</exception>
    public LiveRun Start(string runId, string activity, Func<RunEngine, IProgress<ExecutorEvent>, CancellationToken, Task<Run>> work)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!this._runs.TryGetValue(runId, out var live))
        {
            live = new LiveRun(runId);
            this._runs[runId] = live;
        }

        if (live.IsActive)
        {
            throw new AflException(AflErrorKind.Locked, $"Run {runId} is busy: {live.Activity}. Wait for it or stop it first.");
        }

        live.Start(activity, (progress, ct) => work(this.Engine, progress, ct), finished => this.Finished?.Invoke(this, finished));
        return live;
    }

    /// <summary>
    ///     Stops the app's in-flight call on a run, if any, and waits for it to end.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <returns>A task that completes when the call has ended.</returns>
    public Task StopAsync(string runId)
    {
        return this.Find(runId)?.StopAsync() ?? Task.CompletedTask;
    }

    /// <summary>
    ///     Stops every in-flight call, e.g. when the app closes. The interrupted steps are marked failed so they can be
    ///     retried.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    public void StopAll(TimeSpan timeout)
    {
        foreach (var live in this._runs.Values.Where(r => r.IsActive))
        {
            live.Stop(timeout);
        }
    }
}
