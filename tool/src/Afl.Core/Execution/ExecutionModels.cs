using Afl.Core.Constants;
using Afl.Core.Permissions;
using Afl.Core.Workflows;

namespace Afl.Core.Execution;

/// <summary>
///     What an executor is asked to do for one AI turn.
/// </summary>
public class ExecutionRequest
{
    /// <summary>
    ///     Gets or sets the rendered prompt.
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the directory the AI works in (the repo root).
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the conversation id. A new id starts a conversation; with <see cref="Resume" /> it continues one.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether to continue the conversation <see cref="SessionId" /> rather than start it.
    /// </summary>
    public bool Resume { get; set; }

    /// <summary>
    ///     Gets or sets the Claude Code permission mode.
    /// </summary>
    public string PermissionMode { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets tools the AI may use without asking.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; set; } = [];

    /// <summary>
    ///     Gets or sets the permission rules the agent is refused, whatever else allows them (Claude Code's
    ///     <c>--disallowedTools</c>, which apply even when it bypasses permissions).
    /// </summary>
    public IReadOnlyList<string> DisallowedTools { get; set; } = [];

    /// <summary>
    ///     Gets or sets the model, or null for the default.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    ///     Gets or sets the file raw executor output is appended to, if any.
    /// </summary>
    public string? LogPath { get; set; }

    /// <summary>
    ///     Gets or sets the MCP servers (registry ids) the step gives the agent as extra tools; the agent adds its own.
    /// </summary>
    public IReadOnlyList<string> McpServers { get; set; } = [];

    /// <summary>
    ///     Gets or sets MCP servers given to the agent on top of <see cref="McpServers" /> because the step offers them
    ///     and they're set up; unlike those, they don't stop the agent using the servers its own tool is set up with.
    /// </summary>
    public IReadOnlyList<string> OptionalMcpServers { get; set; } = [];

    /// <summary>
    ///     Gets or sets where an interactive session's MCP client configuration is written (it stays until the step is
    ///     done); headless turns write theirs beside it and delete it when they end. Null uses the temp directory.
    /// </summary>
    public string? McpConfigPath { get; set; }

    /// <summary>
    ///     Gets or sets how a command-line agent's process runs (see <see cref="RunModeRegistry" />): in the background,
    ///     or in a terminal window you can watch. Agents that don't run a command ignore it.
    /// </summary>
    public string RunMode { get; set; } = AgentConstants.RunModeBackground;

    /// <summary>
    ///     Gets or sets the common skills to give the agent; how they reach it is up to the executor
    ///     (<see cref="AbstractStepExecutor.PrepareSkills" />).
    /// </summary>
    public IReadOnlyList<SkillDefinition> Skills { get; set; } = [];

    /// <summary>
    ///     Gets or sets extra directories, outside <see cref="WorkingDirectory" />, the agent may read and write (e.g.
    ///     the run's scratch directory when it works in a worktree).
    /// </summary>
    public IReadOnlyList<string> AdditionalDirectories { get; set; } = [];
}

/// <summary>
///     How an AI turn ended.
/// </summary>
public enum ExecutionStatus
{
    /// <summary>
    ///     The AI finished and produced output.
    /// </summary>
    Completed,

    /// <summary>
    ///     The AI turn failed; see <see cref="ExecutionResult.Error" />.
    /// </summary>
    Failed,

    /// <summary>
    ///     The work was handed to the calling Claude session, which will submit the output later.
    /// </summary>
    AwaitingSession,
}

/// <summary>
///     The outcome of an AI turn.
/// </summary>
/// <param name="Status">How the turn ended.</param>
/// <param name="Output">The AI's final answer, when completed.</param>
/// <param name="SessionId">The conversation id to resume later.</param>
/// <param name="Error">What went wrong, when failed.</param>
/// <param name="Denials">The tool calls the AI was refused; it may not have finished everything.</param>
public record ExecutionResult(
    ExecutionStatus Status,
    string? Output,
    string? SessionId,
    string? Error = null,
    IReadOnlyList<PermissionDenial>? Denials = null);

/// <summary>
///     The kind of a live executor event.
/// </summary>
public enum ExecutorEventKind
{
    /// <summary>
    ///     Something afl is doing, e.g. starting a step.
    /// </summary>
    Status,

    /// <summary>
    ///     Text the AI wrote.
    /// </summary>
    Text,

    /// <summary>
    ///     A tool the AI used.
    /// </summary>
    ToolUse,

    /// <summary>
    ///     A problem.
    /// </summary>
    Error,
}

/// <summary>
///     A live event from a running step, for front-ends to stream.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Text">The text.</param>
public record ExecutorEvent(ExecutorEventKind Kind, string Text);

/// <summary>
///     A command that opens an interactive AI session for a step, for the front-end to run in a terminal.
/// </summary>
public class InteractiveLaunch
{
    /// <summary>
    ///     Gets or sets the executable.
    /// </summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the arguments.
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>
    ///     Gets or sets the working directory.
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets environment variables to set for the command, if any.
    /// </summary>
    public Dictionary<string, string>? Environment { get; set; }
}

/// <summary>
///     A command an agent runs for one headless turn: an <see cref="InteractiveLaunch" /> plus what it reads on stdin.
/// </summary>
public class ProcessLaunch : InteractiveLaunch
{
    /// <summary>
    ///     Gets or sets the text written to the command's standard input, if any.
    /// </summary>
    public string? StandardInput { get; set; }
}
