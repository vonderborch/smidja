using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Authoring;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     What the user asks the AI helper for, in the "New with AI…" and "Improve with AI…" dialog: a description and
///     the agent that drafts it.
/// </summary>
public class AuthorRequestDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AuthorRequestDataContext" /> class.
    /// </summary>
    /// <param name="noun">What one document is called, e.g. "skill".</param>
    /// <param name="baseId">The id of the document to improve, or null for a new one.</param>
    /// <param name="repoName">The repo the agent may read, or null for none.</param>
    /// <param name="agents">The agents that can draft it, the default first.</param>
    public AuthorRequestDataContext(string noun, string? baseId, string? repoName, IReadOnlyList<string> agents)
    {
        this.Noun = noun;
        this.BaseId = baseId;
        this.RepoName = repoName;
        this.Agents = agents;
        this.Agent = agents.FirstOrDefault();
    }

    /// <summary>
    ///     Gets what one document is called.
    /// </summary>
    public string Noun { get; }

    /// <summary>
    ///     Gets the id of the document to improve, or null for a new one.
    /// </summary>
    public string? BaseId { get; }

    /// <summary>
    ///     Gets the repo the agent may read, or null for none.
    /// </summary>
    public string? RepoName { get; }

    /// <summary>
    ///     Gets the dialog's title.
    /// </summary>
    public string Title => this.BaseId is null ? $"New {this.Noun} with AI" : $"Improve {this.BaseId} with AI";

    /// <summary>
    ///     Gets what the dialog explains: what the agent may do, and that nothing is saved until approved.
    /// </summary>
    public string Explanation =>
        (this.BaseId is null
            ? $"Describe the {this.Noun} you want. "
            : $"Describe what to change; leave it blank to let the agent tidy it up. A built-in stays as it is: the improved {this.Noun} is saved as a copy. ") +
        "The agent may search the web, asks before reading a new site, and may ask you questions. " +
        (this.RepoName is null ? string.Empty : $"It can read {this.RepoName} but not change it. ") +
        "Nothing is saved until you approve the draft.";

    /// <summary>
    ///     Gets the description's placeholder.
    /// </summary>
    public string Placeholder => this.BaseId is null
        ? $"e.g. a {this.Noun} for writing conventional commit messages"
        : "e.g. also cover breaking changes";

    /// <summary>
    ///     Gets or sets what the user wants.
    /// </summary>
    public string Description
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.CanStart));
        }
    } = string.Empty;

    /// <summary>
    ///     Gets the agents that can draft it.
    /// </summary>
    public IReadOnlyList<string> Agents { get; }

    /// <summary>
    ///     Gets or sets the agent that drafts it.
    /// </summary>
    public string? Agent
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the request can start: a new document needs a description.
    /// </summary>
    public bool CanStart => this.BaseId is not null || !string.IsNullOrWhiteSpace(this.Description);
}

/// <summary>
///     One AI draft in a Library page's "AI drafts" list.
/// </summary>
/// <param name="view">The draft as the service sees it.</param>
public class AuthoringRowDataContext(AuthoringView view)
{
    /// <summary>
    ///     Gets the draft as the service sees it.
    /// </summary>
    public AuthoringView View { get; } = view;

    /// <summary>
    ///     Gets the draft's id.
    /// </summary>
    public string Id => this.View.Session.Id;

    /// <summary>
    ///     Gets the draft's name: the document's once written, otherwise the first line of what was asked for.
    /// </summary>
    public string Name => this.View.Check.Name
                          ?? (this.View.Session.BaseId is { } baseId ? $"Improve {baseId}" : this.View.Session.Description.Split('\n')[0]);

    /// <summary>
    ///     Gets the line under the name: new or improving, and where the draft is.
    /// </summary>
    public string Meta => $"{(this.View.Session.BaseId is null ? "new" : "improve")} · {AuthoringSessionDataContext.StatusText(this.View)}";

