using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using Afl.Core.Workspaces;

namespace Afl.Core.Tests;

/// <summary>
///     Keeps <c>workflows/workflow.schema.json</c> in step with the workflow model and the registered extension points,
///     and checks the built-in workflows against it.
/// </summary>
public class WorkflowSchemaTests
{
    private static readonly string WorkflowsDirectory = Path.Combine(AppContext.BaseDirectory, "workflows");

    private static readonly JsonObject Schema =
        JsonNode.Parse(File.ReadAllText(Path.Combine(WorkflowsDirectory, "workflow.schema.json")))!.AsObject();

    private static readonly string StepsDirectory = Path.Combine(WorkflowsDirectory, "steps");

    private static readonly JsonObject CommonStepSchema =
        JsonNode.Parse(File.ReadAllText(Path.Combine(StepsDirectory, "common-step.schema.json")))!.AsObject();

    private static readonly System.Text.Json.JsonSerializerOptions Options = ReadOnlyCopyOf(JsonConstants.Options);

    public static TheoryData<string, Type> Definitions => new()
    {
        { "step", typeof(AbstractStepDefinition) },
        { "input", typeof(StepInput) },
        { "arrow", typeof(ArrowDefinition) },
        { "retry", typeof(RetryDefinition) },
        { "condition", typeof(ConditionDefinition) },
        { "parameter", typeof(ParameterDefinition) },
        { "requirement", typeof(WorkflowRequirement) },
        { "childSource", typeof(ChildSourceDefinition) },
    };

