using Afl.Core.Constants;

namespace Afl.Core.Helpers;

/// <summary>
///     Resolves every location in afl's global store. The CLI, the plugin (through the CLI) and the UI all use the
///     same root, so a repo registered by one front-end is visible to the others.
/// </summary>
/// <param name="root">The store root, normally <c>~/.afl</c>.</param>
public class AflPaths(string root)
{
    /// <summary>
    ///     Gets the store root.
    /// </summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    ///     Gets the registered-repos file.
    /// </summary>
    public string ReposFile => Path.Combine(this.Root, PathConstants.ReposFileName);

    /// <summary>
    ///     Gets the git host connections file.
    /// </summary>
    public string GitHostsFile => Path.Combine(this.Root, PathConstants.GitHostsFileName);

    /// <summary>
    ///     Gets the Jira instances file.
    /// </summary>
    public string JiraFile => Path.Combine(this.Root, PathConstants.JiraFileName);

    /// <summary>
    ///     Gets the agents file.
    /// </summary>
    public string AgentsFile => Path.Combine(this.Root, PathConstants.AgentsFileName);

    /// <summary>
    ///     Gets the global settings file.
    /// </summary>
    public string SettingsFile => Path.Combine(this.Root, PathConstants.SettingsFileName);

    /// <summary>
    ///     Gets the MCP servers file.
    /// </summary>
    public string McpServersFile => Path.Combine(this.Root, PathConstants.McpServersFileName);

    /// <summary>
    ///     Gets the file holding the permission rules saved for every repo.
    /// </summary>
    public string PermissionsFile => Path.Combine(this.Root, PathConstants.PermissionsFileName);

    /// <summary>
    ///     Gets the global workflow library directory.
    /// </summary>
    public string WorkflowsDirectory => Path.Combine(this.Root, PathConstants.WorkflowsDirectoryName);

    /// <summary>
    ///     Gets the global common step library directory.
    /// </summary>
    public string StepsDirectory => Path.Combine(this.Root, PathConstants.StepsDirectoryName);

    /// <summary>
    ///     Gets the global common skill library directory.
    /// </summary>
    public string SkillsDirectory => Path.Combine(this.Root, PathConstants.SkillsDirectoryName);

    /// <summary>
    ///     Gets the directory holding the git worktrees child runs work in: beside the afl home, not in it, since the
    ///     guard rails keep agents out of the home (<c>~/.afl/**</c>) and agents work in these worktrees.
    /// </summary>
    public string WorktreesDirectory => this.Root.TrimEnd(Path.DirectorySeparatorChar) + PathConstants.WorktreesSuffix;

    /// <summary>
    ///     Gets the directory holding cached responses from services such as GitHub.
    /// </summary>
    public string CacheDirectory => Path.Combine(this.Root, PathConstants.CacheDirectoryName);

    /// <summary>
    ///     Gets the directory holding every repo's runs.
    /// </summary>
    public string RunsDirectory => Path.Combine(this.Root, PathConstants.RunsDirectoryName);

    /// <summary>
    ///     Gets the directory holding the desktop app's log files.
    /// </summary>
    public string LogsDirectory => Path.Combine(this.Root, PathConstants.LogsDirectoryName);

    /// <summary>
    ///     Gets the directory holding the AI authoring helper's session records.
    /// </summary>
    public string AuthoringDirectory => Path.Combine(this.Root, PathConstants.AuthoringDirectoryName);

    /// <summary>
    ///     Gets the directory holding each authoring session's work folder (its draft and the files its agent reads):
    ///     beside the afl home, not in it, since the guard rails keep agents out of the home (<c>~/.afl/**</c>).
    /// </summary>
    public string AuthoringWorkspacesDirectory => this.Root.TrimEnd(Path.DirectorySeparatorChar) + PathConstants.AuthoringWorkspacesSuffix;

    /// <summary>
    ///     Creates paths for the store at <c>$AFL_HOME</c>, or <c>~/.afl</c> when it isn't set.
    /// </summary>
    /// <returns>The paths.</returns>
    public static AflPaths FromEnvironment()
    {
        var overridden = Environment.GetEnvironmentVariable(EnvironmentConstants.AflHome);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AflPaths(overridden);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new AflPaths(Path.Combine(home, PathConstants.AflDirectoryName));
    }

    /// <summary>
    ///     Gets the directory holding one repo's runs.
    /// </summary>
    /// <param name="repoId">The repo id.</param>
    /// <returns>The directory.</returns>
    public string RunsDirectoryFor(string repoId)
    {
        return Path.Combine(this.RunsDirectory, repoId);
    }

    /// <summary>
    ///     Gets a repo's own (committable) workflow directory.
    /// </summary>
    /// <param name="repoPath">The repo root.</param>
    /// <returns>The directory.</returns>
    public static string RepoWorkflowsDirectory(string repoPath)
    {
        return Path.Combine(repoPath, PathConstants.AflDirectoryName, PathConstants.WorkflowsDirectoryName);
    }

    /// <summary>
    ///     Gets a repo's own (committable) common step directory.
    /// </summary>
    /// <param name="repoPath">The repo root.</param>
    /// <returns>The directory.</returns>
    public static string RepoStepsDirectory(string repoPath)
    {
        return Path.Combine(repoPath, PathConstants.AflDirectoryName, PathConstants.StepsDirectoryName);
    }

    /// <summary>
    ///     Gets a repo's own (committable) common skill directory.
    /// </summary>
    /// <param name="repoPath">The repo root.</param>
    /// <returns>The directory.</returns>
    public static string RepoSkillsDirectory(string repoPath)
    {
        return Path.Combine(repoPath, PathConstants.AflDirectoryName, PathConstants.SkillsDirectoryName);
    }
}