    /// <summary>
    ///     Gets whether the draft is waiting on the user.
    /// </summary>
    public bool NeedsYou => this.View.NeedsUser;
}

/// <summary>
///     An AI draft opened beside a Library page's list: the agent's progress, its questions and permission requests,
///     and the review card that approves the draft into the library, sends it back with changes or discards it. Its
///     hidden run is driven in-process by the shell's <see cref="RunDriver" />.
/// </summary>
public class AuthoringSessionDataContext : AbstractReloadingDataContext
{
    /// <summary>
    ///     The properties that follow the draft's state.
    /// </summary>
    private static readonly string[] StateProperties =
    [
        nameof(View), nameof(Title), nameof(Status), nameof(Summary), nameof(Problems), nameof(HasProblems), nameof(DraftLine),
        nameof(DraftDescription), nameof(TagLine), nameof(Draft), nameof(HasDraftFile), nameof(Failure), nameof(IsDriving), nameof(Activity),
        nameof(ShowsPermissions), nameof(ShowsInputs), nameof(ShowsResume), nameof(ShowsReview), nameof(ShowsRetry), nameof(IsSaved),
        nameof(CanApprove), nameof(CanContinuePermit), nameof(CanStop), nameof(SavedLine),
    ];

    /// <summary>
    ///     The scopes a draft's always-allow rules can be saved to: it has no repo of its own.
    /// </summary>
    private static readonly IReadOnlyList<PermissionScope> PermissionScopes = [PermissionScope.Run, PermissionScope.Global];

    /// <summary>
    ///     How a draft's JSON is shown: indented, with text such as dashes left as it is.
    /// </summary>
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    ///     The step, status and round the questions were read for, so reloads keep the answers typed.
    /// </summary>
    private string? _inputsKey;

    /// <summary>
    ///     The step, status and round the permission requests were read for, so reloads keep the decisions made.
    /// </summary>
    private string? _permissionsKey;

    /// <summary>
    ///     The live run whose output is shown, while it's watched.
    /// </summary>
    private LiveRun? _watched;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AuthoringSessionDataContext" /> class.
    /// </summary>
    /// <param name="shell">The shell that owns the stores, the driver and the authoring service.</param>
    /// <param name="id">The draft's id.</param>
    public AuthoringSessionDataContext(ShellDataContext shell, string id)
    {
        this.Shell = shell;
        this.Id = id;
        this.View = shell.Authoring.View(id);
        this.Noun = AuthoringKindRegistry.Instance.Create(this.View.Session.Kind).Noun;
        this.SaveScopes = this.View.Session.RepoPath is null ? [WorkflowScope.Global] : [WorkflowScope.Global, WorkflowScope.Repo];
        this.SaveScope = this.SaveScopes[^1];
        this.ReloadOnChange(h => shell.Runs.Changed += h, h => shell.Runs.Changed -= h);
        this.ReloadOnChange(h => shell.Authoring.Sessions.Changed += h, h => shell.Authoring.Sessions.Changed -= h);
        EventHandler<LiveRun> finished = (_, live) =>
        {
            if (live.RunId == this.RunId)
            {
                this.Reload();
            }
        };
        this.OnDispose<EventHandler<LiveRun>>(h => shell.Driver.Finished += h, h => shell.Driver.Finished -= h, finished);
        this.Reload();
        this.WatchLive();
    }

    /// <summary>
    ///     Raised when the draft is closed, e.g. once it's discarded.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    ///     Gets the shell.
    /// </summary>
    public ShellDataContext Shell { get; }

    /// <summary>
    ///     Gets the draft's id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    ///     Gets what one document of the draft's kind is called.
    /// </summary>
    public string Noun { get; }

    /// <summary>
    ///     Gets the draft as the service saw it at the last reload.
    /// </summary>
    public AuthoringView View
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the draft's hidden run.
    /// </summary>
    public string RunId => this.View.Session.RunId;

