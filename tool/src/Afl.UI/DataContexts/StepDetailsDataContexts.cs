using System.Collections.ObjectModel;
using System.Text.Json;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Registration;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     Registers a step's details with the <see cref="StepDetailsRegistry" />, by the definition class they edit. Steps
///     whose definition class derives from it use them too, unless that class has details of its own.
/// </summary>
/// <param name="definitionType">The step definition class.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class StepDetailsRegistrationAttribute(Type definitionType) : AbstractRegistrationAttribute(definitionType.FullName!);

/// <summary>
///     Finds the editors for the fields each kind of step has of its own.
/// </summary>
public class StepDetailsRegistry() : TypeRegistry<AbstractStepDetailsDataContext, StepDetailsRegistrationAttribute>("step details")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static StepDetailsRegistry Instance { get; } = new();

    /// <summary>
    ///     Returns the key of the details a step type is edited with: those registered for its definition class, or for
    ///     the nearest class it derives from.
    /// </summary>
    /// <param name="stepType">The step type, e.g. <c>ai</c>.</param>
    /// <returns>The key, or null when the type is unknown or has no fields of its own.</returns>
    public string? KeyFor(string? stepType)
    {
        if (stepType is null || !StepHandlerRegistry.Instance.Contains(stepType))
        {
            return null;
        }

        for (var type = StepHandlerRegistry.Instance.DefinitionTypeOf(stepType); type is not null; type = type.BaseType)
        {
            if (type.FullName is { } key && this.Contains(key))
            {
                return key;
            }
        }

        return null;
    }
}

/// <summary>
///     The part of a step draft that edits the fields its kind of step has of its own, such as an AI step's prompt or a
///     workflow step's child workflow. The step builder shows it with the data template for its class, so a new kind of
///     step brings its own editor by registering a subclass.
/// </summary>
public abstract class AbstractStepDetailsDataContext : ReactiveObject
{
    /// <summary>
    ///     The fields still holding a new step's starting values, and how to clear each.
    /// </summary>
    private readonly Dictionary<string, Action> _placeholders = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractStepDetailsDataContext" /> class.
    /// </summary>
    /// <param name="step">The step the details belong to.</param>
    protected AbstractStepDetailsDataContext(StepDraftDataContext step)
    {
        this.Step = step;
        this.PropertyChanged += (_, e) =>
        {
            if (!this.DerivedProperties.Contains(e.PropertyName))
            {
                this._placeholders.Remove(e.PropertyName!);
                this.RaiseEdited();
            }
        };
    }

    /// <summary>
    ///     Raised when any field changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets the step the details belong to.
    /// </summary>
    public StepDraftDataContext Step { get; }

    /// <summary>
    ///     Gets whether the step's agent is saved: the step names the agent that does its work.
    /// </summary>
    public virtual bool SavesAgent => false;

    /// <summary>
    ///     Gets whether the step takes parameter values of its own (beyond those of a common step it's based on).
    /// </summary>
    public virtual bool TakesParameters => false;

    /// <summary>
    ///     Gets whether the step can say where the work it starts happens, even when it doesn't split the run.
    /// </summary>
    public virtual bool UsesWorkspace => false;

    /// <summary>
    ///     Gets the parameters the step's values are for, when it <see cref="TakesParameters" />.
    /// </summary>
    public virtual IReadOnlyList<ParameterDefinition>? Parameters => null;

    /// <summary>
    ///     Gets what the step's parameter values are for, or null for the usual line.
    /// </summary>
    public virtual string? WithHelp => null;

    /// <summary>
    ///     Gets a few words saying what the step does, shown after its type in the step list and on the graph, or null.
    /// </summary>
    public virtual string? Summary => null;

    /// <summary>
    ///     Gets the badges shown on the step's node.
    /// </summary>
    public virtual IEnumerable<string> Badges => [];

    /// <summary>
    ///     Gets the workflows the step refers to, kept in the workflow pick list even if they aren't defined here.
    /// </summary>
    public virtual IEnumerable<string?> WorkflowsUsed => [];

    /// <summary>
    ///     Gets the properties worked out from others (or that are pick lists), so changing them isn't an edit.
    /// </summary>
    protected virtual IReadOnlyCollection<string> DerivedProperties => [];

    /// <summary>
    ///     Gives the details the defined agents.
    /// </summary>
    /// <param name="ids">The agent ids.</param>
    public virtual void SetAgents(IReadOnlyList<string> ids)
    {
    }

    /// <summary>
    ///     Gives the details the capabilities and MCP servers there are.
    /// </summary>
    /// <param name="capabilities">The capability ids.</param>
    /// <param name="servers">The MCP server ids.</param>
    public virtual void SetCatalog(IReadOnlyList<string> capabilities, IReadOnlyList<string> servers)
    {
    }

