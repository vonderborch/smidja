using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Steps;

/// <summary>
///     What a <c>workflow</c> step starts a child run for (<see cref="WorkflowStepDefinition.ForEach" />). Which fields apply
///     depends on its <see cref="Kind" />; see the matching child source.
/// </summary>
public class ChildSourceDefinition
{
    /// <summary>
    ///     Gets or sets the kind of source, which picks its <see cref="AbstractChildSource" />.
    /// </summary>
    public string Kind { get; set; } = StepConstants.ChildSourceTicket;

    /// <summary>
    ///     Gets or sets the earlier step the children come from, for sources that read one.
    /// </summary>
    public string? Step { get; set; }

    /// <summary>
    ///     Gets or sets the ticket references (templates allowed), for the <c>tickets</c> source.
    /// </summary>
    public List<string>? Tickets { get; set; }

    /// <summary>
    ///     Gets or sets the items as a template of comma- or newline-separated values, for the <c>items</c> source.
    /// </summary>
    public string? Items { get; set; }

    /// <summary>
    ///     Gets or sets a regular expression lines must match to be kept, for the <c>lines</c> source; its first group
    ///     (or the whole match, with no group) is the item. Every line is kept when unset.
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>
    ///     Gets or sets text that splits each kept line into several items, for the <c>lines</c> source (e.g. <c>,</c>);
    ///     a line is one item when unset.
    /// </summary>
    public string? Separator { get; set; }
}

/// <summary>
///     The fields of a <see cref="ChildSourceDefinition" /> a kind of source uses, so front-ends can show the right
///     editors.
/// </summary>
[Flags]
public enum ChildSourceFields
{
    /// <summary>
    ///     No fields.
    /// </summary>
    None = 0,

    /// <summary>
    ///     <see cref="ChildSourceDefinition.Step" />.
    /// </summary>
    Step = 1,

    /// <summary>
    ///     <see cref="ChildSourceDefinition.Tickets" />.
    /// </summary>
    Tickets = 2,

    /// <summary>
    ///     <see cref="ChildSourceDefinition.Items" />.
    /// </summary>
    Items = 4,

    /// <summary>
    ///     <see cref="ChildSourceDefinition.Pattern" /> and <see cref="ChildSourceDefinition.Separator" />.
    /// </summary>
    Pattern = 8,
}

/// <summary>
///     One child run to start: the ticket it works on and the item it was started for.
/// </summary>
/// <param name="Ticket">The ticket.</param>
/// <param name="Item">The item, available as <c>{{item}}</c>: the ticket key, or the line.</param>
public record ChildItem(Ticket Ticket, string Item);

/// <summary>
///     Registers a child source with the <see cref="ChildSourceRegistry" />.
/// </summary>
/// <param name="kind">The value of <see cref="ChildSourceDefinition.Kind" /> that picks the source.</param>
/// <param name="description">What the source gives, for front-ends.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class ChildSourceRegistrationAttribute(string kind, string description) : AbstractRegistrationAttribute(kind)
{
    /// <summary>
    ///     Gets what the source gives, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     Finds child sources by kind.
/// </summary>
public class ChildSourceRegistry() : TypeRegistry<AbstractChildSource, ChildSourceRegistrationAttribute>("child source")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static ChildSourceRegistry Instance { get; } = new();

    /// <summary>
    ///     Checks a step's child source for problems.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step it is on.</param>
    /// <returns>The problems; empty when valid.</returns>
    public IEnumerable<string> Validate(ChildSourceDefinition source, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        if (!this.Contains(source.Kind))
        {
            return [$"unknown forEach kind '{source.Kind}'. Known: {string.Join(", ", this.Keys.Order())}."];
        }

        return this.Create(source.Kind).Validate(source, workflow, step);
    }
}

/// <summary>
///     Base for a kind of child source: what a <c>workflow</c> step starts child runs for. A new kind is one subclass
///     with a <see cref="ChildSourceRegistrationAttribute" />.
/// </summary>
public abstract class AbstractChildSource
{
    /// <summary>
    ///     Gets the fields of a <see cref="ChildSourceDefinition" /> this kind uses.
    /// </summary>
    public virtual ChildSourceFields Fields => ChildSourceFields.None;

    /// <summary>
    ///     Checks a source's settings for problems.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="workflow">The workflow.</param>
    /// <param name="step">The step it is on.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(ChildSourceDefinition source, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        if (this.Fields.HasFlag(ChildSourceFields.Step))
        {
            var machine = new FlowMachine(workflow);
            if (string.IsNullOrWhiteSpace(source.Step) || !machine.Contains(step.Id) || !machine.Ancestors(step.Id).Contains(source.Step))
            {
                yield return $"forEach '{source.Kind}' needs 'step' naming a step that leads to this one.";
            }
        }
        else if (source.Step is not null)
        {
            yield return $"forEach '{source.Kind}' doesn't read a step; remove 'step'.";
        }

