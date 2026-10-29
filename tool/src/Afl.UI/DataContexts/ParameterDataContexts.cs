using System.Collections.ObjectModel;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An editable copy of one parameter a workflow or common step declares. Every edit raises <see cref="Edited" />.
/// </summary>
public class ParameterDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ParameterDraftDataContext" /> class from a parameter.
    /// </summary>
    /// <param name="parameter">The parameter to copy.</param>
    public ParameterDraftDataContext(ParameterDefinition parameter)
    {
        this.Id = parameter.Id;
        this.Label = parameter.Label;
        this.Description = parameter.Description;
        this.Required = parameter.IsRequired;
        this.Default = parameter.Default;
        this.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(this.HasDefault))
            {
                this.Edited?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>
    ///     Raised when any field changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets or sets the id, written <c>{{params.&lt;id&gt;}}</c>.
    /// </summary>
    public string Id
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the label shown to people filling it in.
    /// </summary>
    public string? Label
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets what the parameter is for.
    /// </summary>
    public string? Description
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether a value must be given (the parameter has no default).
    /// </summary>
    public bool Required
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.HasDefault));
        }
    }

    /// <summary>
    ///     Gets whether the parameter has a default, so the default can be edited.
    /// </summary>
    public bool HasDefault => !this.Required;

    /// <summary>
    ///     Gets or sets the default value.
    /// </summary>
    public string? Default
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Creates a parameter with a free id.
    /// </summary>
    /// <param name="taken">The ids already used.</param>
    /// <returns>The parameter.</returns>
    public static ParameterDefinition NewParameter(IEnumerable<string> taken)
    {
        var ids = taken.ToHashSet();
        var number = 1;
        while (ids.Contains($"param-{number}"))
        {
            number++;
        }

        return new ParameterDefinition { Id = $"param-{number}", Default = string.Empty };
    }

    /// <summary>
    ///     Builds the parameter the draft describes.
    /// </summary>
    /// <returns>The parameter.</returns>
    public ParameterDefinition ToDefinition()
    {
        return new ParameterDefinition
        {
            Id = this.Id.Trim(),
            Label = string.IsNullOrWhiteSpace(this.Label) ? null : this.Label.Trim(),
            Description = string.IsNullOrWhiteSpace(this.Description) ? null : this.Description.Trim(),
            Default = this.Required ? null : this.Default ?? string.Empty,
        };
    }
}

