using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Afl.Core.Constants;
using Afl.Core.Registration;

namespace Afl.Core.Flow;

/// <summary>
///     Where an arrow stands in the current pass of a run.
/// </summary>
public enum ArrowState
{
    /// <summary>
    ///     Its step hasn't finished yet, so it may still fire.
    /// </summary>
    Pending,

    /// <summary>
    ///     It fired.
    /// </summary>
    Fired,

    /// <summary>
    ///     It won't fire: its step finished without taking it, or will never run.
    /// </summary>
    Dead,
}

/// <summary>
///     What a step's wait rule says about the arrows coming in.
/// </summary>
public enum WaitResult
{
    /// <summary>
    ///     Not yet: some arrows it needs may still fire.
    /// </summary>
    Pending,

    /// <summary>
    ///     The step can start.
    /// </summary>
    Ready,

    /// <summary>
    ///     The step will never start in this pass, so its own arrows die too.
    /// </summary>
    Dead,
}

/// <summary>
///     Registers a <see cref="AbstractWaitRule" />: its key is how it's written in <c>waitFor</c>.
/// </summary>
/// <param name="key">The key (<c>all</c>, <c>any</c>, <c>atLeast</c>, <c>arrow</c>).</param>
/// <param name="description">What the rule waits for, for front-ends.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class WaitRuleRegistrationAttribute(string key, string description) : AbstractRegistrationAttribute(key)
{
    /// <summary>
    ///     Gets what the rule waits for, for front-ends.
    /// </summary>
    public string Description { get; } = description;
}

/// <summary>
///     The kinds of wait rule, and reading them from JSON: at the top of <c>waitFor</c> a string is the shorthand for a
///     group of every arrow in (<c>"all"</c>, <c>"any"</c>); inside a group a string is the arrow from that step; an object
///     is the rule its one registered key names (<c>{ "all": [...] }</c>, <c>{ "atLeast": 2, "of": [...] }</c>).
/// </summary>
public class WaitRuleRegistry() : TypeRegistry<AbstractWaitRule, WaitRuleRegistrationAttribute>("wait rule")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static WaitRuleRegistry Instance { get; } = new();

    /// <summary>
    ///     Gets the rule a step waits by when it doesn't say: every live arrow in.
    /// </summary>
    public static AbstractWaitRule Default { get; } = new AllWaitRule();

    /// <summary>
    ///     Reads a rule.
    /// </summary>
    /// <param name="node">The JSON.</param>
    /// <param name="top">Whether it is the whole of <c>waitFor</c> (where a string is a shorthand, not a step).</param>
    /// <returns>The rule.</returns>
    /// <exception cref="JsonException">When it isn't a wait rule.</exception>
    public AbstractWaitRule Read(JsonNode? node, bool top)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (!top)
                {
                    return new ArrowWaitRule { Step = text };
                }

                if (this.Contains(text) && this.Create(text) is AbstractWaitGroup group)
                {
                    return group;
                }

                throw new JsonException($"waitFor '{text}' must be one of {string.Join(", ", this.GroupKeys)}, or a group.");
            case JsonObject obj:
                var keys = obj.Select(p => p.Key).Where(this.Contains).ToList();
                if (keys.Count != 1)
                {
                    throw new JsonException($"A wait group needs exactly one of {string.Join(", ", this.Keys.Order())}.");
                }

                var rule = this.Create(keys[0]);
                rule.ReadFrom(obj, keys[0]);
                return rule;
            default:
                throw new JsonException("A wait rule must be a step id, a shorthand such as \"all\", or a group such as { \"any\": [...] }.");
        }
    }

    /// <summary>
    ///     Gets the keys of the rules that group others, which are the shorthands <c>waitFor</c> takes.
    /// </summary>
    public IReadOnlyList<string> GroupKeys =>
        this.Keys.Where(k => typeof(AbstractWaitGroup).IsAssignableFrom(this.Create(k).GetType())).Order().ToList();
}

