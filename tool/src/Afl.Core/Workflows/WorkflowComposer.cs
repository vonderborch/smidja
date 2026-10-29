using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Steps;

namespace Afl.Core.Workflows;

/// <summary>
///     Resolves how a workflow is built from other pieces, so a run holds one plain list of steps: <c>use</c> (a common
///     step, with its parameters filled in and the using step's fields laid over it), <c>include</c> (another workflow's
///     steps spliced in, their ids prefixed and references rewritten), <c>requires</c> (a leading <c>workflow</c> step
///     per requirement) and the workflow's own <c>{{params.x}}</c>. Child <c>workflow</c> steps stay as they are; they
///     start their own runs when they run.
/// </summary>
/// <param name="library">The library the workflows and common steps are found in.</param>
public partial class WorkflowComposer(WorkflowLibrary library)
{
    /// <summary>
    ///     Marks a <c>goto</c> inside an include that meant "the end" until the step after the include is known.
    /// </summary>
    private const string AfterIncludeMarker = "@after:";

    /// <summary>
    ///     The property holding parameter values, whose keys are never step references.
    /// </summary>
    private const string WithPropertyName = "with";

    /// <summary>
    ///     Gets the library the workflows and common steps are found in.
    /// </summary>
    public WorkflowLibrary Library { get; } = library;

    /// <summary>
    ///     Expands a workflow into the plain steps a run follows. A workflow that isn't composed is returned as it is.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="parameters">The values for the workflow's parameters, by id.</param>
    /// <param name="skills">
    ///     The skills picked for the run, in place of the workflow's own <c>skills</c>; null keeps the workflow's.
    /// </param>
    /// <returns>The expanded workflow: a copy, without <c>parameters</c>, <c>requires</c>, <c>use</c> or <c>include</c>.</returns>
    /// <exception cref="AflException">Something it uses is missing, or a parameter isn't given.</exception>
    public WorkflowDefinition Expand(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyList<string>? skills = null)
    {
        if (skills is not null)
        {
            workflow = FromNode<WorkflowDefinition>(ToNode(workflow));
            // An empty pick still goes through the merge, so steps' skipSkills are applied and cleared.
            workflow.Skills = skills.Count == 0 ? [string.Empty] : skills.ToList();
        }

        if (!workflow.IsComposed)
        {
            return workflow;
        }

        var problems = new List<string>();
        var expanded = this.ExpandWorkflow(workflow, parameters, [], problems, "pass --param {0}=…");
        if (problems.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidWorkflow,
                $"Workflow '{workflow.Id}' can't be expanded:\n  - {string.Join("\n  - ", problems.Distinct())}");
        }

