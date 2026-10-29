using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Afl.UI.Diagrams;

/// <summary>
///     Rewrites the CSS a diagram's SVG relies on into plain values the Skia SVG renderer can draw: custom properties
///     (<c>var(--x, fallback)</c>, defined in the root's <c>style</c> and an <c>svg { }</c> rule), <c>color-mix(in srgb,
///     …)</c> and <c>rem</c> sizes. Without it fills come out black and lines and text vanish.
/// </summary>
public static partial class SvgCssFlattener
{
    /// <summary>
    ///     How many pixels a <c>rem</c> is.
    /// </summary>
    private const double PixelsPerRem = 16;

    /// <summary>
    ///     The deepest variables may refer to one another, so a cycle can't loop forever.
    /// </summary>
    private const int MaxDepth = 16;

    /// <summary>
    ///     Matches a custom property declaration, <c>--name: value;</c>.
    /// </summary>
    [GeneratedRegex(@"(?<name>--[\w-]+)\s*:\s*(?<value>[^;}""]+)")]
    private static partial Regex Declaration();

    /// <summary>
    ///     Matches the root element's <c>style</c> attribute.
    /// </summary>
    [GeneratedRegex(@"<svg\b[^>]*?\sstyle=""(?<style>[^""]*)""")]
    private static partial Regex RootStyle();

    /// <summary>
    ///     Matches <c>svg { … }</c> and <c>:root { … }</c> rules in a style sheet.
    /// </summary>
    [GeneratedRegex(@"(?:^|[\s}>])(?:svg|:root)\s*\{(?<body>[^}]*)\}")]
    private static partial Regex RootRule();

    /// <summary>
    ///     Matches a value that is only a <c>rem</c> size.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<n>\d*\.?\d+)rem\s*$")]
    private static partial Regex RemSize();

