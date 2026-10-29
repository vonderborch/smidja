using Afl.Core.Workflows;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Terminals;
using Afl.UI.Flow;
using Avalonia.Controls;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     The runs of the selected repo (or every repo), with the selected run's step timeline, live output and the
///     actions its current step allows. Engine calls run in-process on a background thread through the shell's
///     <see cref="RunDriver" />.
/// </summary>
public class RunsPageDataContext : AbstractPageDataContext
{
    /// <summary>
    ///     The properties that depend on the selected run's state.
    /// </summary>
    private static readonly string[] StateProperties =
    [
        nameof(CurrentStep), nameof(Live), nameof(IsBusyRunning), nameof(CanApprove), nameof(CanMarkDone),
        nameof(CanOpenTerminal), nameof(CanRetry), nameof(CanResume), nameof(CanStop), nameof(CanCancel), nameof(CanDelete),
        nameof(CanAddNote), nameof(CanReplay), nameof(Hint), nameof(ShowInputs), nameof(CanChoose), nameof(CanSendBack), nameof(CanAnswer), nameof(ShowQuestions), nameof(CanPermit), nameof(PermissionsHeading), nameof(CanContinuePermit), nameof(QuestionsHeading), nameof(InputsHeading),
        nameof(PathText), nameof(CanPickAgent), nameof(AgentCandidates), nameof(ModeText), nameof(SkillsText), nameof(ParentText), nameof(PathStepsText),
        nameof(CanOpenStep), nameof(ShowsInlineHint), nameof(CanLabel), nameof(CanMoveToFolder),
    ];

    /// <summary>
    ///     The properties that depend on the selected row.
    /// </summary>
    private static readonly string[] FolderProperties =
    [
        nameof(SelectedFolder), nameof(CanCreateFolder), nameof(CanEditFolder), nameof(RemoveFolderText),
    ];

    /// <summary>
    ///     The properties that depend on the step popup.
    /// </summary>
    private static readonly string[] PopupProperties =
    [
        nameof(IsPopupOpen), nameof(PopupStep), nameof(PopupIsCurrent), nameof(PopupHeading), nameof(PopupPathRunId),
        nameof(PopupPathText), nameof(CanOpenStep), nameof(ShowsInlineHint),
    ];

    /// <summary>
    ///     The properties that depend on which view of the run is shown.
    /// </summary>
    private static readonly string[] ViewProperties =
    [
        nameof(SelectedTab), nameof(ShowsGraph), nameof(ShowsInlineStepActions), nameof(CanOpenStep), nameof(ShowsInlineHint),
    ];

    /// <summary>
    ///     The keys (<see cref="AbstractRunListRowDataContext.ExpandKey" />) of the folders and runs whose contents are
    ///     shown in the list; the rest are collapsed.
    /// </summary>
    private readonly HashSet<string> _expanded = [];

    /// <summary>
    ///     The listed runs in nested order, with their levels, hidden ones included.
    /// </summary>
    private IReadOnlyList<(Run Run, int Level)> _nested = [];

    /// <summary>
    ///     Whether the rows are being rebuilt, so the list clearing its selection doesn't clear the selected run.
    /// </summary>
    private bool _rebuildingRows;

    /// <summary>
    ///     Whether the rows changed while a row's menu was open (or being asked for), so they're rebuilt once it closes.
    /// </summary>
    private bool _rowsStale;

    /// <summary>
    ///     The run, step, status, attempt and ask round the popup last opened by itself for, so it opens again only
    ///     when something new waits.
    /// </summary>
    private string? _popupKey;

    /// <summary>
    ///     The run the popup was opened on, so it closes when another run is picked.
    /// </summary>
    private string? _popupOwnerId;

    /// <summary>
    ///     The run holding the popup's step: the selected run, or the path run the step waits in.
    /// </summary>
    private Run? _popupRun;

    /// <summary>
    ///     The run, step and status the input form was built for, so reloads keep what was typed.
    /// </summary>
    private string? _inputsKey;

    /// <summary>
    ///     The run, step and status the candidate tabs were built for, so reloads keep edits.
    /// </summary>
    private string? _candidatesKey;

    /// <summary>
    ///     The run, step, status and attempt the open questions were read for, so reloads keep the answers typed.
    /// </summary>
    private string? _questionsKey;

    /// <summary>
    ///     The run, step, status and round the permission requests were read for, so reloads keep the decisions made.
    /// </summary>
    private string? _permissionsKey;

    /// <summary>
    ///     The hidden workflow page whose flow graph the Graph tab shows, read-only, for the selected run's workflow.
    /// </summary>
    private readonly LibraryPageDataContext _graphOwner;

    /// <summary>
    ///     The run whose workflow the graph holds, so a reload only redraws the overlay.
    /// </summary>
    private string? _graphRunId;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RunsPageDataContext" /> class.
    /// </summary>
    /// <param name="shell">The shell that owns the stores.</param>
    public RunsPageDataContext(ShellDataContext shell) : base(shell)
    {
        this._graphOwner = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        this.Graph.IsReadOnly = true;
        this.Graph.StepClicked += this.OnGraphStepClicked;
        this.TagFilter.Edited += (_, _) => this.OnFilterEdited();
        this.Filter.Edited += (_, _) => this.OnFilterEdited();
        this.ReloadOnChange(h => shell.Runs.Changed += h, h => shell.Runs.Changed -= h);
        EventHandler<LiveRun> finished = (_, _) => this.Reload();
        this.OnDispose<EventHandler<LiveRun>>(h => shell.Driver.Finished += h, h => shell.Driver.Finished -= h, finished);
        this.Reload();
    }

    /// <summary>
    ///     Raised when a step should be shown in the timeline, with its id: one picked with "Show in timeline".
    /// </summary>
    public event EventHandler<string>? StepFocused;

    /// <summary>
    ///     Gets the selected run's workflow as a read-only flow graph, showing which steps ran, which arrows fired or
    ///     died and where the run is (<see cref="RunOverlay" />).
    /// </summary>
    public FlowGraphDataContext Graph => this._graphOwner.Flow;

