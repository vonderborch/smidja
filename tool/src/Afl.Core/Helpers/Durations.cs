using System.Globalization;
using System.Text.RegularExpressions;

namespace Afl.Core.Helpers;

/// <summary>
///     Reads the short durations workflows use, such as <c>30s</c>, <c>5m</c>, <c>1h30m</c> or <c>2d</c>.
/// </summary>
public static partial class Durations
{
    /// <summary>
    ///     Reads a duration.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="duration">The duration, when it could be read.</param>
    /// <returns>True if it's a duration greater than zero.</returns>
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var match = string.IsNullOrWhiteSpace(text) ? null : Pattern().Match(text.Trim());
        if (match is not { Success: true })
        {
            return false;
        }

        foreach (Capture capture in match.Groups["part"].Captures)
        {
            var number = double.Parse(capture.Value[..^1], CultureInfo.InvariantCulture);
            duration += char.ToLowerInvariant(capture.Value[^1]) switch
            {
                'd' => TimeSpan.FromDays(number),
                'h' => TimeSpan.FromHours(number),
                'm' => TimeSpan.FromMinutes(number),
                _ => TimeSpan.FromSeconds(number),
            };
        }

        return duration > TimeSpan.Zero;
    }

    /// <summary>
    ///     Writes a duration the way workflows do, e.g. <c>1h30m</c>.
    /// </summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The text.</returns>
    public static string Format(TimeSpan duration)
    {
        var parts = new List<string>();
        if (duration.Days > 0)
        {
            parts.Add($"{duration.Days}d");
        }

        if (duration.Hours > 0)
        {
            parts.Add($"{duration.Hours}h");
        }

        if (duration.Minutes > 0)
        {
            parts.Add($"{duration.Minutes}m");
        }

        if (duration.Seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{duration.Seconds}s");
        }

        return string.Concat(parts);
    }

    /// <summary>
    ///     The pattern of a duration: one or more numbers, each with a unit.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^(?<part>\d+(\.\d+)?[dhms])+$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
