using Afl.Core.Constants;
using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Niddy.Avalonia.PageSystem;
using Niddy.Avalonia.Toasts;

namespace Afl.UI.Pages;

/// <summary>
///     A library page of the builder: every document of one kind (workflows, common steps or skills) in every scope, a
///     draft editor with live validation, and saving the draft as JSON to the global library or the selected repo. Each
///     kind is a <see cref="AbstractLibraryPage{TEditor}" /> subclass, registered as its own sidebar page; they all share
///     this page's layout, which shows the parts of the builder their <see cref="AbstractLibraryEditor" /> uses.
/// </summary>
public abstract partial class AbstractLibraryPage : AbstractAflPage<LibraryPageDataContext>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractLibraryPage" /> class.
    /// </summary>
    public AbstractLibraryPage()
    {
        this.InitializeComponent();
        this.AddHandler(LostFocusEvent, this.OnFieldLostFocus, RoutingStrategies.Bubble, true);
    }

    /// <summary>
    ///     Gets the page's data context.
    /// </summary>
    private LibraryPageDataContext Workflows => this.PageDataContext;

    /// <summary>
    ///     Creates the editor for the kind of document this page edits.
    /// </summary>
    /// <returns>The editor.</returns>
    protected abstract AbstractLibraryEditor CreateEditor();

    /// <inheritdoc />
    protected override LibraryPageDataContext CreatePageDataContext()
    {
        return new LibraryPageDataContext(this.AppDataContext, this.CreateEditor());
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Z || this.Workflows is not { HasDraft: true } workflows
            || !e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
        {
            return;
        }

        e.Handled = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? workflows.Redo() : workflows.Undo();
    }

    /// <inheritdoc />
    protected override void OnNavigatingFrom(PageNavigatingFromEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (this.Workflows is not { IsDirty: true })
        {
            return;
        }

        e.Cancel = true;
        _ = this.ConfirmLeavingAsync(e);
    }

    /// <summary>
    ///     Opens the document passed as the navigation parameter, if any (e.g. a common step a workflow's step uses).
    /// </summary>
    /// <param name="e">The navigation.</param>
    protected override void OnNavigatedTo(PageNavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string id)
        {
            this.Workflows.Open(id);
        }
    }

    /// <summary>
    ///     Opens the common step or workflow the selected step refers to: on its own page, or here when this page edits
    ///     that kind of document.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnViewReferenced(object? sender, RoutedEventArgs e)
    {
        if (this.Workflows.SelectedStep is not { ReferencedId: { } id } step)
        {
            return;
        }

        if (step.RefersToCommonStep ? this is CommonStepsPage : this is WorkflowsPage)
        {
            if (await this.ConfirmDiscardAsync())
            {
                this.Workflows.Open(id);
            }
        }
        else if (step.RefersToCommonStep)
        {
            this.NavigateTo<CommonStepsPage>(addToBackStack: true, parameter: id);
        }
        else
        {
            this.NavigateTo<WorkflowsPage>(addToBackStack: true, parameter: id);
        }
    }

    /// <summary>
    ///     Records what was typed into a field in the undo history once the field loses focus.
    /// </summary>
    /// <param name="sender">The page.</param>
    /// <param name="e">The event.</param>
    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        this.Workflows?.CommitEdit();
    }

    /// <summary>
    ///     Undoes the last edit to the draft.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        this.Workflows.Undo();
    }

    /// <summary>
    ///     Redoes the last undone edit to the draft.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        this.Workflows.Redo();
    }

    /// <summary>
    ///     Leaves the page once the user agrees to drop the unsaved edits.
    /// </summary>
    /// <param name="e">The cancelled navigation.</param>
    /// <returns>A task.</returns>
    private async Task ConfirmLeavingAsync(PageNavigatingFromEventArgs e)
    {
        if (await this.ConfirmDiscardAsync())
        {
            e.Continue();
        }
    }

    /// <summary>
    ///     Asks whether unsaved edits can be dropped.
    /// </summary>
    /// <returns>True if there are none or the user agreed.</returns>
    private async Task<bool> ConfirmDiscardAsync()
    {
        return this.Workflows is not { IsDirty: true }
               || await this.WarnAsync("Unsaved changes", $"Discard the changes to the current {this.Workflows.Editor.Noun}?", "Discard");
    }

    /// <summary>
    ///     Adds a parameter to the draft.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddParameter(object? sender, RoutedEventArgs e)
    {
        this.Workflows.AddParameter();
    }

    /// <summary>
    ///     Removes the parameter whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveParameter(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ParameterDraftDataContext parameter })
        {
            this.Workflows.RemoveParameter(parameter);
        }
    }

    /// <summary>
    ///     Adds a requirement on another workflow to the draft.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddRequirement(object? sender, RoutedEventArgs e)
    {
        this.Workflows.AddRequirement();
    }

    /// <summary>
    ///     Removes the requirement whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveRequirement(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RequirementDraftDataContext requirement })
        {
            this.Workflows.RemoveRequirement(requirement);
        }
    }

    /// <summary>
    ///     Adds a supporting file to the skill draft.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddFile(object? sender, RoutedEventArgs e)
    {
        this.Workflows.AddFile();
    }

    /// <summary>
    ///     Removes the skill file whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveFile(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SkillFileDraftDataContext file })
        {
            this.Workflows.RemoveFile(file);
        }
    }

    /// <summary>
    ///     Asks for an id and saves the selected step as a common step.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnSaveAsCommon(object? sender, RoutedEventArgs e)
    {
        if (this.Workflows.SelectedStep is not { } step)
        {
            return;
        }

        var id = await this.AskAsync("Save as common step",
            $"Pick an id for the common step. It's saved to the {this.Workflows.SaveScope} common steps and this step then uses it.",
            step.Id);
        if (!string.IsNullOrWhiteSpace(id) && this.Workflows.SaveStepAsCommon(id) is { } path)
        {
            this.ShowToast($"Saved {path}");
        }
    }

    /// <summary>
    ///     Replaces the selected step with the steps it stands for, written out in the workflow.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnCopyIntoWorkflow(object? sender, RoutedEventArgs e)
    {
        this.Workflows.CopyStepIntoWorkflow();
    }

    /// <summary>
    ///     Clears the search and filters.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnClearFilters(object? sender, RoutedEventArgs e)
    {
        this.Workflows.ClearFilters();
    }

    /// <summary>
    ///     Asks for an id and starts a new workflow.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        if (!await this.ConfirmDiscardAsync())
        {
            return;
        }

        var id = await this.AskAsync(this.Workflows.NewLabel, "Pick an id. It's also the file name, e.g. \"feature\" → feature.json.",
            placeholder: "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            this.Workflows.New(id);
        }
    }

    /// <summary>
    ///     Asks for an id and copies the draft under it.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnDuplicate(object? sender, RoutedEventArgs e)
    {
        if (this.Workflows is not { } workflows)
        {
            return;
        }

        var id = await this.AskAsync($"Duplicate {workflows.Editor.Noun}", "Pick an id for the copy.", workflows.DraftId + "-copy");
        if (!string.IsNullOrWhiteSpace(id))
        {
            workflows.Duplicate(id);
        }
    }

    /// <summary>
    ///     Copies the selected listing's id, e.g. for a <c>use</c> or <c>include</c>.
    /// </summary>
    /// <param name="sender">The menu item.</param>
    /// <param name="e">The event.</param>
    private void OnCopyListingId(object? sender, RoutedEventArgs e)
    {
        if (this.Workflows?.SelectedListing is { } listing)
        {
            this.CopyText(listing.Id, $"id {listing.Id}");
        }
    }

    /// <summary>
    ///     Deletes the selected workflow's file after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (this.Workflows.SelectedListing is not { } listing)
        {
            return;
        }

        var noun = this.Workflows.Editor.Noun;
        if (await this.WarnAsync($"Delete {noun}",
                $"Delete {listing.Path}? A {noun} with the same id in a wider scope takes its place.", "Delete")
            && this.Workflows.Delete())
        {
            this.ShowToast($"Deleted {listing.Id}.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Adds an input to the selected step.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddInput(object? sender, RoutedEventArgs e)
    {
        this.Workflows.SelectedStep?.AddInput();
    }

    /// <summary>
    ///     Adds an arrow to the selected step, to the end until it's pointed somewhere.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddArrow(object? sender, RoutedEventArgs e)
    {
        this.Workflows.SelectedStep?.AddArrow(ArrowConstants.End);
    }

    /// <summary>
    ///     Removes the arrow whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveArrow(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ArrowDraftDataContext arrow })
        {
            this.Workflows.SelectedStep?.RemoveArrow(arrow);
        }
    }

    /// <summary>
    ///     Moves the arrow whose up button was clicked one place up.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnMoveArrowUp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ArrowDraftDataContext arrow })
        {
            this.Workflows.SelectedStep?.MoveArrow(arrow, -1);
        }
    }

    /// <summary>
    ///     Moves the arrow whose down button was clicked one place down.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnMoveArrowDown(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ArrowDraftDataContext arrow })
        {
            this.Workflows.SelectedStep?.MoveArrow(arrow, 1);
        }
    }

    /// <summary>
    ///     Adds a nested condition to the compound condition whose Add button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddCondition(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConditionDraftDataContext condition })
        {
            condition.AddChild();
        }
    }

    /// <summary>
    ///     Removes the nested condition whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveCondition(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConditionDraftDataContext { Parent: { } parent } condition })
        {
            parent.RemoveChild(condition);
        }
    }

    /// <summary>
    ///     Adds a step's arrow to the wait group whose Add step button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddWaitStep(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WaitGroupDraftDataContext group })
        {
            group.AddStep();
        }
    }

    /// <summary>
    ///     Adds a nested group to the wait group whose Add group button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddWaitGroup(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WaitGroupDraftDataContext group })
        {
            group.AddGroup();
        }
    }

    /// <summary>
    ///     Removes the wait item whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveWaitItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AbstractWaitItemDraftDataContext { Parent: { } parent } item })
        {
            parent.Remove(item);
        }
    }

    /// <summary>
    ///     Removes the input whose Remove button was clicked.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveInput(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InputDraftDataContext input })
        {
            this.Workflows.SelectedStep?.RemoveInput(input);
        }
    }

    /// <summary>
    ///     Removes the selected step.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveStep(object? sender, RoutedEventArgs e)
    {
        this.Workflows.RemoveStep();
    }

    /// <summary>
    ///     Closes the selected step's properties, leaving the flow the whole area.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnCloseStepPanel(object? sender, RoutedEventArgs e)
    {
        this.Workflows.Flow.IsPropertiesOpen = false;
    }

    /// <summary>
    ///     Copies the double-clicked variable to the clipboard.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">The event.</param>
    private async void OnCopyVariable(object? sender, TappedEventArgs e)
    {
        if (this.VariableList.SelectedItem is string variable && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(variable);
            this.ShowToast($"Copied {variable}", ToastType.Info);
        }
    }

    /// <summary>
    ///     Saves the draft.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (this.Workflows.Save() is { } path)
        {
            this.ShowToast($"Saved {path}");
        }
    }
}
