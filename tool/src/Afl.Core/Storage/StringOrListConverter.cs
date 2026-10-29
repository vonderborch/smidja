using System.Text.Json;
using System.Text.Json.Serialization;

namespace Afl.Core.Storage;

/// <summary>
///     Reads a list of strings written either as a list or, when it has one item, as a plain string, and writes a list
///     of one item as a plain string.
/// </summary>
public class StringOrListConverter : JsonConverter<List<string>>
{
    /// <inheritdoc />
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return [reader.GetString()!];
            case JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("Expected a string.");
                    }

                    items.Add(reader.GetString()!);
                }

                return items;
            default:
                throw new JsonException("Expected a string or a list of strings.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
