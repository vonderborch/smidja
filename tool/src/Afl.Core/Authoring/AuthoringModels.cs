using System.Text.Json.Serialization;
using Afl.Core.Constants;
using Afl.Core.Runs;
using Afl.Core.Storage;
using Afl.Core.Workflows;

namespace Afl.Core.Authoring;

/// <summary>
///     One go at drafting a document with the AI authoring helper: what was asked for, the hidden run drafting it, and
///     where it was saved once approved. Kept in <c>~/.afl/authoring/&lt;id&gt;.json</c>, with the draft and what the agent
///     reads in its work folder (<see cref="AuthoringStore.FolderFor" />).
/// </summary>
[DocumentVersion(1, 0)]
public class AuthoringSession : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the session's id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the kind of document drafted (an <see cref="AuthoringKindRegistry" /> name).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets what the user asked for.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the id of the document being improved, or null for a new one.
    /// </summary>
    public string? BaseId { get; set; }

    /// <summary>
    ///     Gets or sets the scope the document being improved was in.
    /// </summary>
    public WorkflowScope? BaseScope { get; set; }

    /// <summary>
    ///     Gets or sets the repo the agent may read and whose library the draft is checked against, or null for none.
    /// </summary>
    public string? RepoId { get; set; }

    /// <summary>
    ///     Gets or sets the path of that repo.
    /// </summary>
    public string? RepoPath { get; set; }

    /// <summary>
    ///     Gets or sets the id of the hidden run drafting the document.
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the id of the agent drafting it.
    /// </summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets when the session started.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Gets or sets the id the document was saved under, once approved.
    /// </summary>
    public string? SavedId { get; set; }

    /// <summary>
    ///     Gets or sets the scope the document was saved to, once approved.
    /// </summary>
    public WorkflowScope? SavedScope { get; set; }

    /// <summary>
    ///     Gets or sets the file the document was saved to, once approved.
    /// </summary>
    public string? SavedPath { get; set; }

    /// <summary>
    ///     Gets whether the document has been saved to a library.
    /// </summary>
    [JsonIgnore]
    public bool IsSaved => this.SavedId is not null;

    /// <summary>
    ///     Gets the id of the repo the session's run is filed under.
    /// </summary>
    [JsonIgnore]
    public string RunRepoId => AuthoringConstants.RepoIdPrefix + this.Id;
}

/// <summary>
///     Where an authoring session is, for the user.
/// </summary>
public enum AuthoringStatus
{
    /// <summary>
    ///     The agent is working on the draft.
    /// </summary>
    Working,

    /// <summary>
    ///     The agent has asked the user something, or wants permission for a tool call.
    /// </summary>
    NeedsYou,

    /// <summary>
    ///     The draft is ready for the user to review: approve, ask for changes, edit or discard.
    /// </summary>
    Review,

    /// <summary>
    ///     The agent's work failed; it can be retried.
    /// </summary>
    Failed,

    /// <summary>
    ///     The document was approved and saved.
    /// </summary>
    Saved,

    /// <summary>
    ///     The run was cancelled or ended without the document being saved.
    /// </summary>
    Ended,
}

/// <summary>
///     An authoring session as front-ends show it: the session, its run and step, where it is, and what the draft check
///     found.
/// </summary>
/// <param name="Session">The session.</param>
/// <param name="Run">Its hidden run, or null if it has gone.</param>
/// <param name="Step">The run's drafting step, or null if the run has gone.</param>
/// <param name="Status">Where the session is.</param>
/// <param name="Check">What the draft check found.</param>
/// <param name="DraftPath">The draft file.</param>
public record AuthoringView(
    AuthoringSession Session,
    Run? Run,
    RunStep? Step,
    AuthoringStatus Status,
    AuthoringDraftCheck Check,
    string DraftPath)
{
    /// <summary>
    ///     Gets whether the session is waiting on the user.
    /// </summary>
    public bool NeedsUser => this.Status is AuthoringStatus.NeedsYou or AuthoringStatus.Review or AuthoringStatus.Failed;
}
