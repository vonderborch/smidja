using Afl.Cli.Output;
using Afl.Core.Authoring;
using Afl.Core.Exceptions;
using Afl.Core.Notifications;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Workflows;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for the <c>afl author-*</c> verbs, which drive the AI authoring helper: each acts on an AI draft through
///     the <see cref="AuthoringService" /> and prints where the draft is now.
/// </summary>
public abstract class AbstractAuthoringCommand : AbstractCommand
{
    /// <summary>
    ///     Gets the authoring service, over a run engine that notifies on this machine.
    /// </summary>
    protected AuthoringService Service => new(new RunEngine(this.Paths) { Notifier = RunNotifier.ForThisMachine(this.Paths) });

    /// <summary>
    ///     Gets whether the output includes the draft's JSON.
    /// </summary>
    protected virtual bool IncludesDraft => false;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var service = this.Service;
        var view = await this.ActAsync(service, ct);
        var draft = this.IncludesDraft ? service.ReadDraft(view.Session.Id) : null;
        this.Output.Result(AuthoringSessionView.From(view, draft), AuthoringSessionView.Render);
        return view.Status == AuthoringStatus.Failed ? ExitRunFailed : ExitSuccess;
    }

    /// <summary>
    ///     Does the verb's work on a draft.
    /// </summary>
    /// <param name="service">The authoring service.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the draft is now.</returns>
    protected abstract Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct);

    /// <summary>
    ///     Returns the repo the agent may read and the draft may be saved to: <c>--repo</c>, else the current
    ///     directory's, else none.
    /// </summary>
    /// <returns>The repo, or null.</returns>
    protected RepoRecord? OptionalRepo()
    {
        return this.RepoOption is null ? this.CurrentRepo : this.Repos.GetRequired(this.RepoOption);
    }
}

/// <summary>
///     Base for the verbs that start an AI draft.
/// </summary>
public abstract class AbstractAuthoringStartCommand : AbstractAuthoringCommand
{
    /// <summary>
    ///     Gets or sets the kind of document.
    /// </summary>
    [Value(0, MetaName = "kind", Required = true, HelpText = "What to draft: skill, step (a common step) or workflow.")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the agent to draft with.
    /// </summary>
    [Option('a', "agent", HelpText = "The agent that drafts it (defaults to the default main agent).")]
    public string? Agent { get; set; }
}

/// <summary>
///     Base for the verbs that act on an AI draft by its id.
/// </summary>
public abstract class AbstractAuthoringSessionCommand : AbstractAuthoringCommand
{
    /// <summary>
    ///     Gets or sets the draft's id.
    /// </summary>
    [Value(0, MetaName = "draft-id", Required = true, HelpText = "The AI draft's id (see afl author-list).")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;
}

/// <summary>
///     Starts drafting a new document with the AI.
/// </summary>
[Verb("author-new", HelpText = "Describe a skill, common step or workflow and have an AI draft it for you to review.")]
public class AuthorNewCommand : AbstractAuthoringStartCommand
{
    /// <summary>
    ///     Gets or sets what to draft.
    /// </summary>
    [Option('d', "describe", Required = true, HelpText = "What you want (text, @file, or - for stdin).")]
    public string Describe { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        return service.StartAsync(this.Kind, ReadTextArgument(this.Describe), this.OptionalRepo(), null, this.Agent, this.Output, ct);
    }
}

/// <summary>
///     Starts improving an existing document with the AI.
/// </summary>
[Verb("author-improve", HelpText = "Have an AI improve an existing skill, common step or workflow; a built-in is saved as a copy.")]
public class AuthorImproveCommand : AbstractAuthoringStartCommand
{
    /// <summary>
    ///     Gets or sets the document's id.
    /// </summary>
    [Value(1, MetaName = "id", Required = true, HelpText = "The id of the document to improve.")]
    public string BaseId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets what to change.
    /// </summary>
    [Option('d', "describe", HelpText = "What to change (text, @file, or - for stdin). Without it the AI looks for improvements itself.")]
    public string? Describe { get; set; }

    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        var description = this.Describe is null ? string.Empty : ReadTextArgument(this.Describe);
        return service.StartAsync(this.Kind, description, this.OptionalRepo(), this.BaseId, this.Agent, this.Output, ct);
    }
}

/// <summary>
///     Lists the AI drafts.
/// </summary>
[Verb("author-list", HelpText = "List the AI drafts, newest first.")]
public class AuthorListCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the kind to list.
    /// </summary>
    [Option('k', "kind", HelpText = "Only drafts of this kind: skill, step or workflow.")]
    public string? Kind { get; set; }

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var service = new AuthoringService(new RunEngine(this.Paths));
        var views = service.List(this.Kind).Select(v => AuthoringSessionView.From(v)).ToList();
        this.Output.Result<IReadOnlyList<AuthoringSessionView>>(views, AuthoringSessionView.RenderList);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Shows an AI draft.
/// </summary>
[Verb("author-show", HelpText = "Show an AI draft: where it is, its questions or permission requests, its problems and its JSON.")]
public class AuthorShowCommand : AbstractAuthoringSessionCommand
{
    /// <inheritdoc />
    protected override bool IncludesDraft => true;

    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        return Task.FromResult(service.View(this.Id));
    }
}