/// <summary>
///     What a step waits for before it starts when arrows from several steps come in (<c>waitFor</c>). Rules see only
///     the arrows' states (<see cref="ArrowState" />) and leave dead arrows out, so paths that went another way don't
///     hold a step up. A new kind of rule is one subclass with <see cref="WaitRuleRegistrationAttribute" />.
/// </summary>
[JsonConverter(typeof(WaitRuleConverter))]
public abstract class AbstractWaitRule
{
    /// <summary>
    ///     Gets the rule's key.
    /// </summary>
    public string Kind => this.GetType().GetCustomAttribute<WaitRuleRegistrationAttribute>()!.Key;

    /// <summary>
    ///     Works out whether the step can start.
    /// </summary>
    /// <param name="incoming">The steps whose arrows come in (forward only), in order.</param>
    /// <param name="stateOf">The state of the arrow (or arrows) from a step.</param>
    /// <returns>Whether it's ready, still waiting or dead.</returns>
    public abstract WaitResult Evaluate(IReadOnlyList<string> incoming, Func<string, ArrowState> stateOf);

    /// <summary>
    ///     Returns the steps whose arrows the rule looks at.
    /// </summary>
    /// <param name="incoming">The steps whose arrows come in.</param>
    /// <returns>The step ids.</returns>
    public abstract IEnumerable<string> Sources(IReadOnlyList<string> incoming);

    /// <summary>
    ///     Returns whether the rule can end up dead, given which incoming arrows can.
    /// </summary>
    /// <param name="incoming">The steps whose arrows come in.</param>
    /// <param name="canDie">Whether the arrow from a step can die.</param>
    /// <returns>True if it can.</returns>
    public abstract bool CanDie(IReadOnlyList<string> incoming, Func<string, bool> canDie);

    /// <summary>
    ///     Returns the steps that have always run once the rule holds: what every way of it holding brings.
    /// </summary>
    /// <param name="incoming">The steps whose arrows come in.</param>
    /// <param name="after">The steps that have always run once the arrow from a step fires, that step included.</param>
    /// <param name="canDie">Whether the arrow from a step can die.</param>
    /// <returns>The step ids.</returns>
    public abstract HashSet<string> Guaranteed(IReadOnlyList<string> incoming, Func<string, IReadOnlySet<string>> after, Func<string, bool> canDie);

    /// <summary>
    ///     Checks the rule against the arrows that come in.
    /// </summary>
    /// <param name="incoming">The steps whose arrows come in.</param>
    /// <returns>The problems; empty when valid.</returns>
    public virtual IEnumerable<string> Validate(IReadOnlyList<string> incoming)
    {
        return [];
    }

    /// <summary>
    ///     Writes the rule as JSON.
    /// </summary>
    /// <param name="top">Whether it is the whole of <c>waitFor</c>, where a group of every arrow in is a shorthand.</param>
    /// <returns>The JSON.</returns>
    public abstract JsonNode ToJson(bool top);

    /// <summary>
    ///     Says in a few words what the rule waits for, such as <c>all(build, test) or lint</c>.
    /// </summary>
    /// <returns>The text.</returns>
    public abstract string Describe();

    /// <summary>
    ///     Returns a copy of the rule.
    /// </summary>
    /// <returns>The copy.</returns>
    public AbstractWaitRule Clone()
    {
        return WaitRuleRegistry.Instance.Read(this.ToJson(true), true);
    }

    /// <summary>
    ///     Reads the rule's settings from its JSON object.
    /// </summary>
    /// <param name="node">The object.</param>
    /// <param name="key">The property that named the rule.</param>
    public abstract void ReadFrom(JsonObject node, string key);

    /// <summary>
    ///     Turns an arrow's state into what a rule over it alone says.
    /// </summary>
    /// <param name="state">The arrow's state.</param>
    /// <returns>The result.</returns>
    protected static WaitResult ResultOf(ArrowState state)
    {
        return state switch
        {
            ArrowState.Fired => WaitResult.Ready,
            ArrowState.Dead => WaitResult.Dead,
            _ => WaitResult.Pending,
        };
    }
}

