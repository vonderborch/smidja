using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Workflows;

namespace Afl.Core.Workspaces;

/// <summary>
///     Child runs work in their parent's checkout, as they always have. This is the default.
/// </summary>
[WorkspaceRegistration(StepConstants.WorkspaceShared, "Child runs work in the parent's checkout")]
public class SharedWorkspace : AbstractWorkspace
{
    /// <inheritdoc />
    public override bool IsIsolated => false;

    /// <inheritdoc />
    public override Task<WorkspaceLease> PrepareAsync(WorkspaceContext context, CancellationToken ct)
    {
        return Task.FromResult(new WorkspaceLease(context.BaseCheckout, null));
    }

    /// <inheritdoc />
    public override Task<WorkspaceOutcome> CompleteAsync(WorkspaceContext context, CancellationToken ct)
    {
        return Task.FromResult(new WorkspaceOutcome(WorkspaceOutcomeKind.NothingToMerge, "Worked in the parent's checkout."));
    }

    /// <inheritdoc />
    public override Task DiscardAsync(WorkspaceContext context, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
///     Each child run gets its own git worktree, under afl's home, on a new branch from the parent checkout's
///     current <c>HEAD</c>. Once the child is done its changes are committed and merged (<c>--no-ff</c>) into the
///     parent checkout's current branch, and the worktree and branch removed; with <c>mergeBack</c> off the branch is
///     kept unmerged instead. A merge that conflicts is aborted, leaving the branch and worktree for a person.
/// </summary>
[WorkspaceRegistration(StepConstants.WorkspaceWorktree, "Each child run gets its own git worktree and branch, merged back when done")]
public class WorktreeWorkspace : AbstractWorkspace
{
    /// <summary>
    ///     The prefix of every branch afl makes for a child run.
    /// </summary>
    public const string BranchPrefix = "afl/";

    /// <summary>
    ///     How long to wait for another child to finish changing the same repo's worktrees or branches.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public override async Task<WorkspaceLease> PrepareAsync(WorkspaceContext context, CancellationToken ct)
    {
        var checkout = context.BaseCheckout;
        if (GitHelpers.FindGitRoot(checkout) is null)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"The '{StepConstants.WorkspaceWorktree}' workspace needs a git checkout, and {checkout} isn't in one.");
        }

        var path = Path.Combine(context.Paths.WorktreesDirectory, context.Child.Id);
        using var repoLock = this.LockRepo(context);
        if (context.Base is not null)
        {
            // A lane inside another lane: that lane's checkout is afl's own, so its work so far is committed for this
            // one to start from.
            await this.CommitAsync(context, checkout, ct).ConfigureAwait(false);
        }

        if (System.IO.Directory.Exists(path))
        {
            await this.GitAsync(checkout, ct, "worktree", "remove", "--force", path).ConfigureAwait(false);
            await this.GitAsync(checkout, ct, "worktree", "prune").ConfigureAwait(false);
        }

        var branch = BranchPrefix + TemplateVariables.Slug(context.Parent.Ticket.Key) + "/" + TemplateVariables.Slug(context.Item);
        if (await this.BranchExistsAsync(checkout, branch, ct).ConfigureAwait(false))
        {
            branch += "-" + context.Child.Id;
        }

        System.IO.Directory.CreateDirectory(context.Paths.WorktreesDirectory);
        await this.GitRequiredAsync(checkout, ct, "worktree", "add", path, "-b", branch).ConfigureAwait(false);
        if (context.Child.ScratchDirectory is { } scratch)
        {
            // The worktree's own copy of the scratch root ignores itself too, so committing the child's work never
            // picks up documents an agent wrote there by its relative path.
            ScratchArea.Ensure(path, scratch);
        }

        return new WorkspaceLease(path, branch);
    }

    /// <inheritdoc />
    public override async Task<WorkspaceOutcome> CompleteAsync(WorkspaceContext context, CancellationToken ct)
    {
        var checkout = context.BaseCheckout;
        var branch = context.Child.WorkspaceBranch;
        var path = context.Child.WorkingPath;
        if (branch is null)
        {
            return new WorkspaceOutcome(WorkspaceOutcomeKind.NothingToMerge, "The child had no branch of its own.");
        }

        using var repoLock = this.LockRepo(context);
        if (path is not null && System.IO.Directory.Exists(path))
        {
            await this.CommitAsync(context, path, ct).ConfigureAwait(false);
        }

        if (context.Base is not null)
        {
            await this.CommitAsync(context, checkout, ct).ConfigureAwait(false);
        }

        if (!context.MergeBack)
        {
            await this.RemoveWorktreeAsync(checkout, path, ct).ConfigureAwait(false);
            return new WorkspaceOutcome(WorkspaceOutcomeKind.Kept, $"Kept the work on branch {branch}, unmerged.");
        }

        var ahead = await this.GitAsync(checkout, ct, "rev-list", "--count", $"HEAD..{branch}").ConfigureAwait(false);
        if (ahead.Success && ahead.StandardOutput.Trim() == "0")
        {
            await this.RemoveWorktreeAsync(checkout, path, ct).ConfigureAwait(false);
            await this.GitAsync(checkout, ct, "branch", "-D", branch).ConfigureAwait(false);
            return new WorkspaceOutcome(WorkspaceOutcomeKind.NothingToMerge, $"Branch {branch} had nothing new to merge.");
        }

        var message = $"Merge {context.Item}: {context.Child.Ticket.Title} (afl run {context.Child.Id})";
        var identity = await this.IdentityAsync(checkout, ct).ConfigureAwait(false);
        var merge = await this.GitAsync(checkout, ct, [.. identity, "merge", "--no-ff", "-m", message, branch]).ConfigureAwait(false);
        if (!merge.Success)
        {
            await this.GitAsync(checkout, ct, "merge", "--abort").ConfigureAwait(false);
            var why = FirstLine(merge.StandardError) ?? FirstLine(merge.StandardOutput) ?? "git merge failed";
            return new WorkspaceOutcome(WorkspaceOutcomeKind.Conflict,
                $"Couldn't merge branch {branch} into {checkout} ({why}). Its worktree is at {path}. Resolve it with " +
                $"`git merge {branch}` yourself and retry the step, or retry to let afl try again.");
        }

        await this.RemoveWorktreeAsync(checkout, path, ct).ConfigureAwait(false);
        await this.GitAsync(checkout, ct, "branch", "-D", branch).ConfigureAwait(false);
        return new WorkspaceOutcome(WorkspaceOutcomeKind.Merged, $"Merged branch {branch}.");
    }