    /// <summary>
    ///     Gets the panel's title: new or improving, and the kind.
    /// </summary>
    public string Title => this.View.Session.BaseId is { } baseId
        ? $"Improving {this.Noun} {baseId} with AI"
        : $"New {this.Noun} with AI";

    /// <summary>
    ///     Gets what the user asked for.
    /// </summary>
    public string Description => this.View.Session.Description;

    /// <summary>
    ///     Gets where the draft is, as shown.
    /// </summary>
    public string Status => this.IsDriving ? $"{this.Activity}…" : StatusText(this.View);

    /// <summary>
    ///     Gets the agent and what it may read.
    /// </summary>
    public string AgentLine => $"Agent {this.View.Session.Agent}" +
                               (this.View.Session.RepoId is { } repo ? $" · reads {repo}" : string.Empty) + $" · run {this.RunId}";

    /// <summary>
    ///     Gets what the driver is doing with the run, or null when it's idle.
    /// </summary>
    public string? Activity => this.Live?.Activity;

    /// <summary>
    ///     Gets whether the driver is working on the run.
    /// </summary>
    public bool IsDriving => this.Live?.IsActive == true;

    /// <summary>
    ///     Gets whether the work can be stopped.
    /// </summary>
    public bool CanStop => this.IsDriving;

    /// <summary>
    ///     Gets the run's live output in this app, empty when it hasn't been driven here.
    /// </summary>
    public string Output => this.Live?.Output ?? string.Empty;

    /// <summary>
    ///     Gets the agent's summary of the draft.
    /// </summary>
    public string? Summary => this.View.Step?.Output;

    /// <summary>
    ///     Gets why the agent's work failed, if it did.
    /// </summary>
    public string? Failure => this.View.Status == AuthoringStatus.Failed ? this.View.Step?.Error ?? "The agent's work failed." : null;

    /// <summary>
    ///     Gets the draft's id, name and detail, e.g. "commit-messages — Commit messages (2 files)".
    /// </summary>
    public string? DraftLine => this.View.Check.Id is null && this.View.Check.Name is null
        ? null
        : $"{this.View.Check.Id} — {this.View.Check.Name}{(this.View.Check.Detail is { } detail ? $" ({detail})" : string.Empty)}";

    /// <summary>
    ///     Gets the draft's description.
    /// </summary>
    public string? DraftDescription => this.View.Check.Description;

    /// <summary>
    ///     Gets the draft's tags as a line, or null when it has none.
    /// </summary>
    public string? TagLine => this.View.Check.Tags.Count > 0 ? $"tags {string.Join(", ", this.View.Check.Tags)}" : null;

    /// <summary>
    ///     Gets what the draft check found wrong; Approve needs none.
    /// </summary>
    public IReadOnlyList<string> Problems => this.View.Check.Problems;

    /// <summary>
    ///     Gets whether the draft check found anything wrong.
    /// </summary>
    public bool HasProblems => this.Problems.Count > 0;

    /// <summary>
    ///     Gets the draft's JSON, or null when nothing has been written yet.
    /// </summary>
    public string? Draft
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the agent has written a draft.
    /// </summary>
    public bool HasDraftFile => this.Draft is not null;

    /// <summary>
    ///     Gets the questions the agent is asking now.
    /// </summary>
    public ObservableCollection<StepInputDataContext> Inputs { get; } = [];

    /// <summary>
    ///     Gets the tool calls the agent was refused and is waiting on.
    /// </summary>
    public ObservableCollection<PermissionRequestDataContext> PermissionRequests { get; } = [];

    /// <summary>
    ///     Gets whether the agent is waiting on permission decisions.
    /// </summary>
    public bool ShowsPermissions => !this.IsDriving && this.View.Status == AuthoringStatus.NeedsYou && this.PermissionRequests.Count > 0;

