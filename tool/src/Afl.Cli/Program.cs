using System.Reflection;
using Afl.Cli.Commands;
using CommandLine;

namespace Afl.Cli;

/// <summary>
///     The afl command-line entry point.
/// </summary>
internal static class Program
{
    /// <summary>
    ///     Group names that may be written as separate words before their sub-verb, e.g. <c>afl run approve</c> for
    ///     <c>afl run-approve</c> or <c>afl jira filter add</c> for <c>afl jira-filter-add</c>, and the verb used when the
    ///     group is given on its own (null when the group is itself a verb, or has none).
    /// </summary>
    private static readonly Dictionary<string, string?> VerbGroups = new()
    {
        ["run"] = "run-show",
        ["run-folder"] = "run-folders",
        ["agent"] = "agent-list",
        ["repo"] = null,
        ["workflow"] = "workflow-list",
        ["step"] = "step-list",
        ["skill"] = "skill-list",
        ["jira"] = null,
        ["jira-instance"] = "jira-instance-list",
        ["jira-filter"] = "jira-filter-list",
        ["jira-project"] = "jira",
        ["host"] = "host-list",
        ["mcp"] = "mcp-list",
        ["settings"] = "settings-show",
        ["capability"] = "capability-list",
        ["permission"] = "permission-list",
    };

    /// <summary>
    ///     The most words a verb name is made of.
    /// </summary>
    private const int MaxVerbWords = 3;

    /// <summary>
    ///     Runs afl.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> Main(string[] args)
    {
        var verbs = typeof(Program).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(AbstractCommand).IsAssignableFrom(t) && t.GetCustomAttribute<VerbAttribute>() is not null)
            .ToArray();
        var names = verbs.Select(t => t.GetCustomAttribute<VerbAttribute>()!.Name).ToHashSet();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var parser = new Parser(settings =>
        {
            settings.HelpWriter = Console.Error;
            settings.CaseInsensitiveEnumValues = true;
            settings.AllowMultiInstance = true;
        });
        var result = parser.ParseArguments(ExpandVerbGroups(args, names), verbs);
        return await result.MapResult(
            (AbstractCommand command) => command.RunAsync(cancellation.Token),
            _ => Task.FromResult(AbstractCommand.ExitUsage));
    }

    /// <summary>
    ///     Rewrites <c>group sub-verb</c> to <c>group-sub-verb</c>, and a bare group to its default verb.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <param name="names">The known verb names.</param>
    /// <returns>The rewritten arguments.</returns>
    private static string[] ExpandVerbGroups(string[] args, IReadOnlySet<string> names)
    {
        if (args.Length == 0 || !VerbGroups.ContainsKey(args[0]))
        {
            return args;
        }

        // The longest run of leading words that names a verb wins: "jira filter add" before "jira filter".
        for (var words = Math.Min(MaxVerbWords, args.Length); words > 1; words--)
        {
            var candidate = string.Join('-', args[..words]);
            if (names.Contains(candidate))
            {
                return [candidate, .. args[words..]];
            }
        }

        // Otherwise the longest group given on its own maps to its default verb: "jira filter" -> "jira-filter-list".
        for (var words = Math.Min(MaxVerbWords - 1, args.Length); words > 0; words--)
        {
            var group = string.Join('-', args[..words]);
            if (VerbGroups.TryGetValue(group, out var defaultVerb) && defaultVerb is not null && !names.Contains(group))
            {
                return [defaultVerb, .. args[words..]];
            }
        }

        return args;
    }
}
