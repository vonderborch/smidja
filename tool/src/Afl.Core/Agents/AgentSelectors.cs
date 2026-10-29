using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Agents;

/// <summary>
///     Registers an agent selector with the <see cref="AgentSelectorRegistry" />.
/// </summary>
/// <param name="key">The value of <see cref="AbstractAgentStepDefinition.Select" /> that picks the selector.</param>
/// <param name="displayName">The selector's name, for front-ends.</param>
/// <param name="description">What the selector does, for front-ends.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class AgentSelectorRegistrationAttribute(string key, string displayName, string description) : AbstractRegistrationAttribute(key)
{
    /// <summary>
    ///     Gets the selector's name, for front-ends.
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    ///     Gets what the selector does, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     Finds agent selectors by key.
/// </summary>
public class AgentSelectorRegistry()
    : TypeRegistry<AbstractAgentSelector, AgentSelectorRegistrationAttribute>("agent selector")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static AgentSelectorRegistry Instance { get; } = new();
}

/// <summary>
///     Base for how the agent that does a step is picked from the agents that qualify for it (those with every
///     capability in <see cref="AbstractAgentStepDefinition.Capabilities" />). A new way of choosing is one subclass with an
///     <see cref="AgentSelectorRegistrationAttribute" />; workflows pick it with <see cref="AbstractAgentStepDefinition.Select" />.
/// </summary>
public abstract class AbstractAgentSelector
{
    /// <summary>
    ///     Picks the agent for a step.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="candidates">The agents that qualify, in preference order; never empty.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    ///     The choice (its <see cref="AgentChoice.Candidates" /> and <see cref="AgentChoice.At" /> are filled in by the
    ///     caller), or null to leave the step at <see cref="StepStatus.ChoosingAgent" /> for someone to pick.
    /// </returns>
    public abstract Task<AgentChoice?> SelectAsync(StepContext context, IReadOnlyList<AgentDefinition> candidates, CancellationToken ct);

    /// <summary>
    ///     Chooses the first candidate, saying why.
    /// </summary>
    /// <param name="candidates">The candidates, in preference order.</param>
    /// <param name="reason">Why.</param>
    /// <returns>The choice.</returns>
    protected static AgentChoice First(IReadOnlyList<AgentDefinition> candidates, string reason)
    {
        return new AgentChoice { AgentId = candidates[0].Id, Reason = reason, By = AgentConstants.ChosenBySelector };
    }
}

/// <summary>
///     Takes the first qualifying agent: the global preference order, then the run's agent, then the order agents are
///     listed in.
/// </summary>
[AgentSelectorRegistration(AgentConstants.SelectorFirst, "First in preference order",
    "Take the first installed agent that has the step's capabilities, in the Agents tab's preference order.")]
public class FirstAgentSelector : AbstractAgentSelector
{
    /// <inheritdoc />
    public override Task<AgentChoice?> SelectAsync(StepContext context, IReadOnlyList<AgentDefinition> candidates, CancellationToken ct)
    {
        return Task.FromResult<AgentChoice?>(First(candidates, "first in the preference order"));
    }
}

/// <summary>
///     Asks the run's main agent to choose, with a short read-only prompt describing the step and each candidate. Its
///     answer is read from an <c>afl-agent</c> block; when it can't be read, the first candidate is taken. When the
///     main agent is the calling Claude session, the step waits at <see cref="StepStatus.ChoosingAgent" /> with the
///     prompt for the session to answer with <c>afl run use-agent</c>.
/// </summary>
[AgentSelectorRegistration(AgentConstants.SelectorAi, "Let the main agent choose",
    "The run's main agent reads the step and the qualifying agents' capabilities and picks one, saying why.")]
