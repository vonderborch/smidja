using Afl.Core.Agents;
using Afl.Core.Execution;

namespace Afl.Cli.Output;

/// <summary>
///     An agent as the CLI lists it, with what it can do.
/// </summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Kind">The kind.</param>
/// <param name="BuiltIn">Whether afl defines it.</param>
/// <param name="Command">The command it runs, or its MCP server.</param>
/// <param name="Model">Its default model.</param>
/// <param name="Description">A one-line description.</param>
/// <param name="Resumes">Whether it can continue a step's conversation.</param>
/// <param name="Interactive">Whether interactive steps can use it.</param>
/// <param name="Permissions">Whether it follows a step's permissionMode and allowedTools.</param>
/// <param name="Available">Whether it can run on this machine (its command is installed, its server registered).</param>
/// <param name="Capabilities">What it can do: its declared capabilities and those its kind implies.</param>
/// <param name="McpServers">The MCP servers whose tools it's always given.</param>
public record AgentView(
    string Id,
    string Name,
    string Kind,
    bool BuiltIn,
    string? Command,
    string? Model,
    string? Description,
    bool Resumes,
    bool Interactive,
    bool Permissions,
    bool Available,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> McpServers)
{
    /// <summary>
    ///     Creates the view for an agent.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="store">The agent store, which knows what's installed and the servers.</param>
    /// <returns>The view.</returns>
    public static AgentView From(AgentDefinition agent, AgentStore store)
    {
        AbstractStepExecutor? executor = ExecutorRegistry.Instance.Contains(agent.Kind) ? AgentStore.CreateExecutor(agent) : null;
        return new AgentView(agent.Id, agent.Name, agent.Kind, agent.BuiltIn, agent.Command ?? agent.Server, agent.Model,
            agent.Description, executor?.SupportsResume ?? false, executor?.CanRunInteractive ?? false,
            executor?.SupportsPermissions ?? false, store.IsAvailable(agent),
            executor is null ? agent.Capabilities ?? [] : store.CapabilitiesOf(agent), agent.McpServers ?? []);
    }
}

/// <summary>
///     An agent preset as <c>afl agent detect</c> shows it.
/// </summary>
/// <param name="Agent">The preset.</param>
/// <param name="Installed">Whether its command is on <c>PATH</c>.</param>
/// <param name="Added">Whether an agent with its id is already set up.</param>
public record AgentPresetView(AgentView Agent, bool Installed, bool Added);
