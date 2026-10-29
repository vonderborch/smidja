using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Steps;
using Afl.Core.Storage;

namespace Afl.Core.Workflows;

/// <summary>
///     The workflows available to a repo. Lookup is layered: the repo's own <c>.afl/workflows</c>, then the global
///     library in <c>~/.afl/workflows</c>, then the built-ins embedded in afl. <see cref="Steps" /> is the matching
///     library of common steps.
/// </summary>
/// <param name="paths">The global store paths.</param>
/// <param name="repoPath">The repo root whose own workflows and common steps should be included, if any.</param>
public partial class WorkflowLibrary(AflPaths paths, string? repoPath = null)
    : AbstractLayeredLibrary<WorkflowDefinition, WorkflowListing>(paths.WorkflowsDirectory,
        repoPath is null ? null : AflPaths.RepoWorkflowsDirectory(repoPath))
{
    /// <summary>
    ///     Gets the common steps available to the same repo.
    /// </summary>
    public CommonStepLibrary Steps { get; } = new(paths, repoPath);

    /// <summary>
    ///     Gets the common skills that steps can give their agents.
    /// </summary>
    public SkillLibrary Skills { get; } = new(paths, repoPath);

    /// <inheritdoc />
    protected override string ResourcePrefix => PathConstants.BuiltInWorkflowResourcePrefix;

    /// <inheritdoc />
    protected override string Noun => "workflow";

    /// <inheritdoc />
    protected override WorkflowListing CreateListing(WorkflowDefinition document, WorkflowScope scope, string? path)
    {
        return new WorkflowListing(document, scope, path);
    }

    /// <summary>
    ///     Checks a workflow before it is saved, resolving the common steps and workflows it uses against this library.
    /// </summary>
    /// <param name="document">The workflow.</param>
    /// <returns>The problems; empty when it is valid.</returns>
    public override IReadOnlyList<string> ValidateForSave(WorkflowDefinition document)
    {
        return InternalStepProblems(document).Concat(new WorkflowComposer(this).Validate(document)).ToList();
    }

    /// <summary>
    ///     Checks that a workflow people wrote uses none of afl's internal step types
    ///     (<see cref="AbstractStepHandler.IsInternal" />), which only workflows afl builds itself may use.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The problems; empty when it uses none.</returns>
    public static IEnumerable<string> InternalStepProblems(WorkflowDefinition workflow)
    {
        return workflow.Steps
            .Where(s => StepHandlerRegistry.Instance.IsInternal(s.Type))
            .Select(s => $"Step '{s.Id}': type '{s.Type}' is internal to afl. Use one of {string.Join(", ", StepHandlerRegistry.Instance.PublicKeys)}.");
    }

    /// <summary>
    ///     Checks a step's artifact path template: it must be a relative path that stays inside the repo.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IEnumerable<string> ValidateArtifact(string template)
    {
        var path = template.Trim().Replace('\\', '/');
        if (path.Length == 0 || path.EndsWith('/'))
        {
            yield return "must name a file, e.g. docs/afl/{{ticket.slug}}/DESIGN.md.";
        }
        else if (Path.IsPathRooted(path) || path.StartsWith('~'))
        {
            yield return "must be relative to the repo.";
        }
        else if (path.Split('/').Any(part => part is ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            yield return "must stay inside the repo (no '..' or '.git').";
        }
    }

    /// <summary>
    ///     Returns the full path of a rendered artifact path, or null when it would leave the repo or go into
    ///     <c>.git</c>.
    /// </summary>
    /// <param name="repoPath">The repo.</param>
    /// <param name="relative">The rendered path, relative to the repo.</param>
    /// <returns>The full path, or null.</returns>
    public static string? ResolveArtifactPath(string repoPath, string relative)
    {
        if (ValidateArtifact(relative).Any())
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    ///     Checks a workflow for problems.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The problems found; empty when the workflow is valid.</returns>
    public static IReadOnlyList<string> Validate(WorkflowDefinition workflow)
    {
        var errors = new List<string>();
        if (!IdRegex().IsMatch(workflow.Id))
        {
            errors.Add("Workflow id must be lowercase letters, digits and dashes.");
        }

        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            errors.Add("Workflow name is required.");
        }

        if (workflow.Steps.Count == 0)
        {
            errors.Add("A workflow needs at least one step.");
        }

        if (workflow.Parameters is not null || workflow.Requires is not null)
        {
            errors.Add("The workflow takes parameters or requires other workflows; expand it first (WorkflowComposer).");
        }

        var listed = new List<AbstractStepDefinition>();
        var machine = new FlowMachine(workflow);
        foreach (var step in workflow.Steps)
        {
            var label = string.IsNullOrEmpty(step.Id) ? $"Step {workflow.Steps.IndexOf(step) + 1}" : $"Step '{step.Id}'";
            if (step.IsComposed)
            {
                errors.Add($"{label}: uses a common step or includes a workflow; expand it first (WorkflowComposer).");
                listed.Add(step);
                continue;
            }

            if (!IdRegex().IsMatch(step.Id))
            {
                errors.Add($"{label}: id must be lowercase letters, digits and dashes.");
            }
            else if (listed.Any(s => s.Id == step.Id))
            {
                errors.Add($"{label}: id is used by an earlier step.");
            }

            var earlierSteps = TemplateVariables.StepsBefore(machine, step);
            var possible = TemplateVariables.StepsMaybeBefore(machine, step);

            // The type is always known here: an unknown one fails when the step is read.
            var handler = StepHandlerRegistry.Instance.For(step);
            errors.AddRange(handler.ValidateAll(step).Concat(handler.ValidateInWorkflow(workflow, step)).Select(e => $"{label}: {e}"));

            var agentStep = step as AbstractAgentStepDefinition;
            var agentIds = new[] { step.Agent, agentStep?.MergeAgent }.Concat(agentStep?.Agents ?? []).OfType<string>();
            foreach (var agent in agentIds.Where(a => !IdRegex().IsMatch(a)))
            {
                errors.Add($"{label}: agent '{agent}' isn't an agent id (see afl agent list).");
            }

            foreach (var reference in step.Templates().SelectMany(PromptTemplate.References))
            {
                if (!TemplateVariables.IsAvailable(reference, earlierSteps, workflow.Steps, step, possible))
                {
                    errors.Add($"{label}: '{{{{{reference}}}}}' is not available here. {TemplateVariables.Describe(earlierSteps, workflow.Steps, step, possible)}");
                }
            }

            foreach (var input in step.Inputs ?? [])
            {
                // A before input is asked ahead of the step's other before inputs being answered, so only those
                // asked later can use them.
                var own = input.Timing == StepConstants.WhenBefore ? null : step;
                foreach (var reference in PromptTemplate.References(input.Default).Where(r => !TemplateVariables.IsAvailable(r, earlierSteps, workflow.Steps, own, possible)))
                {
                    errors.Add($"{label}: input '{input.Id}' default: '{{{{{reference}}}}}' is not available here. {TemplateVariables.Describe(earlierSteps, workflow.Steps, own, possible)}");
                }
            }

            foreach (var reference in PromptTemplate.References(agentStep?.MergePrompt))
            {
                if (reference != TemplateVariables.Candidates && !TemplateVariables.IsAvailable(reference, earlierSteps, workflow.Steps, step, possible))
                {
                    errors.Add(
                        $"{label}: '{{{{{reference}}}}}' is not available in the merge prompt. {TemplateVariables.Describe(earlierSteps, workflow.Steps, step, possible)}, {TemplateVariables.Candidates}");
                }
            }

            var ownInputs = (step.Inputs ?? []).Select(i => TemplateVariables.OwnInput(i.Id)).Append(TemplateVariables.Output).ToHashSet();
            foreach (var reference in PromptTemplate.References(step.OutputTemplate)
                         .Where(r => !ownInputs.Contains(r) && !TemplateVariables.IsAvailable(r, earlierSteps, workflow.Steps, step, possible)))
            {
                errors.Add(
                    $"{label}: outputTemplate: '{{{{{reference}}}}}' is not available here. {TemplateVariables.Describe(earlierSteps, workflow.Steps, step, possible)}, {string.Join(", ", ownInputs)}");
            }

            if (step.Artifact is not null)
            {
                errors.AddRange(ValidateArtifact(step.Artifact).Select(e => $"{label}: artifact {e}"));
                foreach (var reference in PromptTemplate.References(step.Artifact).Where(r => !TemplateVariables.IsAvailable(r, earlierSteps, workflow.Steps, step, possible)))
                {
                    errors.Add($"{label}: artifact: '{{{{{reference}}}}}' is not available here. {TemplateVariables.Describe(earlierSteps, workflow.Steps, step, possible)}");
                }
            }

            errors.AddRange(FlowRouter.Instance.Validate(workflow, step).Select(e => $"{label}: {e}"));

            listed.Add(step);
        }

        if (!workflow.IsComposed && workflow.Steps.Count > 0)
        {
            errors.AddRange(FlowRouter.Instance.ValidateFlow(workflow));
        }

        return errors;
    }

    /// <summary>
    ///     Lists things about a workflow that are allowed but probably not meant, such as steps the run can never reach.
    /// </summary>
    /// <param name="workflow">The workflow, expanded.</param>
    /// <returns>The warnings.</returns>
    public static IReadOnlyList<string> Warnings(WorkflowDefinition workflow)
    {
        return workflow.IsComposed ? [] : FlowRouter.Instance.Warnings(workflow);
    }

    /// <summary>
    ///     Validates a workflow and throws listing every problem.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <exception cref="AflException">The workflow is invalid.</exception>
    public static void ThrowIfInvalid(WorkflowDefinition workflow)
    {
        var errors = Validate(workflow);
        if (errors.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidWorkflow,
                $"Workflow '{workflow.Id}' is invalid:\n  - {string.Join("\n  - ", errors)}");
        }
    }

    /// <summary>
    ///     Returns whether an id is valid for a workflow, step or step input: lowercase letters, digits and dashes.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True if valid.</returns>
    public static bool IsValidId(string? id)
    {
        return id is not null && IdRegex().IsMatch(id);
    }

    /// <summary>
    ///     Matches valid workflow and step ids.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdRegex();
}