    /// <summary>
    ///     Gives the details the common skills there are.
    /// </summary>
    /// <param name="skills">The skill ids.</param>
    public virtual void SetSkills(IReadOnlyList<string> skills)
    {
    }

    /// <summary>
    ///     Called when something about the step the details may depend on changed: what it's based on, its id, or the
    ///     workflow's steps.
    /// </summary>
    public virtual void OnStepChanged()
    {
    }

    /// <summary>
    ///     Marks the fields holding a new step's starting values, which <see cref="ClearPlaceholders" /> clears.
    /// </summary>
    public virtual void MarkAsNew()
    {
    }

    /// <summary>
    ///     Clears the fields still holding a new step's starting values.
    /// </summary>
    public void ClearPlaceholders()
    {
        var clears = this._placeholders.Values.ToList();
        this._placeholders.Clear();
        foreach (var clear in clears)
        {
            clear();
        }
    }

    /// <summary>
    ///     Writes the details onto a step being built.
    /// </summary>
    /// <param name="step">The step, created for the draft's type.</param>
    public abstract void Write(AbstractStepDefinition step);

    /// <summary>
    ///     Returns null for blank text, and the text otherwise.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text, or null.</returns>
    internal static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    ///     Splits a comma-separated list, or returns null when it's empty.
    /// </summary>
    /// <param name="value">The list.</param>
    /// <param name="separators">What separates the entries.</param>
    /// <returns>The entries, or null.</returns>
    protected static List<string>? SplitList(string? value, params char[] separators)
    {
        var entries = (value ?? string.Empty).Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    ///     Marks a field as holding a new step's starting value.
    /// </summary>
    /// <param name="name">The property.</param>
    /// <param name="clear">Clears it.</param>
    protected void Placeholder(string name, Action clear)
    {
        this._placeholders[name] = clear;
    }

    /// <summary>
    ///     Raises <see cref="Edited" />.
    /// </summary>
    protected void RaiseEdited()
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged" /> for several properties.
    /// </summary>
    /// <param name="names">The property names.</param>
    protected void RaisePropertiesChanged(params string[] names)
    {
        foreach (var name in names)
        {
            this.RaisePropertyChanged(name);
        }
    }
}

/// <summary>
///     Step details for one definition class.
/// </summary>
/// <typeparam name="TDefinition">The definition class the details edit.</typeparam>
public abstract class AbstractStepDetailsDataContext<TDefinition> : AbstractStepDetailsDataContext
    where TDefinition : AbstractStepDefinition
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractStepDetailsDataContext{TDefinition}" /> class.
    /// </summary>
    /// <param name="step">The step the details belong to.</param>
    protected AbstractStepDetailsDataContext(StepDraftDataContext step)
        : base(step)
    {
    }

    /// <inheritdoc />
    public sealed override void Write(AbstractStepDefinition step)
    {
        if (step is TDefinition definition)
        {
            this.Write(definition);
        }
    }

    /// <summary>
    ///     Writes the details onto a step being built.
    /// </summary>
    /// <param name="step">The step.</param>
    protected abstract void Write(TDefinition step);

    /// <summary>
    ///     Returns the step the draft was made from as <typeparamref name="TDefinition" />, so the details can read their
    ///     fields: the step itself when it is one, a re-read copy for a step based on a common step (whose fields may be
    ///     for another kind, kept with <see cref="AbstractStepDefinition.OtherFields" />), else null.
    /// </summary>
    /// <param name="source">The step the draft was made from.</param>
    /// <param name="stepType">The draft's step type, which picks the class a copy is read as.</param>
    /// <returns>The step, or null.</returns>
    protected static TDefinition? ViewOf(AbstractStepDefinition source, string stepType)
    {
        if (source is TDefinition definition)
        {
            return definition;
        }

        var type = StepHandlerRegistry.Instance.Contains(stepType) ? StepHandlerRegistry.Instance.DefinitionTypeOf(stepType) : typeof(TDefinition);
        if (source.Use is null || type.IsAbstract || !typeof(TDefinition).IsAssignableFrom(type))
        {
            return null;
        }

        return JsonSerializer.SerializeToNode(source, JsonConstants.Options)!.Deserialize(type, JsonConstants.Options) as TDefinition;
    }
}

/// <summary>
///     The fields of a step an agent works on (<c>ai</c> and <c>interactive</c>): its prompt, which agents do it and
///     how they're chosen, and what they're given.
/// </summary>
[StepDetailsRegistration(typeof(AbstractAgentStepDefinition))]
public class AgentStepDetailsDataContext : AbstractStepDetailsDataContext<AbstractAgentStepDefinition>
{
    /// <summary>
    ///     The properties that are worked out from others (or are pick lists), so changing them isn't an edit.
    /// </summary>
    private static readonly string[] Derived =
    [
        nameof(ShowsAsk), nameof(MergeAgentChoices), nameof(ShowsOneAgent), nameof(ShowsSeveralAgents), nameof(ShowsMerge),
        nameof(AgentHeader), nameof(ShowsSelect), nameof(ShowsDelegate), nameof(ShowsDelegateCapabilities), nameof(AgentHelp),
        nameof(ShowsCreateTickets), nameof(PromptPlaceholder),
    ];