        if (!this.Fields.HasFlag(ChildSourceFields.Tickets) && source.Tickets is not null)
        {
            yield return $"forEach '{source.Kind}' doesn't take a ticket list; remove 'tickets'.";
        }

        if (!this.Fields.HasFlag(ChildSourceFields.Items) && source.Items is not null)
        {
            yield return $"forEach '{source.Kind}' doesn't take items; remove 'items'.";
        }

        if (!this.Fields.HasFlag(ChildSourceFields.Pattern) && (source.Pattern is not null || source.Separator is not null))
        {
            yield return $"forEach '{source.Kind}' doesn't filter lines; remove 'pattern' and 'separator'.";
        }
        else if (source.Pattern is not null && !IsValidPattern(source.Pattern))
        {
            yield return $"forEach '{source.Kind}' has a 'pattern' that isn't a valid regular expression.";
        }
    }

    /// <summary>
    ///     Returns whether text is a valid regular expression.
    /// </summary>
    /// <param name="pattern">The text.</param>
    /// <returns>True if it is.</returns>
    private static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Splits text into items: trimmed, without empty ones or repeats, in order.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="separators">What separates items.</param>
    /// <returns>The items.</returns>
    protected static IEnumerable<string> SplitItems(string text, params string[] separators)
    {
        return text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(i => i.Length > 0)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Returns the items a source will give that are already known before the run starts, so a parent can check its
    ///     children's <c>with</c> for each one; null when they're only known at run time.
    /// </summary>
    /// <param name="source">The source, with the workflow's parameters filled in.</param>
    /// <returns>The items, or null.</returns>
    public virtual IReadOnlyList<string>? KnownItems(ChildSourceDefinition source)
    {
        return null;
    }

    /// <summary>
    ///     Lists the child runs to start.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="context">The context of the <c>workflow</c> step.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The children, in order.</returns>
    public abstract Task<IReadOnlyList<ChildItem>> ResolveAsync(ChildSourceDefinition source, StepContext context, CancellationToken ct);

    /// <summary>
    ///     Fetches a ticket by reference through the run's ticket sources, falling back to what is already known about
    ///     it when it can't be fetched (e.g. its source isn't set up), so a child can still start.
    /// </summary>
    /// <param name="context">The context of the <c>workflow</c> step.</param>
    /// <param name="key">The ticket reference.</param>
    /// <param name="known">What is already known, if anything: a title and a URL.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The ticket.</returns>
    protected static async Task<Ticket> FetchTicketAsync(StepContext context, string key, (string? Title, string? Url) known, CancellationToken ct)
    {
        if (context.Tickets is not null && context.Repos?.Find(context.Run.RepoId) is { } repo)
        {
            try
            {
                return await context.Tickets.FetchAsync(repo, key, ct);
            }
            catch (AflException)
            {
                // Fall back to what is known below.
            }
        }

        var source = TicketRef.TryParse(key, out var reference) ? reference!.Source : context.Run.Ticket.Source;
        return new Ticket
        {
            Source = source, Key = key, Title = string.IsNullOrWhiteSpace(known.Title) ? key : known.Title, Url = known.Url,
        };
    }

    /// <summary>
    ///     Returns the state of the earlier step a source reads.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="context">The context of the <c>workflow</c> step.</param>
    /// <returns>The step's state.</returns>
    protected static Runs.RunStep StateOf(ChildSourceDefinition source, StepContext context)
    {
        return context.Run.Steps.First(s => s.StepId == source.Step);
    }
}

/// <summary>
///     One child on the run's own ticket.
/// </summary>
[ChildSourceRegistration(StepConstants.ChildSourceTicket, "One child run on this run's ticket")]
public class TicketChildSource : AbstractChildSource
{
    /// <inheritdoc />
    public override Task<IReadOnlyList<ChildItem>> ResolveAsync(ChildSourceDefinition source, StepContext context, CancellationToken ct)
    {
        IReadOnlyList<ChildItem> items = [new ChildItem(context.Run.Ticket, context.Run.Ticket.Key)];
        return Task.FromResult(items);
    }
}

/// <summary>
///     One child per ticket in a list, each reference rendered as a template first.
/// </summary>
[ChildSourceRegistration(StepConstants.ChildSourceTickets, "One child run per ticket in a list")]
public class TicketsChildSource : AbstractChildSource
{
    /// <inheritdoc />
    public override ChildSourceFields Fields => ChildSourceFields.Tickets;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ChildSourceDefinition source, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        var problems = base.Validate(source, workflow, step).ToList();
        if (source.Tickets is not { Count: > 0 })
        {
            problems.Add("forEach 'tickets' needs a 'tickets' list.");
        }

