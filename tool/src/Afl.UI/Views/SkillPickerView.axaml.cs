using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Niddy.Avalonia.Dialogs;

namespace Afl.UI.Views;

/// <summary>
///     Picks the skills for a run, shown in a dialog by <see cref="Open" />: the skills recommended for the repo to tick,
///     and the run's skills to remove from or add to.
/// </summary>
public partial class SkillPickerView : UserControl
{
    /// <summary>
    ///     Asks for the project's languages and frameworks again, then reloads the picker.
    /// </summary>
    private readonly Func<Control, Task>? _changeProject;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SkillPickerView" /> class, for the designer.
    /// </summary>
    public SkillPickerView()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="SkillPickerView" /> class.
    /// </summary>
    /// <param name="picker">The picker.</param>
    /// <param name="changeProject">Asks for the project's languages and frameworks again, over the given control.</param>
    public SkillPickerView(SkillPickerDataContext picker, Func<Control, Task>? changeProject)
    {
        this.InitializeComponent();
        this.DataContext = picker;
        this._changeProject = changeProject;
    }

    /// <summary>
    ///     Asks someone to pick a run's skills.
    /// </summary>
    /// <param name="parent">A control in the window the dialog opens over.</param>
    /// <param name="picker">The picker; what's picked is read from it afterwards.</param>
    /// <param name="changeProject">Asks for the project's languages and frameworks again, then reloads the picker.</param>
    /// <returns>True to go on with the skills picked, false if cancelled.</returns>
    public static Task<bool> Open(Control parent, SkillPickerDataContext picker, Func<Control, Task>? changeProject)
    {
        return Dialog.View.Open(parent, new SkillPickerView(picker, changeProject), "Skills",
            "Pick the skills this run's agents get. Tick the ones recommended for this repo that you want.", "Continue",
            maxWidth: 620);
    }

    /// <summary>
    ///     Asks for the project's languages and frameworks again.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnChangeProject(object? sender, RoutedEventArgs e)
    {
        if (this._changeProject is { } change)
        {
            await change(this);
        }
    }

    /// <summary>
    ///     Ticks every recommendation.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnTickAll(object? sender, RoutedEventArgs e)
    {
        (this.DataContext as SkillPickerDataContext)?.TickAll();
    }
}
