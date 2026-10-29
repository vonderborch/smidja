using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     A value shown with a check box in a multi-select list, e.g. the Jira projects a repo can be associated with.
/// </summary>
/// <typeparam name="TValue">The value's type.</typeparam>
/// <param name="value">The value.</param>
/// <param name="label">The text shown for the value.</param>
/// <param name="isSelected">Whether the value starts selected.</param>
public class SelectableItem<TValue>(TValue value, string label, bool isSelected = false) : ReactiveObject
{
    /// <summary>
    ///     Gets the value.
    /// </summary>
    public TValue Value { get; } = value;

    /// <summary>
    ///     Gets the text shown for the value.
    /// </summary>
    public string Label { get; } = label;

    /// <summary>
    ///     Gets or sets whether the value is selected.
    /// </summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = isSelected;

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Label;
    }
}
