using Afl.Cli.Output;
using Afl.Core.Runs;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for the commands that change what a run is labelled with: they never drive it, and say what it's labelled
///     with now in a line rather than showing the whole run.
/// </summary>
public abstract class AbstractRunLabelCommand : AbstractRunCommand
{
    /// <inheritdoc />
    protected override Action<RunView, TextWriter> RenderText => TextRendering.RunLabelLine;

    /// <inheritdoc />
    protected override bool ShouldOpenInteractive(Run run)
    {
        return false;
    }
}

/// <summary>
///     Gives a run a name of its own.
/// </summary>
[Verb("run-rename", HelpText = "Give a run a name of its own, shown instead of its ticket's title (leave the name out to show the ticket's again).")]
public class RunRenameCommand : AbstractRunLabelCommand
{
    /// <summary>
    ///     Gets or sets the words of the new name.
    /// </summary>
    [Value(1, MetaName = "name", HelpText = "The new name; leave it out to show the ticket's title again.")]
    public IEnumerable<string> Name { get; set; } = [];

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return Task.FromResult(engine.Rename(this.RunId, string.Join(' ', this.Name)));
    }
}

/// <summary>
///     Adds tags to a run, or removes them.
/// </summary>
[Verb("run-tag", HelpText = "Tag a run: afl run tag <run-id> <tag>... [--remove a,b] [--clear].")]
public class RunTagCommand : AbstractRunLabelCommand
{
    /// <summary>
    ///     Gets or sets the tags to add.
    /// </summary>
    [Value(1, MetaName = "tags", HelpText = "Tags to add.")]
    public IEnumerable<string> Add { get; set; } = [];

    /// <summary>
    ///     Gets or sets the tags to remove.
    /// </summary>
    [Option("remove", Separator = ',', HelpText = "Tags to remove, with commas between them.")]
    public IEnumerable<string> Remove { get; set; } = [];

    /// <summary>
    ///     Gets or sets whether to remove every tag first.
    /// </summary>
    [Option("clear", HelpText = "Remove every tag the run has (before adding the ones given).")]
    public bool Clear { get; set; }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        var run = this.Clear ? engine.SetTags(this.RunId, []) : null;
        return Task.FromResult(engine.Tag(run?.Id ?? this.RunId, this.Add, this.Remove));
    }
}

/// <summary>
///     Files a run in a folder of its repo's runs.
/// </summary>
[Verb("run-move", HelpText = "File a run in a folder, e.g. afl run move <run-id> billing/q4 (leave the folder out for the top level). New folders are created.")]
public class RunMoveCommand : AbstractRunLabelCommand
{
    /// <summary>
    ///     Gets or sets the folder.
    /// </summary>
    [Value(1, MetaName = "folder", HelpText = "The folder path, e.g. billing/q4; leave it out for the top level.")]
    public string? Folder { get; set; }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        return Task.FromResult(engine.MoveToFolder(this.RunId, this.Folder));
    }
}

/// <summary>
///     Base for the commands that work on the folders of the repo's runs.
/// </summary>
public abstract class AbstractRunFolderCommand : AbstractCommand
{
    /// <summary>
    ///     Gets the engine that keeps the folders.
    /// </summary>
    protected RunEngine Engine => field ??= new RunEngine(this.Paths);

    /// <summary>
    ///     Writes the repo's folders, with how many runs each holds (counting its subfolders').
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <param name="moved">The runs the command moved, for the result; null when it moved none.</param>
    protected void WriteFolders(string repoId, IReadOnlyList<string>? moved = null)
    {
        var runs = this.Engine.Store.List(repoId).Where(r => r.ParentRunId is null).ToList();
        var folders = this.Engine.ListFolders(repoId)
            .Select(f => new RunFolderView(f, runs.Count(r => RunLabels.IsWithin(r.Folder, f))))
            .ToList();
        this.Output.Result(new RunFoldersView(folders, moved ?? []), TextRendering.RunFolders);
    }
}

/// <summary>
///     Lists the folders of the repo's runs.
/// </summary>
[Verb("run-folders", HelpText = "List the folders of this repo's runs, with how many runs each holds.")]
public class RunFoldersCommand : AbstractRunFolderCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.WriteFolders(this.RequireRepo().Id);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Creates a folder for the repo's runs.
/// </summary>
[Verb("run-folder-create", HelpText = "Create a folder for this repo's runs, e.g. billing/q4 (with the folders it's in).")]
public class RunFolderCreateCommand : AbstractRunFolderCommand
{
    /// <summary>
    ///     Gets or sets the folder.
    /// </summary>
    [Value(0, MetaName = "folder", Required = true, HelpText = "The folder path, e.g. billing/q4.")]
    public string Folder { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.RequireRepo();
        this.Engine.CreateFolder(repo.Id, this.Folder);
        this.WriteFolders(repo.Id);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Renames or moves a folder of the repo's runs.
/// </summary>
[Verb("run-folder-rename", HelpText = "Rename or move a folder of this repo's runs, with the runs and folders in it.")]
public class RunFolderRenameCommand : AbstractRunFolderCommand
{
    /// <summary>
    ///     Gets or sets the folder.
    /// </summary>
    [Value(0, MetaName = "folder", Required = true, HelpText = "The folder path.")]
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the new path.
    /// </summary>
    [Value(1, MetaName = "new-folder", Required = true, HelpText = "Its new path, e.g. billing/2026.")]
    public string NewFolder { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.RequireRepo();
        this.WriteFolders(repo.Id, this.Engine.RenameFolder(repo.Id, this.Folder, this.NewFolder));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Removes a folder of the repo's runs, moving what's in it up a level.
/// </summary>
[Verb("run-folder-remove", HelpText = "Remove a folder of this repo's runs; its runs and folders move up into the folder it's in. No run is deleted.")]
public class RunFolderRemoveCommand : AbstractRunFolderCommand
{
    /// <summary>
    ///     Gets or sets the folder.
    /// </summary>
    [Value(0, MetaName = "folder", Required = true, HelpText = "The folder path.")]
    public string Folder { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.RequireRepo();
        this.WriteFolders(repo.Id, this.Engine.RemoveFolder(repo.Id, this.Folder));
        return Task.FromResult(ExitSuccess);
    }
}
