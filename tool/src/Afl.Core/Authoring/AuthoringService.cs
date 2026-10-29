using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using Niddy.Core.IO;

namespace Afl.Core.Authoring;

/// <summary>
///     The AI authoring helper: an agent drafts a skill, common step or workflow from what the user describes, asking
///     questions and researching as it's allowed, and the user reviews the draft before it's saved to a library. Each
///     draft is an <see cref="AuthoringSession" /> with its own folder and a hidden run
///     (<see cref="RunKind.Authoring" />) of a one-step workflow, so asking, permission pauses, sending back and approval
///     all come from the run engine, while the Runs page and <c>afl runs</c> never show it.
/// </summary>
/// <param name="engine">The run engine the drafts run on.</param>
public class AuthoringService(RunEngine engine)
{
    /// <summary>
    ///     The tools a drafting agent may use without asking: searching the web, reading, and writing inside its own
    ///     folder. Fetching a page isn't among them, so each new site asks the user first.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedTools = ["WebSearch", "Read", "Glob", "Grep", "Write(./**)", "Edit(./**)"];

    /// <summary>
    ///     Gets the run engine the drafts run on.
    /// </summary>
    public RunEngine Engine { get; } = engine;

    /// <summary>
    ///     Gets the authoring sessions.
    /// </summary>
    public AuthoringStore Sessions { get; } = new(engine.Store.Paths);

