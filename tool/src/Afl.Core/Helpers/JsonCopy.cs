using System.Text.Json;
using Afl.Core.Constants;

namespace Afl.Core.Helpers;

/// <summary>
///     Deep copies of anything afl saves as JSON, made by writing it out and reading it back.
/// </summary>
public static class JsonCopy
{
    /// <summary>
    ///     Returns a deep copy.
    /// </summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The copy.</returns>
    public static T Of<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonConstants.Options), JsonConstants.Options)!;
    }
}
