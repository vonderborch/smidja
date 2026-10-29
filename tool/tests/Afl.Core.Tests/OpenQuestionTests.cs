using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for reading the questions an AI leaves at the end of its output, and answering them by sending the step back.
/// </summary>
public class OpenQuestionTests : AbstractAflTest
{
    private const string Output = """
                                  # Research

                                  Some findings.

                                  ## Open questions
                                  These are assumed as stated; none blocks the options step.
                                  1. **Do the round-2–4 decisions still bind**, given the blank constraints? *Assumed yes.*
                                     Options that break them are kept as contrasts.
                                  2. **Which is "the current architecture"?** *Assumed 1.x*, since it is released.

                                  3. "Python.Net" is read as PythonEmbedded.Net.

                                  SECURITY IMPACT: YES
                                  """;

    private readonly FakeStepExecutor _executor = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor);

    [Fact]
    public void Find_ReadsTheListUnderTheLastQuestionsHeading()
    {
        var questions = OpenQuestion.Find(Output);

        Assert.Equal([1, 2, 3], questions.Select(q => q.Number));
        Assert.Equal("**Do the round-2–4 decisions still bind**, given the blank constraints? *Assumed yes.* Options that break them are kept as contrasts.",
            questions[0].Text);
        Assert.Equal("Which is \"the current architecture\"? Assumed 1.x, since it is released.", questions[1].PlainText);
        Assert.Equal("\"Python.Net\" is read as PythonEmbedded.Net.", questions[2].Text);
    }

    [Theory]
    [InlineData("## Questions for you\n- Keep the cache?\n- Drop v1?\n\nOnce these are settled, it ships.")]
    [InlineData("**Questions:**\n* Keep the cache?\n* Drop v1?")]
    [InlineData("### Open Questions\n1) Keep the cache?\n2) Drop v1?\n```afl-ticket\n{}\n```")]
    public void Find_TakesBulletsBoldHeadingsAndStopsAtTheNextSection(string output)
    {
        var questions = OpenQuestion.Find(output);

        Assert.Equal(["Keep the cache?", "Drop v1?"], questions.Select(q => q.Text));
        Assert.Equal([1, 2], questions.Select(q => q.Number));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("All done, no questions asked.")]
    [InlineData("## Open questions\nNone.")]
    [InlineData("# Spec\n## Risks and open questions\n1. Load?\n## Rollout\n1. Flag it")]
    [InlineData("# Spec\n## Rollout\n1. Flag it\n## Risks and open questions\n1. Load?")]
    public void Find_IsEmptyWithoutAQuestionList(string? output)
    {
        Assert.Empty(OpenQuestion.Find(output));
    }

    [Fact]
    public void FormatAnswers_ListsEachQuestionKeepingAssumptionsForBlanks()
    {
        var questions = OpenQuestion.Find(Output);

        var feedback = OpenQuestion.FormatAnswers(questions, new Dictionary<int, string> { [2] = " v2alt ", [3] = "" }, "Shorter, please.");

        Assert.Contains("1. Do the round-2–4 decisions still bind", feedback);
        Assert.Contains("   Answer: not answered; keep your assumption.", feedback);
        Assert.Contains("2. Which is \"the current architecture\"? Assumed 1.x, since it is released.\n   Answer: v2alt\n", feedback);
        Assert.Contains("Also: Shorter, please.", feedback);
    }

    [Fact]
    public void FormatAnswers_RefusesUnknownQuestionsAndNothingAnswered()
    {
        var questions = OpenQuestion.Find(Output);

        Assert.Equal(AflErrorKind.InvalidInput, Assert.Throws<AflException>(() =>
            OpenQuestion.FormatAnswers(questions, new Dictionary<int, string> { [9] = "x" }, null)).Kind);
        Assert.Equal(AflErrorKind.InvalidInput, Assert.Throws<AflException>(() =>
            OpenQuestion.FormatAnswers(questions, new Dictionary<int, string> { [1] = " " }, " ")).Kind);
    }

    [Fact]
    public async Task AnswerQuestions_SendsTheStepBackWithTheAnswers()
    {
        var step = (UserStepDefinition)StepHandlerRegistry.Instance.CreateDefinition("user");
        step.Id = "u";
        step.Name = "u";
        step.Advance = "approve";
        step.Instructions = "You do u";
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(step), SampleTicket());
        run = await this.Engine.DriveAsync(run.Id);
        run = await this.Engine.CompleteAsync(run.Id, Output);
        Assert.Equal(3, run.CurrentStep!.OpenQuestions.Count);

        run = await this.Engine.AnswerQuestionsAsync(run.Id, new Dictionary<int, string> { [1] = "No, they don't." }, null);

        Assert.Equal(StepStatus.AwaitingUser, run.CurrentStep!.Status);
        Assert.Empty(run.CurrentStep.OpenQuestions);
        Assert.Contains("Answer: No, they don't.", run.CurrentStep.PendingPrompt);
        Assert.Contains("Answer: No, they don't.", Assert.Single(run.CurrentStep.History).Reason);

        var ex = await Assert.ThrowsAsync<AflException>(() =>
            this.Engine.AnswerQuestionsAsync(run.Id, new Dictionary<int, string> { [1] = "x" }, null));
        Assert.Equal(AflErrorKind.InvalidState, ex.Kind);
    }

    [Theory]
    [InlineData(StepConstants.AdvanceApprove, true)]
    [InlineData(StepConstants.AdvanceAuto, false)]
    public async Task AiSteps_AreToldHowToLeaveQuestionsOnlyWhenTheyWaitForApproval(string advance, bool told)
    {
        var step = new AiStepDefinition { Id = "plan", Name = "Plan", Prompt = "Plan it", Advance = advance };
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(step), SampleTicket());

        await this.Engine.DriveAsync(run.Id);

        var prompt = this._executor.Requests.Single().Prompt;
        Assert.StartsWith("Plan it", prompt);
        Assert.Equal(told, prompt.Contains(StepConstants.OpenQuestionsHeading));
    }

    [Fact]
    public void TheInstructionsExample_IsReadAsAQuestion()
    {
        var instructions = string.Format(StepConstants.OpenQuestionsInstructionsFormat, StepConstants.OpenQuestionsHeading);
        var example = instructions[instructions.IndexOf("\n\n## ", StringComparison.Ordinal)..instructions.IndexOf("\n\nMake it", StringComparison.Ordinal)];

        var question = Assert.Single(OpenQuestion.Find($"# Plan\n\nDone.{example}"));

        Assert.Equal("Should the old API keep working? Assumed yes, so it's kept behind a flag.", question.PlainText);
    }
}