/// <summary>
///     A rule over several items (steps' arrows, or other groups), or, without items, over every arrow in.
/// </summary>
public abstract class AbstractWaitGroup : AbstractWaitRule
{
    /// <summary>
    ///     Gets or sets the items, or null for every arrow in.
    /// </summary>
    public List<AbstractWaitRule>? Items { get; set; }

    /// <inheritdoc />
    public override WaitResult Evaluate(IReadOnlyList<string> incoming, Func<string, ArrowState> stateOf)
    {
        var results = this.Items is null
            ? incoming.Select(s => ResultOf(stateOf(s))).ToList()
            : this.Items.Select(i => i.Evaluate(incoming, stateOf)).ToList();
        return this.Combine(results);
    }

    /// <inheritdoc />
    public override IEnumerable<string> Sources(IReadOnlyList<string> incoming)
    {
        return this.Items is null ? incoming : this.Items.SelectMany(i => i.Sources(incoming)).Distinct();
    }

    /// <inheritdoc />
    public override bool CanDie(IReadOnlyList<string> incoming, Func<string, bool> canDie)
    {
        return this.ItemsOver(incoming).All(i => i.CanDie(incoming, canDie));
    }

    /// <inheritdoc />
    public override HashSet<string> Guaranteed(IReadOnlyList<string> incoming, Func<string, IReadOnlySet<string>> after, Func<string, bool> canDie)
    {
        return Intersect(this.ItemsOver(incoming).Select(i => i.Guaranteed(incoming, after, canDie)));
    }

    /// <inheritdoc />
    public override IEnumerable<string> Validate(IReadOnlyList<string> incoming)
    {
        if (this.Items is { Count: 0 })
        {
            return [$"a '{this.Kind}' group needs at least one item."];
        }

        return (this.Items ?? []).SelectMany(i => i.Validate(incoming));
    }

    /// <inheritdoc />
    public override JsonNode ToJson(bool top)
    {
        if (this.Items is null && top)
        {
            return JsonValue.Create(this.Kind);
        }

        return new JsonObject { [this.Kind] = this.ItemsJson() };
    }

    /// <inheritdoc />
    public override string Describe()
    {
        return this.Items is null ? this.Kind : $"{this.Kind}({this.DescribeItems()})";
    }

    /// <inheritdoc />
    public override void ReadFrom(JsonObject node, string key)
    {
        this.Items = ReadItems(node[key], key);
    }

    /// <summary>
    ///     Returns the items, with every arrow in as one leaf each when the group has none.
    /// </summary>
    /// <param name="incoming">The steps whose arrows come in.</param>
    /// <returns>The items.</returns>
    protected IReadOnlyList<AbstractWaitRule> ItemsOver(IReadOnlyList<string> incoming)
    {
        return this.Items ?? incoming.Select(s => (AbstractWaitRule)new ArrowWaitRule { Step = s }).ToList();
    }

    /// <summary>
    ///     Returns the steps in every one of some sets.
    /// </summary>
    /// <param name="sets">The sets.</param>
    /// <returns>The steps; empty when there are no sets.</returns>
    protected static HashSet<string> Intersect(IEnumerable<HashSet<string>> sets)
    {
        HashSet<string>? common = null;
        foreach (var set in sets)
        {
            if (common is null)
            {
                common = [..set];
            }
            else
            {
                common.IntersectWith(set);
            }
        }

        return common ?? [];
    }

    /// <summary>
    ///     Combines what the items say.
    /// </summary>
    /// <param name="results">Each item's result.</param>
    /// <returns>The group's result.</returns>
    protected abstract WaitResult Combine(IReadOnlyList<WaitResult> results);