    /// <inheritdoc />
    public override async Task DiscardAsync(WorkspaceContext context, CancellationToken ct)
    {
        using var repoLock = this.LockRepo(context);
        await this.RemoveWorktreeAsync(context.BaseCheckout, context.Child.WorkingPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Takes the lock that keeps two children from changing the same repo's worktrees and branches at once.
    /// </summary>
    /// <param name="context">The workspace context.</param>
    /// <returns>The held lock.</returns>
    protected virtual FileLock LockRepo(WorkspaceContext context)
    {
        return FileLock.Acquire(Path.Combine(context.Paths.WorktreesDirectory, context.Parent.RepoId + PathConstants.LockExtension),
            LockTimeout, $"the worktrees of {context.BaseCheckout}");
    }

    /// <summary>
    ///     Commits everything a child changed in its worktree, if it changed anything.
    /// </summary>
    /// <param name="context">The workspace context.</param>
    /// <param name="path">The worktree.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task CommitAsync(WorkspaceContext context, string path, CancellationToken ct)
    {
        await this.GitRequiredAsync(path, ct, "add", "-A").ConfigureAwait(false);
        var status = await this.GitRequiredAsync(path, ct, "status", "--porcelain").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return;
        }

        var message = $"{context.Item}: {context.Child.Ticket.Title} (afl run {context.Child.Id})";
        await this.GitRequiredAsync(path, ct, [.. await this.IdentityAsync(path, ct).ConfigureAwait(false), "commit", "-m", message]).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes a child's worktree, if it is still there, keeping its branch.
    /// </summary>
    /// <param name="checkout">The checkout it was made from.</param>
    /// <param name="path">The worktree, or null.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task RemoveWorktreeAsync(string checkout, string? path, CancellationToken ct)
    {
        if (path is null)
        {
            return;
        }

        if (System.IO.Directory.Exists(path))
        {
            await this.GitAsync(checkout, ct, "worktree", "remove", "--force", path).ConfigureAwait(false);
        }

        await this.GitAsync(checkout, ct, "worktree", "prune").ConfigureAwait(false);
    }

    /// <summary>
    ///     Returns whether a local branch exists.
    /// </summary>
    /// <param name="checkout">The checkout.</param>
    /// <param name="branch">The branch name.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if it exists.</returns>
    private async Task<bool> BranchExistsAsync(string checkout, string branch, CancellationToken ct)
    {
        return (await this.GitAsync(checkout, ct, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).ConfigureAwait(false)).Success;
    }

    /// <summary>
    ///     Returns the options giving git a committer identity when the repo has none configured, so afl can commit and
    ///     merge on a machine where nobody has set one.
    /// </summary>
    /// <param name="checkout">The checkout.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The options; empty when an identity is configured.</returns>
    private async Task<string[]> IdentityAsync(string checkout, CancellationToken ct)
    {
        var email = await this.GitAsync(checkout, ct, "config", "user.email").ConfigureAwait(false);
        return email.Success && !string.IsNullOrWhiteSpace(email.StandardOutput)
            ? []
            : ["-c", "user.name=afl", "-c", "user.email=afl@localhost"];
    }

    /// <summary>
    ///     Runs git, failing if it fails.
    /// </summary>
    /// <param name="directory">Where to run it.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>Its result.</returns>
    /// <exception cref="AflException">git failed.</exception>
    private async Task<ProcessResult> GitRequiredAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        var result = await this.GitAsync(directory, ct, arguments).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"git {arguments.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('='))} failed in {directory}: " +
                (FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? $"exit code {result.ExitCode}"));
        }

        return result;
    }

    /// <summary>
    ///     Runs git.
    /// </summary>
    /// <param name="directory">Where to run it.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>Its result.</returns>
    protected virtual Task<ProcessResult> GitAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        return ProcessRunner.RunAsync("git", arguments, directory, null, ct);
    }

    /// <summary>
    ///     Returns the first non-blank line of some text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The line, or null.</returns>
    private static string? FirstLine(string text)
    {
        return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
    }
}