public class AiAgentSelector : AbstractAgentSelector
{
    /// <inheritdoc />
    public override async Task<AgentChoice?> SelectAsync(StepContext context, IReadOnlyList<AgentDefinition> candidates, CancellationToken ct)
    {
        if (candidates.Count == 1)
        {
            return First(candidates, "the only agent that qualifies");
        }

        var pool = context.Pool ?? throw new InvalidOperationException("Choosing an agent needs the step context's agent pool.");
        var lines = string.Join("\n", candidates.Select(pool.Describe));
        var prompt = string.Format(StepConstants.ChooseAgentPromptFormat, context.Definition.Name, context.Render(context.AgentDefinition?.Prompt), lines);
        var main = context.Run.Executor;
        var request = context.CreateRequest(main, prompt, Guid.NewGuid().ToString(), false,
            context.LogPathFor(StepConstants.ChooseAgentLogName), StepConstants.ReadOnlyPermissionMode);
        context.Report($"Asking {main} which agent should do '{context.Definition.Name}'");
        var result = await context.ExecutorFor(main).ExecuteAsync(request, context.ProgressFor(main), ct);
        if (result.Status == ExecutionStatus.AwaitingSession)
        {
            context.State.PendingPrompt = request.Prompt;
            return null;
        }

        if (result.Status == ExecutionStatus.Failed)
        {
            return First(candidates, $"{main} couldn't choose ({result.Error}); took the first in the preference order");
        }

        var block = FencedBlock.FindLast(result.Output, StepConstants.AgentBlockTag);
        var id = block?.GetString("agent");
        var pick = candidates.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (pick is null)
        {
            var why = block is null ? $"no {StepConstants.AgentBlockTag} block in its answer"
                : block.Error ?? $"'{id}' isn't one of the agents that qualify";
            return First(candidates, $"{main} didn't choose ({why}); took the first in the preference order");
        }

        return new AgentChoice { AgentId = pick.Id, Reason = block!.GetString("reason"), By = AgentConstants.ChosenByMainAgent };
    }
}

/// <summary>
///     The agents a run can hand work to, and which of them qualify for a step: those available on this machine, with
///     every capability asked for, that the step's handler accepts. Agents that only work as the run's main agent (the
///     calling Claude session) qualify only when they are it.
/// </summary>
/// <param name="agents">The agent store.</param>
/// <param name="settings">The global settings (for the preference order and default selector).</param>
/// <param name="executors">Returns the executor for an agent id.</param>
public class AgentPool(AgentStore agents, SettingsStore settings, Func<string, AbstractStepExecutor> executors)
{
    /// <summary>
    ///     The settings, read once.
    /// </summary>
    private readonly Lazy<AflSettings> settingsValue = new(settings.Load);

    /// <summary>
    ///     Gets the agent store.
    /// </summary>
    public AgentStore Agents { get; } = agents;

    /// <summary>
    ///     Gets the global settings.
    /// </summary>
    public AflSettings Settings => this.settingsValue.Value;

    /// <summary>
    ///     Returns every agent in preference order: those in <see cref="AflSettings.AgentPreference" />, then the run's
    ///     main agent, then the rest as listed.
    /// </summary>
    /// <param name="mainAgent">The run's main agent.</param>
    /// <returns>The agents.</returns>
    public IReadOnlyList<AgentDefinition> Ordered(string mainAgent)
    {
        var preference = this.Settings.AgentPreference;
        return this.Agents.All()
            .OrderBy(a =>
            {
                var index = preference.FindIndex(p => string.Equals(p, a.Id, StringComparison.OrdinalIgnoreCase));
                return index >= 0 ? index
                    : string.Equals(a.Id, mainAgent, StringComparison.OrdinalIgnoreCase) ? preference.Count
                    : preference.Count + 1;
            })
            .ToList();
    }