    /// <summary>
    ///     What's ticked in each of the capability, server and skill lists, kept while the lists are rebuilt.
    /// </summary>
    private readonly Dictionary<ObservableCollection<SelectableItem<string>>, List<string>> _picked = [];

    /// <summary>
    ///     The agents picked for a step several agents do, kept while the agent list is rebuilt.
    /// </summary>
    private List<string> _pickedAgents;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AgentStepDetailsDataContext" /> class.
    /// </summary>
    /// <param name="step">The step the details belong to.</param>
    /// <param name="source">The step the draft was made from.</param>
    public AgentStepDetailsDataContext(StepDraftDataContext step, AbstractStepDefinition source)
        : base(step)
    {
        var agentStep = ViewOf(source, step.StepType);
        this.Prompt = agentStep?.Prompt;
        this.PermissionMode = agentStep?.PermissionMode ?? StepDraftDataContext.DefaultChoice;
        this.Ask = agentStep?.Ask == true;
        this.Model = agentStep?.Model;
        this.UsesSeveralAgents = agentStep?.IsMultiAgent == true;
        this._pickedAgents = [.. agentStep?.Agents ?? []];
        this.Combine = agentStep?.Combine ?? StepDraftDataContext.DefaultChoice;
        this.MergeAgent = agentStep?.MergeAgent ?? StepDraftDataContext.DefaultChoice;
        this.MergePrompt = agentStep?.MergePrompt;
        this.Select = agentStep?.Select ?? StepDraftDataContext.DefaultChoice;
        this.Delegate = agentStep?.Delegate == true;
        this._picked[this.CapabilityOptions] = [.. agentStep?.Capabilities ?? []];
        this._picked[this.DelegateCapabilityOptions] = [.. agentStep?.DelegateCapabilities ?? []];
        this._picked[this.ServerOptions] = [.. agentStep?.McpServers ?? []];
        this._picked[this.OptionalServerOptions] = [.. agentStep?.OptionalMcpServers ?? []];
        this.Skills.Load(agentStep?.Skills);
        this.Skills.Edited += (_, _) => this.RaiseEdited();
        this.SkipSkills.Load(agentStep?.SkipSkills);
        this.SkipSkills.Edited += (_, _) => this.RaiseEdited();
        this.CreateTickets = agentStep?.CreateTickets == true;
        this.AllowedTools = agentStep?.AllowedTools is { Count: > 0 } tools ? string.Join(", ", tools) : null;
    }

    /// <summary>
    ///     Gets the permission modes that can be chosen.
    /// </summary>
    public static IReadOnlyList<string> PermissionModes { get; } =
    [
        StepDraftDataContext.DefaultChoice, StepConstants.DefaultPermissionMode, StepConstants.ReadOnlyPermissionMode,
        StepConstants.PlanPermissionMode,
    ];

    /// <summary>
    ///     Gets the choices for how the results of a step several agents do become its output: every registered
    ///     combine strategy, after <see cref="StepDraftDataContext.DefaultChoice" /> (choose).
    /// </summary>
    public static IReadOnlyList<string> CombineChoices { get; } =
        [StepDraftDataContext.DefaultChoice, .. CombineStrategyRegistry.Instance.Keys.Order()];

    /// <summary>
    ///     Gets the choices for how the agent is picked from those with the step's capabilities: every registered
    ///     selector, after <see cref="StepDraftDataContext.DefaultChoice" /> (the setting in Settings › Execution).
    /// </summary>
    public static IReadOnlyList<string> SelectChoices { get; } =
        [StepDraftDataContext.DefaultChoice, .. AgentSelectorRegistry.Instance.Keys.Order()];

    /// <inheritdoc />
    public override bool SavesAgent => this.ShowsOneAgent;

    /// <summary>
    ///     Gets or sets the prompt: what the AI is asked to do.
    /// </summary>
    public string? Prompt
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the watermark for the prompt: the common step's, which an empty prompt keeps.
    /// </summary>
    public string? PromptPlaceholder =>
        this.Step.IsUse ? (this.Step.CommonStepOf(this.Step.Use)?.Step as AbstractAgentStepDefinition)?.Prompt : null;

