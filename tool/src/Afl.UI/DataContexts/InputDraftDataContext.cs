using Afl.Core.Constants;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An editable copy of one step input in the builder. Every edit raises <see cref="Edited" /> so the workflow is
///     re-validated live.
/// </summary>
public class InputDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="InputDraftDataContext" /> class from an input.
    /// </summary>
    /// <param name="input">The input to copy.</param>
    public InputDraftDataContext(StepInput input)
    {
        this.Id = input.Id;
        this.Label = input.Label;
        this.Type = input.Type;
        this.When = input.Timing;
        this.Help = input.Help;
        this.Options = input.Options is { Count: > 0 } options ? string.Join(", ", options) : null;
        this.Required = input.Required;
        this.Default = input.Default;
        this.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(this.UsesOptions))
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
    ///     Gets the input types that can be chosen: every registered input type.
    /// </summary>
    public static IReadOnlyList<string> Types { get; } = StepInputTypeRegistry.Instance.Keys.Order().ToList();

    /// <summary>
    ///     Gets when an input can be asked: before the step's work, after it (when it's let through), or when the AI
    ///     asks for it.
    /// </summary>
    public static IReadOnlyList<string> Timings { get; } =
        [StepConstants.WhenBefore, StepConstants.WhenAfter, StepConstants.WhenAi];

    /// <summary>
    ///     Gets or sets when the input is asked: <c>before</c>, <c>after</c> or <c>ai</c>.
    /// </summary>
    public string When
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the input id, used in <c>{{steps.&lt;step&gt;.inputs.&lt;id&gt;}}</c> and <c>--input id=value</c>.
    /// </summary>
    public string Id
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the question shown to the user.
    /// </summary>
    public string Label
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the input type, e.g. <c>choice</c>.
    /// </summary>
    public string Type
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.UsesOptions));
        }
    }

    /// <summary>
    ///     Gets or sets extra help shown under the question.
    /// </summary>
    public string? Help
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the options to pick from, comma-separated.
    /// </summary>
    public string? Options
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether an answer is needed.
    /// </summary>
    public bool Required
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the answer used when none is given.
    /// </summary>
    public string? Default
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the type picks from options, so the options field applies.
    /// </summary>
    public bool UsesOptions => StepInputTypeRegistry.Instance.Contains(this.Type) &&
                               StepInputTypeRegistry.Instance.Create(this.Type).UsesOptions;

    /// <summary>
    ///     Creates a new input with a free id.
    /// </summary>
    /// <param name="number">A number for the id and label.</param>
    /// <param name="when">When it's asked; null for the default (<c>after</c>).</param>
    /// <returns>The input.</returns>
    public static StepInput NewInput(int number, string? when = null)
    {
        return new StepInput { Id = $"input-{number}", Label = $"Question {number}", Type = StepConstants.InputText, When = when };
    }

    /// <summary>
    ///     Builds the input definition the draft describes. Options are left out for types that don't use them, and
    ///     <c>when</c> is left out when it's the default.
    /// </summary>
    /// <returns>The input.</returns>
    public StepInput ToDefinition()
    {
        var options = (this.Options ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return new StepInput
        {
            Id = this.Id.Trim(),
            Label = this.Label.Trim(),
            Type = this.Type,
            When = this.When == StepConstants.WhenAfter ? null : this.When,
            Help = string.IsNullOrWhiteSpace(this.Help) ? null : this.Help.Trim(),
            Options = this.UsesOptions && options.Count > 0 ? options : null,
            Required = this.Required,
            Default = string.IsNullOrWhiteSpace(this.Default) ? null : this.Default.Trim(),
        };
    }
}
