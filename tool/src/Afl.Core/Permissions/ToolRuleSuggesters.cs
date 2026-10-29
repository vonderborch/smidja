using System.Text.Json;
using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Registration;

namespace Afl.Core.Permissions;

/// <summary>
///     Registers an <see cref="AbstractToolRuleSuggester" /> for the tool it describes.
/// </summary>
/// <param name="tool">The tool name, e.g. <c>Bash</c>.</param>
public class ToolRuleSuggesterRegistrationAttribute(string tool) : AbstractRegistrationAttribute(tool);

/// <summary>
///     Describes a refused call to one tool for the user, and suggests the rules that would allow it, most specific
///     first. A tool with nothing registered gets <see cref="FallbackToolRuleSuggester" />: its call summarised from the
///     usual input fields and its name as the only rule. A new tool is one subclass with
///     <see cref="ToolRuleSuggesterRegistrationAttribute" />.
/// </summary>
public abstract class AbstractToolRuleSuggester
{
    /// <summary>
    ///     The input fields that say what a call does, in the order they're looked for.
    /// </summary>
    private static readonly string[] SummaryFields = ["command", "file_path", "notebook_path", "path", "url", "pattern", "query", "description"];

    /// <summary>
    ///     Describes a refused call.
    /// </summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="toolUseId">The agent's id for the call, if any.</param>
    /// <param name="input">The call's input, or an undefined element when there was none.</param>
    /// <param name="workingDirectory">The directory the agent works in, for paths.</param>
    /// <param name="denyRules">The deny rules the agent ran with, to tell whether one blocks the call; null for none.</param>
    /// <returns>The denial, with its suggested rules.</returns>
    public PermissionDenial Describe(
        string tool,
        string? toolUseId,
        JsonElement input,
        string? workingDirectory,
        IReadOnlyList<string>? denyRules = null)
    {
        var suggestions = this.Suggest(tool, input, workingDirectory)
            .Where(PermissionRule.IsValid)
            .Distinct()
            .ToList();
        if (suggestions.Count == 0)
        {
            suggestions.Add(tool);
        }

        return new PermissionDenial
        {
            Tool = tool,
            ToolUseId = toolUseId,
            Summary = Shorten(this.Summarize(tool, input)),
            SuggestedRules = suggestions,
            BlockedBy = denyRules?.FirstOrDefault(rule => PermissionRule.IsValid(rule) && this.IsBlockedBy(rule, tool, input, workingDirectory)),
        };
    }

    /// <summary>
    ///     Returns whether a deny rule matches a call, so allowing the call can't work. By default, a rule naming just
    ///     the tool.
    /// </summary>
    /// <param name="rule">The deny rule.</param>
    /// <param name="tool">The tool name.</param>
    /// <param name="input">The call's input.</param>
    /// <param name="workingDirectory">The directory the agent works in.</param>
    /// <returns>True if the rule matches.</returns>
    protected virtual bool IsBlockedBy(string rule, string tool, JsonElement input, string? workingDirectory)
    {
        return rule.Trim() == tool;
    }

    /// <summary>
    ///     Returns the specifier of a rule about one of <paramref name="tools" />: the part in parentheses.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="tools">The tool names the rule may name.</param>
    /// <returns>The specifier; empty when the rule names the tool alone; null when it's about another tool.</returns>
    protected static string? SpecifierOf(string rule, params string[] tools)
    {
        if (!tools.Contains(PermissionRule.ToolOf(rule)))
        {
            return null;
        }

        var open = rule.IndexOf('(');
        return open < 0 ? string.Empty : rule[(open + 1)..rule.LastIndexOf(')')].Trim();
    }