    public static TheoryData<string> BuiltIns => new(Directory.GetFiles(WorkflowsDirectory, "*.json")
        .Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal)).Select(Path.GetFileName)!);

    public static TheoryData<string> CommonSteps => new(Directory.GetFiles(StepsDirectory, "*.json")
        .Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal)).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Schema_HasEveryPropertyTheModelReadsAndWrites(string definition, Type type)
    {
        // A step reads as its type's subclass, so the schema's one step definition has every subclass's fields.
        var types = type == typeof(AbstractStepDefinition)
            ? StepHandlerRegistry.Instance.Keys.Select(StepHandlerRegistry.Instance.DefinitionTypeOf)
            : [type];
        var expected = types.SelectMany(t => Options.GetTypeInfo(t).Properties).Where(p => p.Get is not null && !p.IsExtensionData).Select(p => p.Name)
            .Distinct().Order();

        Assert.Equal(expected, PropertiesOf(Definition(definition)).Order());
    }

    [Fact]
    public void Schema_HasTheWorkflowsOwnProperties()
    {
        var expected = Options.GetTypeInfo(typeof(WorkflowDefinition)).Properties.Where(p => p.Get is not null).Select(p => p.Name)
            .Append("$schema").Order();

        Assert.Equal(expected, PropertiesOf(Schema).Order());
    }

    [Fact]
    public void Schema_ListsEveryRegisteredKindAndValue()
    {
        AssertEnum(StepHandlerRegistry.Instance.Keys, "step", "type");
        AssertEnum(StepInputTypeRegistry.Instance.Keys, "input", "type");
        AssertEnum(ConditionRegistry.Instance.Keys, "condition", "kind");
        AssertEnum(CombineStrategyRegistry.Instance.Keys, "step", "combine");
        AssertEnum(AgentSelectorRegistry.Instance.Keys, "step", "select");
        AssertEnum([StepConstants.AdvanceAuto, StepConstants.AdvanceApprove, StepConstants.AdvanceApproveFinal], "step", "advance");
        AssertEnum(WorkspaceRegistry.Instance.Keys, "step", "workspace");
        AssertEnum([StepConstants.WhenBefore, StepConstants.WhenAfter, StepConstants.WhenAi], "input", "when");
        AssertEnum([StepConstants.OnLimitFail, StepConstants.OnLimitContinue], "arrow", "onLimit");
        AssertEnum(OutcomeRegistry.Instance.Keys, "arrow", "on");
        AssertEnum(ArrowConstants.FollowModes, "step", "follow");
        AssertEnum(ArrowConstants.OthersModes, "step", "others");
        AssertEnum(ChildSourceRegistry.Instance.Keys, "childSource", "kind");
        AssertEnum([StepConstants.ChildrenWait, StepConstants.ChildrenStart], "step", "children");
        Assert.Equal(CapabilityCatalog.BuiltIns.Select(c => c.Id).Order(),
            Definition("capability")["examples"]!.AsArray().Select(v => v!.GetValue<string>()).Order());
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void BuiltIns_MatchTheSchema(string file)
    {
        var workflow = JsonNode.Parse(File.ReadAllText(Path.Combine(WorkflowsDirectory, file)))!;
        var errors = new List<string>();

        Check(workflow, Schema, "$", errors);

        Assert.Empty(errors);
        Assert.Equal("./workflow.schema.json", workflow["$schema"]?.GetValue<string>());
    }

    [Fact]
    public void CommonStepSchema_HasEveryPropertyTheModelReadsAndWrites()
    {
        var expected = Options.GetTypeInfo(typeof(CommonStepDefinition)).Properties.Where(p => p.Get is not null).Select(p => p.Name);

        Assert.Equal(expected.Append("$schema").Order(), PropertiesOf(CommonStepSchema).Order());
    }

    [Theory]
    [MemberData(nameof(CommonSteps))]
    public void BuiltInCommonSteps_MatchTheSchema(string file)
    {
        var step = JsonNode.Parse(File.ReadAllText(Path.Combine(StepsDirectory, file)))!;
        var errors = new List<string>();

        Check(step, CommonStepSchema, "$", errors);

        Assert.Empty(errors);
        Assert.Equal("./common-step.schema.json", step["$schema"]?.GetValue<string>());
    }

    private static System.Text.Json.JsonSerializerOptions ReadOnlyCopyOf(System.Text.Json.JsonSerializerOptions options)
    {
        var copy = new System.Text.Json.JsonSerializerOptions(options);
        copy.MakeReadOnly(populateMissingResolver: true);
        return copy;
    }

    private static JsonObject Definition(string name)
    {
        return Schema["$defs"]![name]!.AsObject();
    }

    private static IEnumerable<string> PropertiesOf(JsonObject schema)
    {
        return schema["properties"]!.AsObject().Select(p => p.Key);
    }

    private static void AssertEnum(IEnumerable<string> expected, string definition, string property)
    {
        var values = Definition(definition)["properties"]![property]!["enum"]!.AsArray().Select(v => v!.GetValue<string>());

        Assert.Equal(expected.Order(), values.Order());
    }

    /// <summary>
    ///     Checks a value against the parts of JSON Schema the workflow schema uses: <c>$ref</c>, <c>type</c>,
    ///     <c>enum</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c> (false or a schema), <c>items</c> and
    ///     <c>pattern</c>.
    /// </summary>
    private static void Check(JsonNode? value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            // Local refs and the common step schema's refs into this one both name a definition here.
            Check(value, Definition(reference[(reference.IndexOf("#/$defs/", StringComparison.Ordinal) + "#/$defs/".Length)..]), path, errors);
        }

        if (schema["enum"] is JsonArray values && !values.Any(v => JsonNode.DeepEquals(v, value)))
        {
            errors.Add($"{path}: '{value}' isn't one of {values.ToJsonString()}");
        }

        if (schema["pattern"]?.GetValue<string>() is { } pattern
            && !System.Text.RegularExpressions.Regex.IsMatch(value?.GetValue<string>() ?? string.Empty, pattern))
        {
            errors.Add($"{path}: '{value}' doesn't match {pattern}");
        }

        if (value is JsonObject obj)
        {
            var properties = schema["properties"] as JsonObject;
            foreach (var required in schema["required"]?.AsArray() ?? [])
            {
                if (!obj.ContainsKey(required!.GetValue<string>()))
                {
                    errors.Add($"{path}: missing '{required}'");
                }
            }

            foreach (var (key, child) in obj)
            {
                if (properties?[key] is JsonObject childSchema)
                {
                    Check(child, childSchema, $"{path}.{key}", errors);
                }
                else if (schema["additionalProperties"] is JsonObject valueSchema)
                {
                    Check(child, valueSchema, $"{path}.{key}", errors);
                }
                else if (schema["additionalProperties"]?.GetValue<bool>() == false)
                {
                    errors.Add($"{path}: unknown property '{key}'");
                }
            }
        }
        else if (value is JsonArray array && schema["items"] is JsonObject items)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Check(array[i], items, $"{path}[{i}]", errors);
            }
        }
    }
}