    /// <summary>
    ///     Gets or sets the view shown for the selected run: 0 for the graph (the default), whose steps open in a popup,
    ///     or 1 for the timeline, which lists every step with the current one's actions above it.
    /// </summary>
    public int SelectedTab
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                this.RaisePropertiesChanged(ViewProperties);
            }
        }
    }

    /// <summary>
    ///     Gets whether the graph is shown, so the current step's actions live in its popup.
    /// </summary>
    public bool ShowsGraph => this.SelectedTab == 0;

    /// <summary>
    ///     Gets whether the current step's actions are shown above the timeline.
    /// </summary>
    public bool ShowsInlineStepActions => !this.ShowsGraph;

    /// <summary>
    ///     Gets the runs in nested order (child and path runs after the run that started them), newest first, including
    ///     those folded away in the list.
    /// </summary>
    public ObservableCollection<Run> Runs { get; } = [];

    /// <summary>
    ///     Gets the rows shown in the run list: the folders, then the <see cref="Runs" /> the tag filter keeps, without
    ///     those in a collapsed folder or under a collapsed run.
    /// </summary>
    public ObservableCollection<AbstractRunListRowDataContext> Rows { get; } = [];

    /// <summary>
    ///     Gets the runs' rows in <see cref="Rows" />, without the folders'.
    /// </summary>
    public IReadOnlyList<RunRowDataContext> RunRows => [.. this.Rows.OfType<RunRowDataContext>()];

    /// <summary>
    ///     Gets the tag filter: a run is listed when it, or a run started with it, has every picked tag.
    /// </summary>
    public TagFilterDataContext TagFilter { get; } = new();

    /// <summary>
    ///     Gets the search and state filter: a run is listed when it, or a run started with it, matches it (and the
    ///     tag filter).
    /// </summary>
    public RunFilterDataContext Filter { get; } = new();

    /// <summary>
    ///     Gets whether the search, a state or a tag is narrowing the list.
    /// </summary>
    public bool IsFiltered => this.Filter.HasFilter || this.TagFilter.HasSelected;

    /// <summary>
    ///     Gets the folders of the listed runs, sorted: those created for the repo and those its runs are filed in.
    /// </summary>
    public IReadOnlyList<string> Folders { get; private set; } = [];

    /// <summary>
    ///     Gets or sets the selected row: a run's, which selects the run, or a folder's.
    /// </summary>
    public AbstractRunListRowDataContext? SelectedRow
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertiesChanged(FolderProperties);
            if (!this._rebuildingRows && value is RunRowDataContext row)
            {
                this.SelectedRun = row.Run;
            }
        }
    }

    /// <summary>
    ///     Gets the selected folder's row, if a folder is selected.
    /// </summary>
    public FolderRowDataContext? SelectedFolder => this.SelectedRow as FolderRowDataContext;

    /// <summary>
    ///     Gets whether a folder can be created: the list shows one repo's runs.
    /// </summary>
    public bool CanCreateFolder => this.Repo is not null;

    /// <summary>
    ///     Gets whether the selected folder can be renamed or removed.
    /// </summary>
    public bool CanEditFolder => this.SelectedFolder is not null && this.Repo is not null;

    /// <summary>
    ///     Gets what removing the selected folder does, for the confirmation.
    /// </summary>
    public string RemoveFolderText => this.SelectedFolder is { } folder
        ? $"Remove folder {folder.Folder}? What's in it ({folder.Summary?.ToLowerInvariant()}) moves up into " +
          $"{(RunLabels.ParentOf(folder.Folder) is { } parent ? $"folder {parent}" : "the top level")}. No run is deleted."
        : string.Empty;

    /// <summary>
    ///     Gets whether the selected run can be renamed and tagged.
    /// </summary>
    public bool CanLabel => this.SelectedRun is not null;

    /// <summary>
    ///     Gets whether the selected run can be filed in a folder: it was started directly, not by another run.
    /// </summary>
    public bool CanMoveToFolder => this.SelectedRun is { ParentRunId: null };

    /// <summary>
    ///     Gets or sets whether a row's context menu is open. While it is, the rows aren't rebuilt (that would close it
    ///     under the user each time a run's file changes); they catch up when it closes.
    /// </summary>
    public bool IsRowMenuOpen
    {
        get;
        set
        {
            field = value;
            this.CatchUpRows();
        }
    }

    /// <summary>
    ///     Gets or sets whether a row is being right-clicked: from the press until the release is handled, by which time
    ///     its menu has opened (<see cref="IsRowMenuOpen" />) or it won't. Like an open menu, it holds the rows still, so
    ///     a run's file changing between the press and the release doesn't take away the row the menu is asked for on.
    /// </summary>
    public bool IsRowMenuPending
    {
        get;
        set
        {
            field = value;
            this.CatchUpRows();
        }
    }

    /// <summary>
    ///     Gets the run menu's Tags submenu: each tag in use, ticked if the selected run has it (<see cref="UpdateRunMenu" />).
    /// </summary>
    public IReadOnlyList<MenuChoice> RunTagChoices { get; private set; } = [];

    /// <summary>
    ///     Gets the run menu's Move to submenu: the top level and each folder, the run's own ticked (<see cref="UpdateRunMenu" />).
    /// </summary>
    public IReadOnlyList<MenuChoice> RunFolderChoices { get; private set; } = [];

    /// <summary>
    ///     Gets or sets whether the step popup over the graph is open.
    /// </summary>
    public bool IsPopupOpen
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the id of the step the popup shows.
    /// </summary>
    public string? PopupStepId { get; private set; }

    /// <summary>
    ///     Gets the step the popup shows: the selected run's, or the path run's it waits in.
    /// </summary>
    public RunStep? PopupStep
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the popup shows the selected run's current step, so it carries the step's actions.
    /// </summary>
    public bool PopupIsCurrent => this.PopupStep is { } step && this.IsPopupOpen && ReferenceEquals(this._popupRun, this.SelectedRun)
                                  && this.SelectedRun is { IsFinished: false } run && run.CurrentStepId == step.StepId;

    /// <summary>
    ///     Gets the popup's heading.
    /// </summary>
    public string PopupHeading => this.PopupStep switch
    {
        { WaitsForUser: true } when this.PopupIsCurrent => "Waiting for you",
        { WaitsForUser: true } when this.PopupPathRunId is not null => "Waiting for you in a path",
        _ => "Step",
    };

    /// <summary>
    ///     Gets the path run the popup's step waits in, when that isn't the selected run; otherwise null.
    /// </summary>
    public string? PopupPathRunId => this._popupRun is { } run && !ReferenceEquals(run, this.SelectedRun) ? run.Id : null;

    /// <summary>
    ///     Gets the line saying the popup's step waits in a path run, or null.
    /// </summary>
    public string? PopupPathText => this.PopupPathRunId is { } runId && this.PopupStep is { } step
        ? $"This step is {UiConverters.ToText(step.Status)} in path run {runId}. Open that run to act on it."
        : null;

    /// <summary>
    ///     Gets whether the graph is shown with its popup closed while the run is on a step, so it can be opened.
    /// </summary>
    public bool CanOpenStep => this.ShowsGraph && !this.IsPopupOpen && this.SelectedRun is { IsFinished: false, CurrentStep: not null };

    /// <summary>
    ///     Gets whether to show the hint above the view: not while the popup on the current step shows it.
    /// </summary>
    public bool ShowsInlineHint => !(this.ShowsGraph && this.PopupIsCurrent);

    /// <summary>
    ///     Gets or sets the selected run.
    /// </summary>
    public Run? SelectedRun
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                var sameRun = field?.Id == value?.Id;
                this.RaiseAndSetIfChanged(ref field, value);
                this.WatchLive();
                this.RefreshInputs();
                this.RefreshCandidates();
                this.RefreshQuestions();
                this.RefreshPermissions();
                this.RefreshGraph();
                this.RefreshPopup();
                this.SyncSelectedRow(sameRun ? this.SelectedFolder?.Folder : null);
                this.RaisePropertiesChanged(StateProperties);
            }
        }
    }

    /// <summary>
    ///     Gets the current step's inputs, answered when letting it through (approving it, or marking it done).
    /// </summary>
    public ObservableCollection<StepInputDataContext> Inputs { get; } = [];

    /// <summary>
    ///     Gets each agent's result, for a step several agents did that waits for a choice; otherwise empty.
    /// </summary>
    public ObservableCollection<CandidateDataContext> Candidates { get; } = [];

    /// <summary>
    ///     Gets or sets the candidate tab shown.
    /// </summary>
    public CandidateDataContext? SelectedCandidate
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the questions the AI left at the end of the current step's output, while it waits for approval, each with
    ///     the user's answer; otherwise empty.
    /// </summary>
    public ObservableCollection<OpenQuestionDataContext> Questions { get; } = [];

    /// <summary>
    ///     Gets or sets anything else to change, sent after the answers.
    /// </summary>
    public string QuestionsNote
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets whether to show the form for answering the AI's open questions.
    /// </summary>
    public bool ShowQuestions => this.Questions.Count > 0 && this.CanApprove;

    /// <summary>
    ///     Gets the heading over the open questions form.
    /// </summary>
    public string QuestionsHeading => this.Questions.Count == 1
        ? "The AI left a question for you"
        : $"The AI left {this.Questions.Count} questions for you";

    /// <summary>
    ///     Gets the refused tool calls the current step paused on, each with the user's decision; otherwise empty.
    /// </summary>
    public ObservableCollection<PermissionRequestDataContext> PermissionRequests { get; } = [];

    /// <summary>
    ///     Gets whether the current step paused because its agent was refused commands or tools.
    /// </summary>
    public bool CanPermit => this.IsIdleAt(StepStatus.AwaitingPermission);

    /// <summary>
    ///     Gets the heading over the permission requests.
    /// </summary>
    public string PermissionsHeading => this.PermissionRequests.Count == 1
        ? "The agent was refused 1 command or tool"
        : $"The agent was refused {this.PermissionRequests.Count} commands or tools";

    /// <summary>
    ///     Gets whether every permission request is decided with a usable rule, so the step can carry on.
    /// </summary>
    public bool CanContinuePermit => this.CanPermit && this.PermissionRequests.Count > 0 &&
                                     this.PermissionRequests.All(r => r.IsReady);

    /// <summary>
    ///     Gets whether to show the input form: the step has inputs and is waiting for the user.
    /// </summary>
    public bool ShowInputs => this.Inputs.Count > 0 && (this.CanApprove || this.CanMarkDone || this.CanAnswer);

    /// <summary>
    ///     Gets the heading over the input form: who is asking, and when.
    /// </summary>
    public string InputsHeading => this.CurrentStep?.Status switch
    {
        StepStatus.AwaitingInput when this.CurrentStep.OpenAskRound is not null => "The AI is asking",
        StepStatus.AwaitingInput => "Before this step starts",
        _ => "Questions",
    };

    /// <summary>
    ///     Gets whether the current step is waiting for the user to answer questions: before it starts, or because the
    ///     AI asked part-way through.
    /// </summary>
    public bool CanAnswer => this.IsIdleAt(StepStatus.AwaitingInput);

    /// <summary>
    ///     Gets whether the current step is waiting for someone to choose which agent does it.
    /// </summary>
    public bool CanPickAgent => this.IsIdleAt(StepStatus.ChoosingAgent);

    /// <summary>
    ///     Gets the agents that can do the current step, while it waits for a choice; otherwise empty.
    /// </summary>
    public IReadOnlyList<string> AgentCandidates =>
        this.CanPickAgent ? this.CurrentStep?.AgentChoice?.Candidates ?? [] : [];

    /// <summary>
    ///     Gets or sets the agent picked to do the current step.
    /// </summary>
    public string? PickedAgent
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets how the selected run's agents run their commands, e.g. "Background mode".
    /// </summary>
    public string? ModeText => this.SelectedRun is { } run
        ? $"{RunModeRegistry.Instance.Resolve(run.Mode).DisplayName} mode"
        : null;

    /// <summary>
    ///     Gets the skills picked for the selected run when it was started, e.g. "Skills: caveman, csharp-style"; null when
    ///     it keeps its workflow's own.
    /// </summary>
    public string? SkillsText => this.SelectedRun?.Skills is { } skills
        ? skills.Count == 0 ? "No skills" : "Skills: " + string.Join(", ", skills)
        : null;

    /// <summary>
    ///     Gets the selected run's current step, or null when it's finished.
    /// </summary>
    public RunStep? CurrentStep => this.SelectedRun?.CurrentStep;

    /// <summary>
    ///     Gets the app's live output for the selected run, if it has worked on it this session.
    /// </summary>
    public LiveRun? Live => this.SelectedRun is null ? null : this.Shell.Driver.Find(this.SelectedRun.Id);

    /// <summary>
    ///     Gets whether the app is running an engine call for the selected run.
    /// </summary>
    public bool IsBusyRunning => this.Live?.IsActive == true;

    /// <summary>
    ///     Gets whether the current step is waiting for approval (approve or reject).
    /// </summary>
    public bool CanApprove => this.IsIdleAt(StepStatus.AwaitingApproval);

    /// <summary>
    ///     Gets whether the current step, which several agents did, is waiting for the user to choose a result or
    ///     merge them.
    /// </summary>
    public bool CanChoose => this.IsIdleAt(StepStatus.AwaitingChoice);

    /// <summary>
    ///     Gets whether the current step can be sent back with feedback: it waits for approval or for a choice.
    /// </summary>
    public bool CanSendBack => this.CanApprove || this.CanChoose;

    /// <summary>
    ///     Gets the agents that can merge the current step's results, the step's merge agent first.
    /// </summary>
    public IReadOnlyList<string> MergeAgents
    {
        get
        {
            if (this.SelectedRun is not { CurrentStep: { } state } run)
            {
                return [];
            }

            var definition = run.DefinitionOf(state) as AbstractAgentStepDefinition;
            var first = definition?.MergeAgent ?? definition?.Agents?.FirstOrDefault();
            var ids = this.Shell.Agents.All().Select(a => a.Id);
            return (first is null ? ids : ids.Prepend(first)).Distinct().ToList();
        }
    }

    /// <summary>
    ///     Gets whether the current step is waiting for the user to mark it done.
    /// </summary>
    public bool CanMarkDone => this.IsIdleAt(StepStatus.AwaitingUser);

    /// <summary>
    ///     Gets whether the current step is an interactive session that can be opened in a terminal.
    /// </summary>
    public bool CanOpenTerminal => this.CanMarkDone && this.CurrentStep?.Interactive is not null;

    /// <summary>
    ///     Gets whether the current step failed and can be retried.
    /// </summary>
    public bool CanRetry => this.IsIdleAt(StepStatus.Failed);

    /// <summary>
    ///     Gets whether the run has pending work the engine can pick up, e.g. after the app or CLI was closed mid-run.
    /// </summary>
    public bool CanResume => this.IsIdleAt(StepStatus.Pending) || this.IsIdleAt(StepStatus.Running)
                             || this.IsIdleAt(StepStatus.AwaitingChildren) || this.IsIdleAt(StepStatus.AwaitingPaths);

    /// <summary>
    ///     Gets whether the app's in-flight call on the run can be stopped.
    /// </summary>
    public bool CanStop => this.IsBusyRunning;

    /// <summary>
    ///     Gets whether the run can be cancelled.
    /// </summary>
    public bool CanCancel => this.SelectedRun is { IsFinished: false } && !this.IsBusyRunning;

    /// <summary>
    ///     Gets whether the run can be deleted: it isn't busy here, and it isn't a child run whose parent is kept (child
    ///     runs go with the run that started them).
    /// </summary>
    public bool CanDelete => this.SelectedRun is { } run && !this.IsBusyRunning &&
                             (run.ParentRunId is not { } parentId || this.Shell.Runs.Get(parentId) is null);

    /// <summary>
    ///     Gets what deleting the selected run takes with it, for the confirmation.
    /// </summary>
    public string DeleteText
    {
        get
        {
            if (this.SelectedRun is not { } run)
            {
                return string.Empty;
            }

            var level = this._nested.FirstOrDefault(n => n.Run.Id == run.Id).Level;
            var children = this._nested.SkipWhile(n => n.Run.Id != run.Id).Skip(1).TakeWhile(n => n.Level > level).Count();
            var cancel = run.IsFinished ? "Delete" : "Cancel and delete";
            var with = children == 0 ? string.Empty : $" and the {children} child run{(children == 1 ? string.Empty : "s")} it started";
            return $"{cancel} run {run.Id}{with}, with {(children == 0 ? "its" : "their")} step logs? This can't be undone. The ticket's documents in the scratch directory are kept.";
        }
    }

    /// <summary>
    ///     Gets whether a step that has run can be replayed: the app isn't busy with the run. Finished runs can be
    ///     replayed too.
    /// </summary>
    public bool CanReplay => this.SelectedRun is not null && !this.IsBusyRunning;

    /// <summary>
    ///     Gets whether a note can be added to the run.
    /// </summary>
    public bool CanAddNote => this.SelectedRun is not null && !this.IsBusyRunning;

    /// <summary>
    ///     Gets the steps the run has been to, in order ("plan › test › fix › test #2"), once a loop or route has made
    ///     that differ from the step list; otherwise null.
    /// </summary>
    public string? PathText => this.SelectedRun is { } run &&
                               (run.Path.Any(v => v.Iteration > 1 || v.Branch is not null)
                                || run.Steps.Any(s => s.Status == StepStatus.Skipped || run.NotReached(s)))
        ? "Path: " + StepVisit.Describe(run.Path, " › ")
        : null;

    /// <summary>
    ///     Gets the steps waiting in the selected run's path runs, one line each, while its current step waits for
    ///     them; otherwise null.
    /// </summary>
    public string? PathStepsText => this.SelectedRun is { CurrentStep.Status: StepStatus.AwaitingPaths } run &&
                                      this.Shell.Runs.WaitingSteps(run) is { Count: > 0 } waiting
        ? string.Join("\n", waiting.Select(w => $"{w.Step.StepId} is {UiConverters.ToText(w.Step.Status)} in path run {w.Run.Id}"))
        : null;

    /// <summary>
    ///     Gets a line explaining what the run is waiting for.
    /// </summary>
    public string? Hint => this.SelectedRun switch
    {
        null => null,
        _ when this.IsBusyRunning => $"Working: {this.Live!.Activity}…",
        { Status: RunStatus.Done } => "Done. Replay a step to redo it from there.",
        { Status: RunStatus.Cancelled } => "Cancelled. Replay a step to pick it up again from there.",
        _ => this.CurrentStep?.Status switch
        {
            StepStatus.AwaitingInput when this.CurrentStep.OpenAskRound is not null =>
                "The AI paused to ask you something. Answer, and it carries on.",
            StepStatus.AwaitingInput => "Answer the questions below to start this step.",
            StepStatus.AwaitingPermission =>
                "The agent was refused some commands. Allow or deny each one, and it carries on.",
            StepStatus.AwaitingApproval when this.Questions.Count > 0 =>
                "Review the output, then approve it, or answer the AI's questions below to have it redo the step.",
            StepStatus.AwaitingApproval => "Review the output, then approve it or send it back with feedback.",
            StepStatus.AwaitingChoice =>
                "Several agents did this step. Use one's result (edit it first if you like), merge them, or send it back.",
            StepStatus.AwaitingUser when this.CurrentStep.Interactive is not null =>
                "Open the interactive session in a terminal. Mark the step done when you've finished.",
            StepStatus.AwaitingUser => "Your turn: follow the instructions, then mark the step done.",
            StepStatus.ChoosingAgent =>
                "Several agents can do this step. Pick the one to use (the main agent would otherwise choose).",
            StepStatus.AwaitingSession => "Waiting for the Claude Code session driving this run (afl run submit).",
            StepStatus.Failed => "The step failed. Retry it, or cancel the run.",
            StepStatus.Running => "Running elsewhere, or interrupted. Resume to pick it up.",
            StepStatus.AwaitingChildren =>
                "Waiting for its child runs. Open one below to move it on; this run carries on by itself once they finish.",
            StepStatus.AwaitingPaths =>
                "Several paths are running in parallel. Open one below to act on its step; this run routes on as each finishes.",
            _ => "Resume to continue.",
        },
    };

    /// <summary>
    ///     Gets the line saying which run started the selected one, or null when no run did.
    /// </summary>
    public string? ParentText => this.SelectedRun is { ParentRunId: { } parentId } run
        ? (run.IsPathRun ? $"Path {run.PathStepId} of run {parentId}" : $"Part of run {parentId}")
          + (this.Runs.FirstOrDefault(r => r.Id == parentId) is { } parent ? $" ({parent.Workflow.Name})" : string.Empty)
          + (run.ParentStepId is { } step ? $", step {step}" : string.Empty)
        : null;

    /// <summary>
    ///     Selects another run, e.g. a child run or the run that started this one.
    /// </summary>
    /// <param name="runId">The run's id.</param>
    /// <returns>True if the run was found.</returns>
    public bool OpenRun(string runId)
    {
        this.Focus(runId);
        return this.SelectedRun?.Id == runId;
    }

    /// <summary>
    ///     Selects the run that started the selected one.
    /// </summary>
    /// <returns>True if it was found.</returns>
    public bool OpenParent()
    {
        return this.SelectedRun?.ParentRunId is { } parentId && this.OpenRun(parentId);
    }

    /// <summary>
    ///     Shows or hides the child and path runs under a row.
    /// </summary>
    /// <param name="row">The row.</param>
    public void ToggleRow(AbstractRunListRowDataContext row)
    {
        if (!this._expanded.Remove(row.ExpandKey))
        {
            this._expanded.Add(row.ExpandKey);
        }
        else if (row is RunRowDataContext runRow && this.SelectedRun is { } selected && this.IsUnder(selected, runRow.Run.Id))
        {
            // Folding away the selected run selects the run it's folded under.
            this.SelectedRun = runRow.Run;
        }

        this.RebuildRows();
    }

    /// <summary>
    ///     Opens a step of the selected run in the popup over the graph.
    /// </summary>
    /// <param name="stepId">The step's id.</param>
    public void OpenStep(string stepId)
    {
        this.PopupStepId = stepId;
        this._popupOwnerId = this.SelectedRun?.Id;
        this.IsPopupOpen = true;
        this.ResolvePopup();
    }

    /// <summary>
    ///     Opens the step the run waits on in the popup: its current step, or, while its paths run, the first step
    ///     waiting in one of them.
    /// </summary>
    public void OpenCurrentStep()
    {
        if ((this.WaitingForUser()?.Step ?? this.CurrentStep) is { } step)
        {
            this.OpenStep(step.StepId);
        }
    }

    /// <summary>
    ///     Closes the step popup. It opens by itself again only when something new waits on the user.
    /// </summary>
    public void ClosePopup()
    {
        this.IsPopupOpen = false;
        this.ResolvePopup();
    }

    /// <summary>
    ///     Reads the document a step of the selected run saved, to preview it.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The document's text, or null when it has none or it can't be read (the reason is in ErrorMessage).</returns>
    public string? ReadArtifact(RunStep step)
    {
        string? text = null;
        if (this.SelectedRun is { } run && step.ArtifactPath is { } path)
        {
            this.Try(() => text = File.ReadAllText(Path.Combine(run.RepoPath, path)));
        }

        return text;
    }

    /// <summary>
    ///     Switches to the timeline, scrolled to the popup's step.
    /// </summary>
    public void ShowInTimeline()
    {
        var stepId = this.PopupStepId;
        this.ClosePopup();
        this.SelectedTab = 1;
        if (stepId is not null)
        {
            this.StepFocused?.Invoke(this, stepId);
        }
    }

    /// <summary>
    ///     Gets a hint shown when there are no runs.
    /// </summary>
    public string? EmptyMessage => this.Runs.Count == 0
        ? "No runs yet. Start one from the Issues page, or with: afl start <ticket>"
        : this.Rows.Count == 0 && this.IsFiltered
            ? "No runs match the filter."
            : null;

    /// <summary>
    ///     Continues the run until it next needs the user.
    /// </summary>
    public void Resume()
    {
        this.Drive("Resume", (engine, id, progress, ct) => engine.DriveAsync(id, progress, ct));
    }

    /// <summary>
    ///     Approves the current step and continues.
    /// </summary>
    public void Approve()
    {
        if (this.CheckInputs() is { } answers)
        {
            this.Drive("Approve", (engine, id, progress, ct) => engine.ApproveAsync(id, answers, progress, ct));
        }
    }

    /// <summary>
    ///     Answers the questions the current step is waiting on and continues.
    /// </summary>
    public void Answer()
    {
        if (this.CheckInputs() is { } answers)
        {
            this.Drive("Answer", (engine, id, progress, ct) => engine.AnswerAsync(id, answers, progress, ct));
        }
    }

    /// <summary>
    ///     Sends the current step back with feedback: the AI re-runs it, or the user's step reopens, with the feedback.
    /// </summary>
    /// <param name="feedback">What should change.</param>
    public void Reject(string feedback)
    {
        this.Drive("Reject", (engine, id, progress, ct) => engine.RejectAsync(id, feedback, progress, ct));
    }

    /// <summary>
    ///     Sends the current step back with the answers to its open questions (and the note, if any); the AI redoes it
    ///     with them, keeping its assumptions for the questions left blank.
    /// </summary>
    public void SendAnswers()
    {
        var answers = this.Questions.ToDictionary(q => q.Question.Number, q => q.Answer);
        var note = string.IsNullOrWhiteSpace(this.QuestionsNote) ? null : this.QuestionsNote;
        if (answers.Values.All(string.IsNullOrWhiteSpace) && note is null)
        {
            this.ErrorMessage = "Answer at least one question, or add a note.";
            return;
        }

        this.Drive("Send answers", (engine, id, progress, ct) => engine.AnswerQuestionsAsync(id, answers, note, progress, ct));
    }

    /// <summary>
    ///     Carries on a step paused for permission with the user's decisions: allowed rules are given to the agent (and
    ///     saved, for always), and it is told what it may now do.
    /// </summary>
    public void Permit()
    {
        if (!this.CanContinuePermit)
        {
            this.ErrorMessage = "Allow or deny every request first.";
            return;
        }

        var choices = this.PermissionRequests.Select(r => r.ToChoice()).ToList();
        this.Drive("Permit", (engine, id, progress, ct) => engine.PermitAsync(id, choices, false, progress, ct));
    }

    /// <summary>
    ///     Takes the output of a step paused for permission as it is, without allowing anything; the refusals are noted.
    /// </summary>
    public void AcceptWithoutPermissions()
    {
        this.Drive("Accept output", (engine, id, progress, ct) => engine.PermitAsync(id, [], true, progress, ct));
    }

    /// <summary>
    ///     Uses one agent's result (as edited, if it was) as the current step's output and continues.
    /// </summary>
    /// <param name="candidate">The agent's result.</param>
    public void Choose(CandidateDataContext candidate)
    {
        var agent = candidate.Agent;
        var output = candidate.EditedOutput;
        this.Drive("Choose", (engine, id, progress, ct) => engine.ChooseAsync(id, agent, output, progress, ct));
    }

    /// <summary>
    ///     Has an agent merge the current step's results and continues.
    /// </summary>
    /// <param name="agent">The agent that merges; the step's merge agent when null.</param>
    /// <param name="note">What to keep in mind when merging, if anything.</param>
    public void Merge(string? agent, string? note)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        this.Drive("Merge", (engine, id, progress, ct) => engine.MergeAsync(id, agent, text, progress, ct));
    }

    /// <summary>
    ///     Has the picked agent do the current step, which is waiting for a choice, and continues.
    /// </summary>
    public void UseAgent()
    {
        if (this.PickedAgent is not { } agent)
        {
            this.ErrorMessage = "Pick an agent first.";
            return;
        }

        this.Drive("Use agent", (engine, id, progress, ct) =>
            engine.UseAgentAsync(id, agent, null, "Picked on the Runs page", AgentConstants.ChosenByUser, progress, ct));
    }

    /// <summary>
    ///     Replays a step that has run: the run rewinds to it, its earlier attempts are kept in its history, and it runs
    ///     again (resuming the AI conversation with the feedback, when given). Every later step is redone too.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="feedback">What to change, or null to run it fresh.</param>
    public void Replay(RunStep step, string? feedback)
    {
        var stepId = step.StepId;
        var text = string.IsNullOrWhiteSpace(feedback) ? null : feedback.Trim();
        this.Drive("Replay", (engine, id, progress, ct) => engine.ReplayAsync(id, stepId, text, progress, ct));
    }

    /// <summary>
    ///     Describes what replaying a step does, for the confirmation.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The description.</returns>
    public string DescribeReplay(RunStep step)
    {
        var later = this.SelectedRun is { } run ? run.Steps.Skip(run.Steps.IndexOf(step) + 1).Count(s => s.HasRun) : 0;
        var redo = later > 0 ? $" The {later} step(s) after it that have run will be redone too." : string.Empty;
        return $"Replay '{step.Name}'?{redo} Optionally say what to change; leave it blank to run the step fresh.";
    }

    /// <summary>
    ///     Marks the current user or interactive step done and continues.
    /// </summary>
    /// <param name="note">An optional note, used as the step's output.</param>
    public void MarkDone(string? note)
    {
        if (this.CheckInputs() is { } answers)
        {
            this.Drive("Mark done", (engine, id, progress, ct) => engine.CompleteAsync(id, note, answers, progress, ct));
        }
    }

    /// <summary>
    ///     Checks the input form, marking each invalid value.
    /// </summary>
    /// <returns>The answers by input id, or null when a value is invalid.</returns>
    public Dictionary<string, string>? CheckInputs()
    {
        var answers = new Dictionary<string, string>();
        var valid = true;
        foreach (var input in this.Inputs)
        {
            if (!input.TryResolve(out var answer))
            {
                valid = false;
            }
            else if (answer is not null)
            {
                answers[input.Input.Id] = answer;
            }
        }

        return valid ? answers : null;
    }

    /// <summary>
    ///     Re-runs the failed step and continues.
    /// </summary>
    public void Retry()
    {
        this.Drive("Retry", (engine, id, progress, ct) => engine.RetryAsync(id, progress, ct));
    }

    /// <summary>
    ///     Stops the app's in-flight call. The interrupted step is marked failed so it can be retried.
    /// </summary>
    /// <returns>A task that completes when it has stopped.</returns>
    public Task StopAsync()
    {
        return this.SelectedRun is null ? Task.CompletedTask : this.Shell.Driver.StopAsync(this.SelectedRun.Id);
    }

    /// <summary>
    ///     Cancels the run.
    /// </summary>
    /// <returns>True if it succeeded.</returns>
    public bool Cancel()
    {
        return this.SelectedRun is { } run && this.Try(() => this.Shell.Driver.Engine.Cancel(run.Id));
    }

    /// <summary>
    ///     Deletes the run, its logs and the child runs it started, cancelling it first if it's still going.
    /// </summary>
    /// <returns>True if it succeeded.</returns>
    public bool Delete()
    {
        if (this.SelectedRun is not { } run || !this.Try(() =>
            {
                var engine = this.Shell.Driver.Engine;
                if (!run.IsFinished)
                {
                    engine.Cancel(run.Id);
                }

                engine.Delete(run.Id);
            }))
        {
            return false;
        }

        this.Reload();
        return true;
    }

    /// <summary>
    ///     Adds a note to the current step.
    /// </summary>
    /// <param name="note">The note.</param>
    /// <returns>True if it succeeded.</returns>
    public bool AddNote(string note)
    {
        return this.SelectedRun is { } run && this.Try(() => this.Shell.Driver.Engine.AddNote(run.Id, note));
    }

    /// <summary>
    ///     Gives the selected run a name of its own.
    /// </summary>
    /// <param name="title">The name; blank to show the ticket's title again.</param>
    /// <returns>True if it succeeded.</returns>
    public bool Rename(string title)
    {
        return this.SelectedRun is { } run && this.TryAndReload(() => this.Shell.Driver.Engine.Rename(run.Id, title));
    }

    /// <summary>
    ///     Gets the selected run's tags as they're edited: with commas between them.
    /// </summary>
    public string TagsInput => string.Join(", ", this.SelectedRun?.Tags ?? []);

    /// <summary>
    ///     Replaces the selected run's tags.
    /// </summary>
    /// <param name="text">The tags, with commas between them.</param>
    /// <returns>True if it succeeded.</returns>
    public bool SetTags(string text)
    {
        return this.SelectedRun is { } run && this.TryAndReload(() => this.Shell.Driver.Engine.SetTags(run.Id, text.Split(',')));
    }

    /// <summary>
    ///     Lists the folders the selected run can be filed in: its repo's.
    /// </summary>
    /// <returns>The folder paths, sorted.</returns>
    public IReadOnlyList<string> FolderChoices()
    {
        return this.SelectedRun is { } run ? this.Shell.Driver.Engine.ListFolders(run.RepoId) : [];
    }

    /// <summary>
    ///     Fills the run menu's Tags and Move to submenus for the selected run, as the menu opens.
    /// </summary>
    public void UpdateRunMenu()
    {
        if (this.SelectedRun is not { } run)
        {
            this.RunTagChoices = [];
            this.RunFolderChoices = [];
        }
        else
        {
            // Every tag the listed runs have, so a run can be given one another run already has.
            var tags = this.Runs.SelectMany(r => r.Tags).Distinct(RunLabels.Comparer).Order(StringComparer.OrdinalIgnoreCase);
            this.RunTagChoices =
            [
                .. tags.Select(t => new MenuChoice(t, t, run.Tags.Contains(t, RunLabels.Comparer), MenuItemToggleType.CheckBox)),
                MenuChoice.New("Edit tags…"),
            ];
            this.RunFolderChoices = run.ParentRunId is not null
                ? []
                :
                [
                    new MenuChoice("(top level)", string.Empty, run.Folder is null, MenuItemToggleType.Radio),
                    .. this.FolderChoices().Select(f => new MenuChoice(f, f, RunLabels.Comparer.Equals(f, run.Folder), MenuItemToggleType.Radio)),
                    MenuChoice.New("New folder…"),
                ];
        }

        this.RaisePropertyChanged(nameof(this.RunTagChoices));
        this.RaisePropertyChanged(nameof(this.RunFolderChoices));
    }

    /// <summary>
    ///     Puts a tag on the selected run, or takes it off if the run has it.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>True if it succeeded.</returns>
    public bool ToggleTag(string tag)
    {
        if (this.SelectedRun is not { } run)
        {
            return false;
        }

        var has = run.Tags.Contains(tag, RunLabels.Comparer);
        return this.TryAndReload(() => this.Shell.Driver.Engine.Tag(run.Id, has ? [] : [tag], has ? [tag] : []));
    }

    /// <summary>
    ///     Files the selected run in a folder, creating it if it's new, and opens the folder so the run stays in sight.
    /// </summary>
    /// <param name="folder">The folder's path; blank for the top level.</param>
    /// <returns>True if it succeeded.</returns>
    public bool MoveToFolder(string? folder)
    {
        return this.SelectedRun is { } run &&
               this.TryAndReload(() => this.ExpandFolder(this.Shell.Driver.Engine.MoveToFolder(run.Id, folder).Folder));
    }

    /// <summary>
    ///     Creates a folder in the selected folder, or at the top level when no folder is selected, and opens it.
    /// </summary>
    /// <param name="name">The folder's name (or a path of names).</param>
    /// <returns>True if it succeeded.</returns>
    public bool CreateFolder(string name)
    {
        var path = this.SelectedFolder is { } parent ? parent.Folder + RunLabels.FolderSeparator + name : name;
        return this.Repo is { } repo && this.TryAndReload(() => this.ExpandFolder(this.Shell.Driver.Engine.CreateFolder(repo.Id, path)));
    }

    /// <summary>
    ///     Renames or moves the selected folder, with what's in it.
    /// </summary>
    /// <param name="newFolder">Its new path.</param>
    /// <returns>True if it succeeded.</returns>
    public bool RenameFolder(string newFolder)
    {
        return this.Repo is { } repo && this.SelectedFolder is { } folder && this.TryAndReload(() =>
        {
            this.Shell.Driver.Engine.RenameFolder(repo.Id, folder.Folder, newFolder);
            this.ExpandFolder(RunLabels.NormalizeFolder(newFolder));
        });
    }

    /// <summary>
    ///     Removes the selected folder, moving what's in it up a level.
    /// </summary>
    /// <returns>True if it succeeded.</returns>
    public bool RemoveFolder()
    {
        return this.Repo is { } repo && this.SelectedFolder is { } folder &&
               this.TryAndReload(() => this.Shell.Driver.Engine.RemoveFolder(repo.Id, folder.Folder));
    }

    /// <summary>
    ///     Opens the current interactive step in a new system terminal.
    /// </summary>
    /// <returns>True if it succeeded.</returns>
    public bool OpenTerminal()
    {
        if (this.SelectedRun is not { CurrentStep.Interactive: { } launch } run)
        {
            return false;
        }

        var scriptPath = Path.ChangeExtension(this.Shell.Runs.LogPath(run, run.CurrentStep.StepId), null) + ".interactive";
        return this.Try(() => TerminalLauncherRegistry.Instance.ForCurrentPlatform().Open(launch, scriptPath));
    }

    /// <summary>
    ///     Reloads the runs and selects one, e.g. a run just started from the Issues page.
    /// </summary>
    /// <param name="runId">The run id.</param>
    public void Focus(string runId)
    {
        this.Reload();
        if (this.Runs.FirstOrDefault(r => r.Id == runId) is { } run)
        {
            this.ExpandTo(run, true);
            this.RebuildRows();
            this.SelectedRun = run;
        }
    }

    /// <summary>
    ///     Empties the search and unpicks every state and tag, listing every run again.
    /// </summary>
    public void ClearFilters()
    {
        // Each clear lists the runs again; the last one does it with everything cleared.
        this.Filter.Clear();
        this.TagFilter.Clear();
    }

    /// <inheritdoc />
    protected override void Load()
    {
        var runId = this.SelectedRun?.Id;
        var runs = this.Shell.Runs.List(this.Repo?.Id);
        this.Runs.Clear();

        // Child and path runs follow the run that started them, folded under it until it is expanded.
        this._nested = RunStore.Nest(runs, r => r.Id, r => r.ParentRunId);
        foreach (var (run, _) in this._nested)
        {
            this.Runs.Add(run);
        }

        var created = this.Repo is { } repo ? this.Shell.Driver.Engine.ListFolders(repo.Id) : [];
        this.Folders = RunLabels.WithParents(created.Concat(this._nested.Where(n => n.Level == 0).Select(n => n.Run.Folder)));

        // A newly selected run's folders are opened; folders the user closed on the selected run stay closed.
        var selected = this.Runs.FirstOrDefault(r => r.Id == runId) ?? this.Runs.FirstOrDefault();
        if (selected is not null)
        {
            this.ExpandTo(selected, selected.Id != runId);
        }

        this.RebuildRows();
        this.SelectedRun = selected;
    }

    /// <summary>
    ///     Rebuilds the rows if they changed while held still, once nothing holds them any more.
    /// </summary>
    private void CatchUpRows()
    {
        if (this._rowsStale && !this.IsRowMenuOpen && !this.IsRowMenuPending)
        {
            this._rowsStale = false;
            this.RebuildRows();
        }
    }

    /// <summary>
    ///     Rebuilds the list's rows from the folders and the nested runs the filters keep, leaving out those in a
    ///     collapsed folder or under a collapsed run.
    /// </summary>
    private void RebuildRows()
    {
        if (this.IsRowMenuOpen || this.IsRowMenuPending)
        {
            this._rowsStale = true;
            return;
        }

        // Clearing the rows clears the list's selection, so the selected folder is found again by its path.
        var selectedFolder = this.SelectedFolder?.Folder;
        this._rebuildingRows = true;
        try
        {
            // Each tree is a run started directly, with the runs it started; it's kept whole when any run in it has
            // every picked tag.
            var trees = new List<List<(Run Run, int Level)>>();
            foreach (var node in this._nested)
            {
                if (node.Level == 0 || trees.Count == 0)
                {
                    trees.Add([]);
                }

                trees[^1].Add(node);
            }

            var filter = this.Filter.ToFilter(this.TagFilter.Tags);
            var kept = filter.IsEmpty ? trees : trees.Where(t => t.Any(n => filter.Matches(n.Run))).ToList();
            this.TagFilter.Update(kept.SelectMany(t => t).Select(n => (IEnumerable<string>)n.Run.Tags));
            var unstated = filter.WithoutStates();
            this.Filter.Update(this._nested.Select(n => n.Run).Where(unstated.Matches));

            this.Rows.Clear();
            this.AddFolderRows(null, 0, kept, !filter.IsEmpty);
        }
        finally
        {
            this._rebuildingRows = false;
        }

        this.SyncSelectedRow(selectedFolder);
        this.RaisePropertiesChanged(FolderProperties);
        this.RaisePropertyChanged(nameof(this.EmptyMessage));
        this.RaisePropertyChanged(nameof(this.IsFiltered));
    }

    /// <summary>
    ///     Lists the runs again when the filter changes, first opening the runs and folders the runs it matches are
    ///     folded under, so they show.
    /// </summary>
    private void OnFilterEdited()
    {
        var filter = this.Filter.ToFilter(this.TagFilter.Tags);
        if (!filter.IsEmpty)
        {
            foreach (var run in this.Runs.Where(filter.Matches))
            {
                this.ExpandTo(run, true);
            }
        }

        this.RebuildRows();
    }

    /// <summary>
    ///     Adds the rows of what's in a folder: its folders (and, when one is expanded, what's in that), then its runs.
    /// </summary>
    /// <param name="folder">The folder, or null for the top level.</param>
    /// <param name="level">How deep its contents are nested.</param>
    /// <param name="trees">The listed runs, each with the runs it started.</param>
    /// <param name="filtered">Whether the filter is narrowing the list, so folders with no listed run are left out.</param>
    private void AddFolderRows(string? folder, int level, List<List<(Run Run, int Level)>> trees, bool filtered)
    {
        foreach (var sub in this.Folders.Where(f => RunLabels.Comparer.Equals(RunLabels.ParentOf(f), folder)))
        {
            var inside = trees.Where(t => RunLabels.IsWithin(t[0].Run.Folder, sub)).ToList();
            if (filtered && inside.Count == 0)
            {
                continue;
            }

            var hasSubfolders = this.Folders.Any(f => RunLabels.Comparer.Equals(RunLabels.ParentOf(f), sub));
            var expanded = this._expanded.Contains(FolderRowDataContext.KeyOf(sub));
            this.Rows.Add(new FolderRowDataContext(sub, level, [.. inside.SelectMany(t => t.Select(n => n.Run))], inside.Count,
                hasSubfolders, expanded));
            if (expanded)
            {
                this.AddFolderRows(sub, level + 1, trees, filtered);
            }
        }

        foreach (var tree in trees.Where(t => RunLabels.Comparer.Equals(t[0].Run.Folder, folder)))
        {
            this.AddRunRows(tree, level);
        }
    }

    /// <summary>
    ///     Adds the rows of a run and the runs it started, leaving out those under a collapsed run.
    /// </summary>
    /// <param name="tree">The run and the runs it started, in nested order.</param>
    /// <param name="offset">How deep the run is nested in folders.</param>
    private void AddRunRows(List<(Run Run, int Level)> tree, int offset)
    {
        int? foldedBelow = null;
        for (var i = 0; i < tree.Count; i++)
        {
            var (run, level) = tree[i];
            if (foldedBelow is { } folded && level > folded)
            {
                continue;
            }

            foldedBelow = null;
            var descendants = tree.Skip(i + 1).TakeWhile(n => n.Level > level).Select(n => n.Run).ToList();
            var expanded = this._expanded.Contains(RunRowDataContext.KeyOf(run.Id));
            this.Rows.Add(new RunRowDataContext(run, level + offset, descendants, expanded));
            if (descendants.Count > 0 && !expanded)
            {
                foldedBelow = level;
            }
        }
    }

    /// <summary>
    ///     Selects the selected run's row, or none when it is folded away; or keeps the selected folder's row.
    /// </summary>
    /// <param name="folder">The folder to keep selected, if it's listed; null to select the run's row.</param>
    private void SyncSelectedRow(string? folder)
    {
        var runId = this.SelectedRun?.Id;
        AbstractRunListRowDataContext? row = folder is null
            ? null
            : this.Rows.OfType<FolderRowDataContext>().FirstOrDefault(r => RunLabels.Comparer.Equals(r.Folder, folder));
        row ??= this.RunRows.FirstOrDefault(r => r.Run.Id == runId);
        if (!ReferenceEquals(row, this.SelectedRow))
        {
            this._rebuildingRows = true;
            this.SelectedRow = row;
            this._rebuildingRows = false;
        }
    }

    /// <summary>
    ///     Expands every run a run is folded under, and optionally the folders it's filed in, so it shows in the list.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="folders">Whether to open its folders too.</param>
    private void ExpandTo(Run run, bool folders)
    {
        var seen = new HashSet<string>();
        var top = run;
        for (var parentId = run.ParentRunId; parentId is not null && seen.Add(parentId);)
        {
            this._expanded.Add(RunRowDataContext.KeyOf(parentId));
            top = this.Runs.FirstOrDefault(r => r.Id == parentId) ?? top;
            parentId = this.Runs.FirstOrDefault(r => r.Id == parentId)?.ParentRunId;
        }

        if (folders)
        {
            this.ExpandFolder(top.Folder);
        }
    }

    /// <summary>
    ///     Opens a folder and the folders it's in.
    /// </summary>
    /// <param name="folder">The folder, or null for the top level.</param>
    private void ExpandFolder(string? folder)
    {
        for (var f = folder; f is not null; f = RunLabels.ParentOf(f))
        {
            this._expanded.Add(FolderRowDataContext.KeyOf(f));
        }
    }

    /// <summary>
    ///     Makes a change to the runs or their folders and, if it worked, lists them again straight away (the change to
    ///     the repo's folders isn't watched, and the runs' watcher can lag).
    /// </summary>
    /// <param name="change">The change.</param>
    /// <returns>True if it succeeded.</returns>
    private bool TryAndReload(Action change)
    {
        if (!this.Try(change))
        {
            return false;
        }

        this.Reload();
        return true;
    }

    /// <summary>
    ///     Gets whether a run is a child or path run, at any depth, of another.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="ancestorId">The other run's id.</param>
    /// <returns>True if it is under it.</returns>
    private bool IsUnder(Run run, string ancestorId)
    {
        var seen = new HashSet<string>();
        for (var parentId = run.ParentRunId; parentId is not null && seen.Add(parentId);)
        {
            if (parentId == ancestorId)
            {
                return true;
            }

            parentId = this.Runs.FirstOrDefault(r => r.Id == parentId)?.ParentRunId;
        }

        return false;
    }

    /// <summary>
    ///     Opens the popup by itself when the selected run newly waits on the user (another run was picked, or its step,
    ///     status, attempt or questions changed), closes it when another run was picked, and finds its step again.
    /// </summary>
    private void RefreshPopup()
    {
        var runId = this.SelectedRun?.Id;
        var runChanged = runId != this._popupOwnerId;
        var waiting = this.WaitingForUser();
        var key = waiting is { } w
            ? $"{w.Run.Id}/{w.Step.StepId}/{w.Step.Status}/{w.Step.Attempts}/{w.Step.AskRounds.Count}"
            : null;
        if (waiting is { } target && (runChanged || key != this._popupKey))
        {
            this._popupKey = key;
            this.OpenStep(target.Step.StepId);
            return;
        }

        if (runChanged)
        {
            this._popupOwnerId = runId;
            this.IsPopupOpen = false;
        }

        this.ResolvePopup();
    }

    /// <summary>
    ///     Finds the popup's step in the selected run, or in the path run it waits in, after the run reloads.
    /// </summary>
    private void ResolvePopup()
    {
        this._popupRun = null;
        RunStep? step = null;
        if (this.IsPopupOpen && this.PopupStepId is { } stepId && this.SelectedRun is { } run)
        {
            var inPath = run.CurrentStep?.Status == StepStatus.AwaitingPaths
                ? this.Shell.Runs.WaitingSteps(run).FirstOrDefault(w => w.Step.StepId == stepId && w.Run.Id != run.Id)
                : default;
            (this._popupRun, step) = inPath.Run is not null ? (inPath.Run, inPath.Step) : (run, run.StepOf(stepId));
        }

        this.PopupStep = step;
        this.RaisePropertiesChanged(PopupProperties);
    }

    /// <summary>
    ///     Finds the step the selected run waits on a person for: its current step, or, while its paths run, the first
    ///     step waiting in one of its path runs.
    /// </summary>
    /// <returns>The run holding the step, and the step; null when nothing waits on the user.</returns>
    private (Run Run, RunStep Step)? WaitingForUser()
    {
        if (this.SelectedRun is not { IsFinished: false, CurrentStep: { } current } run)
        {
            return null;
        }

        if (current.WaitsForUser)
        {
            return (run, current);
        }

        return current.Status == StepStatus.AwaitingPaths
               && this.Shell.Runs.WaitingSteps(run).FirstOrDefault(w => w.Step.WaitsForUser) is { Run: not null } waiting
            ? waiting
            : null;
    }

    /// <inheritdoc />
    protected override void OnDisposed()
    {
        this.WatchLive(false);
        this.Graph.StepClicked -= this.OnGraphStepClicked;
        this._graphOwner.Dispose();
    }

    /// <summary>
    ///     Shows the selected run in the graph: its workflow, when the run changes, and how it's going.
    /// </summary>
    private void RefreshGraph()
    {
        if (this.SelectedRun is not { } run)
        {
            this._graphRunId = null;
            this.Graph.Overlay = null;
            return;
        }

        if (this._graphRunId != run.Id)
        {
            this._graphRunId = run.Id;
            this._graphOwner.View(run.Workflow);
        }

        this.Graph.Overlay = new RunOverlay(run);
    }

    /// <summary>
    ///     Opens a step clicked in the graph in the popup.
    /// </summary>
    /// <param name="sender">The graph.</param>
    /// <param name="stepId">The step's id.</param>
    private void OnGraphStepClicked(object? sender, string stepId)
    {
        this.OpenStep(stepId);
    }

    /// <summary>
    ///     Starts an engine call on the selected run.
    /// </summary>
    /// <param name="activity">What the call does.</param>
    /// <param name="work">The call.</param>
    private void Drive(string activity, Func<RunEngine, string, IProgress<ExecutorEvent>, CancellationToken, Task<Run>> work)
    {
        if (this.SelectedRun is not { } run)
        {
            return;
        }

        this.Try(() => this.Shell.Driver.Start(run.Id, activity, (engine, progress, ct) => work(engine, run.Id, progress, ct)));
        this.WatchLive();
        this.RaisePropertiesChanged(StateProperties);
    }

    /// <summary>
    ///     Rebuilds the input form when the selected run moves to another step or state, keeping it otherwise.
    /// </summary>
    private void RefreshInputs()
    {
        var run = this.SelectedRun;
        var state = run?.CurrentStep;
        var key = state is null ? null : $"{run!.Id}/{state.StepId}/{state.Status}/{state.AskRounds.Count}";
        if (key == this._inputsKey)
        {
            return;
        }

        this._inputsKey = key;
        this.Inputs.Clear();
        if (run is null || state is null)
        {
            return;
        }

        var definition = run.DefinitionOf(state);
        if (!StepHandlerRegistry.Instance.Contains(definition.Type))
        {
            return;
        }

        foreach (var input in StepHandlerRegistry.Instance.For(definition).InputsAskedNow(run, state))
        {
            if (StepInputTypeRegistry.Instance.Contains(input.Type))
            {
                this.Inputs.Add(new StepInputDataContext(input));
            }
        }
    }

    /// <summary>
    ///     Rebuilds the candidate tabs when the selected run moves to another step or state, keeping edits otherwise.
    /// </summary>
    private void RefreshCandidates()
    {
        var run = this.SelectedRun;
        var state = run?.CurrentStep;
        var key = state is null ? null : $"{run!.Id}/{state.StepId}/{state.Status}/{state.Attempts}";
        if (key == this._candidatesKey)
        {
            return;
        }

        this._candidatesKey = key;
        this.Candidates.Clear();
        if (state is { Status: StepStatus.AwaitingChoice, Candidates: { } candidates })
        {
            foreach (var candidate in candidates)
            {
                this.Candidates.Add(new CandidateDataContext(candidate));
            }
        }

        this.SelectedCandidate = this.Candidates.FirstOrDefault(c => c.CanUse) ?? this.Candidates.FirstOrDefault();
        this.PickedAgent = state is { Status: StepStatus.ChoosingAgent, AgentChoice: { } choice } ? choice.Candidates.FirstOrDefault() : null;
        this.RaisePropertyChanged(nameof(MergeAgents));
    }

    /// <summary>
    ///     Reads the open questions again when the selected run moves to another step, state or attempt, keeping the
    ///     answers typed otherwise.
    /// </summary>
    private void RefreshQuestions()
    {
        var run = this.SelectedRun;
        var state = run?.CurrentStep;
        var key = state is null ? null : $"{run!.Id}/{state.StepId}/{state.Status}/{state.Attempts}";
        if (key == this._questionsKey)
        {
            return;
        }

        this._questionsKey = key;
        this.Questions.Clear();
        this.QuestionsNote = string.Empty;
        foreach (var question in state?.OpenQuestions ?? [])
        {
            this.Questions.Add(new OpenQuestionDataContext(question));
        }
    }

    /// <summary>
    ///     Reads the permission requests again when the selected run moves to another step, state or round, keeping the
    ///     decisions made otherwise.
    /// </summary>
    private void RefreshPermissions()
    {
        var run = this.SelectedRun;
        var state = run?.CurrentStep;
        var key = state is null ? null : $"{run!.Id}/{state.StepId}/{state.Status}/{state.PermissionRounds}";
        if (key == this._permissionsKey)
        {
            return;
        }

        this._permissionsKey = key;
        foreach (var request in this.PermissionRequests)
        {
            request.PropertyChanged -= this.OnPermissionRequestChanged;
        }

        this.PermissionRequests.Clear();
        foreach (var request in state?.OpenPermissionRequests ?? [])
        {
            var row = new PermissionRequestDataContext(request);
            row.PropertyChanged += this.OnPermissionRequestChanged;
            this.PermissionRequests.Add(row);
        }
    }

    /// <summary>
    ///     Updates whether the step can carry on when a permission decision changes.
    /// </summary>
    /// <param name="sender">The request.</param>
    /// <param name="e">The change.</param>
    private void OnPermissionRequestChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PermissionRequestDataContext.IsReady))
        {
            this.RaisePropertyChanged(nameof(this.CanContinuePermit));
        }
    }

    /// <summary>
    ///     Gets whether the selected run's current step has a status and the app isn't busy with the run.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>True if so.</returns>
    private bool IsIdleAt(StepStatus status)
    {
        return !this.IsBusyRunning && this.SelectedRun is { IsFinished: false } && this.CurrentStep?.Status == status;
    }

    /// <summary>
    ///     The live run whose state changes are watched.
    /// </summary>
    private LiveRun? _watched;

    /// <summary>
    ///     Watches the selected run's live state so the actions update when an engine call starts or ends.
    /// </summary>
    /// <param name="watch">False to only stop watching.</param>
    private void WatchLive(bool watch = true)
    {
        if (this._watched is not null)
        {
            this._watched.PropertyChanged -= this.OnLivePropertyChanged;
        }

        this._watched = watch ? this.Live : null;
        if (this._watched is not null)
        {
            this._watched.PropertyChanged += this.OnLivePropertyChanged;
        }
    }

    /// <summary>
    ///     Refreshes the actions when the live run starts or stops working.
    /// </summary>
    /// <param name="sender">The live run.</param>
    /// <param name="e">The change.</param>
    private void OnLivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveRun.IsActive))
        {
            this.RaisePropertiesChanged(StateProperties);
        }
    }
}
