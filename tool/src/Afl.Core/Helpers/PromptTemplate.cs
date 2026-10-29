using System.Text.RegularExpressions;

namespace Afl.Core.Helpers;

/// <summary>
///     Renders <c>{{name}}</c> placeholders in step prompts and instructions. Unknown placeholders render as empty text;
///     workflow validation is what catches bad references. The optional form, <c>{{name?}}</c>, renders the same way but
///     tells validation the value may not be there, e.g. the output of a step on only some of the ways in.
/// </summary>
public static partial class PromptTemplate
{
    /// <summary>
    ///     What ends a placeholder name in the optional form.
    /// </summary>
    public const string OptionalMark = "?";

    /// <summary>
    ///     Returns whether a placeholder name (from <see cref="References" />) is in the optional form.
    /// </summary>
    /// <param name="reference">The name.</param>
    /// <returns>True if it is.</returns>
    public static bool IsOptional(string reference)
    {
        return reference.EndsWith(OptionalMark, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns a placeholder name without the optional mark.
    /// </summary>
    /// <param name="reference">The name.</param>
    /// <returns>The variable it names.</returns>
    public static string NameOf(string reference)
    {
        return IsOptional(reference) ? reference[..^OptionalMark.Length] : reference;
    }

    /// <summary>
    ///     Renders a template.
    /// </summary>
    /// <param name="template">The template text.</param>
    /// <param name="variables">The variable values by name, e.g. <c>ticket.title</c>.</param>
    /// <returns>The rendered text.</returns>
    public static string Render(string? template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex().Replace(template,
            match => variables.TryGetValue(match.Groups["name"].Value, out var value) ? value : string.Empty);
    }

    /// <summary>
    ///     Fills in only the placeholders a value is given for, leaving every other placeholder as it is so it can be
    ///     rendered later (e.g. <c>{{params.x}}</c> when a workflow is expanded, before the run renders the rest).
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="variables">The values by placeholder name.</param>
    /// <returns>The template with those placeholders filled in.</returns>
    public static string Substitute(string template, IReadOnlyDictionary<string, string> variables)
    {
        return PlaceholderRegex().Replace(template,
            match => variables.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Value);
    }

    /// <summary>
    ///     Lists the placeholder names used in a template, each written in the optional form ending with
    ///     <see cref="OptionalMark" /> when it was used that way.
    /// </summary>
    /// <param name="template">The template text.</param>
    /// <returns>The distinct names, in order of first use.</returns>
    public static IReadOnlyList<string> References(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return [];
        }

        return PlaceholderRegex().Matches(template).Select(m => m.Groups["name"].Value + m.Groups["optional"].Value).Distinct().ToList();
    }

    /// <summary>
    ///     Matches <c>{{ name.with.dots }}</c>, and the optional form <c>{{ name.with.dots? }}</c>.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"\{\{\s*(?<name>[\w.-]+)(?<optional>\?)?\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}
