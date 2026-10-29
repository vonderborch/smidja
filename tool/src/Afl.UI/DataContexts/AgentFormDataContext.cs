using System.Collections.ObjectModel;
using System.Text.Json;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Execution;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     One setting in the agent form. Its <see cref="Editor" /> picks the control (see
///     <c>Views/AgentFieldEditors.axaml</c>); single-value editors edit <see cref="Text" />, multi-select ones tick
///     <see cref="Items" />, and either way <see cref="Apply" /> writes the value back through the field.
/// </summary>
public class AgentFieldDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AgentFieldDataContext" /> class.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="agent">The agent whose value it starts with.</param>
    /// <param name="usesItems">Whether the value is the ticked <see cref="Items" /> rather than <see cref="Text" />.</param>
    /// <param name="isReadOnly">Whether the field can't be changed.</param>
    public AgentFieldDataContext(AbstractAgentField field, AgentDefinition agent, bool usesItems, bool isReadOnly)
    {
        this.Field = field;
        this.UsesItems = usesItems;
        this.IsReadOnly = isReadOnly;
        this.Text = field.GetText(agent);
    }

    /// <summary>
    ///     Gets the field.
    /// </summary>
    public AbstractAgentField Field { get; }

    /// <summary>
    ///     Gets the field's JSON name.
    /// </summary>
    public string Name => this.Field.Name;

    /// <summary>
    ///     Gets the label, marked when the field is required.
    /// </summary>
    public string Label => this.Field.Required ? $"{this.Field.Label} *" : this.Field.Label;

    /// <summary>
    ///     Gets the help line.
    /// </summary>
    public string Help => this.Field.Help;

    /// <summary>
    ///     Gets the editor name, which picks the control.
    /// </summary>
    public string Editor => this.Field.Editor;

    /// <summary>
    ///     Gets a value indicating whether the value is the ticked <see cref="Items" />.
    /// </summary>
    public bool UsesItems { get; }

    /// <summary>
    ///     Gets a value indicating whether the field can't be changed (a built-in agent's).
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    ///     Gets a value indicating whether the field can be changed.
    /// </summary>
    public bool IsEditable => !this.IsReadOnly;

    /// <summary>
    ///     Gets or sets the value as text (lines for lists, NAME=value lines for maps, JSON for objects).
    /// </summary>
    public string Text
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value ?? string.Empty);
    } = string.Empty;

    /// <summary>
    ///     Gets the values a single-value editor offers (a choice's options, the servers, a server's tools).
    /// </summary>
    public ObservableCollection<string> Choices { get; } = [];

    /// <summary>
    ///     Gets the values a multi-select editor offers, ticked when the agent has them.
    /// </summary>
    public ObservableCollection<SelectableItem<string>> Items { get; } = [];

    /// <summary>
    ///     Replaces the values on offer, keeping what's set (a value that isn't on offer stays, ticked, so nothing is lost).
    /// </summary>
    /// <param name="choices">The values and their labels.</param>
    public void SetChoices(IEnumerable<(string Value, string Label)> choices)
    {
        var offered = choices.ToList();
        var current = this.UsesItems ? this.SelectedValues() : [this.Text];
        this.Choices.Clear();
        this.Items.Clear();
        foreach (var (value, label) in offered)
        {
            this.Choices.Add(value);
            this.Items.Add(new SelectableItem<string>(value, label, current.Contains(value)));
        }

        foreach (var value in current.Where(v => v.Length > 0 && offered.All(o => o.Value != v)))
        {
            this.Choices.Add(value);
            this.Items.Add(new SelectableItem<string>(value, value, true));
        }

        if (!this.UsesItems)
        {
            // The combo box clears the text when its items change under it.
            var text = this.Text;
            this.Text = string.Empty;
            this.Text = text;
        }
    }

    /// <summary>
    ///     Writes the value into an agent.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <exception cref="FormatException">The text isn't a valid value for the field.</exception>
    public void Apply(AgentDefinition agent)
    {
        this.Field.SetText(agent, this.UsesItems ? string.Join('\n', this.SelectedValues()) : this.Text);
    }

    /// <summary>
    ///     Returns the values that are set: the ticked items, or the text's lines before any items exist.
    /// </summary>
    /// <returns>The values.</returns>
    private List<string> SelectedValues()
    {
        return this.Items.Count > 0
            ? this.Items.Where(i => i.IsSelected).Select(i => i.Value).ToList()
            : this.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
}

