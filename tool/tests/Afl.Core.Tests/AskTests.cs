using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for asking at different points of a step: <c>before</c> inputs, and the AI asking part-way through.
/// </summary>
public class AskTests : AbstractAflTest
{
    private readonly FakeStepExecutor _executor = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor);

    private static StepInput Input(string id, string when, string type = StepConstants.InputText, List<string>? options = null) =>
        new() { Id = id, Label = id, Type = type, When = when, Options = options };

    private static string AskBlock(string json) => $"Halfway there.\n\n```afl-ask\n{json}\n```";

    private static Dictionary<string, string> Answers(params (string Id, string Value)[] answers) =>
        answers.ToDictionary(a => a.Id, a => a.Value);

    /// <summary>
    ///     Answers with an ask block on the first request and echoes the prompt after that.
    /// </summary>
    private void AskOnce(string json)
    {
        var asked = false;
        this._executor.Respond = r =>
        {
            var output = asked ? $"output for: {r.Prompt}" : AskBlock(json);
            asked = true;
            return new ExecutionResult(ExecutionStatus.Completed, output, r.SessionId);
        };
    }

    private Run Start(params AbstractStepDefinition[] steps)
    {
        return this.Engine.Create(this.RegisterRepo(), Workflow(steps), SampleTicket());
    }

    [Fact]
    public async Task BeforeInputs_PauseAnAutoStep_ThenReachItsPrompt()
    {
        var run = this.Start(new AiStepDefinition
        {
            Id = "plan", Name = "Plan", Prompt = "Plan for {{inputs.scope}}", Inputs = [Input("scope", StepConstants.WhenBefore)],
        });

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.AwaitingInput, run.CurrentStep!.Status);
        Assert.Empty(this._executor.Requests);
        var handler = StepHandlerRegistry.Instance.For(run.DefinitionOf(run.CurrentStep));
        Assert.Equal(["scope"], handler.InputsAskedNow(run, run.CurrentStep).Select(i => i.Id));

        run = await this.Engine.AnswerAsync(run.Id, Answers(("scope", "the backend")));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("Plan for the backend", this._executor.Requests.Single().Prompt);
        Assert.Equal("the backend", run.Steps[0].Answers["scope"]);
    }

    [Fact]
    public async Task BeforeInputs_AreRequired()
    {
        var run = this.Start(new AiStepDefinition
        {
            Id = "plan", Name = "Plan", Prompt = "Plan", Inputs = [Input("scope", StepConstants.WhenBefore)],
        });
        run = await this.Engine.DriveAsync(run.Id);

        var ex = await Assert.ThrowsAsync<AflException>(() => this.Engine.AnswerAsync(run.Id, Answers()));

        Assert.Equal(AflErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public async Task BeforeAnswers_AreKeptOnRetry()
    {
        var fail = true;
        this._executor.Respond = r => fail
            ? new ExecutionResult(ExecutionStatus.Failed, "boom", r.SessionId)
            : new ExecutionResult(ExecutionStatus.Completed, $"output for: {r.Prompt}", r.SessionId);
        var run = this.Start(new AiStepDefinition
        {
            Id = "plan", Name = "Plan", Prompt = "Plan {{inputs.scope}}", Inputs = [Input("scope", StepConstants.WhenBefore)],
        });
        run = await this.Engine.DriveAsync(run.Id);
        run = await this.Engine.AnswerAsync(run.Id, Answers(("scope", "api")));
        Assert.Equal(StepStatus.Failed, run.CurrentStep!.Status);

        fail = false;
        run = await this.Engine.RetryAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("Plan api", this._executor.Requests.Last().Prompt);
    }

    [Fact]
    public async Task AiInput_IsAskedWhenTheAiWantsIt_ThenTheConversationResumes()
    {
        this.AskOnce("""{"inputs": ["tone"]}""");
        var run = this.Start(new AiStepDefinition
        {
            Id = "write", Name = "Write", Prompt = "Write it",
            Inputs = [Input("tone", StepConstants.WhenAi, StepConstants.InputChoice, ["formal", "casual"])],
        });

        run = await this.Engine.DriveAsync(run.Id);

        var state = run.CurrentStep!;
        Assert.Equal(StepStatus.AwaitingInput, state.Status);
        Assert.Contains("tone", this._executor.Requests[0].Prompt);
        Assert.Contains(StepConstants.AskBlockTag, this._executor.Requests[0].Prompt);
        Assert.Equal("Halfway there.", state.Output);
        Assert.Equal(["tone"], state.Questions!.Select(q => q.Id));
        Assert.Single(state.AskRounds);

        run = await this.Engine.AnswerAsync(run.Id, Answers(("tone", "casual")));

        var second = this._executor.Requests[1];
        Assert.True(second.Resume);
        Assert.Equal(this._executor.Requests[0].SessionId, second.SessionId);
        Assert.StartsWith(StepConstants.AnswersHeading, second.Prompt);
        Assert.Contains("casual", second.Prompt);
        Assert.DoesNotContain(StepConstants.AskBlockTag, second.Prompt);
        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("casual", run.Steps[0].Answers["tone"]);
        Assert.Equal(Answers(("tone", "casual")), run.Steps[0].AskRounds.Single().Answers);
        Assert.Equal(1, run.Steps[0].Attempts);
        Assert.Equal(["tone → casual"], run.Steps[0].AskRounds.Single().Describe());
    }

    [Fact]
    public async Task OwnQuestions_GetIdsAndAnswersAreStored()
    {
        this.AskOnce("""{"questions": [{"label": "Which database?", "type": "choice", "options": ["pg", "sqlite"]}]}""");
        var run = this.Start(new AiStepDefinition { Id = "write", Name = "Write", Prompt = "Write it", Ask = true });

        run = await this.Engine.DriveAsync(run.Id);

        var question = run.CurrentStep!.Questions!.Single();
        Assert.Equal("ask-1", question.Id);
        Assert.Equal("Which database?", question.Label);
        Assert.Equal(StepConstants.InputChoice, question.Type);

        run = await this.Engine.AnswerAsync(run.Id, Answers(("ask-1", "PG")));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("pg", run.Steps[0].Answers["ask-1"]);
        Assert.Contains("Which database?", this._executor.Requests[1].Prompt);
    }

    [Fact]
    public async Task WithoutResume_TheAiIsRepromptedWithItsEarlierOutput()
    {
        this._executor.Resumes = false;
        this.AskOnce("""{"questions": [{"label": "Which colour?"}]}""");
        var run = this.Start(new AiStepDefinition { Id = "write", Name = "Write", Prompt = "Write it", Ask = true });
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.AnswerAsync(run.Id, Answers(("ask-1", "blue")));

        var second = this._executor.Requests[1];
        Assert.False(second.Resume);
        Assert.StartsWith("Write it", second.Prompt);
        Assert.Contains("Halfway there.", second.Prompt);
        Assert.Contains("blue", second.Prompt);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public async Task MalformedBlock_FinishesTheStepWithANote()
    {
        this._executor.Respond = r => new ExecutionResult(ExecutionStatus.Completed, AskBlock("{not json"), r.SessionId);
        var run = this.Start(new AiStepDefinition { Id = "write", Name = "Write", Prompt = "Write it", Ask = true });

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("Halfway there.", run.Steps[0].Output);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("tried to ask"));
    }

    [Fact]
    public async Task UnknownInputOrUnallowedQuestion_IsRefusedWithANote()
    {
        this.AskOnce("""{"inputs": ["nope"], "questions": [{"label": "Why?"}]}""");
        var run = this.Start(new AiStepDefinition
        {
            Id = "write", Name = "Write", Prompt = "Write it", Inputs = [Input("tone", StepConstants.WhenAi)],
        });

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Single(run.Steps[0].Notes);
    }

    [Fact]
    public async Task RoundLimit_StopsTheAiAsking()
    {
        this._executor.Respond = r =>
            new ExecutionResult(ExecutionStatus.Completed, AskBlock("""{"questions": [{"label": "Again?"}]}"""), r.SessionId);
        var run = this.Start(new AiStepDefinition { Id = "write", Name = "Write", Prompt = "Write it", Ask = true });
        run = await this.Engine.DriveAsync(run.Id);

        for (var round = 1; round <= StepConstants.MaxAskRounds; round++)
        {
            Assert.Equal(StepStatus.AwaitingInput, run.CurrentStep!.Status);
            Assert.Equal($"ask-{round}", run.CurrentStep.Questions!.Single().Id);
            run = await this.Engine.AnswerAsync(run.Id, Answers(($"ask-{round}", "yes")));
        }

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(StepConstants.MaxAskRounds, run.Steps[0].AskRounds.Count);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("tried to ask"));
    }

    [Fact]
    public async Task AskBlock_IsIgnoredOnStepsThatCantAsk()
    {
        this._executor.Respond = r =>
            new ExecutionResult(ExecutionStatus.Completed, AskBlock("""{"questions": [{"label": "Hm?"}]}"""), r.SessionId);
        var run = this.Start(new AiStepDefinition { Id = "write", Name = "Write", Prompt = "Write it" });

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains(StepConstants.AskBlockTag, run.Steps[0].Output);
        Assert.DoesNotContain(StepConstants.AskBlockTag, this._executor.Requests[0].Prompt);
        Assert.Empty(run.Steps[0].Notes);
    }

    [Theory]
    [InlineData(true, StepStatus.Done, StepStatus.Pending)]
    [InlineData(false, StepStatus.Pending, StepStatus.Done)]
    public async Task AiInput_RoutesWhenAsked_AndIsEmptyWhenNot(bool asks, StepStatus formal, StepStatus casual)
    {
        if (asks)
        {
            this.AskOnce("""{"inputs": ["tone"]}""");
        }

        var write = new AiStepDefinition
        {
            Id = "write", Name = "Write", Prompt = "Write it", Inputs = [Input("tone", StepConstants.WhenAi)],
            Follow = ArrowConstants.FollowFirst,
            Arrows =
            [
                new ArrowDefinition { To = "formal", When = new ConditionDefinition { Kind = "input", Input = "tone", EqualTo = "formal" } },
                new ArrowDefinition { To = "casual" },
            ],
        };
        var casualStep = new AiStepDefinition
        {
            Id = "casual", Name = "Casual", Prompt = "Casual '{{steps.write.inputs.tone}}'", Arrows = [],
        };
        var run = this.Start(write, casualStep, new AiStepDefinition { Id = "formal", Name = "Formal", Prompt = "Formal" },
            new AiStepDefinition { Id = "last", Name = "Last", Prompt = "Last" });

        run = await this.Engine.DriveAsync(run.Id);
        if (asks)
        {
            run = await this.Engine.AnswerAsync(run.Id, Answers(("tone", "formal")));
        }

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(casual, run.Steps[1].Status);
        Assert.Equal(formal, run.Steps[2].Status);
        if (!asks)
        {
            Assert.Contains(this._executor.Requests, r => r.Prompt == "Casual ''");
        }
    }

    [Fact]
    public async Task AskRounds_MoveIntoTheAttemptOnReplay()
    {
        this.AskOnce("""{"questions": [{"label": "Which?"}]}""");
        var run = this.Start(new AiStepDefinition { Id = "write", Name = "Write", Prompt = "Write it", Ask = true });
        run = await this.Engine.DriveAsync(run.Id);
        run = await this.Engine.AnswerAsync(run.Id, Answers(("ask-1", "this")));

        run = await this.Engine.ReplayAsync(run.Id, "write", null);

        var attempt = run.Steps[0].History.Single();
        Assert.Equal("this", attempt.AskRounds!.Single().Answers!["ask-1"]);
        Assert.Empty(run.Steps[0].AskRounds);
    }

    [Fact]
    public void Validation_RejectsAskingWhereItCantHappen()
    {
        var errors = WorkflowLibrary.Validate(Workflow(
            new InteractiveStepDefinition
            {
                Id = "talk", Name = "Talk", Prompt = "p", Ask = true,
                Inputs = [Input("a", StepConstants.WhenAi)],
            },
            new AiStepDefinition
            {
                Id = "many", Name = "Many", Prompt = "p", Agents = ["x", "y"], Inputs = [Input("b", StepConstants.WhenAi)],
            },
            new AiStepDefinition { Id = "auto", Name = "Auto", Prompt = "p", Inputs = [Input("c", StepConstants.WhenAfter)] },
            new AiStepDefinition { Id = "odd", Name = "Odd", Prompt = "p", Inputs = [Input("d", "sometime")] },
            new AiStepDefinition
            {
                Id = "late", Name = "Late", Prompt = "{{inputs.e}}", Advance = StepConstants.AdvanceApprove,
                Inputs = [Input("e", StepConstants.WhenAfter)],
            }));

        Assert.Contains(errors, e => e.Contains("talk") && e.Contains("ask"));
        Assert.Contains(errors, e => e.Contains("talk") && e.Contains("'a'"));
        Assert.Contains(errors, e => e.Contains("many") && e.Contains("'b'"));
        Assert.Contains(errors, e => e.Contains("auto") && e.Contains("'before'"));
        Assert.Contains(errors, e => e.Contains("odd") && e.Contains("sometime"));
        Assert.Contains(errors, e => e.Contains("late") && e.Contains("inputs.e"));
    }

    [Fact]
    public void Validation_AllowsBeforeInputsAnywhere()
    {
        var errors = WorkflowLibrary.Validate(Workflow(
            new AiStepDefinition
            {
                Id = "plan", Name = "Plan", Prompt = "{{inputs.x}}", Inputs = [Input("x", StepConstants.WhenBefore)],
            },
            new InteractiveStepDefinition
            {
                Id = "talk", Name = "Talk", Prompt = "{{inputs.y}}",
                Inputs = [Input("y", StepConstants.WhenBefore)],
            }));

        Assert.Empty(errors);
    }
}
