using Avalonia;
using System.Text.RegularExpressions;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Afl.UI.DataContexts;

/// <summary>
///     Value converters the afl pages share in bindings.
/// </summary>
public static class UiConverters
{
    /// <summary>
    ///     True when an integer is more than one, e.g. to show a retry count only after the first attempt.
    /// </summary>
    public static readonly IValueConverter IsMoreThanOne = new FuncValueConverter<int, bool>(value => value > 1);

    /// <summary>
    ///     Joins a list of strings with commas, e.g. a workflow step's allowed tools.
    /// </summary>
    public static readonly IValueConverter JoinWithCommas =
        new FuncValueConverter<IEnumerable<string>?, string>(values => values is null ? string.Empty : string.Join(", ", values));

    /// <summary>
    ///     Joins a multi-binding's values into a "a · b · c" meta line, skipping blank values and writing enum values
    ///     as words (see <see cref="Words" />).
    /// </summary>
    public static readonly IMultiValueConverter MetaLine = new FuncMultiValueConverter<object?, string>(values =>
        string.Join(" · ", values.Select(ToText).Where(text => !string.IsNullOrWhiteSpace(text))));

    /// <summary>
    ///     Writes a step's answers one per line, as <c>id: value</c>.
    /// </summary>
    public static readonly IValueConverter AnswerLines =
        new FuncValueConverter<IReadOnlyDictionary<string, string>?, string>(answers =>
            answers is null ? string.Empty : string.Join("\n", answers.Select(a => $"{a.Key}: {a.Value}")));

    /// <summary>
    ///     Writes the questions the AI asked part-way through a step, and the answers, one per line.
    /// </summary>
    public static readonly IValueConverter AskRoundLines =
        new FuncValueConverter<IEnumerable<AskRound>?, string>(AskRound.Describe);

    /// <summary>
    ///     Writes which agent did a step and who chose it, e.g. "Agent: codex (chosen by main-agent: it edits code)".
    /// </summary>
    public static readonly IValueConverter AgentChoiceLine =
        new FuncValueConverter<AgentChoice?, string>(choice => choice is null ? string.Empty : $"Agent: {choice.Describe()}");

    /// <summary>
    ///     Writes the sub-tasks the AI handed to other agents part-way through a step, one per line.
    /// </summary>
    public static readonly IValueConverter DelegationLines =
        new FuncValueConverter<IEnumerable<Delegation>?, string>(delegations =>
            delegations is null ? string.Empty : string.Join("\n", delegations.Select(d => $"Handed to {d.Describe()}")));

    /// <summary>
    ///     Writes a ticket the AI filed part-way through a step, e.g. "Filed acme/app#12: Add audit logging", or
    ///     "Ticket not created (…)" with where to file it by hand.
    /// </summary>
    public static readonly IValueConverter CreatedTicketLine =
        new FuncValueConverter<CreatedTicket?, string>(ticket =>
            ticket is null ? string.Empty : $"{(ticket.Key is null ? "Ticket" : "Filed")} {ticket.Describe()}");

    /// <summary>
    ///     Describes a child run a step started in one line.
    /// </summary>
    public static readonly IValueConverter ChildRunLine =
        new FuncValueConverter<ChildRun?, string>(child => child switch
        {
            null => string.Empty,
            { Branch: not null } => child.Describe(),
            _ => $"Child run {child.Describe()}",
        });

    /// <summary>
    ///     Indents a run in the run list by how deep it is among child runs.
    /// </summary>
    public static readonly IValueConverter DepthIndent = new FuncValueConverter<int, Thickness>(depth => new Thickness(depth * 16, 0, 0, 0));

    /// <summary>
    ///     Returns the file a step's output was saved to as a link, from a multi-binding of the step and its run; null
    ///     when it has none.
    /// </summary>
    public static readonly IMultiValueConverter ArtifactUri = new FuncMultiValueConverter<object?, Uri?>(values =>
    {
        var list = values.ToList();
        return list is [RunStep { ArtifactPath: { } path }, Run run] ? new Uri(Path.Combine(run.RepoPath, path)) : null;
    });

