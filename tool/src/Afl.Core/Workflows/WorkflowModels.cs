using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Steps;
using Afl.Core.Storage;

namespace Afl.Core.Workflows;

/// <summary>
///     A workflow run against a ticket: a graph of steps joined by arrows. The run starts at <see cref="Start" />; once a
///     step ends, its arrows (<see cref="AbstractStepDefinition.Arrows" />) that fire take the run on, several at once
///     running side by side as paths, and a step the arrows of several paths point at starts by its
///     <see cref="AbstractStepDefinition.WaitFor" />. The run ends when no path is left. The order of <see cref="Steps" /> is
///     only how they are listed.
/// </summary>
[DocumentVersion(5, 0)]
public class WorkflowDefinition : AbstractLibraryDocument
{
    /// <summary>
    ///     Gets or sets the values the workflow takes when it is started, included or run as a child, written
    ///     <c>{{params.&lt;id&gt;}}</c> in its steps.
    /// </summary>
    public List<ParameterDefinition>? Parameters { get; set; }

    /// <summary>
    ///     Gets or sets the workflows that must have finished on the ticket first. Each becomes a leading
    ///     <c>workflow</c> step that reuses a run of it on the same ticket, or starts one.
    /// </summary>
    public List<WorkflowRequirement>? Requires { get; set; }

    /// <summary>
    ///     Gets or sets the common skills (<see cref="SkillLibrary" />) every AI and interactive step's agents get, on
    ///     top of the step's own <see cref="AbstractAgentStepDefinition.Skills" />. Empty entries are ignored, so a parameter can
    ///     turn one off.
    /// </summary>
    public List<string>? Skills { get; set; }

    /// <summary>
    ///     Gets or sets the id of the step the run starts at; the first step listed when unset.
    /// </summary>
    public string? Start { get; set; }

    /// <summary>
    ///     Gets or sets the steps, in the order they are listed.
    /// </summary>
    public List<AbstractStepDefinition> Steps { get; set; } = [];

    /// <summary>
    ///     Gets or sets the most step runs a run of the workflow does in all, counting every loop and path, before it
    ///     fails; 100 when unset. It stops a run that goes round several loops for ever.
    /// </summary>
    public int? MaxSteps { get; set; }

    /// <summary>
    ///     Gets or sets the step a failure goes to when none of the failed step's arrows fire on it. It runs like an arrow
    ///     on <c>always</c>: the run still ends failed.
    /// </summary>
    public string? OnFailure { get; set; }

    /// <summary>
    ///     Gets the id of the step the run starts at: <see cref="Start" />, else the first step listed; null when there
    ///     are no steps.
    /// </summary>
    [JsonIgnore]
    public string? StartStepId => this.Start ?? this.Steps.FirstOrDefault()?.Id;

    /// <summary>
    ///     Gives each step that has no arrows one to the step listed after it. Workflows written in code use it to run
    ///     their steps in the order they're listed.
    /// </summary>
    /// <returns>This workflow.</returns>
    public WorkflowDefinition Chain()
    {
        for (var i = 0; i < this.Steps.Count - 1; i++)
        {
            this.Steps[i].Arrows ??= [new ArrowDefinition { To = this.Steps[i + 1].Id }];
        }

        return this;
    }

    /// <summary>
    ///     Gets whether the workflow uses common steps, includes or requires other workflows, or takes parameters, so
    ///     it has to be expanded (<see cref="WorkflowComposer" />) before it runs.
    /// </summary>
    [JsonIgnore]
    public bool IsComposed =>
        this.Parameters is { Count: > 0 } || this.Requires is { Count: > 0 } || this.Skills is { Count: > 0 } ||
        this.Steps.Any(s => s.IsComposed);

    /// <summary>
    ///     Gets the steps someone can choose to leave out when a run starts (those with
    ///     <see cref="AbstractStepDefinition.Optional" />), in order.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<AbstractStepDefinition> OptionalSteps => this.Steps.Where(s => s.Optional is not null).ToList();