    /// <summary>
    ///     Gets or sets the Claude permission mode, or <see cref="StepDraftDataContext.DefaultChoice" />.
    /// </summary>
    public string PermissionMode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the Claude model, or null for the default.
    /// </summary>
    public string? Model
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the allowed tools, comma-separated.
    /// </summary>
    public string? AllowedTools
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether the AI may ask its own questions while it works.
    /// </summary>
    public bool Ask
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.ShowsAsk));
        }
    }

    /// <summary>
    ///     Gets whether to show the "AI may ask" option: the AI can ask on this step, or it's on (which validation
    ///     then reports).
    /// </summary>
    public bool ShowsAsk => this.Ask || (this.Step.Handler?.CanAiAsk(this.Step.ToDefinition()) ?? false);

    /// <summary>
    ///     Gets or sets whether several agents each do the step's AI work, side by side, instead of one.
    /// </summary>
    public bool UsesSeveralAgents
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertiesChanged(nameof(this.ShowsOneAgent), nameof(this.ShowsSeveralAgents), nameof(this.ShowsMerge),
                nameof(this.ShowsAsk));
            this.RaiseAgentModeChanged();
        }
    }

    /// <summary>
    ///     Gets whether to show the single agent picker.
    /// </summary>
    public bool ShowsOneAgent => !this.UsesSeveralAgents;

    /// <summary>
    ///     Gets whether to show the agents list and how their results are combined.
    /// </summary>
    public bool ShowsSeveralAgents => this.UsesSeveralAgents;

    /// <summary>
    ///     Gets every agent, checked when it's one of the step's agents (for a step several agents do).
    /// </summary>
    public ObservableCollection<SelectableItem<string>> AgentOptions { get; } = [];

    /// <summary>
    ///     Gets or sets how the agents' results become the step's output, or
    ///     <see cref="StepDraftDataContext.DefaultChoice" /> (choose).
    /// </summary>
    public string Combine
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.ShowsMerge));
        }
    }

    /// <summary>
    ///     Gets whether the merge settings apply: several agents, combined by a strategy that may merge.
    /// </summary>
    public bool ShowsMerge => this.ShowsSeveralAgents && this.Combine != StepConstants.CombineAll;

    /// <summary>
    ///     Gets or sets the agent that merges the results, or <see cref="StepDraftDataContext.DefaultChoice" /> (the
    ///     step's first agent).
    /// </summary>
    public string MergeAgent
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the agents that can merge: <see cref="StepDraftDataContext.DefaultChoice" />, then every agent.
    /// </summary>
    public IReadOnlyList<string> MergeAgentChoices
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = [StepDraftDataContext.DefaultChoice];

    /// <summary>
    ///     Gets or sets the prompt that merges the results, or null for the step's prompt plus merge instructions.
    /// </summary>
    public string? MergePrompt
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the capabilities, checked when the step needs them (its agent is then chosen by capability).
    /// </summary>
    public ObservableCollection<SelectableItem<string>> CapabilityOptions { get; } = [];

    /// <summary>
    ///     Gets a value indicating whether the step's agent is chosen by capability.
    /// </summary>
    public bool ChoosesByCapability => this.ShowsOneAgent && this.CapabilityOptions.Any(o => o.IsSelected);

    /// <summary>
    ///     Gets the agent picker's header: the agent, or the preferred one when the step is chosen by capability.
    /// </summary>
    public string AgentHeader => this.ChoosesByCapability ? "Preferred agent" : "Agent";

    /// <summary>
    ///     Gets what the agent fields mean for this step.
    /// </summary>
    public string AgentHelp => this.ChoosesByCapability
        ? "Any installed agent with these capabilities can do the step, so it runs on any machine. The preferred agent is used when it qualifies."
        : "Tick capabilities to let any installed agent that has them do the step, instead of naming one.";

    /// <summary>
    ///     Gets a value indicating whether to show how the agent is picked.
    /// </summary>
    public bool ShowsSelect => this.ChoosesByCapability;

    /// <summary>
    ///     Gets or sets how the agent is picked from those with the step's capabilities, or
    ///     <see cref="StepDraftDataContext.DefaultChoice" />.
    /// </summary>
    public string Select
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether the AI may hand sub-tasks to other agents while it works.
    /// </summary>
    public bool Delegate
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.ShowsDelegateCapabilities));
        }
    }

    /// <summary>
    ///     Gets a value indicating whether to show the delegation switch (a step one agent does).
    /// </summary>
    public bool ShowsDelegate => this.ShowsOneAgent;

    /// <summary>
    ///     Gets a value indicating whether to show the capabilities the agents work is handed to must have.
    /// </summary>
    public bool ShowsDelegateCapabilities => this.ShowsDelegate && this.Delegate;

    /// <summary>
    ///     Gets the capabilities, checked when every agent the AI hands work to must have them.
    /// </summary>
    public ObservableCollection<SelectableItem<string>> DelegateCapabilityOptions { get; } = [];

    /// <summary>
    ///     Gets the registered MCP servers, checked when they're given to the step's agent as extra tools.
    /// </summary>
    public ObservableCollection<SelectableItem<string>> ServerOptions { get; } = [];

    /// <summary>
    ///     Gets the registered MCP servers, checked when they're given to the step's agent if it can take them (and
    ///     skipped with a note otherwise).
    /// </summary>
    public ObservableCollection<SelectableItem<string>> OptionalServerOptions { get; } = [];

    /// <summary>
    ///     Gets the common skills given to the step's agent (with any the workflow gives every step), picked from
    ///     every skill there is or typed, such as a <c>{{params.x}}</c> template.
    /// </summary>
    public PickListDataContext Skills { get; } = new();

    /// <summary>
    ///     Gets the workflow's common skills this step doesn't get, picked from every skill there is or typed, such as a
    ///     <c>{{params.x}}</c> template.
    /// </summary>
    public PickListDataContext SkipSkills { get; } = new();

    /// <summary>
    ///     Gets or sets whether the AI may file tickets while it works.
    /// </summary>
    public bool CreateTickets
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets a value indicating whether to show the ticket-filing switch (a step one agent does).
    /// </summary>
    public bool ShowsCreateTickets => this.ShowsDelegate;

    /// <inheritdoc />
    protected override IReadOnlyCollection<string> DerivedProperties => Derived;

    /// <summary>
    ///     Rebuilds the agent check boxes and merge choices from the defined agents, keeping what's picked (and any
    ///     picked agent that isn't defined here, so opening a step never loses it).
    /// </summary>
    /// <param name="ids">The defined agent ids.</param>
    public override void SetAgents(IReadOnlyList<string> ids)
    {
        if (this.AgentOptions.Count > 0)
        {
            this._pickedAgents = this.AgentOptions.Where(o => o.IsSelected).Select(o => o.Value).ToList();
        }

        foreach (var option in this.AgentOptions)
        {
            option.PropertyChanged -= this.OnAgentOptionChanged;
        }

        this.AgentOptions.Clear();
        foreach (var id in ids.Union(this._pickedAgents))
        {
            var option = new SelectableItem<string>(id, id, this._pickedAgents.Contains(id));
            option.PropertyChanged += this.OnAgentOptionChanged;
            this.AgentOptions.Add(option);
        }

        var mergeAgent = this.MergeAgent;
        var merge = ids.Union(this._pickedAgents).ToList();
        this.MergeAgentChoices =
        [
            StepDraftDataContext.DefaultChoice, .. merge,
            .. merge.Contains(mergeAgent) || mergeAgent == StepDraftDataContext.DefaultChoice ? [] : new[] { mergeAgent },
        ];
        this.MergeAgent = mergeAgent;
    }

    /// <summary>
    ///     Sets the capabilities and MCP servers the step can pick from, keeping what's ticked (and anything ticked
    ///     that isn't defined here, so opening a step never loses it).
    /// </summary>
    /// <param name="capabilities">The capability ids.</param>
    /// <param name="servers">The MCP server ids.</param>
    public override void SetCatalog(IReadOnlyList<string> capabilities, IReadOnlyList<string> servers)
    {
        this.RebuildOptions(this.CapabilityOptions, capabilities);
        this.RebuildOptions(this.DelegateCapabilityOptions, capabilities);
        this.RebuildOptions(this.ServerOptions, servers);
        this.RebuildOptions(this.OptionalServerOptions, servers);
        this.RaiseAgentModeChanged();
    }

    /// <summary>
    ///     Sets the common skills the step can pick from, keeping what's picked (and anything picked that isn't defined
    ///     here, such as a <c>{{params.x}}</c> template, so opening a step never loses it).
    /// </summary>
    /// <param name="skills">The skill ids.</param>
    public override void SetSkills(IReadOnlyList<string> skills)
    {
        this.Skills.SetChoices(skills);
        this.SkipSkills.SetChoices(skills);
    }

    /// <inheritdoc />
    public override void OnStepChanged()
    {
        this.RaisePropertiesChanged(nameof(this.ShowsAsk), nameof(this.PromptPlaceholder));
        this.RaiseAgentModeChanged();
    }

    /// <inheritdoc />
    public override void MarkAsNew()
    {
        this.Placeholder(nameof(this.Prompt), () => this.Prompt = null);
        this.Placeholder(nameof(this.PermissionMode), () => this.PermissionMode = StepDraftDataContext.DefaultChoice);
    }

    /// <inheritdoc />
    protected override void Write(AbstractAgentStepDefinition step)
    {
        var several = this.UsesSeveralAgents;
        var one = !several;
        var agents = this.AgentOptions.Where(o => o.IsSelected).Select(o => o.Value).ToList();
        step.Ask = this.Ask ? true : null;
        step.Prompt = NullIfBlank(this.Prompt);
        step.PermissionMode = this.PermissionMode != StepDraftDataContext.DefaultChoice ? this.PermissionMode : null;
        step.Model = NullIfBlank(this.Model)?.Trim();
        step.Agents = several && agents.Count > 0 ? agents : null;
        step.Combine = several && this.Combine != StepDraftDataContext.DefaultChoice ? this.Combine : null;
        step.MergeAgent = several && this.MergeAgent != StepDraftDataContext.DefaultChoice ? this.MergeAgent : null;
        step.MergePrompt = several ? NullIfBlank(this.MergePrompt) : null;
        step.Capabilities = one ? Picked(this.CapabilityOptions) : null;
        step.Select = one && this.ChoosesByCapability && this.Select != StepDraftDataContext.DefaultChoice ? this.Select : null;
        step.Delegate = one && this.Delegate ? true : null;
        step.DelegateCapabilities = one && this.Delegate ? Picked(this.DelegateCapabilityOptions) : null;
        step.McpServers = Picked(this.ServerOptions);
        step.OptionalMcpServers = Picked(this.OptionalServerOptions);
        step.CreateTickets = one && this.CreateTickets ? true : null;
        step.Skills = this.Skills.ToList();
        step.SkipSkills = this.SkipSkills.ToList();
        step.AllowedTools = SplitList(this.AllowedTools, ',');
    }

    /// <summary>
    ///     Returns the ticked ids of a check list, or null if none are.
    /// </summary>
    /// <param name="options">The list.</param>
    /// <returns>The ids, or null.</returns>
    private static List<string>? Picked(IEnumerable<SelectableItem<string>> options)
    {
        var picked = options.Where(o => o.IsSelected).Select(o => o.Value).ToList();
        return picked.Count > 0 ? picked : null;
    }

    /// <summary>
    ///     Rebuilds a capability or server check list, keeping what's ticked.
    /// </summary>
    /// <param name="options">The list.</param>
    /// <param name="ids">The ids on offer.</param>
    private void RebuildOptions(ObservableCollection<SelectableItem<string>> options, IReadOnlyList<string> ids)
    {
        if (options.Count > 0)
        {
            this._picked[options] = options.Where(o => o.IsSelected).Select(o => o.Value).ToList();
        }

        var picked = this._picked.GetValueOrDefault(options) ?? [];
        foreach (var option in options)
        {
            option.PropertyChanged -= this.OnCatalogOptionChanged;
        }

        options.Clear();
        foreach (var id in ids.Union(picked))
        {
            var option = new SelectableItem<string>(id, id, picked.Contains(id));
            option.PropertyChanged += this.OnCatalogOptionChanged;
            options.Add(option);
        }
    }

    /// <summary>
    ///     Passes a ticked or unticked capability or server on as an edit of the step.
    /// </summary>
    /// <param name="sender">The option.</param>
    /// <param name="e">The change.</param>
    private void OnCatalogOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        this.RaiseAgentModeChanged();
        this.RaiseEdited();
    }

    /// <summary>
    ///     Passes a checked or unchecked agent on as an edit of the step.
    /// </summary>
    /// <param name="sender">The agent option.</param>
    /// <param name="e">The change.</param>
    private void OnAgentOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        this.RaiseEdited();
    }

    /// <summary>
    ///     Reports that whether the agent is chosen by capability (and what's shown for it) may have changed.
    /// </summary>
    private void RaiseAgentModeChanged()
    {
        this.RaisePropertiesChanged(nameof(this.AgentHeader), nameof(this.AgentHelp), nameof(this.ShowsSelect),
            nameof(this.ShowsDelegate), nameof(this.ShowsDelegateCapabilities), nameof(this.ShowsCreateTickets));
    }
}

