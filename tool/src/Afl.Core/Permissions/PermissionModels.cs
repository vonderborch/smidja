using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Afl.Core.Exceptions;
using Afl.Core.Storage;

namespace Afl.Core.Permissions;

/// <summary>
///     Whether a rule lets agents use what it matches or refuses it.
/// </summary>
public enum PermissionRuleKind
{
    /// <summary>
    ///     Agents may use what the rule matches without asking.
    /// </summary>
    Allow,

    /// <summary>
    ///     Agents are refused what the rule matches, whatever else allows it.
    /// </summary>
    Deny,
}

/// <summary>
///     Where a permission rule is saved, and so which runs it applies to.
/// </summary>
public enum PermissionScope
{
    /// <summary>
    ///     This run only (and the child and path runs it starts).
    /// </summary>
    Run,

    /// <summary>
    ///     Every run in the repo.
    /// </summary>
    Repo,

    /// <summary>
    ///     Every run in every repo.
    /// </summary>
    Global,
}

/// <summary>
///     What the user decided about one refused tool call.
/// </summary>
public enum PermissionDecision
{
    /// <summary>
    ///     Allow it for the agent's next turn only.
    /// </summary>
    Once,

    /// <summary>
    ///     Allow it, and things like it, from now on: the rule is saved to a scope.
    /// </summary>
    Always,

    /// <summary>
    ///     Keep refusing it.
    /// </summary>
    Deny,
}

/// <summary>
///     A Claude Code permission rule, such as <c>Bash(dotnet test:*)</c>, <c>WebFetch(domain:github.com)</c>,
///     <c>Edit(src/**)</c> or <c>mcp__jira__search</c>, saved to a scope.
/// </summary>
public partial class PermissionRule
{
    /// <summary>
    ///     Gets or sets the rule: a tool name, optionally followed by a specifier in parentheses.
    /// </summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether the rule allows or denies.
    /// </summary>
    public PermissionRuleKind Kind { get; set; } = PermissionRuleKind.Allow;

    /// <summary>
    ///     Gets or sets when the rule was added.
    /// </summary>
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>
    ///     Gets or sets why the rule was added, e.g. the command it was first allowed for.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    ///     Gets the tool the rule is about: the part before any parentheses.
    /// </summary>
    [JsonIgnore]
    public string Tool => ToolOf(this.Rule);

    /// <summary>
    ///     Returns whether <paramref name="rule" /> is a rule afl can pass to an agent: a tool name, optionally with a
    ///     specifier in parentheses, on one line and without commas (which separate rules on the command line).
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>True if usable.</returns>
    public static bool IsValid(string? rule)
    {
        return !string.IsNullOrWhiteSpace(rule) && RuleShape().IsMatch(rule);
    }

    /// <summary>
    ///     Returns why <paramref name="rule" /> can't be used, or null when it can.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The problem, or null.</returns>
    public static string? Problem(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return "A permission rule can't be empty.";
        }

        return IsValid(rule)
            ? null
            : $"'{rule}' isn't a permission rule. Use a tool name, optionally with a specifier in parentheses, such as " +
              "Bash(dotnet test:*), WebFetch(domain:github.com) or Edit(src/**), on one line and without commas.";
    }

    /// <summary>
    ///     Returns the tool a rule is about: the part before any parentheses.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The tool name.</returns>
    public static string ToolOf(string rule)
    {
        var open = rule.IndexOf('(');
        return (open < 0 ? rule : rule[..open]).Trim();
    }

    /// <summary>
    ///     Adds a rule to a list, replacing one with the same text, and returns whether the list changed.
    /// </summary>
    /// <param name="rules">The list.</param>
    /// <param name="rule">The rule text.</param>
    /// <param name="kind">Allow or deny.</param>
    /// <param name="note">Why it was added.</param>
    /// <returns>True if the rule was added or its kind changed.</returns>
    public static bool AddTo(List<PermissionRule> rules, string rule, PermissionRuleKind kind, string? note = null)
    {
        var problem = Problem(rule);
        if (problem is not null)
        {
            throw new AflException(AflErrorKind.InvalidInput, problem);
        }

        var existing = rules.FirstOrDefault(r => r.Rule == rule.Trim());
        if (existing is not null)
        {
            if (existing.Kind == kind)
            {
                return false;
            }

            existing.Kind = kind;
            return true;
        }

        rules.Add(new PermissionRule { Rule = rule.Trim(), Kind = kind, AddedAt = DateTimeOffset.Now, Note = note });
        return true;
    }

    /// <summary>
    ///     The shape of a usable rule.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^\s*[A-Za-z][A-Za-z0-9_\-]*(\([^,\r\n]+\))?\s*$")]
    private static partial Regex RuleShape();
}

