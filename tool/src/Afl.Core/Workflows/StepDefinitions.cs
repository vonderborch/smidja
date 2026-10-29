using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Steps;
using Afl.Core.Storage;

namespace Afl.Core.Workflows;

/// <summary>
///     One step of a workflow: the fields every kind of step shares. Each kind (<see cref="Type" />) is a subclass with
///     its own fields, made for its handler (<see cref="AbstractStepHandler{TDefinition}" />), so a step only has the
///     fields that apply to it. JSON picks the subclass from <c>type</c> (<see cref="StepDefinitionConverter" />).
/// </summary>
public abstract class AbstractStepDefinition
{
    /// <summary>
    ///     Gets or sets the id, unique within the workflow and used in <c>{{steps.&lt;id&gt;.output}}</c>.
    /// </summary>
    [JsonPropertyOrder(-3)]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    [JsonPropertyOrder(-2)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets the kind of step, which picks its handler: <c>ai</c> (the AI works headless), <c>interactive</c> (the
    ///     user works with the AI in a session), <c>user</c> (the user does it) or <c>workflow</c> (it starts child runs).
    ///     It comes from the subclass.
    /// </summary>
    [JsonPropertyOrder(-1)]
    public string Type => StepHandlerRegistry.Instance.TypeOf(this);

    /// <summary>
    ///     Gets or sets the id of the agent that does the step's AI work, or that a workflow step's child runs use (see
    ///     <c>afl agent list</c>); the run's agent when unset.
    /// </summary>
    public string? Agent { get; set; }

    /// <summary>
    ///     Gets or sets what happens once the step's work is done: <c>auto</c> moves straight on, <c>approve</c> waits
    ///     for the user to approve it or reject it with feedback. Unset is the kind of step's default.
    /// </summary>
    public string? Advance { get; set; }

    /// <summary>
    ///     Gets or sets what the user is asked: before the step's work starts, when they let it through (approve it if
    ///     it waits for approval, otherwise mark it done), or when the AI decides to ask (see
    ///     <see cref="StepInput.When" />).
    ///     Answers are available to later steps as <c>{{steps.&lt;id&gt;.inputs.&lt;input&gt;}}</c>.
    /// </summary>
    public List<StepInput>? Inputs { get; set; }

    /// <summary>
    ///     Gets or sets where to save the step's output, relative to the repo (e.g.
    ///     <c>docs/afl/{{ticket.slug}}/DESIGN.md</c>); template variables are filled in. It's written when the step
    ///     finishes or waits for approval, must stay inside the repo, and is available to later steps as
    ///     <c>{{steps.&lt;id&gt;.artifact}}</c>.
    /// </summary>
    public string? Artifact { get; set; }

    /// <summary>
    ///     Gets or sets the step's arrows: where the run goes once the step ends. Each fires on an outcome (success by
    ///     default) and maybe a condition; arrows that go back round a loop are checked first, then the forward ones by
    ///     <see cref="Follow" />. No arrow that fires ends the path the step is on.
    /// </summary>
    public List<ArrowDefinition>? Arrows { get; set; }

    /// <summary>
    ///     Gets or sets how many of the step's forward arrows that apply it follows: <c>all</c> (the default, several at
    ///     once running side by side) or <c>first</c> (the first conditional arrow that holds, else every unconditional
    ///     one).
    /// </summary>
    public string? Follow { get; set; }

    /// <summary>
    ///     Gets whether the step follows only the first arrow that holds (<see cref="Follow" />).
    /// </summary>
    [JsonIgnore]
    public bool FollowsFirst => this.Follow == ArrowConstants.FollowFirst;

    /// <summary>
    ///     Gets or sets, for a step forward arrows from several steps point at, what it waits for before it starts:
    ///     <c>"all"</c> (the default) or <c>"any"</c>, or a group such as <c>{ "any": ["docs", { "all": ["build", "test"] }] }</c>
    ///     (see <see cref="WaitRuleRegistry" />). Arrows that didn't fire are left out, so paths that went another way
    ///     don't hold it up.
    /// </summary>
    public AbstractWaitRule? WaitFor { get; set; }

    /// <summary>
    ///     Gets or sets what happens to the paths still heading only for this step once its <see cref="WaitFor" /> holds:
    ///     <c>cancel</c> (the default) or <c>finish</c>.
    /// </summary>
    public string? Others { get; set; }

    /// <summary>
    ///     Gets whether the paths still heading for this step finish once it starts (<see cref="Others" />).
    /// </summary>
    [JsonIgnore]
    public bool LetsOthersFinish => this.Others == ArrowConstants.OthersFinish;

    /// <summary>
    ///     Gets or sets how the step is run again when it fails, before its arrows on failure fire.
    /// </summary>
    public RetryDefinition? Retry { get; set; }

    /// <summary>
    ///     Gets or sets how long the step may take, e.g. <c>30m</c>: one that runs over is stopped and fails with a
    ///     timeout (see the <c>timeout</c> outcome).
    /// </summary>
    public string? Timeout { get; set; }

    /// <summary>
    ///     Gets or sets where the builder draws the step, once someone has moved it there; null lets the builder lay it
    ///     out. Runs ignore it.
    /// </summary>
    public StepPosition? Position { get; set; }

    /// <summary>
    ///     Gets or sets a template the step's output is rendered through once it is done, with its own answers as
    ///     <c>{{inputs.&lt;id&gt;}}</c> and what it produced as <c>{{output}}</c>; e.g. a form's answers laid out as a
    ///     document.
    /// </summary>
    public string? OutputTemplate { get; set; }

    /// <summary>
    ///     Gets or sets, for a step someone can leave out, whether it is included unless they say otherwise and what to
    ///     tell them about it. They choose when the run starts (<c>afl start --include/--exclude</c>, or the start
    ///     dialog); a step left out is skipped. Null for a step every run does.
    /// </summary>
    public OptionalStep? Optional { get; set; }

    /// <summary>
    ///     Gets or sets the id of a common step this step is based on. Its fields are the common step's, with the ones
    ///     this step sets overriding them; its type always comes from the common step.
    /// </summary>
    public string? Use { get; set; }

    /// <summary>
    ///     Gets or sets the id of a workflow whose steps take this step's place, their ids prefixed with this step's
    ///     (<c>&lt;id&gt;-&lt;step&gt;</c>).
    /// </summary>
    public string? Include { get; set; }

    /// <summary>
    ///     Gets or sets the parameter values for the common step (<see cref="Use" />), the included workflow
    ///     (<see cref="Include" />) or the child workflow (<see cref="WorkflowStepDefinition.Workflow" />). Values may be
    ///     templates, and in a <c>workflow</c> step may use <c>{{item}}</c>.
    /// </summary>
    public Dictionary<string, string>? With { get; set; }

    /// <summary>
    ///     Gets or sets, for a <c>workflow</c> step, where each child run works, and for a step whose arrows start
    ///     several paths, where each path works (a <see cref="Workspaces.WorkspaceRegistry" /> name): <c>shared</c> (the
    ///     default, the run's checkout) or <c>worktree</c> (its own git worktree and branch, merged back once it is done;
    ///     for a path, when it ends or meets other paths).
    /// </summary>
    public string? Workspace { get; set; }

    /// <summary>
    ///     Gets or sets, for a <c>workflow</c> step or paths with their own workspace, whether a finished child's or
    ///     path's work is merged back (the default) or its branch kept unmerged, e.g. for a throwaway prototype.
    /// </summary>
    public bool? MergeBack { get; set; }

    /// <summary>
    ///     Gets or sets the fields the JSON had that this kind of step doesn't take. A step based on a common step
    ///     (<see cref="Use" />) keeps them as overrides for it; anywhere else they're a validation error.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherFields { get; set; }

    /// <summary>
    ///     Gets whether the step is based on a common step or includes a workflow, so it has to be expanded first.
    /// </summary>
    [JsonIgnore]
    public bool IsComposed => this.Use is not null || this.Include is not null;

    /// <summary>
    ///     Returns the step's inputs asked at one point (<see cref="StepInput.When" />).
    /// </summary>
    /// <param name="when">
    ///     <see cref="StepConstants.WhenBefore" />, <see cref="StepConstants.WhenAfter" /> or
    ///     <see cref="StepConstants.WhenAi" />.
    /// </param>
    /// <returns>The inputs, in order.</returns>
    public IReadOnlyList<StepInput> InputsAsked(string when)
    {
        return (this.Inputs ?? []).Where(i => i.Timing == when).ToList();
    }

    /// <summary>
    ///     Returns every condition the step's arrows check, nested ones included.
    /// </summary>
    /// <returns>The conditions.</returns>
    public IEnumerable<ConditionDefinition> FlowConditions()
    {
        return (this.Arrows ?? []).Select(a => a.Condition)
            .OfType<ConditionDefinition>()
            .SelectMany(c => c.Flatten());
    }

    /// <summary>
    ///     Returns the templates whoever does the step is given (an agent's prompt, a person's instructions), so the
    ///     references in them can be checked.
    /// </summary>
    /// <returns>The templates that are set.</returns>
    public virtual IEnumerable<string> Templates()
    {
        return [];
    }

    /// <summary>
    ///     Fills in starting content for a step just added in the editor, e.g. a prompt to edit. By default there is
    ///     none.
    /// </summary>
    public virtual void FillStarter()
    {
    }
}

/// <summary>
///     A step an agent works on, from a prompt: the fields <c>ai</c> and <c>interactive</c> steps share.
/// </summary>
public abstract class AbstractAgentStepDefinition : AbstractStepDefinition
{
    /// <summary>
    ///     Gets or sets the prompt given to the AI (template).
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    ///     Gets or sets the Claude Code permission mode for AI work; defaults to <c>acceptEdits</c>.
    /// </summary>
    public string? PermissionMode { get; set; }

    /// <summary>
    ///     Gets or sets tools the AI may use without asking, e.g. <c>Bash(dotnet test:*)</c>.
    /// </summary>
    public List<string>? AllowedTools { get; set; }

    /// <summary>
    ///     Gets or sets the model for AI work; the agent's default when unset.
    /// </summary>
    public string? Model { get; set; }


    /// <summary>
    ///     Gets or sets what the agent doing the step must be able to do (capability ids; see
    ///     <c>afl capability-list</c>). When set, any available agent with all of them qualifies, <see cref="AbstractStepDefinition.Agent" />
    ///     becomes a preference used when it qualifies, and <see cref="Select" /> picks among the rest, so the
    ///     workflow works whatever agents a machine has.
    /// </summary>
    public List<string>? Capabilities { get; set; }

    /// <summary>
    ///     Gets or sets how the agent for a step with <see cref="Capabilities" /> is picked from those that qualify:
    ///     <c>ai</c> (the run's agent chooses) or <c>first</c> (the first in the preference order). Unset uses the
    ///     global setting.
    /// </summary>
    public string? Select { get; set; }

    /// <summary>
    ///     Gets or sets the ids of two or more agents that each do the step's AI work, side by side, instead of one
    ///     <see cref="AbstractStepDefinition.Agent" />. Their results are put together as <see cref="Combine" /> says, and each agent's own is
    ///     available to later steps as <c>{{steps.&lt;id&gt;.candidates.&lt;agent&gt;}}</c>.
    /// </summary>
    public List<string>? Agents { get; set; }

    /// <summary>
    ///     Gets or sets how the results of a step with <see cref="Agents" /> become its output: <c>choose</c> (the
    ///     default) waits for the user to choose one or merge them, <c>merge</c> has an agent merge them, and <c>all</c>
    ///     keeps them all, one after another.
    /// </summary>
    public string? Combine { get; set; }

    /// <summary>
    ///     Gets or sets the id of the agent that merges the results of a step with <see cref="Agents" />; its first agent
    ///     when unset.
    /// </summary>
    public string? MergeAgent { get; set; }

    /// <summary>
    ///     Gets or sets the prompt that merges the results of a step with <see cref="Agents" />, where
    ///     <c>{{candidates}}</c> is every result under its agent's heading. When unset, the step's prompt followed by
    ///     merge instructions and the results.
    /// </summary>
    public string? MergePrompt { get; set; }

    /// <summary>
    ///     Gets or sets whether the AI may stop to ask the user its own questions while it works (an <c>ai</c> step done
    ///     by one agent). Unset is false.
    /// </summary>
    public bool? Ask { get; set; }

    /// <summary>
    ///     Gets or sets whether the AI may hand sub-tasks to other agents while it works (an <c>ai</c> step done by one
    ///     agent): it ends its reply with an <c>afl-delegate</c> block, afl runs the tasks, and the AI carries on with
    ///     their results. Unset is false.
    /// </summary>
    public bool? Delegate { get; set; }

    /// <summary>
    ///     Gets or sets the capabilities every agent the AI hands work to must have, on top of what each task asks for.
    /// </summary>
    public List<string>? DelegateCapabilities { get; set; }

    /// <summary>
    ///     Gets or sets MCP servers (ids in <c>mcp-servers.json</c>) given to the step's agent as extra tools. Only agents
    ///     that can take servers (capability <c>mcp-tools</c>) can do the step, and they get only these servers and their
    ///     own, not whatever else their tool is set up with.
    /// </summary>
    public List<string>? McpServers { get; set; }

    /// <summary>
    ///     Gets or sets MCP servers given to the step's agent when they're set up on this machine and the agent can take
    ///     them; a server that isn't is skipped with a note, so a workflow can offer tools without needing them.
    /// </summary>
    public List<string>? OptionalMcpServers { get; set; }

    /// <summary>
    ///     Gets or sets whether the AI may file tickets while it works (an <c>ai</c> step done by one agent): it ends its
    ///     reply with an <c>afl-ticket</c> block, afl creates them in the run's ticket source (GitHub or Jira), and the AI
    ///     carries on with their keys. Unset is false.
    /// </summary>
    public bool? CreateTickets { get; set; }

    /// <summary>
    ///     Gets or sets the common skills (<see cref="SkillLibrary" />) the step's agents get. Empty entries are
    ///     ignored, so a parameter can turn one off.
    /// </summary>
    public List<string>? Skills { get; set; }

    /// <summary>
    ///     Gets or sets the workflow's common skills (<see cref="WorkflowDefinition.Skills" />) this step doesn't get,
    ///     such as a terse style on a step that writes a document for people. Entries may be templates; empty ones are
    ///     ignored.
    /// </summary>
    public List<string>? SkipSkills { get; set; }

    /// <summary>
    ///     Gets or sets folders outside the repo the step's agent may also work in, e.g. other projects to learn from.
    ///     Entries are templates and may hold several folders, one per line; a leading <c>~</c> is the home folder, and
    ///     a folder that doesn't exist is skipped with a note. What the agent may do there is still bounded by its
    ///     permission mode and allowed tools.
    /// </summary>
    public List<string>? Directories { get; set; }

    /// <summary>
    ///     Gets whether several agents do the step's AI work (it has <see cref="Agents" />).
    /// </summary>
    [JsonIgnore]
    public bool IsMultiAgent => this.Agents is { Count: > 0 };

    /// <summary>
    ///     Gets whether the step's agent is chosen by capability (it has <see cref="Capabilities" />).
    /// </summary>
    [JsonIgnore]
    public bool ChoosesAgent => this.Capabilities is { Count: > 0 } && !this.IsMultiAgent;

    /// <summary>
    ///     Returns the ids of the agents that do the step's AI work: its <see cref="Agents" />, else its
    ///     <see cref="AbstractStepDefinition.Agent" />, else the run's.
    /// </summary>
    /// <param name="runAgent">The run's agent.</param>
    /// <returns>The agent ids.</returns>
    public IReadOnlyList<string> AgentIds(string runAgent)
    {
        return this.IsMultiAgent ? this.Agents! : [this.Agent ?? runAgent];
    }

    /// <inheritdoc />
    public override IEnumerable<string> Templates()
    {
        return new[] { this.Prompt }.Concat(this.Directories ?? []).OfType<string>();
    }

    /// <inheritdoc />
    public override void FillStarter()
    {
        this.Prompt = "Work on {{ticket.key}}: {{ticket.title}}\n\n{{ticket.body}}";
        this.PermissionMode = StepConstants.ReadOnlyPermissionMode;
    }
}

/// <summary>
///     An <c>ai</c> step: an agent (or several, side by side) does it on its own, headless.
/// </summary>
public class AiStepDefinition : AbstractAgentStepDefinition;

/// <summary>
///     An <c>interactive</c> step: the user works through it with an agent in a terminal session.
/// </summary>
public class InteractiveStepDefinition : AbstractAgentStepDefinition;

/// <summary>
///     An <c>author-draft</c> step, internal to the AI authoring helper: an <c>ai</c> step that writes a skill, common
///     step or workflow to a draft file, which is checked (and sent back to the AI to fix) before the user reviews it.
/// </summary>
public class AuthorDraftStepDefinition : AiStepDefinition
{
    /// <summary>
    ///     Gets or sets the kind of document drafted (an <see cref="Authoring.AuthoringKindRegistry" /> name).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the draft file, relative to where the run works.
    /// </summary>
    public string DraftFile { get; set; } = AuthoringConstants.DraftFileName;

    /// <summary>
    ///     Gets or sets the repo whose library the draft is checked against (its common steps, workflows and skills),
    ///     or null for the global library only.
    /// </summary>
    public string? LibraryRepoPath { get; set; }

    /// <summary>
    ///     Gets or sets how many times a draft that doesn't check out is sent back to the AI before the user sees it.
    /// </summary>
    public int MaxFixRounds { get; set; } = AuthoringConstants.DefaultMaxFixRounds;
}

/// <summary>
///     A <c>user</c> step: the user does it, following its instructions, and marks it done.
/// </summary>
public class UserStepDefinition : AbstractStepDefinition
{
    /// <summary>
    ///     Gets or sets the instructions shown to the user (template).
    /// </summary>
    public string? Instructions { get; set; }

    /// <inheritdoc />
    public override IEnumerable<string> Templates()
    {
        return new[] { this.Instructions }.OfType<string>();
    }

    /// <inheritdoc />
    public override void FillStarter()
    {
        this.Instructions = "Do this by hand, then mark the step done.";
    }
}

/// <summary>
///     A <c>workflow</c> step: it starts child runs of another workflow and (unless told otherwise) waits for them.
/// </summary>
public class WorkflowStepDefinition : AbstractStepDefinition
{
    /// <summary>
    ///     Gets or sets the id of the workflow its child runs follow.
    /// </summary>
    public string? Workflow { get; set; }

    /// <summary>
    ///     Gets or sets what it starts a child run for; one child on the run's ticket when unset.
    /// </summary>
    public ChildSourceDefinition? ForEach { get; set; }

    /// <summary>
    ///     Gets or sets whether it waits for its children to finish (<c>wait</c>, the default) or finishes once they have
    ///     started (<c>start</c>).
    /// </summary>
    public string? Children { get; set; }

    /// <summary>
    ///     Gets or sets whether its children are driven at the same time rather than one after another. Risky when they
    ///     change the same working tree.
    /// </summary>
    public bool? ChildrenTogether { get; set; }

    /// <summary>
    ///     Gets or sets whether an existing run of the workflow on the same ticket is used instead of starting one: a
    ///     finished run as it is, an unfinished one waited on.
    /// </summary>
    public bool? Reuse { get; set; }

    /// <summary>
    ///     Gets or sets the child step whose output becomes each child's result; its last step with output when unset.
    /// </summary>
    public string? ChildOutput { get; set; }
}

/// <summary>
///     Reads a step as the subclass its <c>type</c> names (<c>ai</c> when unset), and writes it as whatever it is.
/// </summary>
public class StepDefinitionConverter : JsonConverter<AbstractStepDefinition>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(AbstractStepDefinition);
    }

    /// <inheritdoc />
    public override AbstractStepDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A step must be a JSON object.");
        var type = node[StepConstants.TypePropertyName]?.GetValue<string>() ?? StepConstants.TypeAi;
        if (!StepHandlerRegistry.Instance.Contains(type))
        {
            var id = node["id"]?.GetValue<string>();
            throw new JsonException(
                $"Step '{id}' has unknown type '{type}'. Known: {string.Join(", ", StepHandlerRegistry.Instance.PublicKeys)}.");
        }

        return (AbstractStepDefinition)node.Deserialize(StepHandlerRegistry.Instance.DefinitionTypeOf(type), options)!;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AbstractStepDefinition value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
