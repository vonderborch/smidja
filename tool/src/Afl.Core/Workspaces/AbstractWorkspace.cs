using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Runs;

namespace Afl.Core.Workspaces;

/// <summary>
///     Registers an <see cref="AbstractWorkspace" /> under the name <c>workflow</c> steps use for it in
///     <c>workspace</c>.
/// </summary>
/// <param name="name">The workspace name.</param>
/// <param name="description">A line describing it, for front-ends.</param>
public class WorkspaceRegistrationAttribute(string name, string description) : AbstractRegistrationAttribute(name)
{
    /// <summary>
    ///     Gets a line describing the workspace, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     What a workspace works with: the store paths, the parent run whose step started the child, and the child.
/// </summary>
/// <param name="Paths">The global store paths.</param>
/// <param name="Parent">The parent run.</param>
/// <param name="Child">The child run.</param>
/// <param name="Item">The item the child was started for (<c>{{item}}</c>).</param>
/// <param name="MergeBack">Whether a finished child's work goes back into the parent's checkout.</param>
/// <param name="Base">The checkout to start from and go back into instead of the parent's, if any.</param>
public record WorkspaceContext(AflPaths Paths, Run Parent, Run Child, string Item, bool MergeBack, string? Base = null)
{
    /// <summary>
    ///     Gets the checkout the child's work starts from and goes back into: <see cref="Base" /> when set (a lane split
    ///     off inside another lane), else the parent's own working copy.
    /// </summary>
    public string BaseCheckout => this.Base ?? this.Parent.WorkingPath ?? this.Parent.RepoPath;
}

/// <summary>
///     Where a child run was given to work.
/// </summary>
/// <param name="Path">The checkout its agents work in.</param>
/// <param name="Branch">Its branch, if it has its own.</param>
public record WorkspaceLease(string Path, string? Branch);

/// <summary>
///     How a finished child's workspace was settled.
/// </summary>
public enum WorkspaceOutcomeKind
{
    /// <summary>
    ///     Its work went back into the parent's checkout.
    /// </summary>
    Merged,

    /// <summary>
    ///     It changed nothing, so there was nothing to bring back.
    /// </summary>
    NothingToMerge,

    /// <summary>
    ///     Its work was kept on its own branch, unmerged.
    /// </summary>
    Kept,

    /// <summary>
    ///     Its work couldn't be brought back without a person resolving a conflict.
    /// </summary>
    Conflict,
}

/// <summary>
///     How a finished child's workspace was settled, with a line for people.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Message">A line saying what happened, and for a conflict what to do about it.</param>
public record WorkspaceOutcome(WorkspaceOutcomeKind Kind, string Message)
{
    /// <summary>
    ///     Gets whether the workspace is settled, so the child's work needs nothing more from afl.
    /// </summary>
    public bool IsSettled => this.Kind != WorkspaceOutcomeKind.Conflict;
}

/// <summary>
///     Where the child runs of a <c>workflow</c> step work (<c>workspace</c> on the step), and how their work gets
///     back into the parent's checkout. A child's workspace is prepared just before the child is first driven, so
///     children driven one after another each start from the work merged before them; it is completed once the child
///     is done and discarded if it is cancelled. A new workspace is one subclass with
///     <see cref="WorkspaceRegistrationAttribute" />.
/// </summary>
public abstract class AbstractWorkspace
{
    /// <summary>
    ///     Gets a value indicating whether each child gets a checkout of its own; when false the children work in the
    ///     parent's and there is nothing to prepare or settle.
    /// </summary>
    public virtual bool IsIsolated => true;

    /// <summary>
    ///     Gives a child its own place to work.
    /// </summary>
    /// <param name="context">The workspace context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>Where the child works.</returns>
    public abstract Task<WorkspaceLease> PrepareAsync(WorkspaceContext context, CancellationToken ct);

    /// <summary>
    ///     Settles a done child's workspace: brings its work back (or keeps it, without <c>mergeBack</c>) and tidies
    ///     up. Safe to call again after a conflict has been resolved.
    /// </summary>
    /// <param name="context">The workspace context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>What happened.</returns>
    public abstract Task<WorkspaceOutcome> CompleteAsync(WorkspaceContext context, CancellationToken ct);

    /// <summary>
    ///     Tidies away a cancelled child's workspace, keeping whatever work it has already saved.
    /// </summary>
    /// <param name="context">The workspace context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    ///     The engine waits on this synchronously when a run is cancelled or deleted (possibly on the UI thread), so an
    ///     override must use <c>ConfigureAwait(false)</c> on everything it awaits.
    /// </remarks>
    public abstract Task DiscardAsync(WorkspaceContext context, CancellationToken ct);
}

/// <summary>
///     The registered workspaces.
/// </summary>
public class WorkspaceRegistry() : TypeRegistry<AbstractWorkspace, WorkspaceRegistrationAttribute>("workspace")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static WorkspaceRegistry Instance { get; } = new();

    /// <summary>
    ///     Creates the workspace with a name, or the shared one when the name is unset.
    /// </summary>
    /// <param name="name">The name, or null.</param>
    /// <returns>The workspace.</returns>
    public AbstractWorkspace CreateOrShared(string? name)
    {
        return this.Create(string.IsNullOrWhiteSpace(name) ? StepConstants.WorkspaceShared : name);
    }
}