/// <summary>
///     A tool call an agent was refused, as afl shows it to the user.
/// </summary>
public class PermissionDenial
{
    /// <summary>
    ///     Gets or sets the tool, e.g. <c>Bash</c> or <c>mcp__jira__search</c>.
    /// </summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the agent's id for the call, if it gave one.
    /// </summary>
    public string? ToolUseId { get; set; }

    /// <summary>
    ///     Gets or sets what the call would have done, on one line: the command, URL or file path.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the rules that would allow it, most specific first.
    /// </summary>
    public List<string> SuggestedRules { get; set; } = [];

    /// <summary>
    ///     Gets or sets what refuses the call whatever is allowed: the deny rule (a guard rail or a saved deny rule) it
    ///     matches, or <see cref="Constants.PermissionConstants.RefusedAfterAllowed" />. Null when allowing it would work.
    ///     afl doesn't ask about a blocked call, since an allow rule can't win over a deny rule.
    /// </summary>
    public string? BlockedBy { get; set; }

    /// <summary>
    ///     Gets the call as one line: the tool, then what it would have done.
    /// </summary>
    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(this.Summary) || this.Summary == this.Tool
        ? this.Tool
        : $"{this.Tool}: {this.Summary}";
}

/// <summary>
///     A refused tool call a paused step asks the user about, and what they decided.
/// </summary>
public class PermissionRequest
{
    /// <summary>
    ///     Gets or sets the request's number on the step, counting from 1.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    ///     Gets or sets the refused call.
    /// </summary>
    public PermissionDenial Denial { get; set; } = new();

    /// <summary>
    ///     Gets or sets what the user decided; null while undecided.
    /// </summary>
    public PermissionDecision? Decision { get; set; }

    /// <summary>
    ///     Gets or sets the rule the user allowed, for <see cref="PermissionDecision.Once" /> and
    ///     <see cref="PermissionDecision.Always" />.
    /// </summary>
    public string? Rule { get; set; }

    /// <summary>
    ///     Gets or sets where an <see cref="PermissionDecision.Always" /> rule was saved.
    /// </summary>
    public PermissionScope? Scope { get; set; }

    /// <summary>
    ///     Gets or sets the round of asking the request belongs to, counting from 1.
    /// </summary>
    public int Round { get; set; }

    /// <summary>
    ///     Gets or sets when the user decided.
    /// </summary>
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
///     What a front-end passes to decide one permission request.
/// </summary>
/// <param name="Index">The request's number (<see cref="PermissionRequest.Index" />).</param>
/// <param name="Decision">Allow once, always, or deny.</param>
/// <param name="Rule">The rule to allow; null for the request's first suggestion.</param>
/// <param name="Scope">Where to save an <see cref="PermissionDecision.Always" /> rule.</param>
public record PermissionChoice(int Index, PermissionDecision Decision, string? Rule = null,
    PermissionScope Scope = PermissionScope.Repo);

/// <summary>
///     The contents of <c>~/.afl/permissions.json</c>: rules for every run in every repo.
/// </summary>
[DocumentVersion(1, 0)]
public class PermissionRuleDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the rules.
    /// </summary>
    public List<PermissionRule> Rules { get; set; } = [];
}