    /// <summary>
    ///     Flattens an SVG's CSS variables, colour mixes and <c>rem</c> sizes into plain values.
    /// </summary>
    /// <param name="svg">The SVG.</param>
    /// <returns>The SVG with plain values.</returns>
    public static string Flatten(string svg)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RootStyle().Match(svg) is { Success: true } root)
        {
            AddDeclarations(root.Groups["style"].Value, variables);
        }

        foreach (Match rule in RootRule().Matches(svg))
        {
            AddDeclarations(rule.Groups["body"].Value, variables);
        }

        var output = new StringBuilder(svg.Length);
        var index = 0;
        while (NextFunction(svg, index) is var (start, end) && start >= 0)
        {
            output.Append(svg, index, start - index);
            output.Append(ToPixels(Evaluate(svg[start..end], variables, 0)));
            index = end;
        }

        output.Append(svg, index, svg.Length - index);
        return output.ToString();
    }

    /// <summary>
    ///     Reads custom property declarations; the first definition of a name wins, as the root's own style outranks a
    ///     rule.
    /// </summary>
    /// <param name="css">The declarations.</param>
    /// <param name="variables">The variables to add to.</param>
    private static void AddDeclarations(string css, Dictionary<string, string> variables)
    {
        foreach (Match declaration in Declaration().Matches(css))
        {
            variables.TryAdd(declaration.Groups["name"].Value, declaration.Groups["value"].Value.Trim());
        }
    }

    /// <summary>
    ///     Finds the next <c>var(…)</c> or <c>color-mix(…)</c> from a position.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="from">Where to look from.</param>
    /// <returns>Where it starts and ends (past its closing parenthesis), or a start of -1 when there's none.</returns>
    private static (int Start, int End) NextFunction(string text, int from)
    {
        while (true)
        {
            var var = text.IndexOf("var(", from, StringComparison.Ordinal);
            var mix = text.IndexOf("color-mix(", from, StringComparison.Ordinal);
            var start = var < 0 ? mix : mix < 0 ? var : Math.Min(var, mix);
            if (start < 0)
            {
                return (-1, -1);
            }

            var open = text.IndexOf('(', start);
            if (ClosingParen(text, open) is var close and >= 0)
            {
                return (start, close + 1);
            }

            from = open + 1;
        }
    }

    /// <summary>
    ///     Finds the parenthesis closing the one at a position.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="open">Where the opening parenthesis is.</param>
    /// <returns>Where its closing one is, or -1.</returns>
    private static int ClosingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
            else if (text[i] is '"' or '<' or '>')
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Resolves every function in a value.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="variables">The variables.</param>
    /// <param name="depth">How deep the resolving is.</param>
    /// <returns>The plain value.</returns>
    private static string Evaluate(string value, IReadOnlyDictionary<string, string> variables, int depth)
    {
        if (depth > MaxDepth)
        {
            return value;
        }

        var output = new StringBuilder();
        var index = 0;
        while (NextFunction(value, index) is var (start, end) && start >= 0)
        {
            output.Append(value, index, start - index);
            var open = value.IndexOf('(', start);
            var arguments = SplitArguments(value[(open + 1)..(end - 1)]);
            output.Append(value[start..open] == "var"
                ? Variable(arguments, variables, depth)
                : Mix(arguments.Select(a => Evaluate(a, variables, depth + 1)).ToList()) ?? value[start..end]);
            index = end;
        }

        output.Append(value, index, value.Length - index);
        return output.ToString().Trim();
    }

    /// <summary>
    ///     Resolves <c>var(--name, fallback)</c>.
    /// </summary>
    /// <param name="arguments">The name and, if any, the fallback.</param>
    /// <param name="variables">The variables.</param>
    /// <param name="depth">How deep the resolving is.</param>
    /// <returns>The value.</returns>
    private static string Variable(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> variables, int depth)
    {
        if (variables.TryGetValue(arguments[0].Trim(), out var defined))
        {
            return Evaluate(defined, variables, depth + 1);
        }

        // The fallback is everything after the first comma, commas and all.
        return arguments.Count > 1 ? Evaluate(string.Join(",", arguments.Skip(1)), variables, depth + 1) : string.Empty;
    }

    /// <summary>
    ///     Mixes two colours, as <c>color-mix(in srgb, a p%, b q%)</c> does.
    /// </summary>
    /// <param name="arguments">The colour space, then the two colours with their optional percentages.</param>
    /// <returns>The colour, or null when it can't be read.</returns>
    private static string? Mix(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 3 || ReadStop(arguments[1]) is not var (first, firstShare) || ReadStop(arguments[2]) is not var (second, secondShare))
        {
            return null;
        }

        var p = firstShare ?? (secondShare is { } s ? 1 - s : 0.5);
        var q = secondShare ?? 1 - p;
        var total = p + q;
        if (total <= 0)
        {
            return null;
        }

        p /= total;
        q /= total;

        // Mixed premultiplied by alpha, as CSS does, so mixing with transparent fades rather than darkens.
        var alpha = first.A * p + second.A * q;
        if (alpha <= 0)
        {
            return "transparent";
        }

        double Channel(double a, double b)
        {
            return (a * first.A * p + b * second.A * q) / alpha;
        }

        var red = (int)Math.Round(Channel(first.R, second.R));
        var green = (int)Math.Round(Channel(first.G, second.G));
        var blue = (int)Math.Round(Channel(first.B, second.B));
        return alpha >= 0.999
            ? $"#{red:X2}{green:X2}{blue:X2}"
            : string.Create(CultureInfo.InvariantCulture, $"rgba({red},{green},{blue},{alpha:0.###})");
    }

    /// <summary>
    ///     Reads one colour of a mix and its share.
    /// </summary>
    /// <param name="text">The colour, optionally followed by a percentage.</param>
    /// <returns>The colour and its share (0–1, or null when not given), or null when it can't be read.</returns>
    private static ((double R, double G, double B, double A) Color, double? Share)? ReadStop(string text)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2)
        {
            return null;
        }

        double? share = null;
        if (parts.Length == 2)
        {
            if (!parts[1].EndsWith('%') || !double.TryParse(parts[1][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return null;
            }

            share = percent / 100;
        }

        return ReadColor(parts[0]) is { } color ? (color, share) : null;
    }

    /// <summary>
    ///     Reads a colour: <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>transparent</c>, <c>white</c> or
    ///     <c>black</c>.
    /// </summary>
    /// <param name="text">The colour.</param>
    /// <returns>Its channels (0–255) and alpha (0–1), or null.</returns>
    private static (double R, double G, double B, double A)? ReadColor(string text)
    {
        switch (text.ToLowerInvariant())
        {
            case "transparent":
                return (0, 0, 0, 0);
            case "white":
                return (255, 255, 255, 1);
            case "black":
                return (0, 0, 0, 1);
        }

        if (!text.StartsWith('#'))
        {
            return null;
        }

        var hex = text[1..];
        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        }

        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return hex.Length == 6
            ? ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF, 1)
            : ((value >> 24) & 0xFF, (value >> 16) & 0xFF, (value >> 8) & 0xFF, (value & 0xFF) / 255.0);
    }

    /// <summary>
    ///     Splits a function's arguments at its top-level commas.
    /// </summary>
    /// <param name="text">The arguments.</param>
    /// <returns>Each argument.</returns>
    private static List<string> SplitArguments(string text)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
            }
            else if (text[i] == ',' && depth == 0)
            {
                arguments.Add(text[start..i]);
                start = i + 1;
            }
        }

        arguments.Add(text[start..]);
        return arguments;
    }

    /// <summary>
    ///     Turns a <c>rem</c> size into pixels, which an SVG attribute can hold.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The value, in pixels if it was a <c>rem</c> size.</returns>
    private static string ToPixels(string value)
    {
        return RemSize().Match(value) is { Success: true } rem
            ? (double.Parse(rem.Groups["n"].Value, CultureInfo.InvariantCulture) * PixelsPerRem).ToString("0.##", CultureInfo.InvariantCulture)
            : value;
    }
}