/// <summary>
///     The fields of a step a person does (<c>user</c>): what they need to do.
/// </summary>
[StepDetailsRegistration(typeof(UserStepDefinition))]
public class UserStepDetailsDataContext : AbstractStepDetailsDataContext<UserStepDefinition>
{
    /// <summary>
    ///     The properties that are worked out from others, so changing them isn't an edit.
    /// </summary>
    private static readonly string[] Derived = [nameof(InstructionsPlaceholder)];

    /// <summary>
    ///     Initializes a new instance of the <see cref="UserStepDetailsDataContext" /> class.
    /// </summary>
    /// <param name="step">The step the details belong to.</param>
    /// <param name="source">The step the draft was made from.</param>
    public UserStepDetailsDataContext(StepDraftDataContext step, AbstractStepDefinition source)
        : base(step)
    {
        this.Instructions = ViewOf(source, step.StepType)?.Instructions;
    }

    /// <summary>
    ///     Gets or sets the instructions: what the person needs to do.
    /// </summary>
    public string? Instructions
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the watermark for the instructions: the common step's, which empty instructions keep.
    /// </summary>
    public string? InstructionsPlaceholder =>
        this.Step.IsUse ? (this.Step.CommonStepOf(this.Step.Use)?.Step as UserStepDefinition)?.Instructions : null;

