using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Afl.Core.Helpers;

/// <summary>
///     A fenced JSON block an AI ended its reply with, such as <c>```afl-agent</c>, so afl can read a structured answer
///     out of free text. When a reply has several blocks with the same tag, the last one counts.
/// </summary>
public class FencedBlock
{
    /// <summary>
    ///     Gets the block's JSON object; null when it couldn't be read (see <see cref="Error" />).
    /// </summary>
    public JsonObject? Json { get; private init; }

    /// <summary>
    ///     Gets the reply with the block taken out, trimmed.
    /// </summary>
    public string Rest { get; private init; } = string.Empty;

    /// <summary>
    ///     Gets why the block couldn't be read, or null when it could.
    /// </summary>
    public string? Error { get; private init; }

    /// <summary>
    ///     Finds the last block with a tag in a reply.
    /// </summary>
    /// <param name="output">The reply.</param>
    /// <param name="tag">The fence language, such as <c>afl-agent</c>.</param>
    /// <returns>The block (with <see cref="Error" /> set if it's malformed), or null when there's none.</returns>
    public static FencedBlock? FindLast(string? output, string tag)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var pattern = $@"```{Regex.Escape(tag)}[ \t]*\r?\n(?<json>.*?)\r?\n[ \t]*```";
        var match = Regex.Matches(output, pattern, RegexOptions.Singleline).LastOrDefault();
        if (match is null)
        {
            return null;
        }

        var rest = (output[..match.Index] + output[(match.Index + match.Length)..]).Trim();
        try
        {
            return JsonNode.Parse(match.Groups["json"].Value) is JsonObject json
                ? new FencedBlock { Json = json, Rest = rest }
                : new FencedBlock { Rest = rest, Error = $"its {tag} block isn't a JSON object" };
        }
        catch (JsonException e)
        {
            return new FencedBlock { Rest = rest, Error = $"its {tag} block can't be read ({e.Message})" };
        }
    }

    /// <summary>
    ///     Returns a string property of the block.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or null when it's missing or not a string.</returns>
    public string? GetString(string name)
    {
        return this.Json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
