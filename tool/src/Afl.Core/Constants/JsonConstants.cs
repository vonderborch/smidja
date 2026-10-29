using System.Text.Json;
using System.Text.Json.Serialization;
using Afl.Core.Workflows;

namespace Afl.Core.Constants;

/// <summary>
///     JSON settings shared by every afl document and every <c>--json</c> output.
/// </summary>
public static class JsonConstants
{
    /// <summary>
    ///     The name of the schema version property on versioned documents.
    /// </summary>
    public const string SchemaVersionPropertyName = "schemaVersion";

    /// <summary>
    ///     camelCase properties, kebab-case enums (<c>awaiting-approval</c>), nulls omitted, indented.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower), new StepDefinitionConverter() },
    };
}