    /// <inheritdoc />
    protected override IReadOnlyCollection<string> DerivedProperties => Derived;

    /// <inheritdoc />
    public override void OnStepChanged()
    {
        this.RaisePropertyChanged(nameof(this.InstructionsPlaceholder));
    }

    /// <inheritdoc />
    public override void MarkAsNew()
    {
        this.Placeholder(nameof(this.Instructions), () => this.Instructions = null);
    }

    /// <inheritdoc />
    protected override void Write(UserStepDefinition step)
    {
        step.Instructions = NullIfBlank(this.Instructions);
    }
}

/// <summary>
///     The fields of a step that starts child runs of another workflow (<c>workflow</c>): which workflow, what each
///     child is for, and how they're run.
/// </summary>
[StepDetailsRegistration(typeof(WorkflowStepDefinition))]
public class WorkflowStepDetailsDataContext : AbstractStepDetailsDataContext<WorkflowStepDefinition>
{
    /// <summary>
    ///     The properties that are worked out from others, so changing them isn't an edit.
    /// </summary>
    private static readonly string[] Derived =
    [
        nameof(ForEachHelp), nameof(UsesForEachStep), nameof(UsesForEachTickets), nameof(UsesForEachItems),
        nameof(UsesForEachPattern), nameof(ForEachStepChoices),
    ];

    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkflowStepDetailsDataContext" /> class.
    /// </summary>
    /// <param name="step">The step the details belong to.</param>
    /// <param name="source">The step the draft was made from.</param>
    public WorkflowStepDetailsDataContext(StepDraftDataContext step, AbstractStepDefinition source)
        : base(step)
    {
        var workflowStep = ViewOf(source, step.StepType);
        this.ChildWorkflow = workflowStep?.Workflow;
        this.ForEachKind = workflowStep?.ForEach?.Kind ?? StepConstants.ChildSourceTicket;
        this.ForEachStep = workflowStep?.ForEach?.Step;
        this.ForEachTickets = workflowStep?.ForEach?.Tickets is { Count: > 0 } tickets ? string.Join(", ", tickets) : null;
        this.ForEachItems = workflowStep?.ForEach?.Items;
        this.ForEachPattern = workflowStep?.ForEach?.Pattern;
        this.ForEachSeparator = workflowStep?.ForEach?.Separator;
        this.Children = workflowStep?.Children ?? StepDraftDataContext.DefaultChoice;
        this.ChildrenTogether = workflowStep?.ChildrenTogether == true;
        this.Reuse = workflowStep?.Reuse == true;
        this.ChildOutput = workflowStep?.ChildOutput;
    }

