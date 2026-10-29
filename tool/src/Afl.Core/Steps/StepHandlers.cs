using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     The AI does the step on its own, headless. With <c>advance: approve</c> the user then approves the output or
///     rejects it with feedback, and a rejection re-runs the step in the same conversation with the feedback.
/// </summary>
[StepHandlerRegistration(StepConstants.TypeAi)]
public class AiStepHandler : AbstractAiStepHandler
{
    /// <inheritdoc />
    public override Type DefinitionType => typeof(AiStepDefinition);

    /// <inheritdoc />
    public override string DisplayName => "AI step";

    /// <inheritdoc />
    public override string Summary => "An agent does it on its own, in the background.";
}

/// <summary>
///     The user does the step, following its instructions, and marks it done, answering its inputs. Its output is the
///     user's note followed by each answer as <c>Label: value</c>.
/// </summary>
[StepHandlerRegistration(StepConstants.TypeUser)]
public class UserStepHandler : AbstractStepHandler<UserStepDefinition>
{
    /// <inheritdoc />
    public override string DisplayName => "Manual step";

    /// <inheritdoc />
    public override string Summary => "You do it yourself, following its instructions, then mark it done.";

    /// <inheritdoc />
    protected override bool PersonFinishes => true;

    /// <inheritdoc />
    protected override IEnumerable<string> Validate(UserStepDefinition step)
    {
        if (string.IsNullOrWhiteSpace(step.Instructions) && (step.Inputs ?? []).Count == 0)
        {
            yield return "a user step needs 'instructions' or 'inputs'.";
        }

        if (step.Agent is not null)
        {
            yield return "a user step isn't done by an agent; remove 'agent'.";
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(StepContext context, CancellationToken ct)
    {
        context.State.PendingPrompt = context.Render(DefinitionOf(context).Instructions);
        Move(context, StepStatus.AwaitingUser);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Reopens the step for the user, with the feedback after its instructions.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public override Task ReworkAsync(StepContext context, string feedback, CancellationToken ct)
    {
        var instructions = context.Render(DefinitionOf(context).Instructions);
        context.State.PendingPrompt = string.Join("\n\n",
            new[] { instructions, StepConstants.ReworkHeading, feedback }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Move(context, StepStatus.AwaitingUser);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task CompleteAsync(StepContext context, string? note, IReadOnlyDictionary<string, string>? answers, CancellationToken ct)
    {
        Require(context, StepTrigger.Complete, "marked done");
        this.AcceptAnswersOnDone(context, answers);
        return this.FinishAsync(context, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ct);
    }

    /// <summary>
    ///     Builds the output from the user's note followed by each answer as <c>Label: value</c>, or "Done.".
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="output">The user's note, if any.</param>
    /// <returns>The output.</returns>
    protected override string? ComposeOutput(StepContext context, string? output)
    {
        var lines = (DefinitionOf(context).Inputs ?? [])
            .Where(i => context.State.Answers.ContainsKey(i.Id))
            .Select(i => $"{i.Label}: {context.State.Answers[i.Id]}");
        var composed = string.Join("\n\n", new[] { output, string.Join("\n", lines) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return composed.Length > 0 ? composed : "Done.";
    }
}

/// <summary>
///     The user works through the step with the AI in an interactive session, then marks it done. The step output is a
///     summary the AI writes by resuming that session headlessly (or the user's note, if that isn't possible). With the
///     session executor the calling Claude session is the interactive session, and submits the output itself.
/// </summary>
[StepHandlerRegistration(StepConstants.TypeInteractive)]
public class InteractiveStepHandler : AbstractAiStepHandler
{
    /// <inheritdoc />
    public override Type DefinitionType => typeof(InteractiveStepDefinition);

    /// <inheritdoc />
    public override string DisplayName => "Interactive session";

    /// <inheritdoc />
    public override string Summary => "You work through it with an agent in a terminal session, then mark it done.";

    /// <inheritdoc />
    protected override bool RunsHeadless => false;

    /// <inheritdoc />
    protected override bool PersonFinishes => true;

    /// <inheritdoc />
    public override Task StartAsync(StepContext context, CancellationToken ct)
    {
        return this.OpenSessionAsync(context, context.Render(DefinitionOf(context).Prompt), false, ct);
    }

    /// <summary>
    ///     Reopens the interactive session with the feedback, continuing the step's conversation when there is one.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="feedback">What to change.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    public override Task ReworkAsync(StepContext context, string feedback, CancellationToken ct)
    {
        var resume = CanResume(context);
        return this.OpenSessionAsync(context, ReworkPrompt(context, feedback, resume), resume, ct);
    }

    /// <inheritdoc />
    public override async Task CompleteAsync(StepContext context, string? note, IReadOnlyDictionary<string, string>? answers, CancellationToken ct)
    {
        Require(context, StepTrigger.Complete, "marked done");
        this.AcceptAnswersOnDone(context, answers);
        // The session has ended, so the MCP configuration it was given (with secrets in it) goes.
        AbstractStepExecutor.DeleteMcpConfig(context.McpConfigPath);
        var summary = context.State.Status == StepStatus.AwaitingUser ? await this.SummariseAsync(context, ct) : null;
        var output = string.Join("\n\n", new[] { summary, note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        await this.FinishAsync(context, output.Length > 0 ? output : "Interactive session finished.", ct);
    }

    /// <summary>
    ///     Opens an interactive session on a prompt and waits for the user, or, when the executor can't open one (the
    ///     calling Claude session is the interactive session), hands it the prompt.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="resume">Whether to continue the step's existing conversation.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual async Task OpenSessionAsync(StepContext context, string prompt, bool resume, CancellationToken ct)
    {
        var launch = context.Executor.GetInteractiveLaunch(context.CreateRequest(prompt, resume));
        if (launch is null)
        {
            await this.RunAiAsync(context, prompt, resume, ct);
            return;
        }

        context.State.Interactive = launch;
        context.State.PendingPrompt = prompt;
        Move(context, StepStatus.AwaitingUser);
    }

    /// <summary>
    ///     Asks the AI to summarise the interactive session, without changing anything.
    /// </summary>
    /// <param name="context">The step context.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The summary, or null when it couldn't be produced.</returns>
    protected virtual async Task<string?> SummariseAsync(StepContext context, CancellationToken ct)
    {
        if (!CanResume(context))
        {
            return null;
        }

        context.Report($"Summarising the interactive session for '{context.State.Name}'.");
        try
        {
            var request = context.CreateRequest(StepConstants.InteractiveSummaryPrompt, true, StepConstants.ReadOnlyPermissionMode);
            var result = await context.Executor.ExecuteAsync(request, context.Progress, ct);
            return result.Status == ExecutionStatus.Completed ? result.Output : null;
        }
        catch (Exceptions.AflException)
        {
            return null;
        }
    }
}
