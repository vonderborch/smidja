using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.Steps;

namespace Afl.Core.Workflows;

/// <summary>
///     The common steps available to a repo, which workflows <c>use</c>. Lookup is layered like workflows: the repo's
///     own <c>.afl/steps</c>, then the global library in <c>~/.afl/steps</c>, then the built-ins embedded in afl.
/// </summary>
/// <param name="paths">The global store paths.</param>
/// <param name="repoPath">The repo root whose own common steps should be included, if any.</param>
public class CommonStepLibrary(AflPaths paths, string? repoPath = null)
    : AbstractLayeredLibrary<CommonStepDefinition, CommonStepListing>(paths.StepsDirectory,
        repoPath is null ? null : AflPaths.RepoStepsDirectory(repoPath))
{
    /// <inheritdoc />
    protected override string ResourcePrefix => PathConstants.BuiltInStepResourcePrefix;

    /// <inheritdoc />
    protected override string Noun => "common step";

    /// <inheritdoc />
    protected override CommonStepListing CreateListing(CommonStepDefinition document, WorkflowScope scope, string? path)
    {
        return new CommonStepListing(document, scope, path);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> ValidateForSave(CommonStepDefinition document)
    {
        return Validate(document);
    }

    /// <summary>
    ///     Checks a common step for problems: its id and name, its parameters, and the step itself with every parameter
    ///     filled in by its default (or its id, for ones without a default). References to other steps are checked where
    ///     the step is used, since they depend on the workflow.
    /// </summary>
    /// <param name="document">The common step.</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(CommonStepDefinition document)
    {
        var errors = new List<string>();
        if (!WorkflowLibrary.IsValidId(document.Id))
        {
            errors.Add("Common step id must be lowercase letters, digits and dashes.");
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            errors.Add("Common step name is required.");
        }

        errors.AddRange(document.ValidateTags().Select(p => $"Common step {p}"));

        errors.AddRange(WorkflowComposer.ValidateParameters(document.Parameters));
        errors.AddRange(WorkflowComposer.UndeclaredParameters(WorkflowComposer.ToNode(document.Step), document.Parameters)
            .Select(p => $"'{{{{{StepConstants.ParameterPrefix}{p}}}}}' isn't one of the step's parameters."));
        if (document.Step.IsComposed)
        {
            errors.Add("A common step can't itself 'use' a common step or 'include' a workflow.");
            return errors;
        }

        if (!string.IsNullOrEmpty(document.Step.Id) && !WorkflowLibrary.IsValidId(document.Step.Id))
        {
            errors.Add("Step id must be lowercase letters, digits and dashes.");
        }

        var values = WorkflowComposer.ValuesWithDefaults(document.Parameters);
        var step = WorkflowComposer.FromNode<AbstractStepDefinition>(WorkflowComposer.SubstituteParameters(WorkflowComposer.ToNode(document.Step), values));
        if (!StepHandlerRegistry.Instance.Contains(step.Type))
        {
            errors.Add($"Unknown type '{step.Type}'. Known: {string.Join(", ", StepHandlerRegistry.Instance.Keys.Order())}.");
            return errors;
        }

        errors.AddRange(StepHandlerRegistry.Instance.Create(step.Type).ValidateAll(step));
        if (step.Artifact is not null)
        {
            errors.AddRange(WorkflowLibrary.ValidateArtifact(step.Artifact).Select(e => $"artifact {e}"));
        }

        return errors;
    }
}