    /// <summary>
    ///     Gets whether every permission request has a decision, so the agent can carry on.
    /// </summary>
    public bool CanContinuePermit => this.ShowsPermissions && this.PermissionRequests.All(r => r.IsReady);

    /// <summary>
    ///     Gets whether the agent is waiting on answers to its questions.
    /// </summary>
    public bool ShowsInputs => !this.IsDriving && this.View.Status == AuthoringStatus.NeedsYou && !this.ShowsPermissions && this.Inputs.Count > 0;

    /// <summary>
    ///     Gets whether the run is idle without asking anything, e.g. after the app was closed mid-draft, so Continue
    ///     drives it on.
    /// </summary>
    public bool ShowsResume => !this.IsDriving && this.View.Status is AuthoringStatus.Working or AuthoringStatus.NeedsYou &&
                               !this.ShowsPermissions && !this.ShowsInputs;

    /// <summary>
    ///     Gets whether the draft is waiting for review.
    /// </summary>
    public bool ShowsReview => !this.IsDriving && this.View.Status == AuthoringStatus.Review;

    /// <summary>
    ///     Gets whether the draft can be approved: it's waiting for review and has no problems.
    /// </summary>
    public bool CanApprove => this.ShowsReview && this.View.Check.IsValid;

    /// <summary>
    ///     Gets whether the agent's work failed and can be tried again.
    /// </summary>
    public bool ShowsRetry => !this.IsDriving && this.View.Status == AuthoringStatus.Failed;

    /// <summary>
    ///     Gets whether the draft has been approved and saved.
    /// </summary>
    public bool IsSaved => this.View.Status == AuthoringStatus.Saved;

    /// <summary>
    ///     Gets where the approved document was saved.
    /// </summary>
    public string? SavedLine => this.View.Session.SavedPath is { } path
        ? $"Saved as {this.View.Session.SavedId} ({this.View.Session.SavedScope?.ToString().ToLowerInvariant()}) in {path}"
        : null;

    /// <summary>
    ///     Gets the scopes the draft can be saved to: global, and the repo when the draft has one.
    /// </summary>
    public IReadOnlyList<WorkflowScope> SaveScopes { get; }

    /// <summary>
    ///     Gets or sets the scope Approve saves to.
    /// </summary>
    public WorkflowScope SaveScope
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether Approve replaces a document of the same id already in that scope.
    /// </summary>
    public bool Overwrite
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets what to change, for Request changes.
    /// </summary>
    public string Feedback
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets the run as the driver is working on it, or null when it isn't driven here.
    /// </summary>
    private LiveRun? Live => this.Shell.Driver.Find(this.RunId);

    /// <summary>
    ///     Returns where a draft is, as shown.
    /// </summary>
    /// <param name="view">The draft.</param>
    /// <returns>The text.</returns>
    public static string StatusText(AuthoringView view)
    {
        return view.Status switch
        {
            AuthoringStatus.Working => "working",
            AuthoringStatus.NeedsYou => "needs you",
            AuthoringStatus.Review => view.Check.IsValid ? "ready for review" : "review (has problems)",
            AuthoringStatus.Failed => "failed",
            AuthoringStatus.Saved => "saved",
            _ => "ended",
        };
    }

    /// <summary>
    ///     Drives the run until it needs the user: starting a new draft, or one left idle.
    /// </summary>
    public void Resume()
    {
        this.Drive("Drafting", (service, id, progress, ct) => service.DriveAsync(id, progress, ct));
    }

    /// <summary>
    ///     Answers the agent's questions, marking each invalid answer.
    /// </summary>
    public void Answer()
    {
        var answers = new Dictionary<string, string>();
        var valid = true;
        foreach (var input in this.Inputs)
        {
            if (!input.TryResolve(out var answer))
            {
                valid = false;
            }
            else if (answer is not null)
            {
                answers[input.Input.Id] = answer;
            }
        }

        if (valid)
        {
            this.Drive("Answering", (service, id, progress, ct) => service.AnswerAsync(id, answers, progress, ct));
        }
    }

