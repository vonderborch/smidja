using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     Everything a step handler needs to work on the current step of a run.
/// </summary>
/// <param name="run">The run.</param>
/// <param name="state">The step's state.</param>
/// <param name="executors">Returns the executor for an agent id.</param>
/// <param name="logPath">The step's log file.</param>
/// <param name="save">Saves the run, so front-ends see intermediate states such as <see cref="StepStatus.Running" />.</param>
/// <param name="progress">Receives live events, if set.</param>
public class StepContext(
    Run run,
    RunStep state,
    Func<string, AbstractStepExecutor> executors,
    string logPath,
    Action save,
    IProgress<ExecutorEvent>? progress)
{
    /// <summary>
    ///     Gets the run.
    /// </summary>
    public Run Run { get; } = run;

    /// <summary>
    ///     Gets the step's state.
    /// </summary>
    public RunStep State { get; } = state;

    /// <summary>
    ///     Gets the step's definition.
    /// </summary>
    public AbstractStepDefinition Definition { get; } = run.DefinitionOf(state);

    /// <summary>
    ///     Gets the id of the agent doing this step's AI work: the one chosen for it (<see cref="RunStep.AgentChoice" />),
    ///     else the step's own, else the run's.
    /// </summary>
    public string AgentId => this.State.AgentChoice?.AgentId ?? this.Definition.Agent ?? this.Run.Executor;

    /// <summary>
    ///     Gets the executor doing this step's AI work.
    /// </summary>
    public AbstractStepExecutor Executor => executors(this.AgentId);

    /// <summary>
    ///     Gets the ids of the agents doing this step's AI work: several for a multi-agent step, else
    ///     <see cref="AgentId" />.
    /// </summary>
    public IReadOnlyList<string> AgentIds => this.AgentDefinition is { IsMultiAgent: true } multi ? multi.Agents! : [this.AgentId];

    /// <summary>
    ///     Gets the step's definition when an agent does the step's work; null for other kinds of step.
    /// </summary>
    public AbstractAgentStepDefinition? AgentDefinition => this.Definition as AbstractAgentStepDefinition;

    /// <summary>
    ///     Gets or sets the agents the run can hand work to, for choosing a step's agent by capability; null where no
    ///     choosing is done.
    /// </summary>
    public AgentPool? Pool { get; init; }

    /// <summary>
    ///     Gets where tickets the AI files go (<see cref="AbstractAgentStepDefinition.CreateTickets" />). When null, it can't file any.
    /// </summary>
    public TicketSourceRegistry? Tickets { get; init; }

    /// <summary>
    ///     Gets the repo registry, to look up the run's repo. When null, steps that need the repo record can't run.
    /// </summary>
    public RepoRegistry? Repos { get; init; }

    /// <summary>
    ///     Gets the common skills library, to give agents the step's <see cref="AbstractAgentStepDefinition.Skills" />. When null,
    ///     agents get no skills.
    /// </summary>
    public SkillLibrary? Skills { get; init; }

    /// <summary>
    ///     Gets the saved permission rules (repo, global and the guard rails). When null, agents get only the run's own
    ///     rules and afl's default guard rails.
    /// </summary>
    public PermissionRuleStore? PermissionRules { get; init; }

    /// <summary>
    ///     Gets the run's permission policy.
    /// </summary>
    public AbstractPermissionPolicy Policy => PermissionPolicyRegistry.Instance.Resolve(this.Run.PermissionPolicy);

    /// <summary>
    ///     Gets the engine running the step, for steps that start child runs.
    /// </summary>
    public RunEngine? Engine { get; init; }

    /// <summary>
    ///     Returns the executor for an agent.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The executor.</returns>
    public AbstractStepExecutor ExecutorFor(string agentId)
    {
        return executors(agentId);
    }

    /// <summary>
    ///     Gets the step's log file.
    /// </summary>
    public string LogPath { get; } = logPath;

    /// <summary>
    ///     Gets where an interactive session's MCP client configuration for this step is written, beside its log.
    /// </summary>
    public string McpConfigPath => Path.ChangeExtension(this.LogPath, StepConstants.McpConfigExtension);

    /// <summary>
    ///     Gets the receiver for live events, if any.
    /// </summary>
    public IProgress<ExecutorEvent>? Progress { get; } = progress;

    /// <summary>
    ///     Returns the log file for one part of the step's work, e.g. one agent's, next to the step's own log.
    /// </summary>
    /// <param name="name">The part's name, e.g. the agent id.</param>
    /// <returns>The path, <c>&lt;step&gt;.&lt;name&gt;.log</c>.</returns>
    public string LogPathFor(string name)
    {
        var extension = Path.GetExtension(this.LogPath);
        return Path.ChangeExtension(this.LogPath, null) + "." + name + extension;
    }

    /// <summary>
    ///     Returns a receiver for one agent's live events that labels them with the agent, for when several agents work
    ///     at once.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The receiver, or null when no one is listening.</returns>
    public IProgress<ExecutorEvent>? ProgressFor(string agentId)
    {
        return this.Progress is null
            ? null
            : new MappedProgress<ExecutorEvent>(this.Progress, e => e with { Text = $"[{agentId}] {e.Text}" });
    }

    /// <summary>
    ///     Saves the run.
    /// </summary>
    public void Save()
    {
        save();
    }

    /// <summary>
    ///     Renders a template with the run's variables.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <returns>The rendered text.</returns>
    public string Render(string? template)
    {
        return PromptTemplate.Render(template ?? string.Empty, TemplateVariables.Build(this.Run, this.State));
    }

    /// <summary>
    ///     Builds an executor request for this step.
    /// </summary>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="resume">Whether to continue the step's existing conversation rather than start a new one.</param>
    /// <param name="permissionMode">A permission mode overriding the step's, if set.</param>
    /// <returns>The request.</returns>
    public ExecutionRequest CreateRequest(string prompt, bool resume, string? permissionMode = null)
    {
        if (!resume || this.State.SessionId is null)
        {
            resume = false;
            this.State.SessionId = Guid.NewGuid().ToString();
        }

        var request = this.CreateRequest(this.AgentId, prompt, this.State.SessionId, resume, this.LogPath, permissionMode,
            this.State.OnceRules);

        // Rules allowed once last only for the turn they were allowed for.
        this.State.OnceRules = [];
        return request;
    }

    /// <summary>
    ///     Builds an executor request for one agent of a multi-agent step, in the agent's own conversation and log.
    /// </summary>
    /// <param name="candidate">The agent's result so far, whose <see cref="StepCandidate.SessionId" /> is set.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="resume">Whether to continue the agent's existing conversation rather than start a new one.</param>
    /// <returns>The request.</returns>
    public ExecutionRequest CreateRequest(StepCandidate candidate, string prompt, bool resume)
    {
        if (!resume || candidate.SessionId is null)
        {
            resume = false;
            candidate.SessionId = Guid.NewGuid().ToString();
        }

        return this.CreateRequest(candidate.Agent, prompt, candidate.SessionId, resume, this.LogPathFor(candidate.Agent));
    }

    /// <summary>
    ///     Builds an executor request for an agent working on this step.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="sessionId">The conversation id.</param>
    /// <param name="resume">Whether to continue that conversation.</param>
    /// <param name="logPath">The log file.</param>
    /// <param name="permissionMode">A permission mode overriding the step's, if set.</param>
    /// <param name="onceRules">Rules the user allowed for this turn only.</param>
    /// <returns>The request.</returns>
    public ExecutionRequest CreateRequest(
        string agentId,
        string prompt,
        string sessionId,
        bool resume,
        string logPath,
        string? permissionMode = null,
        IReadOnlyList<string>? onceRules = null)
    {
        var executor = this.ExecutorFor(agentId);
        var request = new ExecutionRequest
        {
            Prompt = prompt,
            WorkingDirectory = this.Run.WorkingPath ?? this.Run.RepoPath,
            SessionId = sessionId,
            Resume = resume,
            PermissionMode = permissionMode ?? this.AgentDefinition?.PermissionMode ?? StepConstants.DefaultPermissionMode,
            AllowedTools = this.AgentDefinition?.AllowedTools ?? [],
            Model = (this.AgentDefinition is { IsMultiAgent: false } single ? single.Model : null) ?? executor.Agent.Model,
            LogPath = logPath,
            McpServers = this.AgentDefinition?.McpServers ?? [],
            OptionalMcpServers = this.OptionalMcpServersFor(agentId),
            McpConfigPath = this.McpConfigPath,
            RunMode = this.Run.Mode ?? AgentConstants.RunModeBackground,
            Skills = this.Skills is null || this.AgentDefinition?.Skills is not { Count: > 0 } skills ? [] : this.Skills.Resolve(skills),
            AdditionalDirectories = this.AdditionalDirectories(),
        };
        this.Policy.ApplyTo(request, this.PermissionRulesFor(onceRules ?? []));
        executor.PrepareSkills(request);
        return request;
    }

    /// <summary>
    ///     Gets the permission rules for a turn of this run: its own, its repo's, the global ones and the guard rails.
    /// </summary>
    /// <param name="onceRules">Rules allowed for this turn only.</param>
    /// <returns>The rules.</returns>
    public PermissionRuleSet PermissionRulesFor(IReadOnlyList<string> onceRules)
    {
        return this.PermissionRules?.For(this.Run.PermissionRules, this.Run.RepoId, onceRules)
               ?? PermissionRuleSet.Merge(PermissionConstants.DefaultGuardRules, this.Run.PermissionRules, [], [], onceRules);
    }

    /// <summary>
    ///     Saves the step's output to its <see cref="AbstractStepDefinition.Artifact" /> file, if it has one, and records where
    ///     in <see cref="RunStep.ArtifactPath" />. A file in the scratch directory goes into the main checkout, where
    ///     every run on the ticket shares it; any other file goes into the run's working copy, so a worktree's work
    ///     carries it. A path that would leave the repo (or go into <c>.git</c>), or a file that can't be written, leaves
    ///     a note instead of failing the step.
    /// </summary>
    /// <param name="output">The output.</param>
    public void SaveArtifact(string? output)
    {
        if (string.IsNullOrWhiteSpace(this.Definition.Artifact) || output is null)
        {
            return;
        }

        var relative = this.Render(this.Definition.Artifact).Trim();
        var checkout = WorkflowLibrary.ResolveArtifactPath(this.Run.RepoPath, relative) is { } scratchPath &&
                       ScratchArea.Contains(this.Run.RepoPath, this.Run.ScratchDirectory, scratchPath)
            ? this.Run.RepoPath
            : this.Run.WorkingPath ?? this.Run.RepoPath;
        if (WorkflowLibrary.ResolveArtifactPath(checkout, relative) is not { } path)
        {
            this.State.Notes.Add($"Didn't save the output to '{relative}': the path must stay inside the repo.");
            return;
        }

        try
        {
            if (ScratchArea.Contains(this.Run.RepoPath, this.Run.ScratchDirectory, path))
            {
                ScratchArea.Ensure(this.Run.RepoPath, this.Run.ScratchDirectory!);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, output.TrimEnd() + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.State.Notes.Add($"Couldn't save the output to '{relative}': {ex.Message}");
            return;
        }

        this.State.ArtifactPath = Path.GetRelativePath(Path.GetFullPath(checkout), path).Replace('\\', '/');
        this.Report($"Saved the output to {this.State.ArtifactPath}.");
    }

    /// <summary>
    ///     Returns the folders outside its working copy an agent needs: the run's scratch directory in the main checkout,
    ///     when the run works in a separate working copy such as a worktree, and the step's own
    ///     <see cref="AbstractAgentStepDefinition.Directories" />.
    /// </summary>
    /// <returns>The folders.</returns>
    private IReadOnlyList<string> AdditionalDirectories()
    {
        var directories = new List<string>();
        if (this.Run.WorkingPath is not null && this.Run.ScratchDirectory is not null &&
            Path.GetFullPath(this.Run.WorkingPath) != Path.GetFullPath(this.Run.RepoPath))
        {
            directories.Add(ScratchArea.Ensure(this.Run.RepoPath, this.Run.ScratchDirectory));
        }

        foreach (var entry in this.AgentDefinition?.Directories ?? [])
        {
            foreach (var line in this.Render(entry).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var path = this.ResolveDirectory(line);
                if (path is null)
                {
                    this.AddNoteOnce(string.Format(StepConstants.DirectorySkippedFormat, line));
                }
                else if (!directories.Contains(path))
                {
                    directories.Add(path);
                }
            }
        }

        return directories;
    }

    /// <summary>
    ///     Resolves one of a step's extra folders: expands a leading <c>~</c> and makes it absolute (relative to the
    ///     repo).
    /// </summary>
    /// <param name="entry">The folder as written.</param>
    /// <returns>The full path, or null when it isn't an existing folder.</returns>
    private string? ResolveDirectory(string entry)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = entry == "~" ? home : entry.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, entry[2..]) : entry;
        try
        {
            var path = Path.GetFullPath(expanded, this.Run.RepoPath);
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Adds a note to the step, unless it already has it.
    /// </summary>
    /// <param name="note">The note.</param>
    private void AddNoteOnce(string note)
    {
        if (!this.State.Notes.Contains(note))
        {
            this.State.Notes.Add(note);
        }
    }

    /// <summary>
    ///     Returns the step's optional MCP servers an agent can be given, noting (once) each one it goes without.
    /// </summary>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The server ids.</returns>
    private IReadOnlyList<string> OptionalMcpServersFor(string agentId)
    {
        var wanted = this.AgentDefinition?.OptionalMcpServers ?? [];
        if (wanted.Count == 0)
        {
            return [];
        }

        var usable = this.ExecutorFor(agentId).UsableMcpServers(wanted);
        foreach (var id in wanted.Except(usable))
        {
            this.AddNoteOnce(string.Format(StepConstants.OptionalMcpServerSkippedFormat, id, agentId));
        }

        return usable;
    }

    /// <summary>
    ///     Reports a status event.
    /// </summary>
    /// <param name="text">The text.</param>
    public void Report(string text)
    {
        this.Progress?.Report(new ExecutorEvent(ExecutorEventKind.Status, text));
    }
}
