using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Afl.Core.Constants;

namespace Afl.Core.Agents;

/// <summary>
///     One setting of an <see cref="AgentDefinition" /> that an agent kind reads, as the agent form shows it. Executors
///     declare their fields (<see cref="Execution.AbstractStepExecutor.Fields" />), so the form for a new kind needs no
///     front-end change. Values move to and from the form as text, so every front-end edits them the same way.
/// </summary>
/// <param name="name">The JSON name of the <see cref="AgentDefinition" /> property.</param>
/// <param name="label">The label.</param>
/// <param name="editor">The control that edits it: one of the <see cref="AgentFieldEditors" />.</param>
/// <param name="help">What it does.</param>
public abstract class AbstractAgentField(string name, string label, string editor, string help)
{
    /// <summary>
    ///     The <see cref="AgentDefinition" /> properties by JSON name.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, JsonPropertyInfo>> PropertyMap = new(() =>
    {
        // Metadata is only there once the options are frozen, which the first (de)serialisation would otherwise do.
        if (!JsonConstants.Options.IsReadOnly)
        {
            JsonConstants.Options.MakeReadOnly(true);
        }

        return JsonConstants.Options.GetTypeInfo(typeof(AgentDefinition)).Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
    });

    /// <summary>
    ///     Gets the JSON name of the property.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets the label.
    /// </summary>
    public string Label { get; } = label;

    /// <summary>
    ///     Gets the control that edits it.
    /// </summary>
    public string Editor { get; } = editor;

    /// <summary>
    ///     Gets what it does.
    /// </summary>
    public string Help { get; } = help;

    /// <summary>
    ///     Gets or sets whether the agent needs a value.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    ///     Gets or sets the values a <see cref="AgentFieldEditors.Choice" /> field can take.
    /// </summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>
    ///     Gets or sets the field whose value scopes this one's choices (a tool picker's server field).
    /// </summary>
    public string? DependsOn { get; init; }

    /// <summary>
    ///     Gets the property this field reads and writes.
    /// </summary>
    protected JsonPropertyInfo Property => PropertyMap.Value.TryGetValue(this.Name, out var property)
        ? property
        : throw new InvalidOperationException($"AgentDefinition has no '{this.Name}' property.");

    /// <summary>
    ///     Returns the field's value as the form shows it.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The text; empty when unset.</returns>
    public abstract string GetText(AgentDefinition agent);

    /// <summary>
    ///     Sets the field from the form's text; blank text clears it.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="text">The text.</param>
    /// <exception cref="FormatException">The text can't be read as this field's value.</exception>
    public abstract void SetText(AgentDefinition agent, string? text);

    /// <summary>
    ///     Returns whether the field has a value.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>True if set.</returns>
    public bool HasValue(AgentDefinition agent)
    {
        return this.GetText(agent).Length > 0;
    }
}

/// <summary>
///     Base for fields over an <see cref="AgentDefinition" /> property of type <typeparamref name="TValue" />: it reads and
///     writes the property through its JSON metadata (so fields are named exactly as in <c>agents.json</c>), and
///     subclasses only convert the value to and from text.
/// </summary>
/// <typeparam name="TValue">The property's type.</typeparam>
/// <param name="name">The JSON name of the property.</param>
/// <param name="label">The label.</param>
/// <param name="editor">The control that edits it.</param>
/// <param name="help">What it does.</param>
public abstract class AbstractAgentField<TValue>(string name, string label, string editor, string help)
    : AbstractAgentField(name, label, editor, help)
    where TValue : class
{
    /// <summary>
    ///     Returns the property's value.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The value, or null.</returns>
    public TValue? GetValue(AgentDefinition agent)
    {
        return this.Property.Get!(agent) as TValue;
    }

    /// <summary>
    ///     Sets the property's value.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="value">The value, or null to clear it.</param>
    public void SetValue(AgentDefinition agent, TValue? value)
    {
        this.Property.Set!(agent, value);
    }

    /// <inheritdoc />
    public override string GetText(AgentDefinition agent)
    {
        return this.GetValue(agent) is { } value ? this.Format(value) : string.Empty;
    }

    /// <inheritdoc />
    public override void SetText(AgentDefinition agent, string? text)
    {
        this.SetValue(agent, string.IsNullOrWhiteSpace(text) ? null : this.Parse(text.Trim()));
    }

    /// <summary>
    ///     Converts a value to text.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    protected abstract string Format(TValue value);

    /// <summary>
    ///     Reads a value from non-blank text.
    /// </summary>
    /// <param name="text">The trimmed text.</param>
    /// <returns>The value, or null when it's empty.</returns>
    protected abstract TValue? Parse(string text);

    /// <summary>
    ///     Splits text into its non-blank lines.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The trimmed lines.</returns>
    protected static IEnumerable<string> Lines(string text)
    {
        return text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
    }
}