    /// <summary>
    ///     Starts drafting a document, without driving its run: front-ends drive it themselves, so they can show its
    ///     progress.
    /// </summary>
    /// <param name="kind">The kind of document (an <see cref="AuthoringKindRegistry" /> name).</param>
    /// <param name="description">What the user wants.</param>
    /// <param name="repo">A repo the agent may read and whose library the draft is checked against, or null.</param>
    /// <param name="baseId">The id of a document to improve, or null for a new one.</param>
    /// <param name="agent">The agent drafting it, or null for the default.</param>
    /// <returns>The session.</returns>
    /// <exception cref="AflException">The kind, agent or base document is unknown, or the description is empty.</exception>
    public AuthoringSession Create(string kind, string description, RepoRecord? repo = null, string? baseId = null, string? agent = null)
    {
        var authoringKind = AuthoringKindRegistry.Instance.Create(kind);
        if (string.IsNullOrWhiteSpace(description) && baseId is null)
        {
            throw new AflException(AflErrorKind.InvalidInput, $"Describe the {authoringKind.Noun} you want.");
        }

        var paths = this.Engine.Store.Paths;
        var existing = baseId is null ? null : authoringKind.ReadExisting(paths, repo?.Path, baseId);
        var session = new AuthoringSession
        {
            Id = this.Sessions.NewId(),
            Kind = authoringKind.Name,
            Description = description.Trim(),
            BaseId = existing?.Id,
            BaseScope = existing?.Scope,
            RepoId = repo?.Id,
            RepoPath = repo?.Path,
            Agent = agent ?? this.Engine.Settings.DefaultAgent(),
            CreatedAt = DateTimeOffset.Now,
        };

        var folder = this.Sessions.FolderFor(session.Id);
        try
        {
            Directory.CreateDirectory(folder);
            authoringKind.WriteReference(paths, session.RepoPath, Path.Combine(folder, AuthoringConstants.ReferenceFolderName));
            if (existing is not null)
            {
                AtomicFile.WriteAllText(this.Sessions.DraftPathFor(session.Id), existing.Json);
            }

            var workspace = new RepoRecord { Id = session.RunRepoId, Name = $"AI draft {session.Id}", Path = folder };
            var run = this.Engine.CreateAuthoring(workspace, this.WorkflowFor(authoringKind, session), this.TicketFor(authoringKind, session),
                session.Agent);
            session.RunId = run.Id;
            this.Sessions.Save(session);
            return session;
        }
        catch
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            throw;
        }
    }

    /// <summary>
    ///     Starts drafting a document and drives its run until it needs the user.
    /// </summary>
    /// <param name="kind">The kind of document.</param>
    /// <param name="description">What the user wants.</param>
    /// <param name="repo">A repo the agent may read, or null.</param>
    /// <param name="baseId">The id of a document to improve, or null for a new one.</param>
    /// <param name="agent">The agent drafting it, or null for the default.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the session is.</returns>
    public async Task<AuthoringView> StartAsync(
        string kind,
        string description,
        RepoRecord? repo = null,
        string? baseId = null,
        string? agent = null,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        var session = this.Create(kind, description, repo, baseId, agent);
        await this.Engine.DriveAsync(session.RunId, progress, ct).ConfigureAwait(false);
        return this.View(session.Id);
    }

    /// <summary>
    ///     Lists the sessions, newest first, as front-ends show them.
    /// </summary>
    /// <param name="kind">Only this kind of document, or null for every kind.</param>
    /// <returns>The sessions.</returns>
    public IReadOnlyList<AuthoringView> List(string? kind = null)
    {
        return this.Sessions.List(kind).Select(this.ViewOf).ToList();
    }

    /// <summary>
    ///     Shows a session: its run, where it is and what the draft check finds.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <returns>The view.</returns>
    /// <exception cref="AflException">There's no such session.</exception>
    public AuthoringView View(string id)
    {
        return this.ViewOf(this.Sessions.GetRequired(id));
    }

    /// <summary>
    ///     Finds the session a hidden run belongs to.
    /// </summary>
    /// <param name="runId">The run's id.</param>
    /// <returns>The session, or null when the run isn't an authoring run.</returns>
    public AuthoringSession? SessionOfRun(string runId)
    {
        return this.Sessions.All().FirstOrDefault(s => s.RunId == runId);
    }

    /// <summary>
    ///     Drives a session's run until it needs the user, e.g. after the UI's driver was closed.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the session is.</returns>
    public async Task<AuthoringView> DriveAsync(string id, IProgress<ExecutorEvent>? progress = null, CancellationToken ct = default)
    {
        var session = this.Sessions.GetRequired(id);
        await this.Engine.DriveAsync(session.RunId, progress, ct).ConfigureAwait(false);
        return this.View(id);
    }

    /// <summary>
    ///     Answers the agent's questions, and drives it on.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <param name="answers">The answers, by question id.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the session is.</returns>
    public async Task<AuthoringView> AnswerAsync(
        string id,
        IReadOnlyDictionary<string, string> answers,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        var session = this.Sessions.GetRequired(id);
        await this.Engine.AnswerAsync(session.RunId, answers, progress, ct).ConfigureAwait(false);
        return this.View(id);
    }

    /// <summary>
    ///     Decides on the tool calls the agent asked permission for, and drives it on.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <param name="choices">The decisions, by request number.</param>
    /// <param name="acceptOutput">Whether to take the agent's work as it is, denying what's undecided.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the session is.</returns>
    /// <exception cref="AflException">A rule would be saved to the repo, which an AI draft doesn't have.</exception>
    public async Task<AuthoringView> PermitAsync(
        string id,
        IReadOnlyList<PermissionChoice> choices,
        bool acceptOutput = false,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        var session = this.Sessions.GetRequired(id);
        if (choices.Any(c => c is { Decision: PermissionDecision.Always, Scope: PermissionScope.Repo }))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                "An AI draft has no repo of its own to save a rule in; save it for this draft (run) or globally.");
        }

        await this.Engine.PermitAsync(session.RunId, choices, acceptOutput, progress, ct).ConfigureAwait(false);
        return this.View(id);
    }

    /// <summary>
    ///     Sends the draft back to the agent with what to change.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the session is.</returns>
    /// <exception cref="AflException">The draft isn't waiting for review, or the feedback is empty.</exception>
    public async Task<AuthoringView> RequestChangesAsync(
        string id,
        string feedback,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        var session = this.Sessions.GetRequired(id);
        await this.Engine.RejectAsync(session.RunId, feedback, progress, ct).ConfigureAwait(false);
        return this.View(id);
    }

    /// <summary>
    ///     Tries the agent's work again after it failed.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the session is.</returns>
    public async Task<AuthoringView> RetryAsync(string id, IProgress<ExecutorEvent>? progress = null, CancellationToken ct = default)
    {
        var session = this.Sessions.GetRequired(id);
        await this.Engine.RetryAsync(session.RunId, progress, ct).ConfigureAwait(false);
        return this.View(id);
    }

    /// <summary>
    ///     Reads the draft as it is now.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <returns>The draft's JSON, or null when nothing has been written yet.</returns>
    public string? ReadDraft(string id)
    {
        var path = this.Sessions.DraftPathFor(this.Sessions.GetRequired(id).Id);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    ///     Replaces the draft, e.g. with the user's own edits, and checks it.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <param name="json">The new draft.</param>
    /// <returns>What the draft check finds.</returns>
    /// <exception cref="AflException">The session is already finished.</exception>
    public AuthoringDraftCheck UpdateDraft(string id, string json)
    {
        var view = this.View(id);
        if (view.Status is AuthoringStatus.Saved or AuthoringStatus.Ended)
        {
            throw new AflException(AflErrorKind.InvalidState, $"AI draft {id} is finished; its draft can't change.");
        }

        AtomicFile.WriteAllText(view.DraftPath, json);
        return this.Check(view.Session);
    }

    /// <summary>
    ///     Approves the draft: checks it again (it may have been edited), saves it to a library and finishes the run.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <param name="scope">Where to save it: <see cref="WorkflowScope.Global" />, or <see cref="WorkflowScope.Repo" /> for the session's repo.</param>
    /// <param name="overwrite">Whether to replace a document of the same id already in that scope.</param>
    /// <param name="progress">Receives live events, if set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the session is.</returns>
    /// <exception cref="AflException">
    ///     The draft isn't waiting for review or has problems, the scope can't be written, or a document of that id is
    ///     there and <paramref name="overwrite" /> isn't set.
    /// </exception>
    public async Task<AuthoringView> ApproveAsync(
        string id,
        WorkflowScope scope,
        bool overwrite = false,
        IProgress<ExecutorEvent>? progress = null,
        CancellationToken ct = default)
    {
        var view = this.View(id);
        var session = view.Session;
        if (view.Status != AuthoringStatus.Review)
        {
            throw new AflException(AflErrorKind.InvalidState, $"AI draft {id} isn't waiting for review (it's {view.Status}).");
        }

        if (scope == WorkflowScope.BuiltIn)
        {
            throw new AflException(AflErrorKind.InvalidInput, "Built-ins can't be changed; save to global or repo.");
        }

        if (scope == WorkflowScope.Repo && session.RepoPath is null)
        {
            throw new AflException(AflErrorKind.InvalidInput, $"AI draft {id} has no repo; save it to global.");
        }

        if (!view.Check.IsValid)
        {
            throw new AflException(AflErrorKind.InvalidWorkflow,
                $"The draft can't be saved yet:\n{string.Join("\n", view.Check.Problems.Select(p => $"- {p}"))}");
        }

        var kind = AuthoringKindRegistry.Instance.Create(session.Kind);
        var paths = this.Engine.Store.Paths;
        if (!overwrite && kind.Exists(paths, session.RepoPath, view.Check.Id!, scope))
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"There's already a {scope.ToString().ToLowerInvariant()} {kind.Noun} '{view.Check.Id}'; approve with overwrite to replace it.");
        }

        var (savedId, savedPath) = kind.Save(paths, session.RepoPath, view.DraftPath, scope);
        session.SavedId = savedId;
        session.SavedScope = scope;
        session.SavedPath = savedPath;
        this.Sessions.Save(session);
        await this.Engine.ApproveAsync(session.RunId, null, progress, ct).ConfigureAwait(false);
        return this.View(id);
    }

    /// <summary>
    ///     Discards a session: cancels its run if it's still going, and deletes the run, the folder and the record. A
    ///     document already saved stays in its library.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <exception cref="AflException">There's no such session.</exception>
    public void Discard(string id)
    {
        var session = this.Sessions.GetRequired(id);
        if (this.Engine.Store.Get(session.RunId) is { } run)
        {
            if (!run.IsFinished)
            {
                this.Engine.Cancel(run.Id);
            }

            this.Engine.Delete(run.Id);
        }

        this.Sessions.DeleteWithFolder(session.Id);
    }

    /// <summary>
    ///     Builds a session's view.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The view.</returns>
    private AuthoringView ViewOf(AuthoringSession session)
    {
        var run = this.Engine.Store.Get(session.RunId);
        var step = run?.StepOf(AuthoringConstants.DraftStepId);
        return new AuthoringView(session, run, step, StatusOf(session, run, step), this.Check(session), this.Sessions.DraftPathFor(session.Id));
    }

    /// <summary>
    ///     Checks a session's draft against its kind's library.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>What was found.</returns>
    private AuthoringDraftCheck Check(AuthoringSession session)
    {
        return AuthoringKindRegistry.Instance.Create(session.Kind)
            .Check(this.Engine.Store.Paths, session.RepoPath, this.Sessions.DraftPathFor(session.Id));
    }

    /// <summary>
    ///     Works out where a session is from its run.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="run">Its run, if it's still there.</param>
    /// <param name="step">The run's drafting step.</param>
    /// <returns>The status.</returns>
    private static AuthoringStatus StatusOf(AuthoringSession session, Run? run, RunStep? step)
    {
        if (session.IsSaved)
        {
            return AuthoringStatus.Saved;
        }

        if (run is null || run.Status is RunStatus.Done or RunStatus.Cancelled)
        {
            return AuthoringStatus.Ended;
        }

        if (step?.Status == StepStatus.AwaitingApproval)
        {
            return AuthoringStatus.Review;
        }

        return run.State switch
        {
            RunState.NeedsYou => AuthoringStatus.NeedsYou,
            RunState.Failed => AuthoringStatus.Failed,
            _ => AuthoringStatus.Working,
        };
    }

    /// <summary>
    ///     Builds the one-step workflow drafting a session's document.
    /// </summary>
    /// <param name="kind">The kind of document.</param>
    /// <param name="session">The session.</param>
    /// <returns>The workflow.</returns>
    private WorkflowDefinition WorkflowFor(AbstractAuthoringKind kind, AuthoringSession session)
    {
        var repo = session.RepoPath is null
            ? string.Empty
            : $"""


               The user's project is at `{session.RepoPath}`. Read it where it helps the {kind.Noun} fit (its languages,
               layout and conventions), but don't change anything there.
               """;
        var task = session.BaseId is null
            ? $"Write a new {kind.Noun} for afl, the workflow tool, from what the user wants:"
            : $"Improve the {kind.Noun} `{session.BaseId}` for afl, the workflow tool. Its current version is already in " +
              $"`./{AuthoringConstants.DraftFileName}`; change it there, keeping its id. What the user wants:";
        var draft = new AuthorDraftStepDefinition
        {
            Id = AuthoringConstants.DraftStepId,
            Name = session.BaseId is null ? $"Draft the {kind.Noun}" : $"Improve {session.BaseId}",
            Kind = kind.Name,
            Prompt = $"{task}\n\n{{{{ticket.body}}}}{repo}",
            Ask = true,
            Advance = StepConstants.AdvanceApprove,
            PermissionMode = StepConstants.ReadOnlyPermissionMode,
            AllowedTools = AllowedTools.ToList(),
            Directories = session.RepoPath is null ? null : [session.RepoPath],
            LibraryRepoPath = session.RepoPath,
        };
        return new WorkflowDefinition
        {
            Id = $"author-{kind.Name}",
            Name = $"Draft a {kind.Noun}",
            Steps = [draft],
        }.Chain();
    }

    /// <summary>
    ///     Builds the ticket a session's run works on: what the user asked for.
    /// </summary>
    /// <param name="kind">The kind of document.</param>
    /// <param name="session">The session.</param>
    /// <returns>The ticket.</returns>
    private Ticket TicketFor(AbstractAuthoringKind kind, AuthoringSession session)
    {
        var title = session.BaseId is null ? $"New {kind.Noun}" : $"Improve {kind.Noun} {session.BaseId}";
        return new Ticket
        {
            Source = TicketRef.LocalSource,
            Key = session.Id,
            Title = title,
            Body = string.IsNullOrWhiteSpace(session.Description) ? "(Nothing more: improve it as you see fit.)" : session.Description,
        };
    }
}
