using Afl.Core.Authoring;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     The AI authoring helper's step: the AI drafts a skill, common step or workflow into a draft file, guided by the
///     kind's reference files. When it finishes, the draft is checked as its library would check it; while it has
///     problems, the same conversation is sent back to fix them, up to <see cref="AuthorDraftStepDefinition.MaxFixRounds" />
///     times (<see cref="Runs.RunStep.CheckRounds" />). Then the step waits for the user as usual. Internal: people can't
///     use it in their own workflows.
/// </summary>
[StepHandlerRegistration(StepConstants.TypeAuthorDraft)]
public class AuthorDraftStepHandler : AiStepHandler
{
    /// <inheritdoc />
    public override Type DefinitionType => typeof(AuthorDraftStepDefinition);

    /// <inheritdoc />
    public override string DisplayName => "Draft a document";

    /// <inheritdoc />
    public override string Summary => "An agent drafts a skill, common step or workflow for you to review.";

    /// <inheritdoc />
    public override bool IsInternal => true;

    /// <summary>
    ///     Returns the draft file a step writes to.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The path.</returns>
    public static string DraftPathOf(StepContext context)
    {
        return Path.Combine(context.Run.WorkingPath ?? context.Run.RepoPath, Draft(context).DraftFile);
    }

    /// <summary>
    ///     Checks a step's draft as its library would before saving it.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="AflException">The step has no engine to find the afl home from.</exception>
    public static AuthoringDraftCheck CheckDraft(StepContext context)
    {
        var definition = Draft(context);
        var engine = context.Engine ?? throw new AflException(AflErrorKind.InvalidState, "An author-draft step needs the run engine.");
        return AuthoringKindRegistry.Instance.Create(definition.Kind).Check(engine.Store.Paths, definition.LibraryRepoPath, DraftPathOf(context));
    }

    /// <inheritdoc />
    public override Task StartAsync(StepContext context, CancellationToken ct)
    {
        return this.RunAiAsync(context, PromptOf(context), false, ct);
    }

    /// <inheritdoc />
    public override Task ReworkAsync(StepContext context, string feedback, CancellationToken ct)
    {
        var resume = CanResume(context);
        var prompt = resume ? ReworkPrompt(context, feedback, true) : $"{PromptOf(context)}\n\n{StepConstants.ReworkHeading}\n\n{feedback}";
        return this.RunAiAsync(context, prompt, resume, ct);
    }

    /// <inheritdoc />
    protected override IEnumerable<string> Validate(AbstractAgentStepDefinition step)
    {
        foreach (var problem in base.Validate(step))
        {
            yield return problem;
        }

        if (step is not AuthorDraftStepDefinition draft)
        {
            yield break;
        }

        if (!AuthoringKindRegistry.Instance.Contains(draft.Kind))
        {
            yield return $"unknown kind '{draft.Kind}'. Use one of {string.Join(", ", AuthoringKindRegistry.Instance.Keys.Order())}.";
        }

        if (string.IsNullOrWhiteSpace(draft.DraftFile) || Path.IsPathRooted(draft.DraftFile) || draft.DraftFile.Split('/', '\\').Contains(".."))
        {
            yield return "'draftFile' must be a file name inside where the run works.";
        }

        if (draft.MaxFixRounds < 0)
        {
            yield return "'maxFixRounds' can't be negative.";
        }
    }

    /// <summary>
    ///     Checks the draft once the AI is done: a draft with problems goes back to the AI, while rounds are left; any
    ///     other finishes the step, with a note on problems left over.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected override async Task OnAiCompletedAsync(StepContext context, CancellationToken ct)
    {
        var definition = Draft(context);
        var state = context.State;
        var check = CheckDraft(context);
        if (check.IsValid || state.CheckRounds >= definition.MaxFixRounds)
        {
            if (!check.IsValid)
            {
                state.Notes.Add($"The draft still has {Problems(check.Problems.Count)} after {state.CheckRounds} round(s) of fixes.");
            }

            await this.FinishAsync(context, state.Output, ct);
            return;
        }

        state.CheckRounds++;
        state.Notes.Add($"Sent the draft back to fix {Problems(check.Problems.Count)} (round {state.CheckRounds} of {definition.MaxFixRounds}).");
        context.Report($"The draft has {Problems(check.Problems.Count)}; asking for fixes.");
        var fix = FixPrompt(definition, check);
        var resume = CanResume(context);
        await this.RunAiAsync(context, resume ? fix : $"{PromptOf(context)}\n\n{fix}", resume, ct, newAttempt: false);
    }

    /// <summary>
    ///     Builds the step's whole prompt: the step's own (rendered), then how to write the kind of document, which isn't
    ///     rendered, since the kind's guide quotes template syntax.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The prompt.</returns>
    private static string PromptOf(StepContext context)
    {
        var definition = Draft(context);
        var kind = AuthoringKindRegistry.Instance.Create(definition.Kind);
        var reference = AuthoringConstants.ReferenceFolderName;
        var files = kind.ReferenceFiles
            .Append(AbstractAuthoringKind.ExampleFileName)
            .Append(AbstractAuthoringKind.CatalogFileName)
            .Select(f => $"`{reference}/{f}`");
        return $"""
                {context.Render(definition.Prompt)}

                ## How to write the {kind.Noun}

                Read these first: {string.Join(", ", files)}. `{AbstractAuthoringKind.ExampleFileName}` is a built-in
                {kind.Noun} to follow, and `{AbstractAuthoringKind.CatalogFileName}` lists what's already in the library,
                so reuse what's there rather than writing it again.

                {kind.Guide}

                ## Working

                - Research on the web only where it helps (WebSearch is allowed; fetching a page asks the user first).
                - When something that matters is unclear, ask the user rather than guess.
                - Write the {kind.Noun} as JSON to `./{definition.DraftFile}`, with `$schema` and `schemaVersion` as in the
                  example. Write nothing anywhere else.
                - afl checks the draft when you finish and sends any problems back to you.
                - End with a short summary for the user: what you wrote, why, and anything they should check.
                """;
    }

    /// <summary>
    ///     Builds the prompt that sends a draft's problems back to the AI.
    /// </summary>
    /// <param name="definition">The step.</param>
    /// <param name="check">What the draft check found.</param>
    /// <returns>The prompt.</returns>
    private static string FixPrompt(AuthorDraftStepDefinition definition, AuthoringDraftCheck check)
    {
        return $"afl checked `./{definition.DraftFile}` and it can't be saved yet:\n\n" +
               string.Join("\n", check.Problems.Select(p => $"- {p}")) +
               "\n\nFix the draft file, then end with the same kind of short summary for the user.";
    }

    /// <summary>
    ///     Counts problems in words, e.g. <c>2 problems</c>.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <returns>The words.</returns>
    private static string Problems(int count)
    {
        return count == 1 ? "1 problem" : $"{count} problems";
    }

    /// <summary>
    ///     Returns a context's step as an <c>author-draft</c> step.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <returns>The step.</returns>
    /// <exception cref="AflException">The step isn't one.</exception>
    private static AuthorDraftStepDefinition Draft(StepContext context)
    {
        return context.Definition as AuthorDraftStepDefinition ?? throw new AflException(AflErrorKind.InvalidState,
            $"Step '{context.Definition.Id}' isn't an author-draft step.");
    }
}
