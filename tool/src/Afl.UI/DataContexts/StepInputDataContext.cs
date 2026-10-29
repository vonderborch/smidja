using System.Collections.ObjectModel;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     One of the current step's inputs on the Runs page: the control its type asks with, the value entered so far,
///     and what's wrong with it. The control comes from <see cref="AbstractStepInputType.Editor" />, so a new input
///     type needs no change here.
/// </summary>
public class StepInputDataContext : ReactiveObject
{
    /// <summary>
    ///     The input's type.
    /// </summary>
    private readonly AbstractStepInputType _type;

    /// <summary>
    ///     Initializes a new instance of the <see cref="StepInputDataContext" /> class, filled with the input's default.
    /// </summary>
    /// <param name="input">The input.</param>
    public StepInputDataContext(StepInput input)
    {
        this.Input = input;
        this._type = StepInputTypeRegistry.Instance.For(input);
        var fallback = string.IsNullOrWhiteSpace(input.Default) ? null : this.TryNormalize(input.Default);
        var picked = MultiChoiceInputType.Split(fallback);
        this.Options = input.Options ?? [];
        this.Choices = new ObservableCollection<SelectableItem<string>>(
            this.Options.Select(o => new SelectableItem<string>(o, o, picked.Contains(o))));
        this.Text = this.Editor is StepInputEditor.Text or StepInputEditor.MultilineText ? fallback : null;
        this.Choice = this.Editor == StepInputEditor.Choice ? fallback : null;
        this.IsChecked = fallback == StepConstants.Yes;
    }

    /// <summary>
    ///     Gets the input.
    /// </summary>
    public StepInput Input { get; }

    /// <summary>
    ///     Gets the input's label, marked when optional.
    /// </summary>
    public string Label => this.Input.Required ? this.Input.Label : $"{this.Input.Label} (optional)";

    /// <summary>
    ///     Gets the input's help, if any.
    /// </summary>
    public string? Help => this.Input.Help;

    /// <summary>
    ///     Gets the kind of control to show.
    /// </summary>
    public StepInputEditor Editor => this._type.Editor;

    /// <summary>
    ///     Gets whether to show a single-line text box.
    /// </summary>
    public bool IsText => this.Editor == StepInputEditor.Text;

    /// <summary>
    ///     Gets whether to show a multi-line text box.
    /// </summary>
    public bool IsMultiline => this.Editor == StepInputEditor.MultilineText;

    /// <summary>
    ///     Gets whether to show a drop-down of the options.
    /// </summary>
    public bool IsChoice => this.Editor == StepInputEditor.Choice;

    /// <summary>
    ///     Gets whether to show a check box per option.
    /// </summary>
    public bool IsMultiChoice => this.Editor == StepInputEditor.MultiChoice;

    /// <summary>
    ///     Gets whether to show a yes/no check box.
    /// </summary>
    public bool IsToggle => this.Editor == StepInputEditor.Toggle;

    /// <summary>
    ///     Gets a hint for text boxes, e.g. "number".
    /// </summary>
    public string Placeholder => this._type.Describe(this.Input);

    /// <summary>
    ///     Gets the options, for a drop-down.
    /// </summary>
    public IReadOnlyList<string> Options { get; }

    /// <summary>
    ///     Gets the options with whether each is picked, for check boxes.
    /// </summary>
    public ObservableCollection<SelectableItem<string>> Choices { get; }

    /// <summary>
    ///     Gets or sets the text typed, for text inputs.
    /// </summary>
    public string? Text
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the option picked, for a drop-down.
    /// </summary>
    public string? Choice
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether the yes/no check box is ticked.
    /// </summary>
    public bool IsChecked
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets what's wrong with the value, after the last check.
    /// </summary>
    public string? Error
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the value as an answer the input's type can check.
    /// </summary>
    public string? Raw => this.Editor switch
    {
        StepInputEditor.Choice => this.Choice,
        StepInputEditor.MultiChoice => string.Join(",", this.Choices.Where(c => c.IsSelected).Select(c => c.Value)),
        StepInputEditor.Toggle => this.IsChecked ? StepConstants.Yes : StepConstants.No,
        _ => this.Text,
    };

    /// <summary>
    ///     Checks the value, setting <see cref="Error" />.
    /// </summary>
    /// <param name="answer">The answer in canonical form, or null for an optional input left blank.</param>
    /// <returns>True if the value is valid.</returns>
    public bool TryResolve(out string? answer)
    {
        try
        {
            answer = this._type.Resolve(this.Input, this.Raw);
            this.Error = null;
            return true;
        }
        catch (AflException ex)
        {
            answer = null;
            this.Error = ex.Message;
            return false;
        }
    }

    /// <summary>
    ///     Normalizes a default, ignoring one the type rejects (the workflow validator reports those).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The canonical value, or null.</returns>
    private string? TryNormalize(string value)
    {
        try
        {
            return this._type.Normalize(this.Input, value);
        }
        catch (AflException)
        {
            return null;
        }
    }
}