    /// <summary>
    ///     Returns why an agent can't do a step.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="step">The step.</param>
    /// <param name="mainAgent">The run's main agent.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="capabilities">The capabilities needed; the step's when null.</param>
    /// <returns>The reasons; empty when it qualifies.</returns>
    public IReadOnlyList<string> WhyNot(
        AbstractStepHandler handler,
        AbstractStepDefinition step,
        string mainAgent,
        AgentDefinition agent,
        IReadOnlyList<string>? capabilities = null)
    {
        if (!this.Agents.IsAvailable(agent))
        {
            return ["isn't available on this machine"];
        }

        var reasons = new List<string>();
        var has = this.Agents.CapabilitiesOf(agent);
        var missing = (capabilities ?? (step as AbstractAgentStepDefinition)?.Capabilities ?? []).Except(has, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            reasons.Add($"lacks {string.Join(", ", missing)}");
        }

        var executor = executors(agent.Id);
        if (executor.OnlyAsMainAgent && !string.Equals(agent.Id, mainAgent, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("only works as the run's agent");
        }

        reasons.AddRange(handler.CheckAgent(step, executor));
        return reasons;
    }

    /// <summary>
    ///     Returns the agents that qualify for a step, in preference order.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="step">The step.</param>
    /// <param name="mainAgent">The run's main agent.</param>
    /// <param name="capabilities">The capabilities needed; the step's when null.</param>
    /// <returns>The agents.</returns>
    public IReadOnlyList<AgentDefinition> Qualifying(
        AbstractStepHandler handler,
        AbstractStepDefinition step,
        string mainAgent,
        IReadOnlyList<string>? capabilities = null)
    {
        return this.Ordered(mainAgent).Where(a => this.WhyNot(handler, step, mainAgent, a, capabilities).Count == 0).ToList();
    }

    /// <summary>
    ///     Explains why no agent qualifies for a step: what it needs, and what each agent lacks.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="step">The step.</param>
    /// <param name="mainAgent">The run's main agent.</param>
    /// <returns>The explanation.</returns>
    public string DescribeShortfall(AbstractStepHandler handler, AbstractStepDefinition step, string mainAgent)
    {
        var agents = this.Ordered(mainAgent).Select(a => $"{a.Id} {string.Join("; ", this.WhyNot(handler, step, mainAgent, a))}");
        return $"no agent here has {string.Join(", ", (step as AbstractAgentStepDefinition)?.Capabilities ?? [])} (agents: {string.Join(", ", agents)}). " +
               "Add an agent with these capabilities (afl agent add --capability …) or tag an installed one.";
    }

    /// <summary>
    ///     Describes an agent in one line for a prompt: its id, description, capabilities and model.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The line.</returns>
    public string Describe(AgentDefinition agent)
    {
        return string.Format(StepConstants.ChooseAgentCandidateFormat, agent.Id, agent.Description ?? agent.Name ?? agent.Kind,
            string.Join(", ", this.Agents.CapabilitiesOf(agent)), agent.Model ?? "default");
    }

    /// <summary>
    ///     Chooses the agent for a step whose agent is chosen by capability: its preferred <see cref="AbstractStepDefinition.Agent" />
    ///     when that qualifies, else whatever its selector (<see cref="AbstractAgentStepDefinition.Select" />, else the global
    ///     default) picks.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The choice; its <see cref="AgentChoice.AgentId" /> is null when someone has to pick.</returns>
    /// <exception cref="AflException">No agent qualifies.</exception>
    public async Task<AgentChoice> ChooseAsync(AbstractStepHandler handler, StepContext context, CancellationToken ct)
    {
        var step = context.Definition;
        var main = context.Run.Executor;
        var candidates = this.Qualifying(handler, step, main);
        if (candidates.Count == 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, this.DescribeShortfall(handler, step, main));
        }

        AgentChoice? choice = null;
        string? note = null;
        if (step.Agent is { } preferred)
        {
            var match = candidates.FirstOrDefault(a => string.Equals(a.Id, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                note = $"preferred agent '{preferred}' {this.WhyNotPreferred(handler, step, main, preferred)}";
            }
            else
            {
                choice = new AgentChoice { AgentId = match.Id, Reason = "the step's preferred agent", By = AgentConstants.ChosenByPreference };
            }
        }

        if (choice is null)
        {
            var selector = AgentSelectorRegistry.Instance.Create((step as AbstractAgentStepDefinition)?.Select ?? this.Settings.DefaultSelector);
            choice = await selector.SelectAsync(context, candidates, ct) ?? new AgentChoice();
            if (note is not null)
            {
                choice.Reason = choice.Reason is null ? note : $"{note}; {choice.Reason}";
            }
        }

        choice.Candidates = candidates.Select(a => a.Id).ToList();
        choice.At = DateTimeOffset.Now;
        return choice;
    }

    /// <summary>
    ///     Says why a step's preferred agent wasn't used.
    /// </summary>
    /// <param name="handler">The step's handler.</param>
    /// <param name="step">The step.</param>
    /// <param name="mainAgent">The run's main agent.</param>
    /// <param name="preferred">The preferred agent's id.</param>
    /// <returns>The reason.</returns>
    private string WhyNotPreferred(AbstractStepHandler handler, AbstractStepDefinition step, string mainAgent, string preferred)
    {
        return this.Agents.Find(preferred) is { } agent
            ? string.Join("; ", this.WhyNot(handler, step, mainAgent, agent))
            : "isn't set up here";
    }
}
