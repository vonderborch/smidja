using System.ComponentModel;
using Afl.Core.Runs;
using Afl.UI.DataContexts;
using Afl.UI.Views;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Niddy.Avalonia.Dialogs;
using Niddy.Avalonia.PageSystem;
using Niddy.Avalonia.Toasts;

namespace Afl.UI.Pages;

/// <summary>
///     The runs of the selected repo, child runs folded under their parent: the run on its workflow's graph (steps open
///     in a popup with what they need from the user) or as a step timeline, the live output and the run's actions.
/// </summary>
[PageRegistration(DisplayName = "Runs", SortKey = "2", Shortcut = "Primary+2")]
[PageIcon(PageIcons.Runs)]
public partial class RunsPage : AbstractAflPage<RunsPageDataContext>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RunsPage" /> class.
    /// </summary>
    public RunsPage()
    {
        this.InitializeComponent();

        // Keep the live output scrolled to the newest line.
        this.OutputScroller.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Extent")
            {
                Dispatcher.UIThread.Post(this.OutputScroller.ScrollToEnd);
            }
        };

        // Hold the rows still from a right press until its release is handled, when the row's menu opens on it.
        this.RowList.AddHandler(PointerPressedEvent, this.OnRowListPressed, RoutingStrategies.Tunnel, true);
        this.RowList.AddHandler(PointerReleasedEvent, this.OnRowListReleased, RoutingStrategies.Bubble, true);
    }

    /// <summary>
    ///     Holds the rows still while a row is right-clicked, so a reload between the press and the release can't take
    ///     away the row its menu is asked for on.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">The press.</param>
    private void OnRowListPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ContextClicks.IsContextPress(e))
        {
            this.Runs.IsRowMenuPending = true;
        }
    }

    /// <summary>
    ///     Lets go of the rows once a right-click's release is handled: by then its menu is open (and holds them itself)
    ///     or none opened.
    /// </summary>
    /// <param name="sender">The list.</param>
    /// <param name="e">The release.</param>
    private void OnRowListReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ContextClicks.IsContextRelease(e))
        {
            this.Runs.IsRowMenuPending = false;
        }
    }

    /// <summary>
    ///     Gets the page's data context.
    /// </summary>
    private RunsPageDataContext Runs => this.PageDataContext;

    /// <inheritdoc />
    protected override RunsPageDataContext CreatePageDataContext()
    {
        var runs = new RunsPageDataContext(this.AppDataContext);
        runs.StepFocused += this.OnStepFocused;
        return runs;
    }

    /// <summary>
    ///     Scrolls the timeline to a step, once the Timeline tab is showing.
    /// </summary>
    /// <param name="sender">The data context.</param>
    /// <param name="stepId">The step's id.</param>
    private void OnStepFocused(object? sender, string stepId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var index = this.Runs.SelectedRun?.Steps.FindIndex(s => s.StepId == stepId) ?? -1;
            if (index >= 0 && this.Timeline.ContainerFromIndex(index) is { } container)
            {
                container.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     Selects the run passed as the navigation parameter, if any (e.g. one just started from the Issues page).
    /// </summary>
    /// <param name="e">The navigation.</param>
    protected override void OnNavigatedTo(PageNavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string runId)
        {
            this.Runs.Focus(runId);
        }
    }

    /// <summary>
    ///     Answers the questions the current step is waiting on.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAnswer(object? sender, RoutedEventArgs e)
    {
        this.Runs.Answer();
    }

    /// <summary>
    ///     Has the picked agent do the step waiting for a choice of agent.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnUseAgent(object? sender, RoutedEventArgs e)
    {
        this.Runs.UseAgent();
    }

    /// <summary>
    ///     Approves the current step, with the input form's answers.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnApprove(object? sender, RoutedEventArgs e)
    {
        this.Runs.Approve();
    }

    /// <summary>
    ///     Asks for feedback and sends the current step back with it.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnReject(object? sender, RoutedEventArgs e)
    {
        var feedback = await this.AskAsync("Send step back", "What should change? The step is redone with this feedback.",
            placeholder: "Feedback");
        if (!string.IsNullOrWhiteSpace(feedback))
        {
            this.Runs.Reject(feedback);
        }
    }

    /// <summary>
    ///     Sends the step back with the answers to the AI's open questions.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSendAnswers(object? sender, RoutedEventArgs e)
    {
        this.Runs.SendAnswers();
    }

    /// <summary>
    ///     Carries on a step paused for permission with the user's decisions.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnPermit(object? sender, RoutedEventArgs e)
    {
        this.Runs.Permit();
    }

    /// <summary>
    ///     Takes the output of a step paused for permission as it is, allowing nothing.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAcceptWithoutPermissions(object? sender, RoutedEventArgs e)
    {
        this.Runs.AcceptWithoutPermissions();
    }

    /// <summary>
    ///     Uses the shown agent's result, with the user's edits, as the step's output.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnChoose(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.SelectedCandidate is { CanUse: true } candidate)
        {
            this.Runs.Choose(candidate);
        }
    }

    /// <summary>
    ///     Asks which agent merges and what it should keep in mind, then has it merge the results.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnMerge(object? sender, RoutedEventArgs e)
    {
        var agents = this.Runs.MergeAgents;
        var agent = agents.Count > 1
            ? await Dialog.Selection.Open(this, "Merge results", "Which agent should merge the results?", agents, a => a)
            : agents.FirstOrDefault();
        if (agent is null)
        {
            return;
        }

        var note = await this.AskAsync("Merge results", $"{agent} merges every result into one. Optionally, say what to keep in mind.",
            placeholder: "e.g. keep the first plan's tests (optional)");
        if (note is not null)
        {
            this.Runs.Merge(agent, note);
        }
    }

    /// <summary>
    ///     Asks for optional feedback and replays the clicked step. Cancelling the dialog leaves the run alone.
    /// </summary>
    /// <param name="sender">The button, whose data context is the step.</param>
    /// <param name="e">The event.</param>
    private async void OnReplay(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RunStep step })
        {
            return;
        }

        var feedback = await this.AskAsync("Replay step", this.Runs.DescribeReplay(step), placeholder: "What to change (optional)");
        if (feedback is not null)
        {
            this.Runs.Replay(step, feedback);
        }
    }

    /// <summary>
    ///     Opens the interactive step in a terminal.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnOpenTerminal(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.OpenTerminal() == true)
        {
            this.ShowToast("Opened claude in a terminal. Come back and mark the step done when you've finished.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Marks the current step done, with an optional note.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnMarkDone(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.CheckInputs() is null)
        {
            return;
        }

        var note = await this.AskAsync("Mark step done", "Optionally, add a note. It becomes the step's output.",
            placeholder: "Note (optional)");
        if (note is not null)
        {
            this.Runs.MarkDone(string.IsNullOrWhiteSpace(note) ? null : note);
        }
    }

    /// <summary>
    ///     Retries the failed step.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRetry(object? sender, RoutedEventArgs e)
    {
        this.Runs.Retry();
    }

    /// <summary>
    ///     Shows the markdown document a step saved, formatted.
    /// </summary>
    /// <param name="sender">The link, whose data context is the step.</param>
    /// <param name="e">The event.</param>
    private async void OnPreviewArtifact(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RunStep { ArtifactPath: { } path } step } && this.Runs.ReadArtifact(step) is { } text)
        {
            await MarkdownView.OpenDialog(this, Path.GetFileName(path), text);
        }
    }

    /// <summary>
    ///     Selects a child run a step started.
    /// </summary>
    /// <param name="sender">The button, tagged with the child's run id.</param>
    /// <param name="e">The event.</param>
    private void OnOpenChild(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string runId } && !this.Runs.OpenRun(runId))
        {
            this.ShowToast($"Run {runId} isn't listed for this repo.");
        }
    }

    /// <summary>
    ///     Selects the run that started the selected one.
    /// </summary>
    /// <param name="sender">The link.</param>
    /// <param name="e">The event.</param>
    private void OnOpenParent(object? sender, RoutedEventArgs e)
    {
        if (!this.Runs.OpenParent())
        {
            this.ShowToast("The run that started this one isn't listed for this repo.");
        }
    }

    /// <summary>
    ///     Shows or hides what's in a folder, or the runs a run started.
    /// </summary>
    /// <param name="sender">The toggle, whose data context is the row.</param>
    /// <param name="e">The event.</param>
    private void OnToggleRow(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AbstractRunListRowDataContext row })
        {
            this.Runs.ToggleRow(row);
        }
    }

    /// <summary>
    ///     Empties the search and unpicks every state and tag.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnClearFilters(object? sender, RoutedEventArgs e)
    {
        this.Runs.ClearFilters();
    }

    /// <summary>
    ///     Opens the step the run is on in the popup over the graph.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnOpenStep(object? sender, RoutedEventArgs e)
    {
        this.Runs.OpenCurrentStep();
    }

    /// <summary>
    ///     Closes the step popup.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnClosePopup(object? sender, RoutedEventArgs e)
    {
        this.Runs.ClosePopup();
    }

    /// <summary>
    ///     Switches to the timeline, scrolled to the popup's step.
    /// </summary>
    /// <param name="sender">The link.</param>
    /// <param name="e">The event.</param>
    private void OnShowInTimeline(object? sender, RoutedEventArgs e)
    {
        this.Runs.ShowInTimeline();
    }

    /// <summary>
    ///     Resumes the run.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnResume(object? sender, RoutedEventArgs e)
    {
        this.Runs.Resume();
    }

    /// <summary>
    ///     Stops the in-flight engine call.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (this.Runs is { } runs)
        {
            await runs.StopAsync();
        }
    }

    /// <summary>
    ///     Adds a note to the current step.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnAddNote(object? sender, RoutedEventArgs e)
    {
        var note = await this.AskAsync("Add note", "Notes are kept on the current step for the record (e.g. for a hand-off).",
            placeholder: "Note");
        if (!string.IsNullOrWhiteSpace(note) && this.Runs.AddNote(note) == true)
        {
            this.ShowToast("Note added.");
        }
    }

    /// <summary>
    ///     Asks for the run's own name and renames it. A blank name shows the ticket's title again.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.SelectedRun is not { } run)
        {
            return;
        }

        var title = await this.AskAsync("Rename run", $"Give the run a name of its own. Leave it blank to show the ticket's title ({run.Ticket.Title}).",
            run.Title ?? string.Empty, "Name");
        if (title is not null)
        {
            this.Runs.Rename(title);
        }
    }

    /// <summary>
    ///     Asks for the run's tags and replaces them.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnEditTags(object? sender, RoutedEventArgs e)
    {
        var tags = await this.AskAsync("Tags", "Tags for the run, with commas between them. Filter the list by them above it.",
            this.Runs.TagsInput, "e.g. billing, urgent");
        if (tags is not null)
        {
            this.Runs.SetTags(tags);
        }
    }

    /// <summary>
    ///     Adds a tag of the selected run to the list's filter.
    /// </summary>
    /// <param name="sender">The tag's button, whose data context is the tag.</param>
    /// <param name="e">The event.</param>
    private void OnFilterTag(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string tag })
        {
            this.Runs.TagFilter.Add(tag);
        }
    }

    /// <summary>
    ///     Asks which folder to file the run in (or a new one) and moves it there.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnMoveToFolder(object? sender, RoutedEventArgs e)
    {
        const string TopLevel = "(top level)";
        const string NewFolder = "New folder…";
        string[] choices = [TopLevel, .. this.Runs.FolderChoices(), NewFolder];
        var choice = await Dialog.Selection.Open(this, "Move to folder", "Which folder should the run be filed in?", choices, c => c);
        if (choice == NewFolder)
        {
            await this.MoveToNewFolderAsync();
        }
        else if (choice is not null)
        {
            this.Runs.MoveToFolder(choice == TopLevel ? null : choice);
        }
    }

    /// <summary>
    ///     Holds the rows still while a row's menu is open.
    /// </summary>
    /// <param name="sender">The menu.</param>
    /// <param name="e">The event.</param>
    private void OnRowMenuOpened(object? sender, RoutedEventArgs e)
    {
        this.Runs.IsRowMenuOpen = true;
    }

    /// <summary>
    ///     Lets the rows catch up once a row's menu closes.
    /// </summary>
    /// <param name="sender">The menu.</param>
    /// <param name="e">The event.</param>
    private void OnRowMenuClosed(object? sender, RoutedEventArgs e)
    {
        this.Runs.IsRowMenuOpen = false;
    }

    /// <summary>
    ///     Fills the run menu's submenus for the run it opens on.
    /// </summary>
    /// <param name="sender">The menu.</param>
    /// <param name="e">The event.</param>
    private void OnRunMenuOpening(object? sender, CancelEventArgs e)
    {
        this.Runs.UpdateRunMenu();
    }

    /// <summary>
    ///     Puts the tag picked in the run menu on the run or takes it off, or edits the run's tags.
    /// </summary>
    /// <param name="sender">The Tags item.</param>
    /// <param name="e">The event, from the picked tag's item.</param>
    private void OnPickTag(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem { DataContext: MenuChoice choice })
        {
            return;
        }

        if (choice.Value is { } tag)
        {
            this.Runs.ToggleTag(tag);
        }
        else
        {
            this.OnEditTags(sender, e);
        }
    }

    /// <summary>
    ///     Files the run in the folder picked in the run menu, or asks for a new one.
    /// </summary>
    /// <param name="sender">The Move to item.</param>
    /// <param name="e">The event, from the picked folder's item.</param>
    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem { DataContext: MenuChoice choice })
        {
            return;
        }

        if (choice.Value is { } folder)
        {
            this.Runs.MoveToFolder(folder);
        }
        else
        {
            await this.MoveToNewFolderAsync();
        }
    }

    /// <summary>
    ///     Copies the selected run's id, e.g. to use with the <c>afl</c> CLI.
    /// </summary>
    /// <param name="sender">The menu item.</param>
    /// <param name="e">The event.</param>
    private void OnCopyRunId(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.SelectedRun is { } run)
        {
            this.CopyText(run.Id, $"run id {run.Id}");
        }
    }

    /// <summary>
    ///     Asks for a folder's name and creates it, inside the selected folder if one is selected.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnCreateFolder(object? sender, RoutedEventArgs e)
    {
        var where = this.Runs.SelectedFolder is { } parent ? $"in {parent.Folder}" : "at the top level";
        var name = await this.AskAsync("New folder", $"The folder is created {where}. Use / to create folders inside it too.",
            placeholder: "Folder name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            this.Runs.CreateFolder(name);
        }
    }

    /// <summary>
    ///     Asks for the selected folder's new path and renames (or moves) it.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRenameFolder(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.SelectedFolder is not { } folder)
        {
            return;
        }

        var path = await this.AskAsync("Rename folder", "The folder's new path, with / between folders. What's in it moves with it.",
            folder.Folder, "Folder");
        if (!string.IsNullOrWhiteSpace(path) && this.Runs.SelectedFolder?.Folder == folder.Folder)
        {
            this.Runs.RenameFolder(path);
        }
    }

    /// <summary>
    ///     Removes the selected folder after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRemoveFolder(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.SelectedFolder is { } folder && await this.WarnAsync("Remove folder", this.Runs.RemoveFolderText, "Remove folder")
            && this.Runs.SelectedFolder?.Folder == folder.Folder && this.Runs.RemoveFolder())
        {
            this.ShowToast($"Folder {folder.Folder} removed.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Cancels the run after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (await this.WarnAsync("Cancel run", "Cancel this run? It can't be resumed afterwards.", "Cancel run")
            && this.Runs.Cancel() == true)
        {
            this.ShowToast("Run cancelled.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Deletes the run after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (this.Runs.SelectedRun is { } run && await this.WarnAsync("Delete run", this.Runs.DeleteText, "Delete run")
            && this.Runs.SelectedRun == run && this.Runs.Delete())
        {
            this.ShowToast($"Run {run.Id} deleted.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Asks for a new folder's path and files the selected run in it.
    /// </summary>
    /// <returns>A task that completes once the run is moved, or the question is dismissed.</returns>
    private async Task MoveToNewFolderAsync()
    {
        var folder = await this.AskAsync("Move to folder", "The new folder's path, with / between folders (e.g. billing/q4).",
            placeholder: "Folder");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            this.Runs.MoveToFolder(folder);
        }
    }
}
