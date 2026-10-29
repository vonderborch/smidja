using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Workflows;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Shows the current repo.
/// </summary>
[Verb("repo", HelpText = "Show the current repo (see also repo add|list|remove|set).")]
public class RepoCommand : AbstractCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.RequireRepo(), TextRendering.Repo);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Registers a repo.
/// </summary>
[Verb("repo-add", HelpText = "Register a repo so the UI can see it.")]
public class RepoAddCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the repo path.
    /// </summary>
    [Value(0, MetaName = "path", HelpText = "A path inside the repo (defaults to the current directory).")]
    public string? PathArgument { get; set; }

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var path = Path.GetFullPath(this.PathArgument ?? Environment.CurrentDirectory);
        if (!Directory.Exists(path))
        {
            throw new AflException(AflErrorKind.NotFound, $"No such directory: {path}");
        }

        this.Output.Result(this.Repos.EnsureRegistered(path), TextRendering.Repo);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Lists registered repos.
/// </summary>
[Verb("repo-list", HelpText = "List registered repos.")]
public class RepoListCommand : AbstractCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.Repos.All(), TextRendering.RepoList);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Unregisters a repo.
/// </summary>
[Verb("repo-remove", HelpText = "Unregister a repo (its files and runs are left alone).")]
public class RepoRemoveCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the repo.
    /// </summary>
    [Value(0, MetaName = "repo", Required = true, HelpText = "The repo id, name or path.")]
    public string Repo { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.Repos.GetRequired(this.Repo);
        this.Repos.Remove(repo.Id);
        this.Output.Result(repo, (r, w) => w.WriteLine($"Removed {r.Id}."));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Changes a repo's settings.
/// </summary>
[Verb("repo-set", HelpText = "Change the current repo's settings.")]
public class RepoSetCommand : AbstractCommand
{
    /// <summary>
    ///     Gets or sets the default workflow.
    /// </summary>
    [Option('w', "default-workflow", Required = true, HelpText = "The workflow used when afl start isn't given one.")]
    public string DefaultWorkflow { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.RequireRepo();
        new WorkflowLibrary(this.Paths, repo.Path).GetRequired(this.DefaultWorkflow);
        var updated = this.Repos.Modify(repo.Id, r => r.DefaultWorkflowId = this.DefaultWorkflow);
        this.Output.Result(updated, TextRendering.Repo);
        return Task.FromResult(ExitSuccess);
    }
}
