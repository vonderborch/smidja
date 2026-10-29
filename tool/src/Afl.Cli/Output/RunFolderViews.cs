namespace Afl.Cli.Output;

/// <summary>
///     A folder of a repo's runs as the CLI shows it.
/// </summary>
/// <param name="Folder">The folder's path, e.g. <c>billing/q4</c>.</param>
/// <param name="Runs">How many runs are filed in it or the folders in it.</param>
public record RunFolderView(string Folder, int Runs);

/// <summary>
///     A repo's run folders, and the runs a folder command moved.
/// </summary>
/// <param name="Folders">The folders, sorted.</param>
/// <param name="Moved">The ids of the runs the command moved; empty when it moved none.</param>
public record RunFoldersView(IReadOnlyList<RunFolderView> Folders, IReadOnlyList<string> Moved);