/// <summary>
///     Drives an AI draft on.
/// </summary>
[Verb("author-resume", HelpText = "Carry on with an AI draft that was interrupted.")]
public class AuthorResumeCommand : AbstractAuthoringSessionCommand
{
    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        return service.DriveAsync(this.Id, this.Output, ct);
    }
}

/// <summary>
///     Answers the AI's questions about a draft.
/// </summary>
[Verb("author-answer", HelpText = "Answer the AI's questions about a draft and let it carry on.")]
public class AuthorAnswerCommand : AbstractAuthoringSessionCommand
{
    /// <summary>
    ///     Gets or sets the answers, as <c>id=value</c>.
    /// </summary>
    [Option('i', "input",
        HelpText = "Answer a question: --input ask-1=value (repeatable; value can be @file or -). Missing answers are asked for in a terminal.")]
    public IEnumerable<string> Inputs { get; set; } = [];

    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        var given = ParsePairs(this.Inputs, "--input");
        var answers = service.View(this.Id).Run is { } run ? this.AskForAnswers(run, given) : given;
        return service.AnswerAsync(this.Id, answers, this.Output, ct);
    }
}

/// <summary>
///     Decides on the tool calls the AI asked permission for while drafting, such as fetching a web page.
/// </summary>
[Verb("author-permit", HelpText = "Decide on what the AI was refused while drafting: --once N, --always N[=rule], --deny N, --all once|always|deny, or --accept-output.")]
public class AuthorPermitCommand : AbstractAuthoringSessionCommand
{
    /// <summary>
    ///     Gets or sets the requests to allow for the agent's next turn only.
    /// </summary>
    [Option("once", HelpText = "Allow request N for the agent's next turn only: --once 1 or --once 1=\"WebFetch(domain:example.com)\" (repeatable).")]
    public IEnumerable<string> Once { get; set; } = [];

    /// <summary>
    ///     Gets or sets the requests to allow from now on.
    /// </summary>
    [Option("always", HelpText = "Always allow request N, saving its first suggested rule (or N=rule) to --scope (repeatable).")]
    public IEnumerable<string> Always { get; set; } = [];

    /// <summary>
    ///     Gets or sets the requests to keep refusing.
    /// </summary>
    [Option("deny", HelpText = "Keep refusing request N (repeatable).")]
    public IEnumerable<string> Deny { get; set; } = [];

    /// <summary>
    ///     Gets or sets what to decide for every request not named.
    /// </summary>
    [Option("all", HelpText = "Decide every request not named otherwise: once, always or deny.")]
    public string? All { get; set; }