    /// <summary>
    ///     Gets what the step can start child runs for: every registered child source.
    /// </summary>
    public static IReadOnlyList<string> ForEachChoices { get; } = ChildSourceRegistry.Instance.Keys.Order().ToList();

    /// <summary>
    ///     Gets the choices for whether the step waits for its children.
    /// </summary>
    public static IReadOnlyList<string> ChildrenChoices { get; } =
        [StepDraftDataContext.DefaultChoice, StepConstants.ChildrenWait, StepConstants.ChildrenStart];

    /// <inheritdoc />
    public override bool SavesAgent => true;

    /// <inheritdoc />
    public override bool TakesParameters => true;

    /// <inheritdoc />
    public override bool UsesWorkspace => true;

    /// <inheritdoc />
    public override IReadOnlyList<ParameterDefinition>? Parameters => this.Step.WorkflowOf(this.ChildWorkflow)?.Parameters;

    /// <inheritdoc />
    public override string WithHelp =>
        "Values may be templates, e.g. {{steps.plan.output}}, and {{item}} is the ticket key or line the child run is for. Empty fields keep the defaults.";

    /// <inheritdoc />
    public override string Summary => $"runs {this.ChildWorkflow}";

    /// <inheritdoc />
    public override IEnumerable<string> Badges => this.ChildrenTogether ? ["parallel"] : [];

    /// <inheritdoc />
    public override IEnumerable<string?> WorkflowsUsed => [this.ChildWorkflow];

    /// <summary>
    ///     Gets or sets the workflow the child runs follow.
    /// </summary>
    public string? ChildWorkflow
    {
        get;
        set
        {
            if (value is null && field is not null)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this.Step.RaiseCompositionChanged();
        }
    }