    /// <summary>
    ///     Whether a saved file is a markdown document, which the Runs page can preview formatted.
    /// </summary>
    public static readonly IValueConverter IsMarkdownPath = new FuncValueConverter<string?, bool>(path =>
        path is not null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                             || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    ///     Reverses a list, e.g. to show a step's earlier attempts newest first.
    /// </summary>
    public static readonly IValueConverter NewestFirst =
        new FuncValueConverter<System.Collections.IEnumerable?, System.Collections.IEnumerable>(values =>
            values?.Cast<object>().Reverse().ToList() ?? []);

    /// <summary>
    ///     Writes which time round a run's step is on as "iteration n/max", from a multi-binding of the step and its run;
    ///     empty for a step no loop can bring back.
    /// </summary>
    public static readonly IMultiValueConverter Iteration = new FuncMultiValueConverter<object?, string>(values =>
    {
        var list = values.ToList();
        return list is [RunStep step, Run run] && FlowRouter.Instance.LimitOf(run.Workflow, run.DefinitionOf(step)) is { } max
            ? $"iteration {step.Iteration}/{max}"
            : string.Empty;
    });

    /// <summary>
    ///     Writes a step's status as words, from a multi-binding of the step and its run: "not reached" for a step a
    ///     finished run never came to.
    /// </summary>
    public static readonly IMultiValueConverter StepStatusText = new FuncMultiValueConverter<object?, string>(values =>
    {
        var list = values.ToList();
        return list is [RunStep step, Run run] ? run.NotReached(step) ? "not reached" : ToText(step.Status) : string.Empty;
    });

    /// <summary>
    ///     Indents a run in the run list by how deep it is among child runs; a path run sits one further in than
    ///     the run it belongs to.
    /// </summary>
    public static readonly IValueConverter RunIndent = new FuncValueConverter<Run?, Thickness>(run =>
        run is null ? default : new Thickness((run.Depth + (run.IsPathRun ? 1 : 0)) * 16, 0, 0, 0));

    /// <summary>
    ///     True for a step that was skipped or left out, so the timeline can grey it out.
    /// </summary>
    public static readonly IValueConverter IsSkipped = new FuncValueConverter<StepStatus, bool>(status => status == StepStatus.Skipped);

    /// <summary>
    ///     Writes an enum value as lower-case words, e.g. <c>AwaitingApproval</c> as "awaiting approval".
    /// </summary>
    public static readonly IValueConverter Words = new FuncValueConverter<object?, string>(ToText);

    /// <summary>
    ///     The colour of each run state, readable on light and dark backgrounds.
    /// </summary>
    private static readonly IReadOnlyDictionary<RunState, IBrush> StateBrushes = new Dictionary<RunState, IBrush>
    {
        [RunState.NeedsYou] = new ImmutableSolidColorBrush(Color.Parse("#E8890C")),
        [RunState.Running] = new ImmutableSolidColorBrush(Color.Parse("#3B82F6")),
        [RunState.Waiting] = new ImmutableSolidColorBrush(Color.Parse("#8B7FD6")),
        [RunState.Failed] = new ImmutableSolidColorBrush(Color.Parse("#E5484D")),
        [RunState.Done] = new ImmutableSolidColorBrush(Color.Parse("#30A46C")),
        [RunState.Cancelled] = new ImmutableSolidColorBrush(Color.Parse("#8B8D98")),
    };

    /// <summary>
    ///     Gets the colour a run state's symbol is drawn in.
    /// </summary>
    public static readonly IValueConverter StateBrush =
        new FuncValueConverter<RunState, IBrush>(state => StateBrushes[state]);

    /// <summary>
    ///     Writes a run state as its symbol and name, e.g. "● Needs you".
    /// </summary>
    public static readonly IValueConverter StateText =
        new FuncValueConverter<RunState, string>(state => $"{RunStates.Info(state).Symbol} {RunStates.Info(state).Label}");

    /// <summary>
    ///     Gets the tooltip for a run state, saying what it means.
    /// </summary>
    public static readonly IValueConverter StateDescription =
        new FuncValueConverter<RunState, string>(state => RunStates.Info(state).Description);

    /// <summary>
    ///     Converts a bound value to display text.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The text; enum values become lower-case words.</returns>
    internal static string ToText(object? value)
    {
        return value is Enum
            ? Regex.Replace(value.ToString()!, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant()
            : value?.ToString() ?? string.Empty;
    }
}