    /// <summary>
    ///     Gives the agent what was allowed, and lets it carry on.
    /// </summary>
    public void Permit()
    {
        var choices = this.PermissionRequests.Select(r => r.ToChoice()).ToList();
        this.Drive("Permitting", (service, id, progress, ct) => service.PermitAsync(id, choices, false, progress, ct));
    }

    /// <summary>
    ///     Takes the agent's work as it is, allowing nothing.
    /// </summary>
    public void AcceptOutput()
    {
        this.Drive("Accepting", (service, id, progress, ct) => service.PermitAsync(id, [], true, progress, ct));
    }

    /// <summary>
    ///     Sends the draft back to the agent with <see cref="Feedback" />.
    /// </summary>
    public void RequestChanges()
    {
        var feedback = this.Feedback.Trim();
        if (feedback.Length == 0)
        {
            this.ErrorMessage = "Say what to change.";
            return;
        }

        if (this.Drive("Revising", (service, id, progress, ct) => service.RequestChangesAsync(id, feedback, progress, ct)))
        {
            this.Feedback = string.Empty;
        }
    }

    /// <summary>
    ///     Approves the draft into <see cref="SaveScope" />.
    /// </summary>
    public void Approve()
    {
        var scope = this.SaveScope;
        var overwrite = this.Overwrite;
        this.Drive("Saving", (service, id, progress, ct) => service.ApproveAsync(id, scope, overwrite, progress, ct));
    }

    /// <summary>
    ///     Tries the agent's work again after it failed.
    /// </summary>
    public void Retry()
    {
        this.Drive("Retrying", (service, id, progress, ct) => service.RetryAsync(id, progress, ct));
    }

    /// <summary>
    ///     Stops the agent's work; the draft can be continued later.
    /// </summary>
    /// <returns>A task that completes once the work has stopped.</returns>
    public Task StopAsync()
    {
        return this.Shell.Driver.StopAsync(this.RunId);
    }

    /// <summary>
    ///     Discards the draft: stops its work, and deletes its run and folder. A document already saved stays.
    /// </summary>
    /// <returns>A task whose result is true if it succeeded.</returns>
    public async Task<bool> DiscardAsync()
    {
        await this.StopAsync();
        if (!this.Try(() => this.Shell.Authoring.Discard(this.Id)))
        {
            return false;
        }

        this.Close();
        return true;
    }