    /// <summary>
    ///     Says what a call would have done, on one line. By default, the first of the usual input fields that's set.
    /// </summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="input">The call's input.</param>
    /// <returns>The summary; the tool name when the input says nothing useful.</returns>
    protected virtual string Summarize(string tool, JsonElement input)
    {
        foreach (var field in SummaryFields)
        {
            var value = Text(input, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return tool;
    }

    /// <summary>
    ///     Suggests the rules that would allow a call, most specific first. Unusable rules are dropped afterwards, and
    ///     the tool name is used when none is left.
    /// </summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="input">The call's input.</param>
    /// <param name="workingDirectory">The directory the agent works in.</param>
    /// <returns>The rules.</returns>
    protected abstract IEnumerable<string> Suggest(string tool, JsonElement input, string? workingDirectory);

    /// <summary>
    ///     Reads a string field of a call's input.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="field">The field.</param>
    /// <returns>The value, or null.</returns>
    protected static string? Text(JsonElement input, string field)
    {
        return input.ValueKind == JsonValueKind.Object && input.TryGetProperty(field, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    ///     Puts text on one line and caps its length.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The shortened text.</returns>
    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > PermissionConstants.MaxSummaryLength
            ? line[..(PermissionConstants.MaxSummaryLength - 3)] + "..."
            : line;
    }
}

/// <summary>
///     Suggests the tool's name: for MCP tools and anything else without its own suggester.
/// </summary>
public class FallbackToolRuleSuggester : AbstractToolRuleSuggester
{
    /// <inheritdoc />
    protected override IEnumerable<string> Suggest(string tool, JsonElement input, string? workingDirectory)
    {
        yield return tool;
    }
}

/// <summary>
///     Shell commands: the exact command, then the same command with any arguments after its first two words, then
///     after its first word. A compound command (<c>&amp;&amp;</c>, <c>;</c>, a pipe, a substitution) gets only the exact
///     rule, since a prefix rule would let anything follow.
/// </summary>
[ToolRuleSuggesterRegistration("Bash")]
public class BashRuleSuggester : AbstractToolRuleSuggester
{
    /// <summary>
    ///     What makes a command more than one command.
    /// </summary>
    private static readonly string[] CompoundMarks = ["&&", "||", ";", "|", "`", "$(", ">", "<", "\n", "&"];

    /// <inheritdoc />
    protected override IEnumerable<string> Suggest(string tool, JsonElement input, string? workingDirectory)
    {
        var command = Text(input, "command")?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            yield return tool;
            yield break;
        }

        if (command.Length <= PermissionConstants.MaxExactRuleLength)
        {
            yield return $"{tool}({command})";
        }

        if (CompoundMarks.Any(command.Contains))
        {
            yield break;
        }

        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 2 && IsPlainWord(words[1]))
        {
            yield return $"{tool}({words[0]} {words[1]}:*)";
        }

        if (words.Length > 1)
        {
            yield return $"{tool}({words[0]}:*)";
        }
    }

    /// <inheritdoc />
    protected override bool IsBlockedBy(string rule, string tool, JsonElement input, string? workingDirectory)
    {
        var specifier = SpecifierOf(rule, tool);
        var command = Text(input, "command")?.Trim();
        if (specifier is null || specifier.Length == 0 || command is null)
        {
            return specifier?.Length == 0;
        }

        if (!specifier.EndsWith(":*", StringComparison.Ordinal))
        {
            return command == specifier;
        }

        var prefix = specifier[..^2].TrimEnd();
        return command == prefix || command.StartsWith(prefix + " ", StringComparison.Ordinal) ||
               (prefix.EndsWith(' ') && command.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Returns whether a word reads like a subcommand (<c>test</c>, <c>run</c>) rather than an option or a path.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <returns>True if it does.</returns>
    private static bool IsPlainWord(string word)
    {
        return word.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && char.IsAsciiLetter(word[0]);
    }
}

/// <summary>
///     Web fetches: the site's domain.
/// </summary>
[ToolRuleSuggesterRegistration("WebFetch")]
public class WebFetchRuleSuggester : AbstractToolRuleSuggester
{
    /// <inheritdoc />
    protected override IEnumerable<string> Suggest(string tool, JsonElement input, string? workingDirectory)
    {
        if (Uri.TryCreate(Text(input, "url"), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            yield return $"{tool}(domain:{uri.Host})";
        }

        yield return tool;
    }
}

/// <summary>
///     Tools that take a file: the file, then everything in its folder. Paths inside the working directory are written
///     relative to it, others absolute (<c>//abs/path</c>), the way Claude Code reads rules.
/// </summary>
/// <param name="ruleTool">The tool name rules use; Claude Code's <c>Edit</c> rules cover every tool that writes files.</param>
public abstract class AbstractPathRuleSuggester(string ruleTool) : AbstractToolRuleSuggester
{
    /// <summary>
    ///     Gets the tool name rules use.
    /// </summary>
    public string RuleTool { get; } = ruleTool;

    /// <inheritdoc />
    protected override IEnumerable<string> Suggest(string tool, JsonElement input, string? workingDirectory)
    {
        var file = this.PathOf(input);
        if (string.IsNullOrWhiteSpace(file))
        {
            yield return this.RuleTool;
            yield break;
        }

        var rulePath = RulePath(file, workingDirectory);
        yield return $"{this.RuleTool}({rulePath})";
        var folder = rulePath.Contains('/') ? rulePath[..rulePath.LastIndexOf('/')] : null;
        yield return string.IsNullOrEmpty(folder) || folder == "/" ? $"{this.RuleTool}(**)" : $"{this.RuleTool}({folder}/**)";
    }

    /// <inheritdoc />
    protected override bool IsBlockedBy(string rule, string tool, JsonElement input, string? workingDirectory)
    {
        var specifier = SpecifierOf(rule, tool, this.RuleTool);
        if (specifier is null || specifier.Length == 0)
        {
            return specifier?.Length == 0;
        }

        var file = this.PathOf(input);
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        var directory = workingDirectory ?? Environment.CurrentDirectory;
        var path = Path.GetFullPath(file.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Home, file[2..]) : Path.Combine(directory, file))
            .Replace('\\', '/').TrimEnd('/');
        return PathGlob(AbsolutePattern(specifier, directory)).IsMatch(path);
    }

    /// <summary>
    ///     Gets the path a call works on. By default, its file (or notebook, or path).
    /// </summary>
    /// <param name="input">The call's input.</param>
    /// <returns>The path, or null.</returns>
    protected virtual string? PathOf(JsonElement input)
    {
        return Text(input, "file_path") ?? Text(input, "notebook_path") ?? Text(input, "path");
    }

    /// <summary>
    ///     Gets the user's home folder.
    /// </summary>
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    ///     Writes a rule's path pattern as an absolute one, the way Claude Code reads it: <c>~/</c> is the home folder,
    ///     <c>//</c> the file system root, and anything else is relative to the working directory.
    /// </summary>
    /// <param name="pattern">The rule's pattern.</param>
    /// <param name="workingDirectory">The directory the agent works in.</param>
    /// <returns>The absolute pattern, with forward slashes.</returns>
    private static string AbsolutePattern(string pattern, string workingDirectory)
    {
        var absolute = pattern switch
        {
            _ when pattern.StartsWith("~/", StringComparison.Ordinal) => Home.Replace('\\', '/').TrimEnd('/') + pattern[1..],
            _ when pattern.StartsWith("//", StringComparison.Ordinal) => pattern[1..],
            _ => workingDirectory.Replace('\\', '/').TrimEnd('/') + "/" +
                 (pattern.StartsWith("./", StringComparison.Ordinal) ? pattern[2..] : pattern.TrimStart('/')),
        };
        return absolute.TrimEnd('/');
    }

    /// <summary>
    ///     Turns a path pattern into a regular expression: <c>**</c> is any number of folders, <c>*</c> and <c>?</c> stay
    ///     within one. A pattern ending <c>/**</c> also matches the folder itself.
    /// </summary>
    /// <param name="pattern">The absolute pattern.</param>
    /// <returns>The expression.</returns>
    private static Regex PathGlob(string pattern)
    {
        var expression = Regex.Escape(pattern)
            .Replace(@"/\*\*", "(/.*)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new Regex($"^{expression}$", RegexOptions.CultureInvariant);
    }

    /// <summary>
    ///     Writes a path the way a rule reads it.
    /// </summary>
    /// <param name="file">The path the agent used.</param>
    /// <param name="workingDirectory">The directory the agent works in.</param>
    /// <returns>The rule's path.</returns>
    protected static string RulePath(string file, string? workingDirectory)
    {
        if (!Path.IsPathRooted(file))
        {
            return file.Replace('\\', '/');
        }

        if (workingDirectory is not null)
        {
            var relative = Path.GetRelativePath(workingDirectory, file);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative.Replace('\\', '/');
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && file.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return "~/" + file[(home.Length + 1)..].Replace('\\', '/');
        }

        return "/" + file.Replace('\\', '/');
    }
}

/// <summary>
///     The <c>Edit</c> tool.
/// </summary>
[ToolRuleSuggesterRegistration("Edit")]
public class EditRuleSuggester() : AbstractPathRuleSuggester("Edit");

/// <summary>
///     The <c>MultiEdit</c> tool, allowed through <c>Edit</c> rules.
/// </summary>
[ToolRuleSuggesterRegistration("MultiEdit")]
public class MultiEditRuleSuggester() : AbstractPathRuleSuggester("Edit");

/// <summary>
///     The <c>Write</c> tool, allowed through <c>Edit</c> rules.
/// </summary>
[ToolRuleSuggesterRegistration("Write")]
public class WriteRuleSuggester() : AbstractPathRuleSuggester("Edit");

/// <summary>
///     The <c>NotebookEdit</c> tool, allowed through <c>Edit</c> rules.
/// </summary>
[ToolRuleSuggesterRegistration("NotebookEdit")]
public class NotebookEditRuleSuggester() : AbstractPathRuleSuggester("Edit");

/// <summary>
///     The <c>Read</c> tool.
/// </summary>
[ToolRuleSuggesterRegistration("Read")]
public class ReadRuleSuggester() : AbstractPathRuleSuggester("Read");

/// <summary>
///     Tools that search a folder (<c>Glob</c>, <c>Grep</c>), which Claude Code's <c>Read</c> rules cover: everything
///     in the folder, then the tool itself.
/// </summary>
public abstract class AbstractSearchRuleSuggester() : AbstractPathRuleSuggester("Read")
{
    /// <summary>
    ///     The characters that start the wildcard part of a pattern.
    /// </summary>
    private static readonly char[] Wildcards = ['*', '?', '[', '{'];

    /// <inheritdoc />
    protected override IEnumerable<string> Suggest(string tool, JsonElement input, string? workingDirectory)
    {
        if (this.PathOf(input) is { } folder && !string.IsNullOrWhiteSpace(folder))
        {
            var rulePath = RulePath(folder, workingDirectory).TrimEnd('/');
            yield return rulePath is "" or "." ? $"{this.RuleTool}(**)" : $"{this.RuleTool}({rulePath}/**)";
        }

        yield return tool;
    }

    /// <inheritdoc />
    protected override string? PathOf(JsonElement input)
    {
        if (Text(input, "path") is { } path && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        // A pattern that starts with a folder ("/abs/dir/**/*.cs") searches there.
        var pattern = Text(input, "pattern");
        if (pattern is null || !(Path.IsPathRooted(pattern) || pattern.StartsWith("~/", StringComparison.Ordinal)))
        {
            return null;
        }

        var wildcard = pattern.IndexOfAny(Wildcards);
        var fixedPart = wildcard < 0 ? pattern : pattern[..wildcard];
        return fixedPart.Contains('/') ? fixedPart[..fixedPart.LastIndexOf('/')] : fixedPart;
    }
}

/// <summary>
///     The <c>Glob</c> tool.
/// </summary>
[ToolRuleSuggesterRegistration("Glob")]
public class GlobRuleSuggester : AbstractSearchRuleSuggester;

/// <summary>
///     The <c>Grep</c> tool, whose pattern is a regular expression, so only its path says where it searches.
/// </summary>
[ToolRuleSuggesterRegistration("Grep")]
public class GrepRuleSuggester : AbstractSearchRuleSuggester
{
    /// <inheritdoc />
    protected override string? PathOf(JsonElement input)
    {
        return Text(input, "path");
    }
}

/// <summary>
///     Finds the suggester for a tool.
/// </summary>
public class ToolRuleSuggesterRegistry()
    : TypeRegistry<AbstractToolRuleSuggester, ToolRuleSuggesterRegistrationAttribute>("tool rule suggester")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static ToolRuleSuggesterRegistry Instance { get; } = new();

    /// <summary>
    ///     Returns the suggester for a tool, or the fallback when it has none.
    /// </summary>
    /// <param name="tool">The tool name.</param>
    /// <returns>The suggester.</returns>
    public AbstractToolRuleSuggester For(string tool)
    {
        return this.Contains(tool) ? this.Create(tool) : new FallbackToolRuleSuggester();
    }

    /// <summary>
    ///     Describes a refused call, with the rules that would allow it.
    /// </summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="toolUseId">The agent's id for the call, if any.</param>
    /// <param name="input">The call's input.</param>
    /// <param name="workingDirectory">The directory the agent works in.</param>
    /// <param name="denyRules">The deny rules the agent ran with; null for none.</param>
    /// <returns>The denial.</returns>
    public PermissionDenial Describe(
        string tool,
        string? toolUseId,
        JsonElement input,
        string? workingDirectory,
        IReadOnlyList<string>? denyRules = null)
    {
        return this.For(tool).Describe(tool, toolUseId, input, workingDirectory, denyRules);
    }
}
