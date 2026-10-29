using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using Afl.Core.Workspaces;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An editable copy of one workflow step in the builder. Every edit raises <see cref="Edited" /> so the workflow
///     is re-validated live.
/// </summary>
public class StepDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     The permission-mode choice that leaves the field unset, so afl's default applies.
    /// </summary>
    public const string DefaultChoice = "(default)";

    /// <summary>
    ///     The agent choice that leaves the field unset, so the step uses the run's agent.
    /// </summary>
    public const string RunAgentChoice = "(run default)";

    /// <summary>
    ///     The basis choice for a step written out in full in the workflow.
    /// </summary>
    public const string OwnStepChoice = "own step";

    /// <summary>
    ///     The basis choice for a step based on a common step (<see cref="AbstractStepDefinition.Use" />).
    /// </summary>
    public const string UseChoice = "common step";

    /// <summary>
    ///     The basis choice for a step that includes another workflow's steps (<see cref="AbstractStepDefinition.Include" />).
    /// </summary>
    public const string IncludeChoice = "include workflow";

    /// <summary>
    ///     The properties that are worked out from others (or are pick lists), so changing them isn't an edit.
    /// </summary>
    private static readonly string[] DerivedProperties =
    [
        nameof(Title), nameof(AdvanceHelp), nameof(AgentChoices), nameof(Details), nameof(WaitChoice), nameof(FlowSummary),
        nameof(IsOwnStep), nameof(IsUse), nameof(IsInclude), nameof(ShowsStepFields), nameof(ShowsWith), nameof(WithHelp),
        nameof(CommonStepChoices), nameof(WorkflowChoices), nameof(BasisHelp), nameof(NamePlaceholder), nameof(CanSaveAsCommon),
        nameof(IsComposedStep), nameof(ShowsMergeBack), nameof(IncomingIds), nameof(ShowsWaitFor), nameof(WaitGroup),
        nameof(ShowsWaitGroup), nameof(ShowsComposedWith), nameof(ShowsOwnWith), nameof(ReferencedId), nameof(CanViewReferenced),
        nameof(ShowsViewReferenced), nameof(ViewReferencedLabel), nameof(OverridesHeader),
    ];

    /// <summary>
    ///     The fields of a step that place it in the workflow it's in (where it sits and how the run gets to it and goes
    ///     on from it), rather than say what it does, so a step based on a common step doesn't count them as overrides.
    /// </summary>
    private static readonly HashSet<string> PlacementProperties =
    [
        "id", "name", "type", "use", "with", "position", "optional", "follow", "waitFor", "others", "retry", "timeout",
        "workspace", "mergeBack", ArrowConstants.ArrowsPropertyName,
    ];

    /// <summary>
    ///     The step the draft was made from, which the parts for each step type read their fields from.
    /// </summary>
    private readonly AbstractStepDefinition _source;

    /// <summary>
    ///     The parts made for each step type picked so far, by registry key.
    /// </summary>
    private readonly Dictionary<string, AbstractStepDetailsDataContext> _details = [];

    /// <summary>
    ///     The capabilities there are, for parts made later.
    /// </summary>
    private IReadOnlyList<string> _capabilities = [];

    /// <summary>
    ///     The MCP servers there are, for parts made later.
    /// </summary>
    private IReadOnlyList<string> _servers = [];

    /// <summary>
    ///     The common skills there are, for parts made later.
    /// </summary>
    private IReadOnlyList<string> _skills = [];

    /// <summary>
    ///     The common steps a step can be based on.
    /// </summary>
    private IReadOnlyList<CommonStepDefinition> _commonSteps = [];

    /// <summary>
    ///     The workflows a step can include or start child runs of.
    /// </summary>
    private IReadOnlyList<WorkflowDefinition> _workflows = [];

    /// <summary>
    ///     The workflows the step can't include or start, as they'd make a cycle.
    /// </summary>
    private IReadOnlySet<string> _excluded = new HashSet<string>();

    /// <summary>
    ///     The ids of the workflow's steps, for the flow pickers.
    /// </summary>
    private IReadOnlyList<string> _stepIds = [];

    /// <summary>
    ///     The steps whose arrows lead into this one.
    /// </summary>
    private IReadOnlyList<string> _incomingIds = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="StepDraftDataContext" /> class from a step.
    /// </summary>
    /// <param name="step">The step to copy.</param>
    public StepDraftDataContext(AbstractStepDefinition step)
    {
        this._source = step;
        this.With = new WithValuesDataContext(step.With);
        this.With.Edited += this.OnPartEdited;
        this.Id = step.Id;
        this.Name = step.Name;
        this.StepType = step.Type;
        this.CommonStepChoices = step.Use is null ? [] : [step.Use];
        this.WorkflowChoices = new[] { step.Include }.Concat(this.Details?.WorkflowsUsed ?? []).OfType<string>().Distinct().ToList();
        this.Use = step.Use;
        this.Include = step.Include;
        this.Basis = step.Include is not null ? IncludeChoice : step.Use is not null ? UseChoice : OwnStepChoice;
        this.Workspace = step.Workspace ?? DefaultChoice;
        this.MergeBack = step.MergeBack != false;
        this.Advance = step.Advance ?? DefaultChoice;
        this.Agent = step.Agent ?? RunAgentChoice;
        this.AgentChoices = [RunAgentChoice, .. step.Agent is null ? [] : new[] { step.Agent }];
        this.Artifact = step.Artifact;
        this.OutputTemplate = step.OutputTemplate;
        foreach (var input in step.Inputs ?? [])
        {
            this.Inputs.Add(this.Track(new InputDraftDataContext(input)));
        }

        foreach (var arrow in step.Arrows ?? [])
        {
            this.Arrows.Add(this.Track(new ArrowDraftDataContext(arrow)));
        }

        this.FollowsFirst = step.FollowsFirst;
        this.WaitChoice = step.WaitFor switch
        {
            null => ArrowConstants.WaitAll,
            AllWaitRule { Items: null } => ArrowConstants.WaitAll,
            AnyWaitRule { Items: null } => ArrowConstants.WaitAny,
            AbstractWaitGroup group => this.UseWaitGroup(group),
            { } leaf => this.UseWaitGroup(new AllWaitRule { Items = [leaf.Clone()] }),
        };
        this.LetsOthersFinish = step.LetsOthersFinish;
        this.RetryMax = step.Retry?.Max;
        this.RetryDelay = step.Retry?.Delay;
        this.Timeout = step.Timeout;
        this.Position = step.Position;
        this.IsOptional = step.Optional is not null;
        this.OptionalDefault = step.Optional?.Default ?? true;
        this.OptionalHelp = step.Optional?.Help;

        this.PropertyChanged += (_, e) =>
        {
            if (!DerivedProperties.Contains(e.PropertyName))
            {
                if (e.PropertyName == nameof(this.Name))
                {
                    this._nameIsPlaceholder = false;
                }

                this.Edited?.Invoke(this, EventArgs.Empty);
            }
        };
        this.Edited += (_, _) =>
        {
            if (this.IsUse)
            {
                this.RaisePropertyChanged(nameof(this.OverridesHeader));
            }
        };
    }

    /// <summary>
    ///     Whether the step was just added, so its parts mark their starting values as placeholders.
    /// </summary>
    private bool _isNew;

    /// <summary>
    ///     Whether the name still holds a new step's starting value.
    /// </summary>
    private bool _nameIsPlaceholder;

    /// <summary>
    ///     Marks the step's starting values (its name, and fields such as the prompt) as a new step's: switching the
    ///     step to a common step or an include before they're edited clears them, so they don't override it.
    /// </summary>
    public void MarkAsNew()
    {
        this._isNew = true;
        this._nameIsPlaceholder = true;
        foreach (var details in this._details.Values)
        {
            details.MarkAsNew();
        }
    }

    /// <summary>
    ///     Clears the fields still holding a new step's starting values.
    /// </summary>
    private void ClearPlaceholders()
    {
        if (this._nameIsPlaceholder)
        {
            this.Name = string.Empty;
        }

        this._nameIsPlaceholder = false;
        foreach (var details in this._details.Values)
        {
            details.ClearPlaceholders();
        }
    }

    /// <summary>
    ///     Raised when any field changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets the step types that can be chosen: every registered step handler key, e.g. "ai" or "mixed/approve".
    /// </summary>
    public static IReadOnlyList<string> StepTypes { get; } = StepHandlerRegistry.Instance.PublicKeys;

    /// <summary>
    ///     Gets the choices for what happens once the step's work is done.
    /// </summary>
    public static IReadOnlyList<string> AdvanceChoices { get; } =
        [DefaultChoice, StepConstants.AdvanceAuto, StepConstants.AdvanceApprove, StepConstants.AdvanceApproveFinal];

    /// <summary>
    ///     Gets or sets the repo-relative file the step's output is saved to, or null for none.
    /// </summary>
    public string? Artifact
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the template the step's output is rendered from once its work is done (its answers as
    ///     <c>{{inputs.x}}</c>, its own output as <c>{{output}}</c>), or null to keep the output as it is.
    /// </summary>
    public string? OutputTemplate
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Sets the common skills the step can pick from, keeping what's ticked.
    /// </summary>
    /// <param name="skills">The skill ids.</param>
    public void SetSkills(IReadOnlyList<string> skills)
    {
        this._skills = skills;
        foreach (var details in this._details.Values)
        {
            details.SetSkills(skills);
        }
    }

    /// <summary>
    ///     Gets or sets whether whoever starts a run chooses whether to include the step.
    /// </summary>
    public bool IsOptional
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.FlowSummary));
        }
    }

    /// <summary>
    ///     Gets or sets whether an optional step is included when nobody chooses otherwise.
    /// </summary>
    public bool OptionalDefault
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>
    ///     Gets or sets what to tell someone choosing whether to include an optional step.
    /// </summary>
    public string? OptionalHelp
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Sets the capabilities and MCP servers the step can pick from, keeping what's ticked.
    /// </summary>
    /// <param name="capabilities">The capability ids.</param>
    /// <param name="servers">The MCP server ids.</param>
    public void SetCatalog(IReadOnlyList<string> capabilities, IReadOnlyList<string> servers)
    {
        (this._capabilities, this._servers) = (capabilities, servers);
        foreach (var details in this._details.Values)
        {
            details.SetCatalog(capabilities, servers);
        }
    }

    /// <summary>
    ///     Gets or sets the step id.
    /// </summary>
    public string Id
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.Title));
        }
    }

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.Title));
        }
    }

    /// <summary>
    ///     Gets or sets the step type, which picks its handler, e.g. <c>ai</c>, <c>user</c> or <c>workflow</c>, and the
    ///     <see cref="Details" /> that edit its own fields.
    /// </summary>
    public string StepType
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.Details = this.DetailsFor(value);
            this.RaisePropertyChanged(nameof(this.Title));
            this.RaisePropertyChanged(nameof(this.AdvanceHelp));
            this.RaiseCompositionChanged();
        }
    }

    /// <summary>
    ///     Gets the part that edits the fields the step's type has of its own, or null when it has none.
    /// </summary>
    public AbstractStepDetailsDataContext? Details
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets what happens once the step's work is done: <c>auto</c>, <c>approve</c>, or
    ///     <see cref="DefaultChoice" /> for the step type's default.
    /// </summary>
    public string Advance
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.AdvanceHelp));
        }
    }

    /// <summary>
    ///     Gets a line explaining the move-on choice, with the step type's default.
    /// </summary>
    public string AdvanceHelp
    {
        get
        {
            var fallback = this.Handler?.DefaultAdvance ?? StepConstants.AdvanceAuto;
            return $"auto: move on by itself. approve: wait for you to approve it or send it back. Default for this type: {fallback}.";
        }
    }

    /// <summary>
    ///     Gets the questions the user answers: before the step's work, when letting it through, or when the AI asks.
    /// </summary>
    public ObservableCollection<InputDraftDataContext> Inputs { get; } = [];

    /// <summary>
    ///     Gets whether the step can ask inputs after its work: a person finishes it, or it waits for approval.
    /// </summary>
    public bool TakesInputs => this.Handler?.TakesInputs(this.ToDefinition()) ?? false;

    /// <summary>
    ///     Gets the handler for the step type, or null when the type is unknown.
    /// </summary>
    internal AbstractStepHandler? Handler =>
        this.StepType is { } type && StepHandlerRegistry.Instance.Contains(type) ? StepHandlerRegistry.Instance.Create(type) : null;

    /// <summary>
    ///     Gets or sets the id of the agent that does the step's AI work, or <see cref="RunAgentChoice" />.
    /// </summary>
    public string Agent
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the agents that can be picked: <see cref="RunAgentChoice" />, then every agent id. An agent the
    ///     step already names stays in the list even if it isn't defined here, so opening a step never loses it.
    /// </summary>
    public IReadOnlyList<string> AgentChoices
    {
        get;
        set
        {
            var choices = value.Contains(this.Agent) ? value : [.. value, this.Agent];
            var agent = this.Agent;
            this.RaiseAndSetIfChanged(ref field, choices);
            this.Agent = agent;
            var ids = value.Where(c => c != RunAgentChoice).ToList();
            foreach (var details in this._details.Values)
            {
                details.SetAgents(ids);
            }

            this.SetFlowChoices();
        }
    }

    /// <summary>
    ///     Gets a line describing the step's flow, shown on the Flow expander.
    /// </summary>
    public string FlowSummary
    {
        get
        {
            var parts = new List<string>();
            if (this.IsOptional)
            {
                parts.Add("optional");
            }

            var targets = this.Arrows.Select(a => a.To).Distinct().ToList();
            parts.Add(targets.Count switch
            {
                0 => "ends the run",
                1 => $"to {targets[0]}",
                _ when this.FollowsFirst => $"to the first of {string.Join(", ", targets)}",
                _ => $"to {string.Join(" + ", targets)}",
            });
            return $"Flow: {string.Join(", ", parts)}";
        }
    }

    /// <summary>
    ///     Gets or sets the ids of the workflow's steps, for the go-to and loop pickers.
    /// </summary>
    public IReadOnlyList<string> StepIds
    {
        get => this._stepIds;
        set
        {
            if (this._stepIds.SequenceEqual(value))
            {
                return;
            }

            this._stepIds = value;
            this.SetFlowChoices();
            this.RaiseDetailsChanged();
        }
    }

    /// <summary>
    ///     Gets the step's arrows: where the run goes once the step ends, in order.
    /// </summary>
    public ObservableCollection<ArrowDraftDataContext> Arrows { get; } = [];

    /// <summary>
    ///     Gets the ids of the steps the step's arrows go to, in order, without <c>end</c>.
    /// </summary>
    public IReadOnlyList<string> Targets => [.. this.Arrows.Select(a => a.To).Where(t => t != ArrowConstants.End).Distinct()];

    /// <summary>
    ///     Gets whether the step can start several paths at once: it follows every arrow that applies and has
    ///     several to other steps.
    /// </summary>
    public bool IsFork => !this.FollowsFirst && this.Targets.Count(t => t != this.Id.Trim()) > 1;

    /// <summary>
    ///     Gets or sets whether the step follows only the first of its arrows that holds (<c>follow: first</c>), rather
    ///     than every one that applies.
    /// </summary>
    public bool FollowsFirst
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsFork));
            this.RaisePropertyChanged(nameof(this.FlowSummary));
        }
    }

    /// <summary>
    ///     The wait choice for a step whose <c>waitFor</c> is a group the shorthand can't say.
    /// </summary>
    public const string WaitCustom = "custom";

    /// <summary>
    ///     Gets the ways a step that arrows from several steps point at can wait for them.
    /// </summary>
    public static IReadOnlyList<string> WaitChoices { get; } = [ArrowConstants.WaitAll, ArrowConstants.WaitAny, WaitCustom];

    /// <summary>
    ///     Gets or sets what the step waits for when arrows from several steps point at it: <c>all</c>, <c>any</c>,
    ///     or <see cref="WaitCustom" /> for the group it was read with.
    /// </summary>
    public string WaitChoice
    {
        get;
        set
        {
            if (value is null || value == field)
            {
                return;
            }

            if (value == WaitCustom && this.WaitGroup is null)
            {
                AbstractWaitGroup shorthand = field == ArrowConstants.WaitAny ? new AnyWaitRule() : new AllWaitRule();
                this.UseWaitGroup(shorthand);
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.ShowsWaitGroup));
            this.RaisePropertyChanged(nameof(this.ShowsWaitFor));
            this.Edited?.Invoke(this, EventArgs.Empty);
        }
    } = ArrowConstants.WaitAll;

    /// <summary>
    ///     Gets the custom group the step waits for (<see cref="WaitCustom" />), or null until one is chosen. It's kept
    ///     while another choice is picked, so switching back doesn't lose it.
    /// </summary>
    public WaitGroupDraftDataContext? WaitGroup
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the custom group editor is shown.
    /// </summary>
    public bool ShowsWaitGroup => this.WaitChoice == WaitCustom && this.WaitGroup is not null;

    /// <summary>
    ///     Gets or sets the steps whose arrows lead into this one, for the wait group's pickers; set by the builder.
    /// </summary>
    public IReadOnlyList<string> IncomingIds
    {
        get => this._incomingIds;
        set
        {
            if (this._incomingIds.SequenceEqual(value))
            {
                return;
            }

            this._incomingIds = value;
            this.WaitGroup?.SetChoices(value);
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(this.ShowsWaitFor));
        }
    }

    /// <summary>
    ///     Gets whether arrows from several steps lead here, so what it waits for matters (or a custom wait is set).
    /// </summary>
    public bool ShowsWaitFor => this._incomingIds.Count >= 2 || this.WaitChoice != ArrowConstants.WaitAll;

    /// <summary>
    ///     Gets or sets whether the paths still heading for the step finish once it starts (<c>others: finish</c>),
    ///     rather than being cancelled.
    /// </summary>
    public bool LetsOthersFinish
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets how many more times the step is run when it fails, or null not to retry it.
    /// </summary>
    public decimal? RetryMax
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets how long to wait before retrying, e.g. <c>30s</c>.
    /// </summary>
    public string? RetryDelay
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets how long the step may run before it's cancelled and fails, e.g. <c>30m</c>.
    /// </summary>
    public string? Timeout
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets where the step was moved to on the canvas, or null to lay it out with the rest.
    /// </summary>
    public StepPosition? Position
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Takes a removed step out of the step's arrows, pointing them at where it went instead.
    /// </summary>
    /// <param name="removedId">The removed step's id.</param>
    /// <param name="onTo">Where the removed step went, when that was one step; otherwise null.</param>
    public void RemoveTarget(string removedId, string? onTo)
    {
        foreach (var arrow in this.Arrows.Where(a => a.To == removedId).ToList())
        {
            if (onTo is null || onTo == this.Id.Trim() || this.Arrows.Any(a => a.To == onTo && a.On == arrow.On))
            {
                this.RemoveArrow(arrow);
            }
            else
            {
                arrow.To = onTo;
            }
        }
    }

    /// <summary>
    ///     Replaces a step id in the step's arrows, after the step it names was renamed.
    /// </summary>
    /// <param name="oldId">The old id.</param>
    /// <param name="newId">The new id.</param>
    public void RenameTarget(string oldId, string newId)
    {
        foreach (var arrow in this.Arrows.Where(a => a.To == oldId))
        {
            arrow.To = newId;
        }

        this.WaitGroup?.RenameStep(oldId, newId);
    }

    /// <summary>
    ///     Adds an arrow, unless the step already has one to the same place on the same outcome.
    /// </summary>
    /// <param name="to">Where it goes: a step id, or <c>end</c>.</param>
    /// <param name="on">The outcome it fires on.</param>
    /// <returns>The new arrow, or null if there already was one.</returns>
    public ArrowDraftDataContext? AddArrow(string to, string on = ArrowConstants.OnSuccess)
    {
        return this.Arrows.Any(a => a.To == to && a.On == on) ? null : this.AddArrow(new ArrowDefinition { To = to, On = on });
    }

    /// <summary>
    ///     Adds an arrow as it's defined, e.g. one moved from another step.
    /// </summary>
    /// <param name="definition">The arrow.</param>
    /// <returns>The new arrow.</returns>
    public ArrowDraftDataContext AddArrow(ArrowDefinition definition)
    {
        var arrow = this.Track(new ArrowDraftDataContext(definition));
        arrow.SetChoices(this._stepIds, this.AgentIds);
        this.Arrows.Add(arrow);
        this.OnArrowsChanged();
        return arrow;
    }

    /// <summary>
    ///     Removes an arrow.
    /// </summary>
    /// <param name="arrow">The arrow.</param>
    public void RemoveArrow(ArrowDraftDataContext arrow)
    {
        if (this.Arrows.Remove(arrow))
        {
            arrow.Edited -= this.OnArrowEdited;
            this.OnArrowsChanged();
        }
    }

    /// <summary>
    ///     Puts another arrow in an arrow's place, keeping its place in the order.
    /// </summary>
    /// <param name="arrow">The arrow to replace.</param>
    /// <param name="definition">The arrow to put there.</param>
    /// <returns>The new arrow.</returns>
    public ArrowDraftDataContext ReplaceArrow(ArrowDraftDataContext arrow, ArrowDefinition definition)
    {
        var index = this.Arrows.IndexOf(arrow);
        var replacement = this.Track(new ArrowDraftDataContext(definition));
        replacement.SetChoices(this._stepIds, this.AgentIds);
        if (index < 0)
        {
            this.Arrows.Add(replacement);
        }
        else
        {
            arrow.Edited -= this.OnArrowEdited;
            this.Arrows[index] = replacement;
        }

        this.OnArrowsChanged();
        return replacement;
    }

    /// <summary>
    ///     Moves an arrow up or down the list, which is the order <c>follow: first</c> checks them in.
    /// </summary>
    /// <param name="arrow">The arrow.</param>
    /// <param name="by">How far: -1 is up one.</param>
    public void MoveArrow(ArrowDraftDataContext arrow, int by)
    {
        var from = this.Arrows.IndexOf(arrow);
        var to = from + by;
        if (from >= 0 && to >= 0 && to < this.Arrows.Count)
        {
            this.Arrows.Move(from, to);
            this.OnArrowsChanged();
        }
    }

    /// <summary>
    ///     Gets the line shown in the step list.
    /// </summary>
    public string Title
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(this.Name) ? this.NamePlaceholder ?? this.Id : this.Name;
            if (this.IsInclude)
            {
                var count = this.WorkflowOf(this.Include)?.Steps.Count;
                return $"{(string.IsNullOrWhiteSpace(this.Name) ? this.Id : this.Name)} · includes {this.Include}"
                       + (count is null ? string.Empty : count == 1 ? " (1 step)" : $" ({count} steps)");
            }

            if (this.IsUse)
            {
                return $"{name} · use {this.Use}";
            }

            return $"{name} · {this.StepType}" + (this.Details?.Summary is { } summary ? $" · {summary}" : string.Empty);
        }
    }

    /// <summary>
    ///     Gets the choices for what the step is: written out here, based on a common step, or another workflow's steps.
    /// </summary>
    public static IReadOnlyList<string> BasisChoices { get; } = [OwnStepChoice, UseChoice, IncludeChoice];

    /// <summary>
    ///     Gets or sets what the step is: one of <see cref="BasisChoices" />.
    /// </summary>
    public string Basis
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? OwnStepChoice);
            if (!this.IsOwnStep)
            {
                this.ClearPlaceholders();
            }

            if (this.IsUse && string.IsNullOrEmpty(this.Use))
            {
                this.Use = this._commonSteps.FirstOrDefault()?.Id;
            }

            if (this.IsInclude && string.IsNullOrEmpty(this.Include))
            {
                this.Include = this._workflows.FirstOrDefault(w => !this._excluded.Contains(w.Id))?.Id;
            }

            this.AdoptCommonType();
            this.RaiseCompositionChanged();
        }
    } = OwnStepChoice;

    /// <summary>
    ///     Gets whether the step is written out in full here.
    /// </summary>
    public bool IsOwnStep => this.Basis == OwnStepChoice;

    /// <summary>
    ///     Gets whether the step is based on a common step.
    /// </summary>
    public bool IsUse => this.Basis == UseChoice;

    /// <summary>
    ///     Gets whether the step is replaced by another workflow's steps.
    /// </summary>
    public bool IsInclude => this.Basis == IncludeChoice;

    /// <summary>
    ///     Gets whether the step has fields of its own (an include has only its id, name and parameter values).
    /// </summary>
    public bool ShowsStepFields => !this.IsInclude;

    /// <summary>
    ///     Gets whether the step stands for steps written elsewhere (a common step or an included workflow), so it can
    ///     be copied into the workflow.
    /// </summary>
    public bool IsComposedStep => !this.IsOwnStep;

    /// <summary>
    ///     Gets whether the parameter values are shown with the step's reference (a common step or included workflow),
    ///     rather than with the fields of a step written out here.
    /// </summary>
    public bool ShowsComposedWith => !this.IsOwnStep;

    /// <summary>
    ///     Gets whether a step written out here takes parameter values of its own (e.g. a child workflow's).
    /// </summary>
    public bool ShowsOwnWith => this.IsOwnStep && this.Details?.TakesParameters == true;

    /// <summary>
    ///     Gets the header of the fields a step based on a common step can override, with how many it does.
    /// </summary>
    public string OverridesHeader => this.IsUse && this.OverrideCount() is var count and > 0
        ? $"Override the common step ({count} set)"
        : "Override the common step";

    /// <summary>
    ///     Gets the common step or workflow the step refers to, when it exists so it can be opened, or null.
    /// </summary>
    public string? ReferencedId => this.IsUse
        ? this.CommonStepOf(this.Use) is not null ? this.Use : null
        : (this.IsInclude ? [this.Include] : this.Details?.WorkflowsUsed ?? []).FirstOrDefault(w => this.WorkflowOf(w) is not null);

    /// <summary>
    ///     Gets whether what the step refers to is a common step (otherwise it's a workflow).
    /// </summary>
    public bool RefersToCommonStep => this.IsUse;

    /// <summary>
    ///     Gets whether the step refers to a common step or workflow, so the button to open it is shown.
    /// </summary>
    public bool ShowsViewReferenced => this.IsComposedStep || this.Details?.WorkflowsUsed.Any() == true;

    /// <summary>
    ///     Gets whether what the step refers to exists and can be opened.
    /// </summary>
    public bool CanViewReferenced => this.ReferencedId is not null;

    /// <summary>
    ///     Gets the text of the button that opens what the step refers to.
    /// </summary>
    public string ViewReferencedLabel => this.RefersToCommonStep ? "View common step" : "View workflow";

    /// <summary>
    ///     Gets whether the step can be saved as a common step: one written out here.
    /// </summary>
    public bool CanSaveAsCommon => this.IsOwnStep;

    /// <summary>
    ///     Gets what the chosen basis means.
    /// </summary>
    public string BasisHelp => this.Basis switch
    {
        UseChoice => (this.CommonStepOf(this.Use)?.Description is { Length: > 0 } description ? description + " " : string.Empty)
                     + "What it does comes from the common step; here you set where it sits in this workflow. To change what it does for this workflow only, fill in the overrides below.",
        IncludeChoice => "The workflow's steps take this step's place when a run starts, their ids prefixed with this step's id (e.g. "
                         + $"{(this.Id.Length > 0 ? this.Id : "<id>")}-<step>). Later steps can read {{{{steps.{this.Id}.output}}}}, the included workflow's last step.",
        _ => string.Empty,
    };

    /// <summary>
    ///     Gets or sets the common step the step is based on.
    /// </summary>
    public string? Use
    {
        get;
        set
        {
            if (value is null && field is not null && this.IsUse)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this.AdoptCommonType();
            this.RaiseCompositionChanged();
        }
    }

    /// <summary>
    ///     Gets the common steps that can be picked.
    /// </summary>
    public IReadOnlyList<string> CommonStepChoices
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the workflow whose steps take this step's place.
    /// </summary>
    public string? Include
    {
        get;
        set
        {
            if (value is null && field is not null && this.IsInclude)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this.RaiseCompositionChanged();
        }
    }

    /// <summary>
    ///     Gets the workflows that can be included or started as child runs.
    /// </summary>
    public IReadOnlyList<string> WorkflowChoices
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the values for the parameters of the common step, included workflow or child workflow.
    /// </summary>
    public WithValuesDataContext With { get; }

    /// <summary>
    ///     Gets whether the step takes parameter values.
    /// </summary>
    public bool ShowsWith => this.IsUse || this.IsInclude || this.Details?.TakesParameters == true;

    /// <summary>
    ///     Gets what the parameter values are for.
    /// </summary>
    public string WithHelp => !this.With.HasValues
        ? "It has no parameters."
        : !this.IsInclude && this.Details?.WithHelp is { } help
            ? help
            : "Values may be templates, e.g. {{steps.plan.output}}. Empty fields keep the defaults.";

    /// <summary>
    ///     Gets the watermark for the name: the common step's, which an empty name keeps.
    /// </summary>
    public string? NamePlaceholder => this.IsUse ? this.CommonStepOf(this.Use) is { } common
        ? string.IsNullOrWhiteSpace(common.Step.Name) ? common.Name : common.Step.Name
        : null : null;

    /// <summary>
    ///     Gets the choices for where the children work: every registered workspace, after
    ///     <see cref="DefaultChoice" /> (shared).
    /// </summary>
    public static IReadOnlyList<string> WorkspaceChoices { get; } = [DefaultChoice, .. WorkspaceRegistry.Instance.Keys.Order()];

    /// <summary>
    ///     Gets or sets where the children work, or <see cref="DefaultChoice" />.
    /// </summary>
    public string Workspace
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? DefaultChoice);
            this.RaisePropertyChanged(nameof(this.ShowsMergeBack));
        }
    } = DefaultChoice;

    /// <summary>
    ///     Gets whether the chosen workspace gives each child a checkout of its own, so its work can be merged back.
    /// </summary>
    public bool ShowsMergeBack => this.Workspace != DefaultChoice && WorkspaceRegistry.Instance.Contains(this.Workspace) &&
                                  WorkspaceRegistry.Instance.Create(this.Workspace).IsIsolated;

    /// <summary>
    ///     Gets or sets whether a finished child's work is merged back (otherwise kept on its branch).
    /// </summary>
    public bool MergeBack
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>
    ///     Gives the step the common steps and workflows it can refer to, keeping what's picked.
    /// </summary>
    /// <param name="commonSteps">The common steps.</param>
    /// <param name="workflows">The workflows.</param>
    /// <param name="excluded">
    ///     The workflows it can't include or start: the one it's in, and those that lead back to it. One already
    ///     picked stays listed, so validation can say what's wrong.
    /// </param>
    public void SetLibrary(IReadOnlyList<CommonStepDefinition> commonSteps, IReadOnlyList<WorkflowDefinition> workflows, IReadOnlySet<string> excluded)
    {
        this._commonSteps = commonSteps;
        this._workflows = workflows;
        this._excluded = excluded;
        var (use, include) = (this.Use, this.Include);
        this.CommonStepChoices = Choices(commonSteps.Select(s => s.Id), use);
        this.WorkflowChoices = Choices(workflows.Select(w => w.Id).Where(id => !excluded.Contains(id)), [include, .. this._details.Values.SelectMany(d => d.WorkflowsUsed)]);
        (this.Use, this.Include) = (use, include);
        this.AdoptCommonType();
        this.RaiseCompositionChanged();
    }

    /// <summary>
    ///     Adds an input with a free id.
    /// </summary>
    /// <returns>The new input.</returns>
    public InputDraftDataContext AddInput()
    {
        var number = this.Inputs.Count + 1;
        while (this.Inputs.Any(i => i.Id == $"input-{number}"))
        {
            number++;
        }

        var when = this.TakesInputs ? null : StepConstants.WhenBefore;
        var input = this.Track(new InputDraftDataContext(InputDraftDataContext.NewInput(number, when)));
        this.Inputs.Add(input);
        this.OnInputsChanged();
        return input;
    }

    /// <summary>
    ///     Removes an input.
    /// </summary>
    /// <param name="input">The input.</param>
    public void RemoveInput(InputDraftDataContext input)
    {
        if (this.Inputs.Remove(input))
        {
            input.Edited -= this.OnInputEdited;
            this.OnInputsChanged();
        }
    }

    /// <summary>
    ///     Builds the step definition the draft describes. Fields that don't apply to the step type are left out.
    /// </summary>
    /// <returns>The step.</returns>
    public AbstractStepDefinition ToDefinition()
    {
        if (this.IsInclude)
        {
            return new AiStepDefinition
            {
                Id = this.Id.Trim(),
                Name = this.Name.Trim(),
                Include = NullIfBlank(this.Include),
                With = this.With.ToDictionary(),
                Arrows = this.ArrowDefinitions(),
                Position = this.Position,
            };
        }

        var splits = this.Details?.UsesWorkspace == true || this.IsFork;
        var step = StepHandlerRegistry.Instance.CreateDefinition(this.StepType);
        step.Id = this.Id.Trim();
        step.Name = this.Name.Trim();
        step.Advance = this.Advance == DefaultChoice ? null : this.Advance;
        step.Agent = this.Details?.SavesAgent == true && this.Agent != RunAgentChoice ? this.Agent : null;
        step.Artifact = NullIfBlank(this.Artifact)?.Trim();
        step.OutputTemplate = NullIfBlank(this.OutputTemplate);
        step.Optional = this.IsOptional ? new OptionalStep { Default = this.OptionalDefault, Help = NullIfBlank(this.OptionalHelp)?.Trim() } : null;
        step.Inputs = this.Inputs.Count > 0 ? this.Inputs.Select(i => i.ToDefinition()).ToList() : null;
        step.Arrows = this.ArrowDefinitions();
        step.Follow = this.FollowsFirst ? ArrowConstants.FollowFirst : null;
        step.WaitFor = this.WaitChoice switch
        {
            ArrowConstants.WaitAny => new AnyWaitRule(),
            WaitCustom => this.WaitGroup?.ToRule(),
            _ => null,
        };
        step.Others = this.LetsOthersFinish ? ArrowConstants.OthersFinish : null;
        step.Retry = this.RetryMax is { } retries ? new RetryDefinition { Max = (int)retries, Delay = NullIfBlank(this.RetryDelay)?.Trim() } : null;
        step.Timeout = NullIfBlank(this.Timeout)?.Trim();
        step.Position = this.Position;
        step.Use = this.IsUse ? NullIfBlank(this.Use) : null;
        step.With = this.IsUse || this.Details?.TakesParameters == true ? this.With.ToDictionary() : null;
        step.Workspace = splits && this.Workspace != DefaultChoice ? this.Workspace : null;
        step.MergeBack = splits && this.ShowsMergeBack && !this.MergeBack ? false : null;
        this.Details?.Write(step);
        return step;
    }

    /// <summary>
    ///     Returns a common step by id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The common step, or null.</returns>
    internal CommonStepDefinition? CommonStepOf(string? id)
    {
        return this._commonSteps.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    ///     Returns a workflow by id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The workflow, or null.</returns>
    internal WorkflowDefinition? WorkflowOf(string? id)
    {
        return this._workflows.FirstOrDefault(w => w.Id == id);
    }

    /// <summary>
    ///     Takes the type of the common step the step is based on, since a using step can't change it.
    /// </summary>
    private void AdoptCommonType()
    {
        if (this.IsUse && this.CommonStepOf(this.Use) is { } common && this.StepType != common.Step.Type)
        {
            this.StepType = common.Step.Type;
        }
    }

    /// <summary>
    ///     Counts the fields a step based on a common step sets beyond where it sits in the workflow, each overriding the
    ///     common step's.
    /// </summary>
    /// <returns>The number of overriding fields.</returns>
    private int OverrideCount()
    {
        return WorkflowComposer.ToNode(this.ToDefinition()) is JsonObject fields
            ? fields.Count(f => !PlacementProperties.Contains(f.Key))
            : 0;
    }

    /// <summary>
    ///     Rebuilds the parameter fields for what the step refers to and tells the view what changed.
    /// </summary>
    internal void RaiseCompositionChanged()
    {
        if (this.With is not null)
        {
            var parameters = this.IsUse ? this.CommonStepOf(this.Use)?.Parameters
                : this.IsInclude ? this.WorkflowOf(this.Include)?.Parameters
                : this.Details?.Parameters;
            this.With.Rebuild(parameters);
        }

        this.RaisePropertiesChanged(nameof(this.IsOwnStep), nameof(this.IsUse), nameof(this.IsInclude),
            nameof(this.ShowsStepFields), nameof(this.ShowsWith), nameof(this.WithHelp), nameof(this.Title),
            nameof(this.BasisHelp), nameof(this.NamePlaceholder), nameof(this.CanSaveAsCommon), nameof(this.IsComposedStep),
            nameof(this.ShowsComposedWith), nameof(this.ShowsOwnWith), nameof(this.ReferencedId), nameof(this.CanViewReferenced),
            nameof(this.ShowsViewReferenced), nameof(this.ViewReferencedLabel), nameof(this.OverridesHeader));
        this.RaiseDetailsChanged();
    }

    /// <summary>
    ///     Tells every part the draft has made for a step type that something about the step changed.
    /// </summary>
    private void RaiseDetailsChanged()
    {
        foreach (var details in this._details.Values)
        {
            details.OnStepChanged();
        }
    }

    /// <summary>
    ///     Returns the part that edits a step type's own fields, made the first time the type is picked and kept, so
    ///     switching back to a type keeps what was entered for it.
    /// </summary>
    /// <param name="type">The step type.</param>
    /// <returns>The part, or null when the type is unknown or has no fields of its own.</returns>
    private AbstractStepDetailsDataContext? DetailsFor(string? type)
    {
        if (StepDetailsRegistry.Instance.KeyFor(type) is not { } key)
        {
            return null;
        }

        if (!this._details.TryGetValue(key, out var details))
        {
            details = StepDetailsRegistry.Instance.Create(key, this, this._source);
            details.SetAgents(this.AgentIds);
            details.SetCatalog(this._capabilities, this._servers);
            details.SetSkills(this._skills);
            if (this._isNew)
            {
                details.MarkAsNew();
            }

            details.Edited += this.OnPartEdited;
            this._details[key] = details;
        }

        return details;
    }

    /// <summary>
    ///     Builds a pick list from the ids available plus the ones already picked, so a missing one still shows.
    /// </summary>
    /// <param name="available">The ids available.</param>
    /// <param name="picked">The ids picked.</param>
    /// <returns>The choices, sorted.</returns>
    private static IReadOnlyList<string> Choices(IEnumerable<string> available, params string?[] picked)
    {
        return [.. available.Concat(picked.OfType<string>()).Where(id => id.Length > 0).Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Gets the ids of the defined agents.
    /// </summary>
    internal IReadOnlyList<string> AgentIds => (this.AgentChoices ?? []).Where(c => c != RunAgentChoice).ToList();

    /// <summary>
    ///     Passes the step and agent ids on to the routes and loop, keeping what's picked.
    /// </summary>
    private void SetFlowChoices()
    {
        var agents = this.AgentIds;
        foreach (var arrow in this.Arrows)
        {
            arrow.SetChoices(this._stepIds, agents);
        }
    }

    /// <summary>
    ///     Reports an added, removed or moved arrow.
    /// </summary>
    private void OnArrowsChanged()
    {
        this.RaisePropertyChanged(nameof(this.Targets));
        this.RaisePropertyChanged(nameof(this.IsFork));
        this.RaisePropertyChanged(nameof(this.FlowSummary));
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Reports an arrow's edit, which may change where the step goes.
    /// </summary>
    /// <param name="sender">The arrow.</param>
    /// <param name="e">The event.</param>
    private void OnArrowEdited(object? sender, EventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.Targets));
        this.RaisePropertyChanged(nameof(this.IsFork));
        this.RaisePropertyChanged(nameof(this.FlowSummary));
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Makes the step wait for a custom group, edited from now on in <see cref="WaitGroup" />.
    /// </summary>
    /// <param name="group">The group; one without items starts with one item per step whose arrow leads in.</param>
    /// <returns><see cref="WaitCustom" />.</returns>
    private string UseWaitGroup(AbstractWaitGroup group)
    {
        if (this.WaitGroup is { } old)
        {
            old.Edited -= this.OnWaitGroupEdited;
        }

        this.WaitGroup = new WaitGroupDraftDataContext(group, null, this._incomingIds);
        this.WaitGroup.SetChoices(this._incomingIds);
        this.WaitGroup.Edited += this.OnWaitGroupEdited;
        return WaitCustom;
    }

    /// <summary>
    ///     Passes an edit of the wait group on as an edit of the step.
    /// </summary>
    /// <param name="sender">The group.</param>
    /// <param name="e">The event.</param>
    private void OnWaitGroupEdited(object? sender, EventArgs e)
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Subscribes to an arrow's edits.
    /// </summary>
    /// <param name="arrow">The arrow.</param>
    /// <returns>The arrow.</returns>
    private ArrowDraftDataContext Track(ArrowDraftDataContext arrow)
    {
        arrow.Edited += this.OnArrowEdited;
        return arrow;
    }

    /// <summary>
    ///     Builds the step's arrows, or null when it has none.
    /// </summary>
    /// <returns>The arrows.</returns>
    private List<ArrowDefinition>? ArrowDefinitions()
    {
        return this.Arrows.Count > 0 ? this.Arrows.Select(a => a.ToDefinition()).ToList() : null;
    }

    /// <summary>
    ///     Passes a part's edit on as an edit of the step.
    /// </summary>
    /// <param name="sender">The part.</param>
    /// <param name="e">The event.</param>
    private void OnPartEdited(object? sender, EventArgs e)
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged" /> for several properties.
    /// </summary>
    /// <param name="names">The property names.</param>
    private void RaisePropertiesChanged(params string[] names)
    {
        foreach (var name in names)
        {
            this.RaisePropertyChanged(name);
        }
    }

    /// <summary>
    ///     Subscribes to an input's edits.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>The input.</returns>
    private InputDraftDataContext Track(InputDraftDataContext input)
    {
        input.Edited += this.OnInputEdited;
        return input;
    }

    /// <summary>
    ///     Passes an input's edit on as an edit of the step.
    /// </summary>
    /// <param name="sender">The input.</param>
    /// <param name="e">The event.</param>
    private void OnInputEdited(object? sender, EventArgs e)
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Reports an added or removed input.
    /// </summary>
    private void OnInputsChanged()
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Returns null for blank text, and the text otherwise.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text, or null.</returns>
    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
