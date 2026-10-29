using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for step inputs: the input types, resolving answers, validation, and answers reaching later steps.
/// </summary>
public class StepInputTests : AbstractAflTest
{
    private readonly FakeStepExecutor _executor = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor);

    private static StepInput Input(string id, string type = StepConstants.InputText, List<string>? options = null,
        bool required = true, string? fallback = null) =>
        new() { Id = id, Label = id, Type = type, Options = options, Required = required, Default = fallback };

    private static string Normalize(StepInput input, string value) =>
        StepInputTypeRegistry.Instance.For(input).Normalize(input, value);

    private static IEnumerable<string> ValidateInput(StepInput input) =>
        StepInputTypeRegistry.Instance.For(input).Validate(input);

    [Fact]
    public void Types_NormalizeAnswers()
    {
        Assert.Equal("hi", Normalize(Input("t"), "  hi "));
        Assert.Equal("a\nb", Normalize(Input("m", StepConstants.InputMultiline), "a\r\nb"));
        Assert.Equal("3.50", Normalize(Input("n", StepConstants.InputNumber), " 3.50 "));
        Assert.Equal("High", Normalize(Input("c", StepConstants.InputChoice, ["Low", "High"]), "high"));
        Assert.Equal("a, c",
            Normalize(Input("mc", StepConstants.InputMultiChoice, ["a", "b", "c"]), "C, a"));
        Assert.Equal(StepConstants.Yes, Normalize(Input("y", StepConstants.InputConfirm), "Y"));
        Assert.Equal(StepConstants.No, Normalize(Input("y", StepConstants.InputConfirm), "false"));
    }

    [Theory]
    [InlineData(StepConstants.InputNumber, null, "lots")]
    [InlineData(StepConstants.InputChoice, "Low,High", "medium")]
    [InlineData(StepConstants.InputMultiChoice, "a,b", "a, z")]
    [InlineData(StepConstants.InputConfirm, null, "maybe")]
    public void Types_RejectBadAnswers(string type, string? options, string value)
    {
        var input = Input("x", type, options?.Split(',').ToList());

        var ex = Assert.Throws<AflException>(() => Normalize(input, value));
        Assert.Equal(AflErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public void Types_ValidateTheirDefinitions()
    {
        Assert.Empty(ValidateInput(Input("c", StepConstants.InputChoice, ["a", "b"], fallback: "a")));
        Assert.NotEmpty(ValidateInput(Input("c", StepConstants.InputChoice)));
        Assert.NotEmpty(ValidateInput(Input("c", StepConstants.InputChoice, ["a", "a"])));
        Assert.NotEmpty(ValidateInput(Input("t", StepConstants.InputText, ["a"])));
        Assert.NotEmpty(ValidateInput(Input("mc", StepConstants.InputMultiChoice, ["a,b", "c"])));
        Assert.NotEmpty(ValidateInput(Input("n", StepConstants.InputNumber, fallback: "ten")));
    }

    [Fact]
    public void Resolve_AppliesDefaultsAndSkipsBlankOptionalInputs()
    {
        List<StepInput> inputs =
        [
            Input("size", StepConstants.InputChoice, ["S", "M", "L"], fallback: "M"),
            Input("notes", required: false),
            Input("ok", StepConstants.InputConfirm),
        ];

        var answers = StepInputTypeRegistry.Instance.Resolve(inputs, new Dictionary<string, string> { ["ok"] = "y" });

        Assert.Equal(new Dictionary<string, string> { ["size"] = "M", ["ok"] = StepConstants.Yes }, answers);
    }

    [Fact]
    public void Resolve_ReportsMissingAndUnknownAnswersTogether()
    {
        List<StepInput> inputs = [Input("name"), Input("count", StepConstants.InputNumber)];

        var ex = Assert.Throws<AflException>(() => StepInputTypeRegistry.Instance.Resolve(inputs,
            new Dictionary<string, string> { ["count"] = "x", ["extra"] = "1" }));

        Assert.Equal(AflErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("name", ex.Message);
        Assert.Contains("count", ex.Message);
        Assert.Contains("extra", ex.Message);
    }

    [Fact]
    public void Validation_AllowsInputsOnlyOnStepsThatAskTheUser()
    {
        var ai = new AiStepDefinition { Id = "a", Name = "a", Prompt = "p", Inputs = [Input("x")] };
        var user = new UserStepDefinition { Id = "u", Name = "u", Inputs = [Input("x")] };
        var duplicate = new UserStepDefinition { Id = "d", Name = "d", Inputs = [Input("x"), Input("x")] };

        Assert.Contains(WorkflowLibrary.Validate(Workflow(ai)), e => e.Contains("input", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(WorkflowLibrary.Validate(Workflow(user)));
        Assert.NotEmpty(WorkflowLibrary.Validate(Workflow(duplicate)));
    }

    [Fact]
    public void Validation_KnowsAboutEarlierStepsInputs()
    {
        var ask = new UserStepDefinition { Id = "ask", Name = "ask", Inputs = [Input("scope")] };
        var use = new AiStepDefinition { Id = "use", Name = "use", Prompt = "Do {{steps.ask.inputs.scope}}" };
        var bad = new AiStepDefinition { Id = "bad", Name = "bad", Prompt = "Do {{steps.ask.inputs.nope}}" };

        Assert.Empty(WorkflowLibrary.Validate(Workflow(ask, use)));
        Assert.NotEmpty(WorkflowLibrary.Validate(Workflow(ask, bad)));
        Assert.Contains("steps.ask.inputs.scope", TemplateVariables.Available([ask]));
    }

    [Fact]
    public async Task UserStep_AnswersBecomeOutputAndReachLaterSteps()
    {
        var ask = new UserStepDefinition
        {
            Id = "ask", Name = "ask", Instructions = "Pick a scope",
            Inputs = [Input("scope", StepConstants.InputChoice, ["small", "large"]), Input("why", required: false)],
        };
        var use = new AiStepDefinition { Id = "use", Name = "use", Prompt = "Build it {{steps.ask.inputs.scope}}" };
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(ask, use), SampleTicket());
        run = await this.Engine.DriveAsync(run.Id);

        await Assert.ThrowsAsync<AflException>(() => this.Engine.CompleteAsync(run.Id, "noted"));
        run = await this.Engine.CompleteAsync(run.Id, "noted", new Dictionary<string, string> { ["scope"] = "LARGE" });

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(new Dictionary<string, string> { ["scope"] = "large" }, run.Steps[0].Answers);
        Assert.Equal("noted\n\nscope: large", run.Steps[0].Output);
        Assert.Equal("Build it large", this._executor.Requests[0].Prompt);
    }

    [Fact]
    public async Task ApproveGate_StoresAnswersForLaterSteps()
    {
        var plan = new AiStepDefinition
        {
            Id = "plan", Name = "plan", Advance = "approve", Prompt = "Plan",
            Inputs = [Input("ship", StepConstants.InputConfirm, fallback: "no")],
        };
        var next = new UserStepDefinition { Id = "next", Name = "next", Instructions = "{{steps.plan.inputs.ship}}" };
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(plan, next), SampleTicket());
        run = await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.ApproveAsync(run.Id, new Dictionary<string, string> { ["ship"] = "true" });

        Assert.Equal(StepConstants.Yes, run.Steps[0].Answers["ship"]);
        Assert.Equal(StepConstants.Yes, run.CurrentStep!.PendingPrompt);
    }

    [Fact]
    public async Task TemplateDefault_IsRenderedWhenAskedAndUsedForABlankAnswer()
    {
        var ask = new UserStepDefinition { Id = "ask", Name = "ask", Instructions = "Pick", Inputs = [Input("scope")] };
        var check = new UserStepDefinition
        {
            Id = "check", Name = "check", Instructions = "Check",
            Inputs = [Input("scope", fallback: "{{steps.ask.inputs.scope}}"), Input("note", required: false, fallback: "{{feedback}}")],
        };
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(ask, check), SampleTicket());
        run = await this.Engine.DriveAsync(run.Id);
        run = await this.Engine.CompleteAsync(run.Id, answers: new Dictionary<string, string> { ["scope"] = "the toggle" });

        var asked = StepHandlerRegistry.Instance.For(check).InputsAskedNow(run, run.CurrentStep!);

        Assert.Equal(["the toggle", null], asked.Select(i => i.Default));
        Assert.Equal("{{steps.ask.inputs.scope}}", run.Workflow.Steps[1].Inputs![0].Default);

        run = await this.Engine.CompleteAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(new Dictionary<string, string> { ["scope"] = "the toggle" }, run.Steps[1].Answers);
    }

    [Fact]
    public void Validation_ChecksTemplateDefaultsLikePrompts()
    {
        var ask = new UserStepDefinition { Id = "ask", Name = "ask", Inputs = [Input("scope")] };
        AbstractStepDefinition Using(string fallback, string when = StepConstants.WhenAfter)
        {
            var first = Input("first");
            first.When = StepConstants.WhenBefore;
            var input = Input("x", fallback: fallback);
            input.When = when;
            return new AiStepDefinition { Id = "use", Name = "use", Advance = "approve", Prompt = "p", Inputs = [first, input] };
        }

        Assert.Empty(WorkflowLibrary.Validate(Workflow(ask, Using("{{steps.ask.inputs.scope}}"))));
        Assert.Empty(WorkflowLibrary.Validate(Workflow(ask, Using("{{inputs.first}}"))));
        Assert.Contains(WorkflowLibrary.Validate(Workflow(ask, Using("{{inputs.first}}", StepConstants.WhenBefore))),
            e => e.Contains("input 'x' default: '{{inputs.first}}' is not available here"));
        Assert.Contains(WorkflowLibrary.Validate(Workflow(ask, Using("{{steps.later.output}}"))),
            e => e.Contains("input 'x' default: '{{steps.later.output}}' is not available here"));
    }
}