    /// <summary>
    ///     Writes the items, or null for every arrow in.
    /// </summary>
    /// <returns>The JSON array, or null.</returns>
    protected JsonArray? ItemsJson()
    {
        return this.Items is null ? null : new JsonArray(this.Items.Select(i => (JsonNode?)i.ToJson(false)).ToArray());
    }

    /// <summary>
    ///     Describes the items, comma-separated, or "every arrow in".
    /// </summary>
    /// <returns>The text.</returns>
    protected string DescribeItems()
    {
        return this.Items is null ? "every arrow in" : string.Join(", ", this.Items.Select(i => i.Describe()));
    }

    /// <summary>
    ///     Reads a group's items.
    /// </summary>
    /// <param name="node">The JSON array, or null for every arrow in.</param>
    /// <param name="key">The property, for the error.</param>
    /// <returns>The items, or null.</returns>
    /// <exception cref="JsonException">When it isn't an array.</exception>
    protected static List<AbstractWaitRule>? ReadItems(JsonNode? node, string key)
    {
        return node switch
        {
            null => null,
            JsonArray array => array.Select(i => WaitRuleRegistry.Instance.Read(i, false)).ToList(),
            _ => throw new JsonException($"'{key}' in a wait group must be a list of steps and groups."),
        };
    }
}

/// <summary>
///     Waits for every live item: ready once none is pending and at least one is ready; dead when all are dead.
/// </summary>
[WaitRuleRegistration(ArrowConstants.WaitAll, "Every path that's coming")]
public class AllWaitRule : AbstractWaitGroup
{
    /// <inheritdoc />
    public override HashSet<string> Guaranteed(IReadOnlyList<string> incoming, Func<string, IReadOnlySet<string>> after, Func<string, bool> canDie)
    {
        // Items that can't die must all have fired, so everything they bring has run; otherwise only what all bring.
        var sure = this.ItemsOver(incoming).Where(i => !i.CanDie(incoming, canDie)).ToList();
        if (sure.Count == 0)
        {
            return base.Guaranteed(incoming, after, canDie);
        }

        var union = new HashSet<string>();
        foreach (var item in sure)
        {
            union.UnionWith(item.Guaranteed(incoming, after, canDie));
        }

        return union;
    }

    /// <inheritdoc />
    protected override WaitResult Combine(IReadOnlyList<WaitResult> results)
    {
        if (results.Contains(WaitResult.Pending))
        {
            return WaitResult.Pending;
        }

        return results.Contains(WaitResult.Ready) ? WaitResult.Ready : WaitResult.Dead;
    }
}

/// <summary>
///     Waits for one item: ready once one is; dead when all are dead.
/// </summary>
[WaitRuleRegistration(ArrowConstants.WaitAny, "The first path to arrive")]
public class AnyWaitRule : AbstractWaitGroup
{
    /// <inheritdoc />
    protected override WaitResult Combine(IReadOnlyList<WaitResult> results)
    {
        if (results.Contains(WaitResult.Ready))
        {
            return WaitResult.Ready;
        }

        return results.Contains(WaitResult.Pending) ? WaitResult.Pending : WaitResult.Dead;
    }
}

/// <summary>
///     Waits for a number of items: ready once that many are; dead once too few live ones are left to get there.
/// </summary>
[WaitRuleRegistration(ArrowConstants.WaitAtLeast, "A number of the paths")]
public class AtLeastWaitRule : AbstractWaitGroup
{
    /// <summary>
    ///     Gets or sets how many items must be ready.
    /// </summary>
    public int Count { get; set; } = 1;

    /// <inheritdoc />
    public override bool CanDie(IReadOnlyList<string> incoming, Func<string, bool> canDie)
    {
        return this.ItemsOver(incoming).Count(i => !i.CanDie(incoming, canDie)) < this.Count;
    }

    /// <inheritdoc />
    public override IEnumerable<string> Validate(IReadOnlyList<string> incoming)
    {
        var problems = base.Validate(incoming).ToList();
        var count = this.Items?.Count ?? incoming.Count;
        if (this.Count < 1 || this.Count > count)
        {
            problems.Add($"'{ArrowConstants.WaitAtLeast}' must be between 1 and {count}, the number of items it counts.");
        }

        return problems;
    }

