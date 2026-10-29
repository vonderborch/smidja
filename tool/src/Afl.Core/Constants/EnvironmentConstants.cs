namespace Afl.Core.Constants;

/// <summary>
///     Environment variables afl reads.
/// </summary>
public static class EnvironmentConstants
{
    /// <summary>
    ///     Overrides the global store location (default <c>~/.afl</c>).
    /// </summary>
    public const string AflHome = "AFL_HOME";

    /// <summary>
    ///     Overrides the <c>claude</c> executable used for AI steps.
    /// </summary>
    public const string ClaudeExecutable = "AFL_CLAUDE";

    /// <summary>
    ///     Overrides the <c>gh</c> executable afl borrows a GitHub sign-in from.
    /// </summary>
    public const string GhExecutable = "AFL_GH";

    /// <summary>
    ///     Overrides the <c>glab</c> executable afl borrows a GitLab sign-in from.
    /// </summary>
    public const string GlabExecutable = "AFL_GLAB";

    /// <summary>
    ///     The standard GitHub token variables, used for github.com when no token is stored for it.
    /// </summary>
    public static readonly IReadOnlyList<string> GitHubTokens = ["GH_TOKEN", "GITHUB_TOKEN"];

    /// <summary>
    ///     The standard GitLab token variables, used for gitlab.com when no token is stored for it.
    /// </summary>
    public static readonly IReadOnlyList<string> GitLabTokens = ["GITLAB_TOKEN"];

    /// <summary>
    ///     The prefix of the variables that override a git host connection's stored token, e.g.
    ///     <c>AFL_GITHOST_TOKEN_GITHUB</c> for the connection <c>github</c>.
    /// </summary>
    public const string GitHostTokenPrefix = "AFL_GITHOST_TOKEN_";

    /// <summary>
    ///     The prefix of the variables that override a stored Jira token, e.g. <c>AFL_JIRA_TOKEN_WORK</c> for the instance
    ///     <c>work</c>.
    /// </summary>
    public const string JiraTokenPrefix = "AFL_JIRA_TOKEN_";

    /// <summary>
    ///     The prefix of the environment variables that override an MCP server's secrets
    ///     (<c>AFL_MCP_SECRET_&lt;SERVER&gt;_&lt;NAME&gt;</c>).
    /// </summary>
    public const string McpSecretPrefix = "AFL_MCP_SECRET_";
}