    /// <summary>
    ///     Works out which optional steps a run leaves out: each one is in or out as <paramref name="choices" /> says,
    ///     else as its <see cref="OptionalStep.Default" /> says.
    /// </summary>
    /// <param name="choices">Whether to include each optional step, by step id; null or missing for its default.</param>
    /// <returns>The ids of the steps left out, in order.</returns>
    /// <exception cref="AflException">A choice names a step that isn't an optional step of the workflow.</exception>
    public List<string> LeftOutSteps(IReadOnlyDictionary<string, bool>? choices)
    {
        var optional = this.OptionalSteps;
        var unknown = (choices ?? new Dictionary<string, bool>()).Keys.Where(id => optional.All(s => s.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            var known = optional.Count == 0 ? "it has none" : $"they are {string.Join(", ", optional.Select(s => s.Id))}";
            throw new AflException(AflErrorKind.InvalidInput,
                $"Workflow '{this.Id}' has no optional step {string.Join(", ", unknown.Select(u => $"'{u}'"))}; {known}.");
        }

        return optional
            .Where(s => !(choices is not null && choices.TryGetValue(s.Id, out var include) ? include : s.Optional!.Default))
            .Select(s => s.Id)
            .ToList();
    }
}

/// <summary>
///     How a step can be left out of a run (<see cref="AbstractStepDefinition.Optional" />).
/// </summary>
public class OptionalStep
{
    /// <summary>
    ///     Gets or sets whether the step is included when nobody chooses otherwise.
    /// </summary>
    public bool Default { get; set; } = true;

    /// <summary>
    ///     Gets or sets what to tell someone choosing whether to include it, e.g. when it's worth doing; the step's name
    ///     is shown when unset.
    /// </summary>
    public string? Help { get; set; }
}

/// <summary>
///     Where the builder draws a step on its canvas, by the top left of the step's box.
/// </summary>
public class StepPosition
{
    /// <summary>
    ///     Gets or sets how far from the canvas's left edge the step is.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    ///     Gets or sets how far from the canvas's top edge the step is.
    /// </summary>
    public double Y { get; set; }
}

/// <summary>
///     An arrow from a step to another (or to <c>end</c>): when it fires (<see cref="On" />), on what condition
///     (<see cref="When" /> or <see cref="Until" />), and, for an arrow that goes back round a loop, how often.
/// </summary>
public class ArrowDefinition
{
    /// <summary>
    ///     Gets or sets the id of the step the arrow goes to, or <c>end</c> to stop the path it's on.
    /// </summary>
    public string To { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets how the step must have ended for the arrow to fire: <c>success</c> (the default),
    ///     <c>failure</c>, <c>timeout</c> or <c>always</c> (see <see cref="OutcomeRegistry" />).
    /// </summary>
    public string? On { get; set; }

    /// <summary>
    ///     Gets or sets a condition the arrow needs, checked once its step has ended.
    /// </summary>
    public ConditionDefinition? When { get; set; }

    /// <summary>
    ///     Gets or sets a condition that stops the arrow: it fires while this doesn't hold. Usual on an arrow back round a
    ///     loop.
    /// </summary>
    public ConditionDefinition? Until { get; set; }

    /// <summary>
    ///     Gets or sets, for an arrow that goes back (to its own step or one before it), the most times the step it goes
    ///     to runs; 5 when unset.
    /// </summary>
    public int? Max { get; set; }

    /// <summary>
    ///     Gets or sets what happens when an arrow that goes back reaches <see cref="Max" />: <c>fail</c> (the default)
    ///     fails the step, <c>continue</c> treats the arrow as not taken.
    /// </summary>
    public string? OnLimit { get; set; }

    /// <summary>
    ///     Gets or sets a short label shown on the arrow instead of a summary of its condition.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    ///     Gets the outcome the arrow fires on, <c>success</c> when unset.
    /// </summary>
    [JsonIgnore]
    public string Outcome => string.IsNullOrEmpty(this.On) ? ArrowConstants.OnSuccess : this.On;

    /// <summary>
    ///     Gets the arrow's condition, if any.
    /// </summary>
    [JsonIgnore]
    public ConditionDefinition? Condition => this.When ?? this.Until;

    /// <summary>
    ///     Gets whether the arrow has a condition.
    /// </summary>
    [JsonIgnore]
    public bool IsConditional => this.When is not null || this.Until is not null;

    /// <summary>
    ///     Gets whether the arrow stops the path rather than going to a step.
    /// </summary>
    [JsonIgnore]
    public bool Ends => this.To == ArrowConstants.End;

    /// <summary>
    ///     Returns a copy of the arrow.
    /// </summary>
    /// <returns>The copy.</returns>
    public ArrowDefinition Clone()
    {
        return Helpers.JsonCopy.Of(this);
    }
}

/// <summary>
///     How a step that fails is run again before any of its arrows on failure fire (<see cref="AbstractStepDefinition.Retry" />).
/// </summary>
public class RetryDefinition
{
    /// <summary>
    ///     Gets or sets how many more times the step runs after it fails.
    /// </summary>
    public int Max { get; set; } = 1;