/// <summary>
///     The form for one agent, built from its kind's <see cref="AbstractStepExecutor.Fields" /> so a new kind needs no
///     XAML. It can switch to the raw JSON (<see cref="IsJson" />); changing <see cref="Kind" /> rebuilds the fields,
///     keeping the values the new kind also has.
/// </summary>
public class AgentFormDataContext : ReactiveObject
{
    /// <summary>
    ///     The editors whose value is a set of ticked items.
    /// </summary>
    public static readonly IReadOnlySet<string> MultiSelectEditors =
        new HashSet<string>(StringComparer.Ordinal) { AgentFieldEditors.Capabilities, AgentFieldEditors.Servers };

    /// <summary>
    ///     What each editor offers, by editor name; editors not listed offer nothing.
    /// </summary>
    private readonly IReadOnlyDictionary<string, Func<AgentFieldDataContext, IEnumerable<(string Value, string Label)>>> _choiceSources;

    /// <summary>
    ///     The agent as last built or loaded, holding everything the fields don't show.
    /// </summary>
    private AgentDefinition _draft;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AgentFormDataContext" /> class.
    /// </summary>
    /// <param name="agent">The agent to edit.</param>
    /// <param name="capabilities">The capabilities on offer, with their descriptions.</param>
    /// <param name="servers">The registered MCP server ids.</param>
    /// <param name="tools">The known tools of each server, filled as servers are tested.</param>
    public AgentFormDataContext(
        AgentDefinition agent,
        IReadOnlyList<CapabilityDefinition> capabilities,
        IReadOnlyList<string> servers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tools)
    {
        this._draft = agent.Clone();
        this.Tools = tools;
        this._choiceSources = new Dictionary<string, Func<AgentFieldDataContext, IEnumerable<(string, string)>>>
        {
            [AgentFieldEditors.Choice] = f => new[] { string.Empty }.Concat(f.Field.Options).Select(o => (o, o)),
            [AgentFieldEditors.Server] = _ => servers.Select(s => (s, s)),
            [AgentFieldEditors.Servers] = _ => servers.Select(s => (s, s)),
            [AgentFieldEditors.Capabilities] = _ => capabilities.Select(c => (c.Id, $"{c.Id} — {c.Description}")),
            [AgentFieldEditors.Tool] = f => this.ToolsFor(f).Select(t => (t, t)),
        };
        this.KindChoices = ExecutorRegistry.Instance.Keys.Order().ToList();
        this.Kind = agent.Kind;
    }

    /// <summary>
    ///     Gets the agent kinds.
    /// </summary>
    public IReadOnlyList<string> KindChoices { get; }

    /// <summary>
    ///     Gets a value indicating whether the agent is built in and can't be changed.
    /// </summary>
    public bool IsReadOnly => this._draft.BuiltIn;

    /// <summary>
    ///     Gets a value indicating whether the agent can be changed.
    /// </summary>
    public bool IsEditable => !this.IsReadOnly;

