using System.Collections.ObjectModel;
using Afl.Core.Authoring;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Afl.Core.Workflows;
using Avalonia.Threading;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     The AI helper's part of a Library page: the "AI drafts" list of this kind's drafts, starting one, the draft opened
///     beside the list, and saving a draft opened in the editor, which approves it.
/// </summary>
public partial class LibraryPageDataContext
{
    /// <summary>
    ///     Gets this kind's AI drafts not yet saved, newest first.
    /// </summary>
    public ObservableCollection<AuthoringRowDataContext> AiDrafts { get; } = [];

    /// <summary>
    ///     Gets whether there are any AI drafts.
    /// </summary>
    public bool HasAiDrafts => this.AiDrafts.Count > 0;

    /// <summary>
    ///     Gets the "AI drafts" header, with how many wait on the user.
    /// </summary>
    public string AiDraftsHeader
    {
        get
        {
            var waiting = this.AiDrafts.Count(d => d.NeedsYou);
            return waiting > 0 ? $"AI drafts ({this.AiDrafts.Count}, {waiting} need you)" : $"AI drafts ({this.AiDrafts.Count})";
        }
    }

    /// <summary>
    ///     Gets or sets the AI draft opened beside the list, if any; it shows in place of the editor.
    /// </summary>
    public AuthoringSessionDataContext? ActiveAuthoring
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertiesChanged(nameof(this.HasAuthoring), nameof(this.ShowsEditor), nameof(this.ShowsHint));
        }
    }

    /// <summary>
    ///     Gets whether an AI draft is open beside the list.
    /// </summary>
    public bool HasAuthoring => this.ActiveAuthoring is not null;

    /// <summary>
    ///     Gets whether the editor shows: a draft is open and no AI draft is shown in its place.
    /// </summary>
    public bool ShowsEditor => this.HasDraft && !this.HasAuthoring;

    /// <summary>
    ///     Gets whether the "select one" hint shows.
    /// </summary>
    public bool ShowsHint => !this.HasDraft && !this.HasAuthoring;

    /// <summary>
    ///     Gets the id of the AI draft the editor holds, so Save approves it; null for an ordinary draft.
    /// </summary>
    public string? AuthoringDraftId
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsEditingAiDraft));
        }
    }

    /// <summary>
    ///     Gets whether the editor holds an AI draft.
    /// </summary>
    public bool IsEditingAiDraft => this.AuthoringDraftId is not null;

    /// <summary>
    ///     Gets whether a document is selected for "Improve with AI…".
    /// </summary>
    public bool CanImprove => this.SelectedListing is not null;

    /// <summary>
    ///     Creates the request for a new document of this kind.
    /// </summary>
    /// <returns>The request.</returns>
    public AuthorRequestDataContext NewAuthorRequest()
    {
        return new AuthorRequestDataContext(this.Editor.Noun, null, this.Repo?.Name, this.AuthoringAgents());
    }

    /// <summary>
    ///     Creates the request to improve the selected document, or null when none is selected.
    /// </summary>
    /// <returns>The request, or null.</returns>
    public AuthorRequestDataContext? ImproveAuthorRequest()
    {
        return this.SelectedListing is { } listing
            ? new AuthorRequestDataContext(this.Editor.Noun, listing.Id, this.Repo?.Name, this.AuthoringAgents())
            : null;
    }

    /// <summary>
    ///     Starts an AI draft from a request, opens it and drives it until it needs the user.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True if it started.</returns>
    public bool StartAuthoring(AuthorRequestDataContext request)
    {
        AuthoringSession? session = null;
        if (!this.Try(() => session = this.Shell.Authoring.Create(this.Editor.AuthoringKind, request.Description.Trim(), this.Repo,
                request.BaseId, request.Agent)))
        {
            return false;
        }

        this.LoadAiDrafts();
        this.OpenAuthoring(session!.Id);
        this.ActiveAuthoring?.Resume();
        return true;
    }

    /// <summary>
    ///     Opens an AI draft beside the list, in place of the editor.
    /// </summary>
    /// <param name="id">The draft's id.</param>
    public void OpenAuthoring(string id)
    {
        if (this.ActiveAuthoring?.Id == id)
        {
            return;
        }

        AuthoringSessionDataContext? opened = null;
        if (!this.Try(() => opened = new AuthoringSessionDataContext(this.Shell, id)))
        {
            return;
        }

        this.CloseAuthoring();
        this.SelectListingQuietly(null);
        opened!.Closed += this.OnAuthoringClosed;
        this.ActiveAuthoring = opened;
    }

    /// <summary>
    ///     Closes the AI draft beside the list; its work carries on.
    /// </summary>
    public void CloseAuthoring()
    {
        if (this.ActiveAuthoring is not { } active)
        {
            return;
        }

        active.Closed -= this.OnAuthoringClosed;
        this.ActiveAuthoring = null;
        active.Dispose();
    }

    /// <summary>
    ///     Subscribes to what changes the AI drafts list.
    /// </summary>
    /// <param name="shell">The shell.</param>
    private void InitializeAuthoring(ShellDataContext shell)
    {
        this.AiDrafts.CollectionChanged += (_, _) => this.RaisePropertiesChanged(nameof(this.HasAiDrafts), nameof(this.AiDraftsHeader));
        this.OnDispose<EventHandler>(h => shell.Authoring.Sessions.Changed += h, h => shell.Authoring.Sessions.Changed -= h,
            (_, _) => Dispatcher.UIThread.Post(this.LoadAiDrafts));
        this.OnDispose<EventHandler>(h => shell.Runs.Changed += h, h => shell.Runs.Changed -= h,
            (_, _) => Dispatcher.UIThread.Post(this.LoadAiDrafts));
        this.OnDispose<EventHandler<LiveRun>>(h => shell.Driver.Finished += h, h => shell.Driver.Finished -= h, (_, live) =>
        {
            // A draft's work finishing may have saved it into the library, so the list is read again too.
            if (shell.Authoring.SessionOfRun(live.RunId) is not null)
            {
                Dispatcher.UIThread.Post(this.Reload);
            }
        });
    }

    /// <summary>
    ///     Reads this kind's AI drafts not yet saved.
    /// </summary>
    private void LoadAiDrafts()
    {
        IReadOnlyList<AuthoringView> views = [];
        if (!this.Try(() => views = this.Shell.Authoring.List(this.Editor.AuthoringKind)))
        {
            return;
        }

        this.AiDrafts.Clear();
        foreach (var view in views.Where(v => v.Status != AuthoringStatus.Saved).OrderByDescending(v => v.Session.CreatedAt))
        {
            this.AiDrafts.Add(new AuthoringRowDataContext(view));
        }
    }

    /// <summary>
    ///     Returns the agents a draft can use: the default first, without the calling-session agent.
    /// </summary>
    /// <returns>The agents' ids.</returns>
    private IReadOnlyList<string> AuthoringAgents()
    {
        IReadOnlyList<string> agents = [];
        this.Try(() => agents = this.Shell.Agents.All().Select(a => a.Id).ToList());
        var preferred = this.Shell.Settings.DefaultAgent();
        return agents.Where(a => a != SessionExecutor.ExecutorName).OrderByDescending(a => a == preferred).ToList();
    }

    /// <summary>
    ///     Opens the open AI draft's document in the editor, replacing the draft there, so Save approves it.
    /// </summary>
    public void OpenAuthoringInEditor()
    {
        if (this.ActiveAuthoring is not { } session)
        {
            return;
        }

        WorkflowDefinition? draft = null;
        if (!this.Try(() => draft = this.Editor.ReadExternal(this.Library(), session.View.DraftPath)))
        {
            return;
        }

        var scope = session.SaveScope;
        this.CloseAuthoring();
        this.SelectListingQuietly(null);
        this.Edit(draft!, scope);
        this.AuthoringDraftId = session.Id;
        this.IsDirty = true;
        this._history.MarkUnsaved();
    }

    /// <summary>
    ///     Closes the AI draft once it's been discarded.
    /// </summary>
    /// <param name="sender">The AI draft.</param>
    /// <param name="e">The event.</param>
    private void OnAuthoringClosed(object? sender, EventArgs e)
    {
        this.CloseAuthoring();
        this.LoadAiDrafts();
    }

    /// <summary>
    ///     Saves the AI draft the editor holds: writes it back to the draft and approves it to <see cref="SaveScope" />,
    ///     replacing a document of the same id.
    /// </summary>
    /// <returns>Where it's saved, or null if it failed.</returns>
    private string? SaveAiDraft()
    {
        this.CommitEdit();
        var id = this.AuthoringDraftId!;
        var scope = this.SaveScope;
        AuthoringDraftCheck? check = null;
        if (!this.Try(() => check = this.Shell.Authoring.UpdateDraft(id, this.Editor.Serialize(this.Library(), this.BuildDraft()))))
        {
            return null;
        }

        if (!check!.IsValid)
        {
            this.ErrorMessage = $"The draft has problems:\n{string.Join("\n", check.Problems)}";
            return null;
        }

        var path = this.SavePath;
        if (!this.StartApproval(id, scope))
        {
            return null;
        }

        this.AuthoringDraftId = null;
        this._history.MarkSaved();
        this.IsDirty = false;
        return path;
    }

    /// <summary>
    ///     Approves an AI draft through the driver, replacing a document of the same id; the list is read again once
    ///     it's saved.
    /// </summary>
    /// <param name="id">The draft's id.</param>
    /// <param name="scope">The scope to save to.</param>
    /// <returns>True if the approval started.</returns>
    private bool StartApproval(string id, WorkflowScope scope)
    {
        var service = this.Shell.Authoring;
        LiveRun? live = null;
        if (!this.Try(() =>
            {
                var runId = service.View(id).Session.RunId;
                live = this.Shell.Driver.Start(runId, "Saving", async (engine, progress, ct) =>
                {
                    await service.ApproveAsync(id, scope, true, progress, ct).ConfigureAwait(false);
                    return engine.Store.GetRequired(runId);
                });
            }))
        {
            return false;
        }

        return live is not null;
    }

    /// <summary>
    ///     Forgets the AI draft the editor held, when another draft replaces it or it's closed.
    /// </summary>
    private void ForgetAuthoringDraft()
    {
        this.AuthoringDraftId = null;
    }
}