    /// <summary>
    ///     Gets or sets where <c>--always</c> rules are saved.
    /// </summary>
    [Option("scope", Default = "run", HelpText = "Where --always rules are saved: run (this draft only, the default) or global.")]
    public string Scope { get; set; } = "run";

    /// <summary>
    ///     Gets or sets whether to keep the AI's work as it is, refusing whatever is undecided.
    /// </summary>
    [Option("accept-output", HelpText = "Keep what the AI did without what it was refused: undecided requests are denied and the draft goes to review.")]
    public bool AcceptOutput { get; set; }

    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        var open = service.View(this.Id).Step?.OpenPermissionRequests ?? [];
        var choices = RunPermitCommand.ChoicesFrom(this.Output, open, this.Once, this.Always, this.Deny, this.All,
            AbstractPermissionCommand.ParseScope(this.Scope), this.AcceptOutput, $"afl author-show {this.Id}");
        return service.PermitAsync(this.Id, choices, this.AcceptOutput, this.Output, ct);
    }
}

/// <summary>
///     Sends a draft back to the AI with what to change.
/// </summary>
[Verb("author-changes", HelpText = "Send an AI draft back with what to change; the AI revises it.")]
public class AuthorChangesCommand : AbstractAuthoringSessionCommand
{
    /// <summary>
    ///     Gets or sets what to change.
    /// </summary>
    [Option('f', "feedback", Required = true, HelpText = "What to change (text, @file, or - for stdin).")]
    public string Feedback { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        return service.RequestChangesAsync(this.Id, ReadTextArgument(this.Feedback), this.Output, ct);
    }
}

/// <summary>
///     Retries a draft whose AI work failed.
/// </summary>
[Verb("author-retry", HelpText = "Try an AI draft again after its AI work failed.")]
public class AuthorRetryCommand : AbstractAuthoringSessionCommand
{
    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        return service.RetryAsync(this.Id, this.Output, ct);
    }
}

/// <summary>
///     Approves a draft, saving it to a library.
/// </summary>
[Verb("author-approve", HelpText = "Approve an AI draft and save it to the global library or its repo's.")]
public class AuthorApproveCommand : AbstractAuthoringSessionCommand
{
    /// <summary>
    ///     Gets or sets where to save it.
    /// </summary>
    [Option("scope", Required = true, HelpText = "Where to save it: global (~/.afl) or repo (the repo the draft was started in).")]
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether a document with the same id in that scope may be replaced.
    /// </summary>
    [Option("overwrite", HelpText = "Replace a document with the same id already in that scope.")]
    public bool Overwrite { get; set; }

    /// <inheritdoc />
    protected override Task<AuthoringView> ActAsync(AuthoringService service, CancellationToken ct)
    {
        var scope = this.Scope.Trim().ToLowerInvariant() switch
        {
            "global" => WorkflowScope.Global,
            "repo" => WorkflowScope.Repo,
            _ => throw new AflException(AflErrorKind.InvalidInput, $"--scope takes global or repo, not '{this.Scope}'."),
        };
        return service.ApproveAsync(this.Id, scope, this.Overwrite, this.Output, ct);
    }
}

/// <summary>
///     Discards a draft: its run, its folder and its record.
/// </summary>
[Verb("author-discard", HelpText = "Discard an AI draft: stop it and delete its run and work folder (nothing saved is touched).")]
public class AuthorDiscardCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the draft's id.
    /// </summary>
    [Value(0, MetaName = "draft-id", Required = true, HelpText = "The AI draft's id (see afl author-list).")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var service = new AuthoringService(new RunEngine(this.Paths));
        var id = service.Sessions.GetRequired(this.Id).Id;
        service.Discard(id);
        this.Output.Result(new { id, discarded = true }, (r, w) => w.WriteLine($"Discarded AI draft {r.id}."));
        return Task.FromResult(ExitSuccess);
    }
}
