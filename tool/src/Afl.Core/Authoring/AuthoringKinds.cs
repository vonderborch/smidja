using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.Workflows;

namespace Afl.Core.Authoring;

/// <summary>
///     Drafts skills: instructions an agent follows during a step, kept in the <see cref="SkillLibrary" />.
/// </summary>
[AuthoringKindRegistration(AuthoringConstants.KindSkill)]
public class SkillAuthoringKind() : AbstractAuthoringKind<SkillDefinition, SkillListing, SkillLibrary>(AuthoringConstants.KindSkill)
{
    /// <inheritdoc />
    public override string Noun => "skill";

    /// <inheritdoc />
    public override IReadOnlyList<string> ReferenceFiles => ["skill.schema.json"];

    /// <inheritdoc />
    public override string ExampleId => "test-first";

    /// <inheritdoc />
    public override string Guide =>
        """
        A skill is Markdown instructions (`content`) that an agent follows while it works on a step, plus a short
        `description` that says what it does and when to use it (agents and people pick skills by it), and `tags`.
        - Keep `content` focused and practical: rules, steps and short examples the agent can act on, not an essay.
        - Use `recommend` only when there's a clear signal (files, languages or frameworks) that the skill fits a repo.
        - `files` holds supporting files by relative path, for templates or checklists `content` points at; most skills
          need none.
        """;

    /// <inheritdoc />
    public override SkillLibrary CreateLibrary(AflPaths paths, string? repoPath)
    {
        return new SkillLibrary(paths, repoPath);
    }

    /// <inheritdoc />
    protected override string Detail(SkillDefinition document)
    {
        var files = document.Files?.Count ?? 0;
        var lines = document.Content.Split('\n').Length;
        return files == 0 ? $"{lines} lines of instructions" : $"{lines} lines of instructions, {files} supporting file(s)";
    }
}

/// <summary>
///     Drafts common steps: steps workflows reuse with <c>use</c>, kept in the <see cref="CommonStepLibrary" />.
/// </summary>
[AuthoringKindRegistration(AuthoringConstants.KindStep)]
public class CommonStepAuthoringKind()
    : AbstractAuthoringKind<CommonStepDefinition, CommonStepListing, CommonStepLibrary>(AuthoringConstants.KindStep)
{
    /// <inheritdoc />
    public override string Noun => "common step";

    /// <inheritdoc />
    public override IReadOnlyList<string> ReferenceFiles => ["common-step.schema.json", "workflow.schema.json", "README.md"];

    /// <inheritdoc />
    public override string ExampleId => "code-review";

    /// <inheritdoc />
    public override string Guide =>
        """
        A common step is one step that workflows reuse with `"use": "<id>"`, with `parameters` the workflow fills in
        (`{{params.<id>}}` in the step). Its `step` is an ordinary workflow step: see workflow.schema.json for the step
        fields and README.md ("Common steps and composition", "Steps" and "Template variables") for how they behave.
        - Give every parameter a sensible `default`, so the step works when a workflow just uses it.
        - A common step can't itself `use` another common step or `include` a workflow.
        """;

    /// <inheritdoc />
    public override CommonStepLibrary CreateLibrary(AflPaths paths, string? repoPath)
    {
        return new CommonStepLibrary(paths, repoPath);
    }

    /// <inheritdoc />
    protected override string Detail(CommonStepDefinition document)
    {
        var parameters = document.Parameters?.Count ?? 0;
        return $"{document.Step.Type} step, {parameters} parameter{(parameters == 1 ? string.Empty : "s")}";
    }
}

/// <summary>
///     Drafts workflows, kept in the <see cref="WorkflowLibrary" />.
/// </summary>
[AuthoringKindRegistration(AuthoringConstants.KindWorkflow)]
public class WorkflowAuthoringKind()
    : AbstractAuthoringKind<WorkflowDefinition, WorkflowListing, WorkflowLibrary>(AuthoringConstants.KindWorkflow)
{
    /// <inheritdoc />
    public override string Noun => "workflow";

    /// <inheritdoc />
    public override IReadOnlyList<string> ReferenceFiles => ["workflow.schema.json", "README.md"];

    /// <inheritdoc />
    public override string ExampleId => "bugfix";

    /// <inheritdoc />
    public override string Guide =>
        """
        A workflow is the steps a run goes through, with arrows between them. README.md explains every field; read its
        "Steps", "Common steps and composition", "Flow" and "Template variables" sections before writing one.
        - Reuse what's there: `"use": "<id>"` a common step from the catalog, and name skills from it, rather than
          writing your own versions.
        - Steps that change code usually come after one that plans or asks, and before one that reviews; let the user
          approve (`"advance": "approve"`) where their judgement matters.
        - Give each step only the tools it needs (`allowedTools`), and the least `permissionMode` that works.
        """;

    /// <inheritdoc />
    public override WorkflowLibrary CreateLibrary(AflPaths paths, string? repoPath)
    {
        return new WorkflowLibrary(paths, repoPath);
    }

    /// <inheritdoc />
    protected override string Detail(WorkflowDefinition document)
    {
        return $"{document.Steps.Count} step{(document.Steps.Count == 1 ? string.Empty : "s")}";
    }
}
