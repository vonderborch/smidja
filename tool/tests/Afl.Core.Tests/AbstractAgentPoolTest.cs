using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Mcp;
using Afl.Core.Runs;
using Afl.Core.Workflows;
using Niddy.Core.Security;

namespace Afl.Core.Tests;

/// <summary>
///     Base for tests of steps whose agents come from the installed pool: a scratch agent store whose PATH holds
///     stub commands for <c>main</c>, <c>alpha</c>, <c>beta</c> and <c>gamma</c> (but not <c>missing</c>), and an engine
///     that gives each agent a <see cref="FakeStepExecutor" /> echoing its prompt (the session agent stays real).
/// </summary>
public abstract class AbstractAgentPoolTest : AbstractAflTest
{
    private readonly Dictionary<string, AbstractStepExecutor> _executors = new(StringComparer.OrdinalIgnoreCase);
/// <summary>
    ///     Initializes a new instance of the <see cref="AbstractAgentPoolTest" /> class.
    /// </summary>
    protected AbstractAgentPoolTest()
    {
        var bin = Path.Combine(this.Root, "bin");
        Directory.CreateDirectory(bin);
        this.Agents = new AgentStore(this.Paths, new McpServerStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests")))
        {
            SearchPath = bin,
        };
        this.AddAgent("main", ["plan"]);
        this.AddAgent("alpha", ["review", "edit-code"]);
        this.AddAgent("beta", ["review"]);
        this.AddAgent("gamma", ["edit-code"]);
        this.AddAgent("missing", ["review"], installed: false);
    }

    /// <summary>
    ///     Gets the scratch agent store.
    /// </summary>
    protected AgentStore Agents { get; }

    /// <summary>
    ///     Gets an engine over the scratch store and fake executors.
    /// </summary>
    protected RunEngine Engine => new(new RunStore(this.Paths), this.ExecutorFor) { Agents = this.Agents };

    /// <summary>
    ///     Returns an agent's fake executor.
    /// </summary>
    /// <param name="id">The agent id.</param>
    /// <returns>The executor.</returns>
    protected FakeStepExecutor Fake(string id) => (FakeStepExecutor)this.ExecutorFor(id);

    /// <summary>
    ///     Returns an agent's executor, making it the first time.
    /// </summary>
    /// <param name="id">The agent id.</param>
    /// <returns>The executor.</returns>
    protected AbstractStepExecutor ExecutorFor(string id)
    {
        if (!this._executors.TryGetValue(id, out var executor))
        {
            executor = id == SessionExecutor.ExecutorName
                ? AgentStore.CreateExecutor(this.Agents.GetRequired(id))
                : new FakeStepExecutor { Respond = r => new ExecutionResult(ExecutionStatus.Completed, $"{id} did: {r.Prompt}", r.SessionId) };
            this._executors[id] = executor;
        }

        return executor;
    }

    /// <summary>
    ///     Adds a command-line agent.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="capabilities">Its capabilities.</param>
    /// <param name="installed">Whether its command is on the scratch PATH.</param>
    protected void AddAgent(string id, List<string> capabilities, bool installed = true)
    {
        if (installed)
        {
            File.WriteAllText(Path.Combine(this.Agents.SearchPath!, id + "-cli"), "#!/bin/sh\n");
        }

        this.Agents.Save(new AgentDefinition
        {
            Id = id, Kind = AgentConstants.KindCli, Command = id + "-cli", Description = $"The {id} agent.", Capabilities = capabilities,
        });
    }

    /// <summary>
    ///     Starts a run of the given steps for the sample ticket.
    /// </summary>
    /// <param name="mainAgent">The run's agent.</param>
    /// <param name="steps">The steps.</param>
    /// <returns>The run.</returns>
    protected Run Start(string mainAgent, params AbstractStepDefinition[] steps)
    {
        return this.Engine.Create(this.RegisterRepo(), Workflow(steps), SampleTicket(), mainAgent);
    }
}
