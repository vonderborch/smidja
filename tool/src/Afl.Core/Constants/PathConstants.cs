namespace Afl.Core.Constants;

/// <summary>
///     File and directory names used by afl's global store (<c>~/.afl</c>) and per-repo store (<c>&lt;repo&gt;/.afl</c>).
/// </summary>
public static class PathConstants
{
    /// <summary>
    ///     The default scratch directory at a repo root, where runs keep working documents git never tracks.
    /// </summary>
    public const string DefaultScratchDirectory = ".scratch";

    /// <summary>
    ///     The name of the afl directory, both in the user's home directory and at a repo root.
    /// </summary>
    public const string AflDirectoryName = ".afl";

    /// <summary>
    ///     The file holding the registered repos.
    /// </summary>
    public const string ReposFileName = "repos.json";

    /// <summary>
    ///     The git host connections (GitHub, GitLab) file in the afl home directory.
    /// </summary>
    public const string GitHostsFileName = "githosts.json";

    /// <summary>
    ///     The Jira instances file in the afl home directory.
    /// </summary>
    public const string JiraFileName = "jira.json";

    /// <summary>
    ///     The name of the agents file.
    /// </summary>
    public const string AgentsFileName = "agents.json";

    /// <summary>
    ///     The name of the global settings file.
    /// </summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    ///     The name of the MCP servers file.
    /// </summary>
    public const string McpServersFileName = "mcp-servers.json";

    /// <summary>
    ///     The name of the file holding the permission rules saved for every repo.
    /// </summary>
    public const string PermissionsFileName = "permissions.json";

    /// <summary>
    ///     The directory holding workflow definitions.
    /// </summary>
    public const string WorkflowsDirectoryName = "workflows";

    /// <summary>
    ///     The directory holding run state.
    /// </summary>
    public const string RunsDirectoryName = "runs";

    /// <summary>
    ///     The name of the directory holding the desktop app's log files.
    /// </summary>
    public const string LogsDirectoryName = "logs";

    /// <summary>
    ///     The extension of every JSON document afl writes.
    /// </summary>
    public const string JsonExtension = ".json";

    /// <summary>
    ///     The extension of lock files.
    /// </summary>
    public const string LockExtension = ".lock";

    /// <summary>
    ///     The extension of step log files.
    /// </summary>
    public const string LogExtension = ".log";

    /// <summary>
    ///     The prefix of embedded built-in workflow resources.
    /// </summary>
    public const string BuiltInWorkflowResourcePrefix = "workflows/";

    /// <summary>
    ///     The directory holding common steps, under the global store and under a repo's <c>.afl</c>.
    /// </summary>
    public const string StepsDirectoryName = "steps";

    /// <summary>
    ///     The prefix of embedded built-in common step resources.
    /// </summary>
    public const string BuiltInStepResourcePrefix = "steps/";

    /// <summary>
    ///     The directory holding common skills, under the global store and under a repo's <c>.afl</c>.
    /// </summary>
    public const string SkillsDirectoryName = "skills";

    /// <summary>
    ///     The prefix of embedded built-in common skill resources.
    /// </summary>
    public const string BuiltInSkillResourcePrefix = "skills/";

    /// <summary>
    ///     The directory holding the git worktrees runs work in, under the global store.
    /// </summary>
    public const string WorktreesDirectoryName = "worktrees";

    /// <summary>
    ///     The directory holding cached responses from services such as GitHub, under the global store. Anything in it
    ///     can be deleted.
    /// </summary>
    public const string CacheDirectoryName = "cache";

    /// <summary>
    ///     The file name of a skill's instructions, as agents read them.
    /// </summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>
    ///     The name of the embedded agent presets resource.
    /// </summary>
    public const string AgentPresetsResourceName = "agents/presets.json";

    /// <summary>
    ///     The resource name of the built-in capability catalogue.
    /// </summary>
    public const string CapabilitiesResourceName = "agents/capabilities.json";
}