    /// <summary>
    ///     Closes the draft; its work carries on.
    /// </summary>
    public void Close()
    {
        this.Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void Load()
    {
        this.View = this.Shell.Authoring.View(this.Id);
        this.Draft = Indented(this.Shell.Authoring.ReadDraft(this.Id));
        this.RefreshInputs();
        this.RefreshPermissions();
        this.RaiseStateChanged();
    }

    /// <summary>
    ///     Returns a draft's JSON indented for reading, or as it is when it doesn't parse.
    /// </summary>
    /// <param name="json">The draft's JSON, or null.</param>
    /// <returns>The JSON to show.</returns>
    private static string? Indented(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            return JsonNode.Parse(json)?.ToJsonString(DisplayOptions) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <inheritdoc />
    protected override void OnDisposed()
    {
        this.Watch(null);
        foreach (var request in this.PermissionRequests)
        {
            request.PropertyChanged -= this.OnPermissionRequestChanged;
        }
    }

    /// <summary>
    ///     Starts a service call on the draft through the driver, so its progress shows here.
    /// </summary>
    /// <param name="activity">What the call does.</param>
    /// <param name="work">The call, given the service and the draft's id.</param>
    /// <returns>True if it started.</returns>
    private bool Drive(string activity, Func<AuthoringService, string, IProgress<ExecutorEvent>, CancellationToken, Task<AuthoringView>> work)
    {
        var service = this.Shell.Authoring;
        var id = this.Id;
        var runId = this.RunId;
        var started = this.Try(() => this.Shell.Driver.Start(runId, activity, async (engine, progress, ct) =>
        {
            await work(service, id, progress, ct).ConfigureAwait(false);
            return engine.Store.GetRequired(runId);
        }));
        this.WatchLive();
        this.RaiseStateChanged();
        return started;
    }

    /// <summary>
    ///     Follows the driver's live run, if it has one, so its output and activity show.
    /// </summary>
    private void WatchLive()
    {
        this.Watch(this.Live);
    }

    /// <summary>
    ///     Follows a live run instead of the one watched before.
    /// </summary>
    /// <param name="live">The live run, or null to stop watching.</param>
    private void Watch(LiveRun? live)
    {
        if (ReferenceEquals(live, this._watched))
        {
            return;
        }

        if (this._watched is not null)
        {
            this._watched.PropertyChanged -= this.OnLiveChanged;
        }

        this._watched = live;
        if (live is not null)
        {
            live.PropertyChanged += this.OnLiveChanged;
        }

        this.RaisePropertyChanged(nameof(this.Output));
    }

    /// <summary>
    ///     Shows the live run's new output or activity.
    /// </summary>
    /// <param name="sender">The live run.</param>
    /// <param name="e">The change.</param>
    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveRun.Output))
        {
            this.RaisePropertyChanged(nameof(this.Output));
        }
        else if (e.PropertyName == nameof(LiveRun.Activity))
        {
            this.RaiseStateChanged();
        }
    }

    /// <summary>
    ///     Rebuilds the questions when the run moves to another step, state or round, keeping the answers typed otherwise.
    /// </summary>
    private void RefreshInputs()
    {
        var run = this.View.Run;
        var state = this.View.Step;
        var key = run is null || state is null ? null : $"{state.Status}/{state.AskRounds.Count}";
        if (key == this._inputsKey)
        {
            return;
        }

        this._inputsKey = key;
        this.Inputs.Clear();
        if (run is null || state is null || this.View.Status != AuthoringStatus.NeedsYou)
        {
            return;
        }

        var definition = run.DefinitionOf(state);
        if (!StepHandlerRegistry.Instance.Contains(definition.Type))
        {
            return;
        }

        foreach (var input in StepHandlerRegistry.Instance.For(definition).InputsAskedNow(run, state))
        {
            if (StepInputTypeRegistry.Instance.Contains(input.Type))
            {
                this.Inputs.Add(new StepInputDataContext(input));
            }
        }
    }

    /// <summary>
    ///     Reads the permission requests again when the run moves to another state or round, keeping the decisions
    ///     made otherwise.
    /// </summary>
    private void RefreshPermissions()
    {
        var state = this.View.Step;
        var key = state is null ? null : $"{state.Status}/{state.PermissionRounds}";
        if (key == this._permissionsKey)
        {
            return;
        }

        this._permissionsKey = key;
        foreach (var request in this.PermissionRequests)
        {
            request.PropertyChanged -= this.OnPermissionRequestChanged;
        }

        this.PermissionRequests.Clear();
        foreach (var request in state?.OpenPermissionRequests ?? [])
        {
            var row = new PermissionRequestDataContext(request, PermissionScopes);
            row.PropertyChanged += this.OnPermissionRequestChanged;
            this.PermissionRequests.Add(row);
        }
    }

    /// <summary>
    ///     Updates whether the agent can carry on when a permission decision changes.
    /// </summary>
    /// <param name="sender">The request.</param>
    /// <param name="e">The change.</param>
    private void OnPermissionRequestChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PermissionRequestDataContext.IsReady))
        {
            this.RaisePropertyChanged(nameof(this.CanContinuePermit));
        }
    }

    /// <summary>
    ///     Raises the properties that follow the draft's state.
    /// </summary>
    private void RaiseStateChanged()
    {
        foreach (var name in StateProperties)
        {
            this.RaisePropertyChanged(name);
        }
    }
}