/// <summary>
///     A single text value: plain text, a choice, an MCP server or an MCP tool.
/// </summary>
/// <param name="name">The JSON name of the property.</param>
/// <param name="label">The label.</param>
/// <param name="help">What it does.</param>
/// <param name="editor">The control that edits it.</param>
public class TextAgentField(string name, string label, string help, string editor = AgentFieldEditors.Text)
    : AbstractAgentField<string>(name, label, editor, help)
{
    /// <inheritdoc />
    protected override string Format(string value)
    {
        return value;
    }

    /// <inheritdoc />
    protected override string Parse(string text)
    {
        return text;
    }
}

/// <summary>
///     A list of values, one per line: arguments or capabilities.
/// </summary>
/// <param name="name">The JSON name of the property.</param>
/// <param name="label">The label.</param>
/// <param name="help">What it does.</param>
/// <param name="editor">The control that edits it.</param>
public class ListAgentField(string name, string label, string help, string editor = AgentFieldEditors.List)
    : AbstractAgentField<List<string>>(name, label, editor, help)
{
    /// <inheritdoc />
    protected override string Format(List<string> value)
    {
        return string.Join('\n', value);
    }

    /// <inheritdoc />
    protected override List<string>? Parse(string text)
    {
        var lines = Lines(text).ToList();
        return lines.Count > 0 ? lines : null;
    }
}

/// <summary>
///     Names and values, one <c>NAME=value</c> per line (environment variables).
/// </summary>
/// <param name="name">The JSON name of the property.</param>
/// <param name="label">The label.</param>
/// <param name="help">What it does.</param>
public class MapAgentField(string name, string label, string help)
    : AbstractAgentField<Dictionary<string, string>>(name, label, AgentFieldEditors.Map, help)
{
    /// <inheritdoc />
    protected override string Format(Dictionary<string, string> value)
    {
        return string.Join('\n', value.Select(entry => $"{entry.Key}={entry.Value}"));
    }

    /// <inheritdoc />
    protected override Dictionary<string, string>? Parse(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in Lines(text))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                throw new FormatException($"{this.Label}: '{line}' should be NAME=value.");
            }

            map[line[..equals].Trim()] = line[(equals + 1)..];
        }

        return map.Count > 0 ? map : null;
    }
}

/// <summary>
///     A JSON object (an MCP tool's arguments).
/// </summary>
/// <param name="name">The JSON name of the property.</param>
/// <param name="label">The label.</param>
/// <param name="help">What it does.</param>
public class JsonAgentField(string name, string label, string help)
    : AbstractAgentField<Dictionary<string, JsonNode?>>(name, label, AgentFieldEditors.Json, help)
{
    /// <inheritdoc />
    protected override string Format(Dictionary<string, JsonNode?> value)
    {
        return JsonSerializer.Serialize(value, JsonConstants.Options);
    }

    /// <inheritdoc />
    protected override Dictionary<string, JsonNode?>? Parse(string text)
    {
        try
        {
            var value = JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(text, JsonConstants.Options);
            return value is { Count: > 0 } ? value : null;
        }
        catch (JsonException e)
        {
            throw new FormatException($"{this.Label} must be a JSON object: {e.Message}", e);
        }
    }
}

/// <summary>
///     The controls agent fields are edited with (<see cref="AbstractAgentField.Editor" />).
/// </summary>
public static class AgentFieldEditors
{
    /// <summary>
    ///     A text box.
    /// </summary>
    public const string Text = "text";

    /// <summary>
    ///     A drop-down of <see cref="AbstractAgentField.Options" />.
    /// </summary>
    public const string Choice = "choice";

    /// <summary>
    ///     A multi-line box, one value per line.
    /// </summary>
    public const string List = "list";

    /// <summary>
    ///     A multi-line box, one <c>NAME=value</c> per line.
    /// </summary>
    public const string Map = "map";

    /// <summary>
    ///     A JSON editor.
    /// </summary>
    public const string Json = "json";

    /// <summary>
    ///     A drop-down of the registered MCP servers.
    /// </summary>
    public const string Server = "server";

    /// <summary>
    ///     A drop-down of the tools on the server named by <see cref="AbstractAgentField.DependsOn" />.
    /// </summary>
    public const string Tool = "tool";

    /// <summary>
    ///     Check boxes over the capability catalogue.
    /// </summary>
    public const string Capabilities = "capabilities";

    /// <summary>
    ///     Check boxes over the registered MCP servers.
    /// </summary>
    public const string Servers = "servers";
}