    /// <summary>
    ///     Gets or sets how long to wait before each retry, e.g. <c>30s</c> or <c>5m</c>; no wait when unset.
    /// </summary>
    public string? Delay { get; set; }
}

/// <summary>
///     Something a step asks the user, e.g. a reviewer's name, a yes/no check, or a choice between options. How it is
///     asked and checked depends on <see cref="Type" />; see <c>Steps/StepInputTypes.cs</c>.
/// </summary>
public class StepInput
{
    /// <summary>
    ///     Gets or sets the id, unique within the step and used in <c>{{steps.&lt;step&gt;.inputs.&lt;id&gt;}}</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the question shown to the user.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the input type: <c>text</c>, <c>multiline</c>, <c>number</c>, <c>choice</c>,
    ///     <c>multichoice</c> or <c>confirm</c>.
    /// </summary>
    public string Type { get; set; } = StepConstants.InputText;

    /// <summary>
    ///     Gets or sets extra help shown under the question.
    /// </summary>
    public string? Help { get; set; }

    /// <summary>
    ///     Gets or sets the options to pick from, for <c>choice</c> and <c>multichoice</c>.
    /// </summary>
    public List<string>? Options { get; set; }

    /// <summary>
    ///     Gets or sets whether an answer is needed before the step can finish. Defaults to true.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>
    ///     Gets or sets the answer used when the user gives none.
    /// </summary>
    public string? Default { get; set; }

    /// <summary>
    ///     Gets or sets when the input is asked: <c>before</c> the step's work starts, <c>after</c> it (when the step is
    ///     let through), or <c>ai</c> (only if the AI decides it needs the answer). Unset is <c>after</c>.
    /// </summary>
    public string? When { get; set; }

    /// <summary>
    ///     Gets when the input is asked, with unset meaning <see cref="StepConstants.WhenAfter" />.
    /// </summary>
    [JsonIgnore]
    public string Timing => string.IsNullOrEmpty(this.When) ? StepConstants.WhenAfter : this.When;

    /// <summary>
    ///     Returns a copy of the input.
    /// </summary>
    /// <returns>The copy.</returns>
    public StepInput Clone()
    {
        var copy = (StepInput)this.MemberwiseClone();
        copy.Options = this.Options is null ? null : [..this.Options];
        return copy;
    }

    /// <summary>
    ///     Gets whether <see cref="Default" /> uses template variables, so it's only known once the step runs.
    /// </summary>
    [JsonIgnore]
    public bool HasTemplateDefault => Helpers.PromptTemplate.References(this.Default).Count > 0;

    /// <summary>
    ///     Returns the input with its <see cref="Default" /> rendered, when it uses template variables; a default that
    ///     renders blank becomes none.
    /// </summary>
    /// <param name="variables">Builds the run's variables; only called when the default needs them.</param>
    /// <returns>This input when its default is plain text, else a copy.</returns>
    public StepInput WithDefaultRendered(Func<IReadOnlyDictionary<string, string>> variables)
    {
        if (!this.HasTemplateDefault)
        {
            return this;
        }

        var copy = this.Clone();
        var rendered = Helpers.PromptTemplate.Render(this.Default, variables()).Trim();
        copy.Default = rendered.Length == 0 ? null : rendered;
        return copy;
    }
}

/// <summary>
///     Where a workflow definition came from. Later scopes override earlier ones with the same id.
/// </summary>
public enum WorkflowScope
{
    /// <summary>
    ///     Embedded in afl from the smidja repo's <c>workflows/</c> folder.
    /// </summary>
    BuiltIn,

    /// <summary>
    ///     The user's library in <c>~/.afl/workflows</c>.
    /// </summary>
    Global,

    /// <summary>
    ///     Committed in the repo's own <c>.afl/workflows</c>.
    /// </summary>
    Repo,
}