        return problems;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ChildItem>> ResolveAsync(ChildSourceDefinition source, StepContext context, CancellationToken ct)
    {
        var keys = (source.Tickets ?? [])
            .SelectMany(t => context.Render(t).Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = new List<ChildItem>();
        foreach (var key in keys)
        {
            items.Add(new ChildItem(await FetchTicketAsync(context, key, (null, null), ct), key));
        }

        return items;
    }
}

/// <summary>
///     One child per ticket an earlier step filed (<see cref="AbstractAgentStepDefinition.CreateTickets" />).
/// </summary>
[ChildSourceRegistration(StepConstants.ChildSourceCreatedTickets, "One child run per ticket an earlier step filed")]
public class CreatedTicketsChildSource : AbstractChildSource
{
    /// <inheritdoc />
    public override ChildSourceFields Fields => ChildSourceFields.Step;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ChildSourceDefinition source, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        var problems = base.Validate(source, workflow, step).ToList();
        if (workflow.Steps.FirstOrDefault(s => s.Id == source.Step) is { } referenced && referenced is not AbstractAgentStepDefinition { CreateTickets: true })
        {
            problems.Add($"forEach 'created-tickets' reads step '{referenced.Id}', which doesn't file tickets (createTickets).");
        }

        return problems;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ChildItem>> ResolveAsync(ChildSourceDefinition source, StepContext context, CancellationToken ct)
    {
        var items = new List<ChildItem>();
        foreach (var created in StateOf(source, context).CreatedTickets.Where(t => t.Key is not null))
        {
            items.Add(new ChildItem(await FetchTicketAsync(context, created.Key!, (created.Title, created.Url), ct), created.Key!));
        }

        return items;
    }
}

/// <summary>
///     One child per non-empty line of an earlier step's output, all on the run's own ticket. Leading list markers
///     (<c>-</c>, <c>*</c>, <c>1.</c>) are dropped from each line; then, if set, only lines matching
///     <see cref="ChildSourceDefinition.Pattern" /> are kept (as its first group), and each is split on
///     <see cref="ChildSourceDefinition.Separator" />.
/// </summary>
[ChildSourceRegistration(StepConstants.ChildSourceLines, "One child run per line of an earlier step's output")]
public class LinesChildSource : AbstractChildSource
{
    /// <inheritdoc />
    public override ChildSourceFields Fields => ChildSourceFields.Step | ChildSourceFields.Pattern;

    /// <inheritdoc />
    public override Task<IReadOnlyList<ChildItem>> ResolveAsync(ChildSourceDefinition source, StepContext context, CancellationToken ct)
    {
        var pattern = source.Pattern is null ? null : new Regex(source.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        var lines = (StateOf(source, context).Output ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripListMarker)
            .Select(l => pattern is null ? l : Pick(pattern.Match(l)))
            .OfType<string>();
        IReadOnlyList<ChildItem> items = (string.IsNullOrEmpty(source.Separator)
                ? lines.Select(l => l.Trim()).Where(l => l.Length > 0)
                : lines.SelectMany(l => SplitItems(l, source.Separator)))
            .Distinct(StringComparer.Ordinal)
            .Select(l => new ChildItem(context.Run.Ticket, l))
            .ToList();
        return Task.FromResult(items);
    }

    /// <summary>
    ///     Returns what a line's match keeps: its first group, or the whole match with no groups.
    /// </summary>
    /// <param name="match">The match.</param>
    /// <returns>The kept text, or null when the line didn't match.</returns>
    private static string? Pick(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }

    /// <summary>
    ///     Drops a leading list marker from a line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The line without it.</returns>
    private static string StripListMarker(string line)
    {
        var trimmed = line.TrimStart('-', '*', '+', ' ', '\t');
        var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && trimmed[..dot].All(char.IsAsciiDigit))
        {
            trimmed = trimmed[(dot + 2)..];
        }

        return trimmed.Trim();
    }
}

/// <summary>
///     One child per item in a template of comma- or newline-separated values (e.g. <c>{{params.tickets}}</c> or
///     <c>standards, specs, style</c>), all on the run's own ticket.
/// </summary>
[ChildSourceRegistration(StepConstants.ChildSourceItems, "One child run per item in a list")]
public class ItemsChildSource : AbstractChildSource
{
    /// <inheritdoc />
    public override ChildSourceFields Fields => ChildSourceFields.Items;

    /// <inheritdoc />
    public override IEnumerable<string> Validate(ChildSourceDefinition source, WorkflowDefinition workflow, AbstractStepDefinition step)
    {
        var problems = base.Validate(source, workflow, step).ToList();
        if (string.IsNullOrWhiteSpace(source.Items))
        {
            problems.Add("forEach 'items' needs 'items': comma- or newline-separated values.");
        }

        return problems;
    }

    /// <inheritdoc />
    public override IReadOnlyList<string>? KnownItems(ChildSourceDefinition source)
    {
        return source.Items is null || PromptTemplate.References(source.Items).Any()
            ? null
            : SplitItems(source.Items.ReplaceLineEndings("\n"), "\n", ",").ToList();
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<ChildItem>> ResolveAsync(ChildSourceDefinition source, StepContext context, CancellationToken ct)
    {
        IReadOnlyList<ChildItem> items = SplitItems(context.Render(source.Items).ReplaceLineEndings("\n"), "\n", ",")
            .Select(i => new ChildItem(context.Run.Ticket, i))
            .ToList();
        return Task.FromResult(items);
    }
}
