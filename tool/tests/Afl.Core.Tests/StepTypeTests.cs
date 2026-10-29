using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the step <c>type</c> field and the definition subclass each type reads as.
/// </summary>
public class StepTypeTests : AbstractAflTest
{
    [Fact]
    public void Reading_NamesUnknownTypes()
    {
        var error = Assert.Throws<JsonException>(() => ReadStep("""{ "id": "a", "name": "a", "type": "mixed", "prompt": "p" }"""));

        Assert.Contains("unknown type 'mixed'", error.Message);
        Assert.Contains("ai, interactive, user, workflow", error.Message);
    }

    [Theory]
    [InlineData("""{ "id": "a", "name": "A", "prompt": "p", "agents": ["x", "y"] }""", typeof(AiStepDefinition))]
    [InlineData("""{ "id": "a", "name": "A", "type": "interactive", "prompt": "p" }""", typeof(InteractiveStepDefinition))]
    [InlineData("""{ "id": "a", "name": "A", "type": "user", "instructions": "do it" }""", typeof(UserStepDefinition))]
    [InlineData("""{ "id": "a", "name": "A", "type": "workflow", "workflow": "child", "reuse": true }""", typeof(WorkflowStepDefinition))]
    public void EachType_ReadsAsItsSubclassAndRoundTrips(string json, Type expected)
    {
        var step = ReadStep(json);

        Assert.IsType(expected, step);
        Assert.Null(step.OtherFields);
        var written = JsonSerializer.Serialize(step, JsonConstants.Options);
        var again = ReadStep(written);
        Assert.IsType(expected, again);
        Assert.Equal(written, JsonSerializer.Serialize(again, JsonConstants.Options));
        Assert.Equal(StepHandlerTypeOf(json), again.Type);
    }

    [Fact]
    public void AFieldOfAnotherType_IsAValidationError()
    {
        var step = ReadStep("""{ "id": "w", "name": "W", "type": "workflow", "workflow": "child", "prompt": "p" }""");

        Assert.Contains(WorkflowLibrary.Validate(Workflow(step)), e => e.Contains("a workflow step doesn't take 'prompt'"));
    }

    /// <summary>
    ///     Gets the type a step's JSON names, <c>ai</c> when it names none.
    /// </summary>
    private static string StepHandlerTypeOf(string json)
    {
        return JsonDocument.Parse(json).RootElement.TryGetProperty("type", out var type) ? type.GetString()! : StepConstants.TypeAi;
    }
}