    /// <summary>
    ///     Gets or sets what the step starts child runs for.
    /// </summary>
    public string ForEachKind
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? StepConstants.ChildSourceTicket);
            this.RaisePropertiesChanged(nameof(this.ForEachHelp), nameof(this.UsesForEachStep), nameof(this.UsesForEachTickets),
                nameof(this.UsesForEachItems), nameof(this.UsesForEachPattern));
        }
    } = StepConstants.ChildSourceTicket;

    /// <summary>
    ///     Gets what the chosen child source gives.
    /// </summary>
    public string ForEachHelp =>
        ChildSourceRegistry.Instance.Registrations.FirstOrDefault(r => r.Key == this.ForEachKind)?.Description ?? string.Empty;

    /// <summary>
    ///     Gets whether the chosen child source reads an earlier step.
    /// </summary>
    public bool UsesForEachStep => this.ForEachFields.HasFlag(ChildSourceFields.Step);

    /// <summary>
    ///     Gets whether the chosen child source takes a list of tickets.
    /// </summary>
    public bool UsesForEachTickets => this.ForEachFields.HasFlag(ChildSourceFields.Tickets);

    /// <summary>
    ///     Gets whether the chosen child source takes a list of items.
    /// </summary>
    public bool UsesForEachItems => this.ForEachFields.HasFlag(ChildSourceFields.Items);

    /// <summary>
    ///     Gets whether the chosen child source can pick out and split lines with a pattern and separator.
    /// </summary>
    public bool UsesForEachPattern => this.ForEachFields.HasFlag(ChildSourceFields.Pattern);

    /// <summary>
    ///     Gets or sets the earlier step the child source reads.
    /// </summary>
    public string? ForEachStep
    {
        get;
        set
        {
            if (value is not null || field is null)
            {
                this.RaiseAndSetIfChanged(ref field, value);
            }
        }
    }

    /// <summary>
    ///     Gets the steps the child source can read.
    /// </summary>
    public IReadOnlyList<string> ForEachStepChoices =>
        [.. this.Step.StepIds.Where(id => id != this.Step.Id.Trim()).Union(this.ForEachStep is { Length: > 0 } step ? [step] : [])];

    /// <summary>
    ///     Gets or sets the tickets to start child runs for, comma- or line-separated; templates allowed.
    /// </summary>
    public string? ForEachTickets
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the items to start child runs for, comma- or line-separated; templates allowed.
    /// </summary>
    public string? ForEachItems
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the regex lines must match to give a child (its first group, or the whole match, is the item).
    /// </summary>
    public string? ForEachPattern
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets what splits each kept line into several items, or null to keep it whole.
    /// </summary>
    public string? ForEachSeparator
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether the step waits for its children: <c>wait</c>, <c>start</c> or
    ///     <see cref="StepDraftDataContext.DefaultChoice" />.
    /// </summary>
    public string Children
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value ?? StepDraftDataContext.DefaultChoice);
    } = StepDraftDataContext.DefaultChoice;

    /// <summary>
    ///     Gets or sets whether the children are driven at the same time.
    /// </summary>
    public bool ChildrenTogether
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether an existing run of the workflow on the same ticket is used instead of starting one.
    /// </summary>
    public bool Reuse
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the child step whose output becomes each child's result.
    /// </summary>
    public string? ChildOutput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<string> DerivedProperties => Derived;

    /// <summary>
    ///     Gets the fields the chosen child source uses.
    /// </summary>
    private ChildSourceFields ForEachFields => ChildSourceRegistry.Instance.Contains(this.ForEachKind)
        ? ChildSourceRegistry.Instance.Create(this.ForEachKind).Fields
        : ChildSourceFields.None;

    /// <inheritdoc />
    public override void OnStepChanged()
    {
        this.RaisePropertyChanged(nameof(this.ForEachStepChoices));
    }

    /// <inheritdoc />
    protected override void Write(WorkflowStepDefinition step)
    {
        step.Workflow = NullIfBlank(this.ChildWorkflow);
        step.ForEach = this.ForEachDefinition();
        step.Children = this.Children != StepDraftDataContext.DefaultChoice ? this.Children : null;
        step.ChildrenTogether = this.ChildrenTogether ? true : null;
        step.Reuse = this.Reuse ? true : null;
        step.ChildOutput = NullIfBlank(this.ChildOutput)?.Trim();
    }

    /// <summary>
    ///     Builds the child source the draft describes; null for the default, one child on the run's ticket.
    /// </summary>
    /// <returns>The child source.</returns>
    private ChildSourceDefinition? ForEachDefinition()
    {
        var source = new ChildSourceDefinition
        {
            Kind = this.ForEachKind,
            Step = this.UsesForEachStep ? NullIfBlank(this.ForEachStep) : null,
            Tickets = this.UsesForEachTickets ? SplitList(this.ForEachTickets, ',', '\n') : null,
            Items = this.UsesForEachItems ? NullIfBlank(this.ForEachItems) : null,
            Pattern = this.UsesForEachPattern ? NullIfBlank(this.ForEachPattern) : null,
            Separator = this.UsesForEachPattern && !string.IsNullOrEmpty(this.ForEachSeparator) ? this.ForEachSeparator : null,
        };
        return source is { Kind: StepConstants.ChildSourceTicket, Step: null, Tickets: null } ? null : source;
    }
}