/// <summary>
///     One value given for a parameter of a common step, workflow or child workflow.
/// </summary>
public class ParameterValueDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ParameterValueDataContext" /> class.
    /// </summary>
    /// <param name="id">The parameter id.</param>
    /// <param name="parameter">The parameter it fills, or null when the target doesn't declare it.</param>
    /// <param name="value">The current value.</param>
    public ParameterValueDataContext(string id, ParameterDefinition? parameter, string? value)
    {
        this.Id = id;
        this.Parameter = parameter;
        this.Value = value;
    }

    /// <summary>
    ///     Gets the parameter id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    ///     Gets the parameter it fills, or null when the target doesn't declare it.
    /// </summary>
    public ParameterDefinition? Parameter { get; }

    /// <summary>
    ///     Gets the label: the parameter's label, else its id, marked when a value must be given.
    /// </summary>
    public string Label => (this.Parameter?.Label ?? this.Id) + (this.Parameter is { IsRequired: true } ? " *" : string.Empty);

    /// <summary>
    ///     Gets the help shown under the field.
    /// </summary>
    public string? Help => this.Parameter is null ? "Not one of its parameters; clear it." : this.Parameter.Description;

    /// <summary>
    ///     Gets the watermark: the default an empty field leaves in place.
    /// </summary>
    public string Placeholder => this.Parameter switch
    {
        null => string.Empty,
        { IsRequired: true } => "(required)",
        { Default: "" } => "(empty)",
        { Default: var d } => d!,
    };

    /// <summary>
    ///     Gets or sets the value; empty leaves the default.
    /// </summary>
    public string? Value
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>
///     The values given for a target's parameters (a step's or requirement's <c>with</c>, or a run's parameters): one
///     field per parameter the target declares, plus any given value the target doesn't declare so it can be cleared.
/// </summary>
public class WithValuesDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="WithValuesDataContext" /> class.
    /// </summary>
    /// <param name="values">The values given so far.</param>
    public WithValuesDataContext(IReadOnlyDictionary<string, string>? values = null)
    {
        this.Rebuild(null, values);
    }

    /// <summary>
    ///     Raised when a value changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets the fields.
    /// </summary>
    public ObservableCollection<ParameterValueDataContext> Values { get; } = [];

    /// <summary>
    ///     Gets whether there is any field to show.
    /// </summary>
    public bool HasValues => this.Values.Count > 0;

    /// <summary>
    ///     Rebuilds the fields for a target's parameters, keeping the values given so far.
    /// </summary>
    /// <param name="parameters">The parameters the target declares.</param>
    /// <param name="values">The values to keep; the current ones when null.</param>
    public void Rebuild(IReadOnlyList<ParameterDefinition>? parameters, IReadOnlyDictionary<string, string>? values = null)
    {
        var kept = values ?? this.ToDictionary() ?? new Dictionary<string, string>();
        foreach (var field in this.Values)
        {
            field.PropertyChanged -= this.OnValueChanged;
        }

        this.Values.Clear();
        var declared = parameters ?? [];
        foreach (var parameter in declared)
        {
            this.Add(new ParameterValueDataContext(parameter.Id, parameter, kept.GetValueOrDefault(parameter.Id)));
        }

        foreach (var (id, value) in kept.Where(p => declared.All(d => d.Id != p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            this.Add(new ParameterValueDataContext(id, null, value));
        }

        this.RaisePropertyChanged(nameof(this.HasValues));
    }

    /// <summary>
    ///     Returns the values given, leaving out empty ones (which keep the defaults).
    /// </summary>
    /// <returns>The values, or null when none is given.</returns>
    public Dictionary<string, string>? ToDictionary()
    {
        var values = this.Values
            .Where(v => !string.IsNullOrEmpty(v.Value))
            .ToDictionary(v => v.Id, v => v.Value!);
        return values.Count > 0 ? values : null;
    }

    /// <summary>
    ///     Adds a field and watches it.
    /// </summary>
    /// <param name="field">The field.</param>
    private void Add(ParameterValueDataContext field)
    {
        field.PropertyChanged += this.OnValueChanged;
        this.Values.Add(field);
    }

    /// <summary>
    ///     Passes a field's edit on.
    /// </summary>
    /// <param name="sender">The field.</param>
    /// <param name="e">The event.</param>
    private void OnValueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        this.Edited?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
///     An editable copy of one workflow a workflow requires to have finished first.
/// </summary>
public class RequirementDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     The workflows that can be picked, with their parameters.
    /// </summary>
    private IReadOnlyList<WorkflowDefinition> _workflows = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="RequirementDraftDataContext" /> class from a requirement.
    /// </summary>
    /// <param name="requirement">The requirement to copy.</param>
    public RequirementDraftDataContext(WorkflowRequirement requirement)
    {
        this.Workflow = requirement.Workflow;
        this.WorkflowChoices = [requirement.Workflow];
        this.With = new WithValuesDataContext(requirement.With);
        this.With.Edited += (_, _) => this.Edited?.Invoke(this, EventArgs.Empty);
        this.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(this.Workflow))
            {
                this.With.Rebuild(this.ParametersOf(this.Workflow));
                this.Edited?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>
    ///     Raised when any field changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets or sets the id of the workflow required.
    /// </summary>
    public string Workflow
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value ?? string.Empty);
    }

    /// <summary>
    ///     Gets the workflows that can be picked.
    /// </summary>
    public IReadOnlyList<string> WorkflowChoices
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the values for the workflow's parameters.
    /// </summary>
    public WithValuesDataContext With { get; }

    /// <summary>
    ///     Sets the workflows that can be picked, keeping the one picked.
    /// </summary>
    /// <param name="workflows">The workflows.</param>
    /// <param name="excluded">The workflows it can't require, as they'd make a cycle; one already picked stays listed.</param>
    public void SetWorkflows(IReadOnlyList<WorkflowDefinition> workflows, IReadOnlySet<string> excluded)
    {
        this._workflows = workflows;
        var workflow = this.Workflow;
        this.WorkflowChoices = [.. workflows.Select(w => w.Id).Where(id => !excluded.Contains(id)).Append(workflow).Where(id => id.Length > 0).Distinct().Order()];
        this.Workflow = workflow;
        this.With.Rebuild(this.ParametersOf(workflow));
    }

    /// <summary>
    ///     Builds the requirement the draft describes.
    /// </summary>
    /// <returns>The requirement.</returns>
    public WorkflowRequirement ToDefinition()
    {
        return new WorkflowRequirement { Workflow = this.Workflow.Trim(), With = this.With.ToDictionary() };
    }

    /// <summary>
    ///     Returns a workflow's parameters.
    /// </summary>
    /// <param name="id">The workflow id.</param>
    /// <returns>Its parameters, or null when unknown.</returns>
    private IReadOnlyList<ParameterDefinition>? ParametersOf(string id)
    {
        return this._workflows.FirstOrDefault(w => w.Id == id)?.Parameters;
    }
}