    /// <summary>
    ///     Gets or sets the agent kind; changing it rebuilds the fields.
    /// </summary>
    public string Kind
    {
        get;
        set
        {
            if (value is null || value == field)
            {
                return;
            }

            if (field.Length > 0)
            {
                this.TryApplyFields();
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this._draft.Kind = value;
            this.BuildFields();
        }
    } = string.Empty;

    /// <summary>
    ///     Gets the fields of the current kind.
    /// </summary>
    public ObservableCollection<AgentFieldDataContext> Fields { get; } = [];

    /// <summary>
    ///     Gets a value indicating whether any field picks a server's tool, so the form offers to list the tools.
    /// </summary>
    public bool HasToolFields => this.Fields.Any(f => f.Editor == AgentFieldEditors.Tool);

    /// <summary>
    ///     Gets a value indicating whether the raw JSON is shown instead of the fields.
    /// </summary>
    public bool IsJson
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsForm));
        }
    }

    /// <summary>
    ///     Gets a value indicating whether the fields are shown.
    /// </summary>
    public bool IsForm => !this.IsJson;

    /// <summary>
    ///     Gets or sets the raw JSON, while <see cref="IsJson" /> is on.
    /// </summary>
    public string Json
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets the known tools of each server.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Tools { get; private set; }

    /// <summary>
    ///     Gets the server the tool fields pick from, if one is set.
    /// </summary>
    public string? ToolServer => this.Fields.FirstOrDefault(f => f.Editor == AgentFieldEditors.Server)?.Text is { Length: > 0 } server
        ? server
        : null;

    /// <summary>
    ///     Switches between the fields and the raw JSON, carrying the values across.
    /// </summary>
    /// <param name="json">True to show the JSON.</param>
    /// <exception cref="FormatException">A field's text isn't valid, so the JSON can't be built.</exception>
    /// <exception cref="InvalidOperationException">The JSON doesn't parse, so it stays open.</exception>
    public void ShowJson(bool json)
    {
        if (json == this.IsJson)
        {
            return;
        }

        if (json)
        {
            this.Json = JsonSerializer.Serialize(this.ToAgent(), JsonConstants.Options);
        }
        else
        {
            this.Load(this.ParseJson());
        }

        this.IsJson = json;
    }

    /// <summary>
    ///     Builds the agent from the form (or the JSON).
    /// </summary>
    /// <returns>A new agent.</returns>
    /// <exception cref="FormatException">A field's text isn't valid.</exception>
    /// <exception cref="InvalidOperationException">The JSON isn't valid.</exception>
    public AgentDefinition ToAgent()
    {
        if (this.IsJson)
        {
            return this.ParseJson();
        }

        var agent = this._draft.Clone();
        agent.Kind = this.Kind;
        foreach (var field in this.Fields)
        {
            field.Apply(agent);
        }

        return agent;
    }

    /// <summary>
    ///     Offers new tool lists (after a server was tested), keeping what's typed.
    /// </summary>
    /// <param name="tools">The known tools of each server.</param>
    public void SetTools(IReadOnlyDictionary<string, IReadOnlyList<string>> tools)
    {
        this.Tools = tools;
        foreach (var field in this.Fields.Where(f => f.Editor == AgentFieldEditors.Tool))
        {
            field.SetChoices(this._choiceSources[AgentFieldEditors.Tool](field));
        }
    }

    /// <summary>
    ///     Writes the field values into the draft when they're valid, so they survive a kind change.
    /// </summary>
    private void TryApplyFields()
    {
        try
        {
            this._draft = this.ToAgent();
        }
        catch (FormatException)
        {
            // An unfinished value is simply dropped when the kind changes.
        }
    }

    /// <summary>
    ///     Loads another agent into the form.
    /// </summary>
    /// <param name="agent">The agent.</param>
    private void Load(AgentDefinition agent)
    {
        this._draft = agent;
        if (agent.Kind != this.Kind && ExecutorRegistry.Instance.Contains(agent.Kind))
        {
            this.Kind = agent.Kind;
        }
        else
        {
            this.BuildFields();
        }
    }

    /// <summary>
    ///     Builds the fields of the current kind from the draft.
    /// </summary>
    private void BuildFields()
    {
        this.Fields.Clear();
        if (ExecutorRegistry.Instance.Contains(this.Kind))
        {
            foreach (var field in AgentStore.FieldsOf(this.Kind))
            {
                var view = new AgentFieldDataContext(field, this._draft, MultiSelectEditors.Contains(field.Editor), this.IsReadOnly);
                this.Fields.Add(view);
            }

            foreach (var field in this.Fields.Where(f => this._choiceSources.ContainsKey(f.Editor)))
            {
                field.SetChoices(this._choiceSources[field.Editor](field));
            }
        }

        this.RaisePropertyChanged(nameof(this.HasToolFields));
    }

    /// <summary>
    ///     Returns the known tools of the server a tool field depends on.
    /// </summary>
    /// <param name="field">The tool field.</param>
    /// <returns>The tool names.</returns>
    private IReadOnlyList<string> ToolsFor(AgentFieldDataContext field)
    {
        var server = this.Fields.FirstOrDefault(f => f.Name == field.Field.DependsOn)?.Text ?? this._draft.Server;
        return server is not null && this.Tools.TryGetValue(server, out var tools) ? tools : [];
    }

    /// <summary>
    ///     Reads the JSON.
    /// </summary>
    /// <returns>The agent.</returns>
    /// <exception cref="InvalidOperationException">The JSON isn't valid.</exception>
    private AgentDefinition ParseJson()
    {
        AgentDefinition? agent;
        try
        {
            agent = JsonSerializer.Deserialize<AgentDefinition>(this.Json, JsonConstants.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The agent JSON isn't valid: {ex.Message}", ex);
        }

        if (agent is null)
        {
            throw new InvalidOperationException("Enter the agent's JSON definition.");
        }

        agent.BuiltIn = this._draft.BuiltIn && agent.Id == this._draft.Id;
        return agent;
    }
}