        return expanded;
    }

    /// <summary>
    ///     Checks a workflow for problems, resolving what it uses against the library: unknown common steps and
    ///     workflows, parameter problems, cycles and depth, and then every check on the expanded workflow, whose
    ///     messages name the expanded step ids. Parameters the workflow itself takes are filled in with samples.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The problems; empty when valid.</returns>
    public IReadOnlyList<string> Validate(WorkflowDefinition workflow)
    {
        var problems = new List<string>();
        problems.AddRange(workflow.ValidateTags().Select(p => $"Workflow {p}"));
        problems.AddRange(ValidateParameters(workflow.Parameters).Select(p => $"Workflow parameters: {p}"));
        problems.AddRange(this.CheckReferences(workflow));
        if (problems.Count > 0)
        {
            return problems.Distinct().ToList();
        }

        var expanded = this.ExpandWorkflow(workflow, null, [], problems, null);
        if (problems.Count > 0)
        {
            return problems.Distinct().ToList();
        }

        problems.AddRange(this.ValidateChildSteps(expanded));
        problems.AddRange(this.ValidateSkills(expanded));
        problems.AddRange(WorkflowLibrary.Validate(expanded));
        return problems;
    }

    /// <summary>
    ///     Validates a workflow and throws listing every problem.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <exception cref="AflException">The workflow is invalid.</exception>
    public void ThrowIfInvalid(WorkflowDefinition workflow)
    {
        var errors = this.Validate(workflow);
        if (errors.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidWorkflow,
                $"Workflow '{workflow.Id}' is invalid:\n  - {string.Join("\n  - ", errors)}");
        }
    }

    /// <summary>
    ///     Checks a list of parameter definitions: valid, unique ids.
    /// </summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IEnumerable<string> ValidateParameters(IReadOnlyList<ParameterDefinition>? parameters)
    {
        var seen = new HashSet<string>();
        foreach (var parameter in parameters ?? [])
        {
            if (!WorkflowLibrary.IsValidId(parameter.Id))
            {
                yield return $"parameter id '{parameter.Id}' must be lowercase letters, digits and dashes.";
            }
            else if (!seen.Add(parameter.Id))
            {
                yield return $"parameter '{parameter.Id}' is declared twice.";
            }
        }
    }

    /// <summary>
    ///     Lists the <c>{{params.x}}</c> a document uses that aren't declared.
    /// </summary>
    /// <param name="node">The document, as JSON.</param>
    /// <param name="parameters">The declared parameters.</param>
    /// <returns>The undeclared parameter ids.</returns>
    public static IReadOnlyList<string> UndeclaredParameters(JsonNode? node, IReadOnlyList<ParameterDefinition>? parameters)
    {
        var declared = (parameters ?? []).Select(p => p.Id).ToHashSet();
        var found = new List<string>();
        Walk(node, (_, text) =>
        {
            found.AddRange(PromptTemplate.References(text)
                .Where(r => r.StartsWith(StepConstants.ParameterPrefix, StringComparison.Ordinal))
                .Select(r => r[StepConstants.ParameterPrefix.Length..])
                .Where(id => !declared.Contains(id)));
            return text;
        });
        return found.Distinct().ToList();
    }

    /// <summary>
    ///     Returns sample values for parameters, for checking a definition on its own: each default, or the parameter's
    ///     id when it has none.
    /// </summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The values, keyed <c>params.&lt;id&gt;</c>.</returns>
    public static IReadOnlyDictionary<string, string> ValuesWithDefaults(IReadOnlyList<ParameterDefinition>? parameters)
    {
        return (parameters ?? []).DistinctBy(p => p.Id).ToDictionary(p => StepConstants.ParameterPrefix + p.Id, p => p.Default ?? p.Id);
    }

    /// <summary>
    ///     Fills <c>{{params.x}}</c> into every string of a document, leaving every other placeholder for later.
    /// </summary>
    /// <param name="node">The document, as JSON; changed in place.</param>
    /// <param name="values">The values, keyed <c>params.&lt;id&gt;</c>.</param>
    /// <returns>The document.</returns>
    public static JsonNode? SubstituteParameters(JsonNode? node, IReadOnlyDictionary<string, string> values)
    {
        return values.Count == 0 ? node : Walk(node, (_, text) => PromptTemplate.Substitute(text, values));
    }

    /// <summary>
    ///     Gives steps new ids, and points what they say about each other at the new ids: their arrows, conditions,
    ///     <c>waitFor</c> groups and <c>{{steps.&lt;id&gt;.…}}</c> placeholders. References to steps not being renamed
    ///     are left alone. Used to copy steps within a workflow.
    /// </summary>
    /// <param name="steps">The steps.</param>
    /// <param name="ids">The new id of each step being renamed, by its old id.</param>
    /// <returns>Renamed copies of the steps.</returns>
    public static List<AbstractStepDefinition> RenameSteps(IEnumerable<AbstractStepDefinition> steps, IReadOnlyDictionary<string, string> ids)
    {
        return steps.Select(step =>
        {
            var copy = FromNode<AbstractStepDefinition>(Walk(ToNode(step), (key, text) => key switch
            {
                null => RewriteTemplateIds(text, id => ids.GetValueOrDefault(id)),
                _ => ids.GetValueOrDefault(text, text),
            }));
            copy.Id = ids.GetValueOrDefault(step.Id, step.Id);
            return copy;
        }).ToList();
    }

    /// <summary>
    ///     Converts a definition to JSON, the way it is stored.
    /// </summary>
    /// <param name="value">The definition.</param>
    /// <returns>Its JSON.</returns>
    public static JsonNode? ToNode(object value)
    {
        return JsonSerializer.SerializeToNode(value, value.GetType(), JsonConstants.Options);
    }

    /// <summary>
    ///     Converts JSON back to a definition.
    /// </summary>
    /// <param name="node">The JSON.</param>
    /// <typeparam name="T">The definition type.</typeparam>
    /// <returns>The definition.</returns>
    public static T FromNode<T>(JsonNode? node)
    {
        return node.Deserialize<T>(JsonConstants.Options)!;
    }

    /// <summary>
    ///     Expands one workflow: fills in its parameters, then its requirements, common steps and includes.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="with">The values given for its parameters.</param>
    /// <param name="chain">The workflows being included that led here, outermost first.</param>
    /// <param name="problems">Where problems are added.</param>
    /// <param name="missingHint">
    ///     How to give a missing parameter (formatted with its id); null to fill missing ones with samples instead.
    /// </param>
    /// <returns>The expanded copy.</returns>
    private WorkflowDefinition ExpandWorkflow(WorkflowDefinition workflow, IReadOnlyDictionary<string, string>? with,
        IReadOnlyList<string> chain, List<string> problems, string? missingHint)
    {
        var here = chain.Append(workflow.Id).ToList();
        var owner = $"Workflow '{workflow.Id}'";
        var source = ToNode(workflow);
        problems.AddRange(UndeclaredParameters(source, workflow.Parameters).Select(p => $"{owner} uses '{{{{{StepConstants.ParameterPrefix}{p}}}}}', which isn't one of its parameters."));
        var values = ResolveValues(workflow.Parameters, with, owner, problems, missingHint);
        var copy = FromNode<WorkflowDefinition>(SubstituteParameters(source, values));

        var start = copy.StartStepId;
        var steps = new List<AbstractStepDefinition>();
        foreach (var requirement in copy.Requires ?? [])
        {
            steps.Add(new WorkflowStepDefinition
            {
                Id = StepConstants.RequirementStepPrefix + requirement.Workflow,
                Name = $"Requires {this.Library.Get(requirement.Workflow)?.Name ?? requirement.Workflow}",
                Workflow = requirement.Workflow,
                With = requirement.With,
                Reuse = true,
            });
        }

        for (var i = 0; i < steps.Count; i++)
        {
            steps[i].Arrows = i + 1 < steps.Count ? [new ArrowDefinition { To = steps[i + 1].Id }] : start is null ? null : [new ArrowDefinition { To = start }];
        }

        if (steps.Count > 0)
        {
            copy.Start = steps[0].Id;
        }

        var includes = new Dictionary<string, IncludeSpan>();
        foreach (var step in copy.Steps)
        {
            var label = string.IsNullOrEmpty(step.Id) ? $"{owner}, step {copy.Steps.IndexOf(step) + 1}" : $"{owner}, step '{step.Id}'";
            if (step.Use is not null && step.Include is not null)
            {
                problems.Add($"{label}: a step either uses a common step or includes a workflow, not both.");
            }
            else if (step.Use is not null)
            {
                if (this.ExpandUse(step, label, problems) is { } used)
                {
                    steps.Add(used);
                }
            }
            else if (step.Include is not null)
            {
                var (included, includedStart) = this.ExpandInclude(step, label, here, problems);
                if (included.Count > 0)
                {
                    includes[step.Id] = new IncludeSpan(includedStart ?? included[0].Id, included[^1].Id, step.Arrows ?? []);
                    steps.AddRange(included);
                }
            }
            else
            {
                steps.Add(step);
            }
        }

        if (includes.Count > 0)
        {
            steps = RewriteIncludeReferences(steps, includes, owner, problems);
            if (copy.Start is { } named && includes.TryGetValue(named, out var startSpan))
            {
                copy.Start = startSpan.First;
            }
        }

        copy.Steps = steps;
        MergeSkills(copy.Steps, copy.Skills);
        copy.Parameters = null;
        copy.Requires = null;
        copy.Skills = null;
        return copy;
    }

    /// <summary>
    ///     Gives every agent step the workflow's default skills ahead of its own, less the ones it skips, and drops empty
    ///     entries (a skill parameter left blank means no skill).
    /// </summary>
    /// <param name="steps">The expanded steps.</param>
    /// <param name="defaults">The workflow-level skills, if any.</param>
    private static void MergeSkills(IEnumerable<AbstractStepDefinition> steps, IReadOnlyList<string>? defaults)
    {
        foreach (var step in steps.OfType<AbstractAgentStepDefinition>())
        {
            var skipped = CleanSkills(step.SkipSkills).ToHashSet(StringComparer.Ordinal);
            var merged = CleanSkills(defaults).Where(s => !skipped.Contains(s))
                .Concat(CleanSkills(step.Skills))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            step.Skills = merged.Count == 0 ? null : merged;
            step.SkipSkills = null;
        }
    }

    /// <summary>
    ///     Trims skill ids and drops empty ones.
    /// </summary>
    /// <param name="skills">The skill ids, if any.</param>
    /// <returns>The non-empty ids.</returns>
    private static IEnumerable<string> CleanSkills(IEnumerable<string>? skills)
    {
        return (skills ?? []).Select(s => s.Trim()).Where(s => s.Length > 0);
    }

    /// <summary>
    ///     Reports the skills an expanded workflow's steps name that aren't in the library.
    /// </summary>
    /// <param name="expanded">The expanded workflow.</param>
    /// <returns>The problems.</returns>
    private IEnumerable<string> ValidateSkills(WorkflowDefinition expanded)
    {
        foreach (var step in expanded.Steps.OfType<AbstractAgentStepDefinition>())
        {
            foreach (var skill in (step.Skills ?? []).Where(s => this.Library.Skills.Get(s) is null))
            {
                var available = string.Join(", ", this.Library.Skills.List().Select(l => l.Skill.Id));
                yield return $"Step '{step.Id}': no skill '{skill}'. Available: {(available.Length == 0 ? "none" : available)}.";
            }
        }
    }

    /// <summary>
    ///     Expands a step that uses a common step: the common step with its parameters filled in, overlaid with every
    ///     field the using step sets. Empty fields inherit; lists replace; the type always comes from the common step. An
    ///     arrow in the common step back to its own id goes back to the using step instead, and comes before the using
    ///     step's own arrows, so a common step can loop wherever it's used.
    /// </summary>
    /// <param name="step">The using step.</param>
    /// <param name="label">The step, for messages.</param>
    /// <param name="problems">Where problems are added.</param>
    /// <returns>The step, or null when the common step is missing.</returns>
    private AbstractStepDefinition? ExpandUse(AbstractStepDefinition step, string label, List<string> problems)
    {
        var common = this.Library.Steps.Get(step.Use!);
        if (common is null)
        {
            var available = string.Join(", ", this.Library.Steps.List().Select(l => l.Step.Id));
            problems.Add($"{label}: no common step '{step.Use}'. Available: {available}.");
            return null;
        }

        var values = ResolveValues(common.Parameters, step.With, $"{label} (common step '{common.Id}')", problems, "set {0} in 'with'");
        var node = (JsonObject)SubstituteParameters(ToNode(common.Step), values)!;
        var overlay = (JsonObject)ToNode(step)!;
        foreach (var (name, value) in overlay.ToList())
        {
            if (name is "use" or WithPropertyName or "type" || IsEmpty(value))
            {
                continue;
            }

            node[name] = value!.DeepClone();
        }

        var result = FromNode<AbstractStepDefinition>(node);
        if (string.IsNullOrEmpty(result.Id))
        {
            result.Id = common.Id;
        }

        if (string.IsNullOrWhiteSpace(result.Name))
        {
            result.Name = common.Name;
        }

        var ownIds = new[] { common.Id, common.Step.Id }.Where(i => !string.IsNullOrEmpty(i)).ToHashSet();
        var loops = FromNode<AbstractStepDefinition>(SubstituteParameters(ToNode(common.Step), values)).Arrows?
            .Where(a => ownIds.Contains(a.To))
            .ToList() ?? [];
        if (loops.Count > 0)
        {
            foreach (var loop in loops)
            {
                loop.To = result.Id;
            }

            result.Arrows = [.. loops, .. (result.Arrows ?? []).Where(a => !ownIds.Contains(a.To) && a.To != result.Id)];
        }

        if (result.IsComposed)
        {
            problems.Add($"{label}: common step '{common.Id}' can't itself use a common step or include a workflow.");
            return null;
        }

        return result;
    }

    /// <summary>
    ///     Expands a step that includes another workflow: that workflow's expanded steps, with their ids prefixed by the
    ///     including step's id, their names by its name, and their references to each other rewritten. A step that would
    ///     end the included workflow (one with no arrows, or an arrow to <c>end</c>) follows the include step's own
    ///     arrows instead.
    /// </summary>
    /// <param name="step">The including step.</param>
    /// <param name="label">The step, for messages.</param>
    /// <param name="chain">The workflows being included that led here, outermost first.</param>
    /// <param name="problems">Where problems are added.</param>
    /// <returns>The included steps, empty on a problem, and the (prefixed) id of the one the included workflow starts at.</returns>
    private (List<AbstractStepDefinition> Steps, string? Start) ExpandInclude(AbstractStepDefinition step, string label, IReadOnlyList<string> chain, List<string> problems)
    {
        var extra = ((JsonObject)ToNode(step)!).Select(p => p.Key)
            .Where(k => k is not ("id" or "name" or "type" or "include" or "position" or WithPropertyName or ArrowConstants.ArrowsPropertyName))
            .ToList();
        if (extra.Count > 0)
        {
            problems.Add($"{label}: an include step only takes id, name, include, with and arrows; remove {string.Join(", ", extra.Select(e => $"'{e}'"))}.");
        }

        if (!WorkflowLibrary.IsValidId(step.Id))
        {
            problems.Add($"{label}: an include step needs an id, which prefixes the included step ids.");
            return ([], null);
        }

        if (chain.Contains(step.Include))
        {
            problems.Add($"{label}: including '{step.Include}' makes a cycle: {string.Join(" → ", chain.Append(step.Include!))}.");
            return ([], null);
        }

        if (chain.Count >= StepConstants.MaxCompositionDepth)
        {
            problems.Add($"{label}: includes are nested more than {StepConstants.MaxCompositionDepth} deep.");
            return ([], null);
        }

        var included = this.Library.Get(step.Include!);
        if (included is null)
        {
            problems.Add($"{label}: no workflow '{step.Include}' to include.");
            return ([], null);
        }

        var expanded = this.ExpandWorkflow(included, step.With, chain, problems, "set {0} in 'with'");
        var ids = expanded.Steps.ToDictionary(s => s.Id, s => $"{step.Id}-{s.Id}");
        var prefix = string.IsNullOrWhiteSpace(step.Name) ? expanded.Name : step.Name;
        var marker = AfterIncludeMarker + step.Id;
        var result = new List<AbstractStepDefinition>();
        foreach (var inner in expanded.Steps)
        {
            var node = Walk(ToNode(inner), (key, text) => key switch
            {
                null => RewriteTemplateIds(text, id => ids.GetValueOrDefault(id)),
                ArrowConstants.ToPropertyName when text == ArrowConstants.End => marker,
                _ => ids.GetValueOrDefault(text, text),
            });
            var copy = FromNode<AbstractStepDefinition>(node);
            copy.Id = ids[inner.Id];
            copy.Name = $"{prefix}: {inner.Name}";
            copy.Arrows = copy.Arrows is { Count: > 0 } arrows ? arrows : [new ArrowDefinition { To = marker }];
            result.Add(copy);
        }

        return (result, expanded.StartStepId is { } first ? ids.GetValueOrDefault(first) : null);
    }

    /// <summary>
    ///     Rewrites references to include steps, which no longer exist once expanded: an arrow to one goes to the step its
    ///     workflow starts at, and reading one (<c>{{steps.&lt;id&gt;.output}}</c>, a condition's <c>step</c>) reads its
    ///     last. Where an included workflow would end, the run follows the include step's own arrows.
    /// </summary>
    /// <param name="steps">The expanded steps.</param>
    /// <param name="includes">Each include, by the include step's id.</param>
    /// <param name="owner">The workflow, for messages.</param>
    /// <param name="problems">Where problems are added.</param>
    /// <returns>The rewritten steps.</returns>
    private static List<AbstractStepDefinition> RewriteIncludeReferences(List<AbstractStepDefinition> steps, IReadOnlyDictionary<string, IncludeSpan> includes,
        string owner, List<string> problems)
    {
        string Into(string id) => includes.TryGetValue(id, out var span) ? span.First : id;

        IReadOnlyList<ArrowDefinition> After(string marker) => includes[marker[AfterIncludeMarker.Length..]].Arrows;

        var result = steps.Select(step => FromNode<AbstractStepDefinition>(Walk(ToNode(step), (key, text) => key switch
        {
            null => RewriteTemplateIds(text, id => includes.TryGetValue(id, out var span) ? span.Last : null),
            _ when text.StartsWith(AfterIncludeMarker, StringComparison.Ordinal) => text,
            ArrowConstants.ToPropertyName or StepConstants.FromPropertyName or StepConstants.StartPropertyName => Into(text),
            _ => includes.TryGetValue(text, out var read) ? read.Last : text,
        }))).ToList();
        foreach (var step in result.Where(s => s.Arrows?.Any(a => a.To.StartsWith(AfterIncludeMarker, StringComparison.Ordinal)) == true))
        {
            step.Arrows = step.Arrows!.SelectMany(a => a.To.StartsWith(AfterIncludeMarker, StringComparison.Ordinal) ? Follow(a, After(a.To)) : [a]).ToList();
        }

        return result;
    }

    /// <summary>
    ///     Works out the values for a definition's parameters from what was given, reporting unknown keys and missing
    ///     values.
    /// </summary>
    /// <param name="parameters">The parameters the definition takes.</param>
    /// <param name="with">The values given.</param>
    /// <param name="owner">The definition, for messages.</param>
    /// <param name="problems">Where problems are added.</param>
    /// <param name="missingHint">How to give a missing value (formatted with its id); null to fill in samples.</param>
    /// <returns>The values, keyed <c>params.&lt;id&gt;</c>.</returns>
    private static Dictionary<string, string> ResolveValues(IReadOnlyList<ParameterDefinition>? parameters,
        IReadOnlyDictionary<string, string>? with, string owner, List<string> problems, string? missingHint)
    {
        var declared = parameters ?? [];
        foreach (var key in (with?.Keys ?? []).Where(k => declared.All(p => p.Id != k)))
        {
            var known = declared.Count == 0 ? "none" : string.Join(", ", declared.Select(p => p.Id));
            problems.Add($"{owner} has no parameter '{key}' (parameters: {known}).");
        }

        var values = new Dictionary<string, string>();
        foreach (var parameter in declared.DistinctBy(p => p.Id))
        {
            string? value = null;
            if (with?.TryGetValue(parameter.Id, out var given) == true)
            {
                value = given;
            }

            value ??= parameter.Default;
            if (value is null && missingHint is not null)
            {
                problems.Add($"{owner} needs parameter '{parameter.Id}'; {string.Format(missingHint, parameter.Id)}.");
            }

            values[StepConstants.ParameterPrefix + parameter.Id] = value ?? parameter.Id;
        }

        return values;
    }

    /// <summary>
    ///     Checks every workflow a workflow refers to (includes, child <c>workflow</c> steps, requirements, and those of
    ///     the common steps it uses) exists, without cycles or nesting too deep.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The problems.</returns>
    private IEnumerable<string> CheckReferences(WorkflowDefinition workflow)
    {
        var problems = new List<string>();
        var explored = new HashSet<string>();

        void Visit(WorkflowDefinition current, List<string> path)
        {
            foreach (var (verb, target) in this.ReferencedWorkflows(current))
            {
                if (path.Contains(target))
                {
                    problems.Add($"Workflow '{workflow.Id}' refers back to itself: {string.Join(" → ", path.Append(target))}.");
                    continue;
                }

                var next = this.Library.Get(target);
                if (next is null)
                {
                    var available = string.Join(", ", this.Library.List().Select(l => l.Workflow.Id));
                    problems.Add($"Workflow '{current.Id}' {verb} '{target}', which doesn't exist. Available: {available}.");
                    continue;
                }

                if (path.Count >= StepConstants.MaxCompositionDepth)
                {
                    problems.Add($"Workflow '{workflow.Id}' nests other workflows more than {StepConstants.MaxCompositionDepth} deep: {string.Join(" → ", path.Append(target))}.");
                    continue;
                }

                if (explored.Add(target))
                {
                    Visit(next, [.. path, target]);
                }
            }
        }

        Visit(workflow, [workflow.Id]);
        return problems;
    }

    /// <summary>
    ///     Returns whether a workflow leads to another through what it refers to (includes, child <c>workflow</c> steps,
    ///     requirements, and those of the common steps it uses), directly or through the workflows those refer to, so
    ///     referring to it from the other would make a cycle. A workflow always leads to itself.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="target">The id of the workflow it might lead to.</param>
    /// <returns>True if it leads there.</returns>
    public bool LeadsTo(WorkflowDefinition workflow, string target)
    {
        var explored = new HashSet<string> { workflow.Id };
        var pending = new Stack<WorkflowDefinition>([workflow]);
        while (pending.TryPop(out var current))
        {
            if (current.Id == target)
            {
                return true;
            }

            foreach (var (_, next) in this.ReferencedWorkflows(current))
            {
                if (next == target)
                {
                    return true;
                }

                if (explored.Add(next) && this.Library.Get(next) is { } found)
                {
                    pending.Push(found);
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Lists the workflows a workflow refers to directly, with how.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The verb ("includes", "starts", "requires") and workflow id of each reference.</returns>
    private IEnumerable<(string Verb, string Target)> ReferencedWorkflows(WorkflowDefinition workflow)
    {
        foreach (var requirement in workflow.Requires ?? [])
        {
            yield return ("requires", requirement.Workflow);
        }

        foreach (var step in workflow.Steps)
        {
            var common = step.Use is null ? null : this.Library.Steps.Get(step.Use);
            var type = common?.Step.Type ?? step.Type;
            var child = (step as WorkflowStepDefinition)?.Workflow ?? (common?.Step as WorkflowStepDefinition)?.Workflow;
            if (step.Include is not null)
            {
                yield return ("includes", step.Include);
            }
            else if (type == StepConstants.TypeWorkflow && !string.IsNullOrWhiteSpace(child) && PromptTemplate.References(child).Count == 0)
            {
                yield return ("starts", child);
            }
        }
    }

    /// <summary>
    ///     Checks the <c>with</c> and <c>childOutput</c> of each child <c>workflow</c> step against the workflow it starts:
    ///     once for each item when its <c>forEach</c> items are known before the run (so e.g. a templated <c>use</c> in
    ///     the child is checked for every value), otherwise once as written.
    /// </summary>
    /// <param name="expanded">The expanded workflow.</param>
    /// <returns>The problems.</returns>
    private IEnumerable<string> ValidateChildSteps(WorkflowDefinition expanded)
    {
        foreach (var step in expanded.Steps.OfType<WorkflowStepDefinition>().Where(s => s.Workflow is not null))
        {
            var child = this.Library.Get(step.Workflow!);
            if (child is null)
            {
                continue;
            }

            var label = $"Step '{step.Id}'";
            var problems = new List<string>();
            var items = step.ForEach is { } source && ChildSourceRegistry.Instance.Contains(source.Kind)
                ? ChildSourceRegistry.Instance.Create(source.Kind).KnownItems(source)
                : null;
            var placeholder = "{{" + StepConstants.ItemVariable + "}}";
            WorkflowDefinition? childExpanded = null;
            foreach (var item in items ?? [placeholder])
            {
                var sample = step.With?.ToDictionary(p => p.Key, p => p.Value.Replace(placeholder, item, StringComparison.Ordinal));
                childExpanded = this.ExpandWorkflow(child, sample, [], problems, "set {0} in 'with'");
            }

            foreach (var problem in problems.Distinct())
            {
                yield return $"{label}: {problem}";
            }

            if (step.ChildOutput is not null && childExpanded is not null && childExpanded.Steps.All(s => s.Id != step.ChildOutput))
            {
                yield return $"{label}: childOutput '{step.ChildOutput}' isn't a step of '{child.Id}'. Its steps: {string.Join(", ", childExpanded.Steps.Select(s => s.Id))}.";
            }
        }
    }

    /// <summary>
    ///     Returns whether a JSON value is empty: null, an empty string, list or object.
    /// </summary>
    /// <param name="node">The value.</param>
    /// <returns>True if empty.</returns>
    private static bool IsEmpty(JsonNode? node)
    {
        return node switch
        {
            null => true,
            JsonArray array => array.Count == 0,
            JsonObject obj => obj.Count == 0,
            JsonValue value when value.TryGetValue<string>(out var text) => text.Length == 0,
            _ => false,
        };
    }

    /// <summary>
    ///     Rewrites the step ids in <c>{{steps.&lt;id&gt;.…}}</c> placeholders.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="map">The new id for an id, or null to leave it.</param>
    /// <returns>The rewritten text.</returns>
    private static string RewriteTemplateIds(string text, Func<string, string?> map)
    {
        return StepPlaceholderRegex().Replace(text, m => m.Groups["pre"].Value + (map(m.Groups["id"].Value) ?? m.Groups["id"].Value));
    }

    /// <summary>
    ///     Visits every string in a JSON document, replacing it with what <paramref name="rewrite" /> returns. Strings
    ///     held by a step reference property (<see cref="StepConstants.StepReferenceProperties" />), directly or in a
    ///     list, are passed with that property's name, as are the step ids in a <c>waitFor</c> group (with
    ///     <c>waitFor</c>); every other string, including the keys and values of <c>with</c>, with null.
    /// </summary>
    /// <param name="node">The document; changed in place.</param>
    /// <param name="rewrite">Takes the reference property (or null) and the string, and returns its replacement.</param>
    /// <param name="key">The property holding <paramref name="node" />, when it's a step reference property.</param>
    /// <param name="inWith">Whether <paramref name="node" /> is inside a <c>with</c>.</param>
    /// <returns>The node, or its replacement.</returns>
    private static JsonNode? Walk(JsonNode? node, Func<string?, string, string> rewrite, string? key = null, bool inWith = false)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    // Inside a waitFor every string is a step id, except the shorthand for a group ("all", "any").
                    var reference = inWith ? null
                        : key == ArrowConstants.WaitForPropertyName ? key
                        : name == ArrowConstants.WaitForPropertyName ? IsWaitShorthand(value) ? null : name
                        : StepConstants.StepReferenceProperties.Contains(name) ? name : null;
                    var replaced = Walk(value, rewrite, reference, inWith || name == WithPropertyName);
                    if (!ReferenceEquals(replaced, value))
                    {
                        obj[name] = replaced;
                    }
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    var replaced = Walk(item, rewrite, key, inWith);
                    if (!ReferenceEquals(replaced, item))
                    {
                        array[i] = replaced;
                    }
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                var result = rewrite(key, text);
                return result == text ? value : JsonValue.Create(result);
            default:
                return node;
        }
    }

    /// <summary>
    ///     Returns whether a <c>waitFor</c> is written as the shorthand for a group over every arrow in, e.g. <c>"all"</c>.
    /// </summary>
    /// <param name="node">The <c>waitFor</c>.</param>
    /// <returns>True if it is.</returns>
    private static bool IsWaitShorthand(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) && WaitRuleRegistry.Instance.GroupKeys.Contains(text);
    }

    /// <summary>
    ///     Matches the start of a <c>{{steps.&lt;id&gt;.</c> placeholder.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"(?<pre>\{\{\s*steps\.)(?<id>[a-z0-9][a-z0-9-]*)(?=\.)")]
    private static partial Regex StepPlaceholderRegex();

    /// <summary>
    ///     Where an include's steps begin and end once expanded, and where it goes on to.
    /// </summary>
    /// <param name="First">The step its workflow starts at.</param>
    /// <param name="Last">Its last step, whose output is the include's.</param>
    /// <param name="Arrows">The include step's own arrows.</param>
    private sealed record IncludeSpan(string First, string Last, IReadOnlyList<ArrowDefinition> Arrows);

    /// <summary>
    ///     Turns an included step's arrow out of its workflow into the include step's own arrows: each keeps where the
    ///     include's arrow goes (and its loop limits), and the inner arrow's outcome and condition when it has them. With
    ///     no arrows on the include, the path ends there.
    /// </summary>
    /// <param name="inner">The included step's arrow out of its workflow.</param>
    /// <param name="after">The include step's arrows, already pointing at expanded steps.</param>
    /// <returns>The arrows.</returns>
    private static IEnumerable<ArrowDefinition> Follow(ArrowDefinition inner, IReadOnlyList<ArrowDefinition> after)
    {
        if (after.Count == 0)
        {
            var end = inner.Clone();
            end.To = ArrowConstants.End;
            yield return end;
            yield break;
        }

        foreach (var outer in after)
        {
            var arrow = outer.Clone();
            if (!string.IsNullOrEmpty(inner.On))
            {
                arrow.On = inner.On;
            }

            if (inner.IsConditional)
            {
                arrow.When = inner.When;
                arrow.Until = inner.Until;
                arrow.Label = inner.Label;
            }

            yield return arrow;
        }
    }
}