    /// <inheritdoc />
    public override JsonNode ToJson(bool top)
    {
        var node = new JsonObject { [this.Kind] = this.Count };
        if (this.Items is not null)
        {
            node[ArrowConstants.WaitOfPropertyName] = this.ItemsJson();
        }

        return node;
    }

    /// <inheritdoc />
    public override string Describe()
    {
        return $"{this.Count} of ({this.DescribeItems()})";
    }

    /// <inheritdoc />
    public override void ReadFrom(JsonObject node, string key)
    {
        this.Count = node[key] is JsonValue value && value.TryGetValue<int>(out var count)
            ? count
            : throw new JsonException($"'{key}' must be a number.");
        this.Items = ReadItems(node[ArrowConstants.WaitOfPropertyName], ArrowConstants.WaitOfPropertyName);
    }

    /// <inheritdoc />
    protected override WaitResult Combine(IReadOnlyList<WaitResult> results)
    {
        var ready = results.Count(r => r == WaitResult.Ready);
        if (ready >= this.Count)
        {
            return WaitResult.Ready;
        }

        return ready + results.Count(r => r == WaitResult.Pending) < this.Count ? WaitResult.Dead : WaitResult.Pending;
    }
}

/// <summary>
///     The arrow (or arrows) from one step: the leaf of a group.
/// </summary>
[WaitRuleRegistration(ArrowConstants.WaitArrow, "The path from one step")]
public class ArrowWaitRule : AbstractWaitRule
{
    /// <summary>
    ///     Gets or sets the step the arrow comes from.
    /// </summary>
    public string Step { get; set; } = string.Empty;

    /// <inheritdoc />
    public override WaitResult Evaluate(IReadOnlyList<string> incoming, Func<string, ArrowState> stateOf)
    {
        return ResultOf(stateOf(this.Step));
    }

    /// <inheritdoc />
    public override IEnumerable<string> Sources(IReadOnlyList<string> incoming)
    {
        return [this.Step];
    }

    /// <inheritdoc />
    public override bool CanDie(IReadOnlyList<string> incoming, Func<string, bool> canDie)
    {
        return canDie(this.Step);
    }

    /// <inheritdoc />
    public override HashSet<string> Guaranteed(IReadOnlyList<string> incoming, Func<string, IReadOnlySet<string>> after, Func<string, bool> canDie)
    {
        return [..after(this.Step)];
    }

    /// <inheritdoc />
    public override IEnumerable<string> Validate(IReadOnlyList<string> incoming)
    {
        return incoming.Contains(this.Step) ? [] : [$"waitFor names '{this.Step}', but no forward arrow comes in from it."];
    }

    /// <inheritdoc />
    public override JsonNode ToJson(bool top)
    {
        return top ? new JsonObject { [this.Kind] = this.Step } : JsonValue.Create(this.Step);
    }

    /// <inheritdoc />
    public override string Describe()
    {
        return this.Step;
    }

    /// <inheritdoc />
    public override void ReadFrom(JsonObject node, string key)
    {
        this.Step = node[key] is JsonValue value && value.TryGetValue<string>(out var step)
            ? step
            : throw new JsonException($"'{key}' must be a step id.");
    }
}

/// <summary>
///     Reads and writes <c>waitFor</c> through the <see cref="WaitRuleRegistry" />.
/// </summary>
public class WaitRuleConverter : JsonConverter<AbstractWaitRule>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(AbstractWaitRule).IsAssignableFrom(typeToConvert);
    }

    /// <inheritdoc />
    public override AbstractWaitRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return WaitRuleRegistry.Instance.Read(JsonNode.Parse(ref reader), true);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AbstractWaitRule value, JsonSerializerOptions options)
    {
        value.ToJson(true).WriteTo(writer, options);
    }
}
