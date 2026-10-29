using System.Collections.ObjectModel;
using System.Text.Json;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Workflows;
using ReactiveUI;

using Afl.UI.Flow;

namespace Afl.UI.DataContexts;

/// <summary>
///     The workflow builder. Lists every workflow (or, with the other <see cref="Editor" />, every common step) the
///     selected repo can see (its own, the global library and the built-ins), edits a draft copy of one with live
///     validation, and saves it as JSON to the global library (<c>~/.afl/workflows/</c>, <c>~/.afl/steps/</c>) or the
///     repo (<c>&lt;repo&gt;/.afl/…</c>) through the editor's layered library.
/// </summary>
public partial class LibraryPageDataContext : AbstractPageDataContext
{
    /// <summary>
    ///     The id each step had when the step ids were last handed out, to follow renames.
    /// </summary>
    private readonly Dictionary<StepDraftDataContext, string> _knownIds = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     Whether <see cref="Edit" /> is filling the draft, so the edits it makes aren't counted.
    /// </summary>
    private bool _loadingDraft;

    /// <summary>
    ///     The draft's undo and redo history.
    /// </summary>
    private readonly DraftHistory<WorkflowDefinition> _history = new(JsonConstants.Options);

    /// <summary>
    ///     Whether the draft has changed since the history last recorded it.
    /// </summary>
    private bool _editPending;

    /// <summary>
    ///     How deep inside <see cref="Change" /> the edits being made are, so a compound edit is recorded once.
    /// </summary>
    private int _changeDepth;

    /// <summary>
    ///     Whether <see cref="SelectListingQuietly" /> is changing the selection.
    /// </summary>
    private bool _selectingQuietly;

    /// <summary>
    ///     Whether <see cref="ClearFilters" /> is changing several filters, so the list is filtered once at the end.
    /// </summary>
    private bool _applying;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LibraryPageDataContext" /> class.
    /// </summary>
    /// <param name="shell">The shell that owns the stores.</param>
    public LibraryPageDataContext(ShellDataContext shell, AbstractLibraryEditor editor) : base(shell)
    {
        this.Editor = editor;
        this.Flow = new FlowGraphDataContext(this);
        this.OnDispose<EventHandler>(h => shell.Agents.Changed += h, h => shell.Agents.Changed -= h, (_, _) => this.RefreshAgentChoices());
        this.OnDispose<EventHandler>(h => shell.Servers.Changed += h, h => shell.Servers.Changed -= h, (_, _) => this.RefreshAgentChoices());
        this.OnDispose<EventHandler>(h => shell.Settings.Changed += h, h => shell.Settings.Changed -= h, (_, _) => this.RefreshAgentChoices());
        this.Parameters.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(this.PartsHeader));
        this.Requires.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(this.PartsHeader));
        this.Skills.Edited += (_, _) => this.OnDraftEdited();
        this.Tags.Edited += (_, _) => this.ApplyFilters();
        this.InitializeAuthoring(shell);
        this.Reload();
    }

    /// <summary>
    ///     The common steps the steps can use, as the selected repo sees them.
    /// </summary>
    private IReadOnlyList<CommonStepDefinition> _commonSteps = [];

    /// <summary>
    ///     The workflows the steps and requirements can refer to, as the selected repo sees them.
    /// </summary>
    private IReadOnlyList<WorkflowDefinition> _workflows = [];

    /// <summary>
    ///     The workflows the draft can't refer to: itself, and those that lead back to it.
    /// </summary>
    private IReadOnlySet<string> _excluded = new HashSet<string>();

    /// <summary>
    ///     The ids of the skills the draft and its steps can pick from.
    /// </summary>
    private IReadOnlyList<string> _skills = [];

    /// <summary>
    ///     Gets the flow graph of the draft's steps, which the step area shows and edits.
    /// </summary>
    public FlowGraphDataContext Flow { get; }

    /// <summary>
    ///     Gets what the page edits: workflows, common steps or skills.
    /// </summary>
    public AbstractLibraryEditor Editor { get; }

    /// <summary>
    ///     Gets whether a draft has a list of steps (a common step has exactly one).
    /// </summary>
    public bool HasSteps => this.Editor.HasSteps;

    /// <summary>
    ///     Gets whether a draft can require other workflows to have finished first.
    /// </summary>
    public bool HasRequirements => this.Editor.HasRequirements;

    /// <summary>
    ///     Gets whether a draft takes parameters (a skill doesn't).
    /// </summary>
    public bool HasParameters => this.Editor.HasParameters;

    /// <summary>
    ///     Gets whether a draft can name skills its AI steps all get.
    /// </summary>
    public bool HasDefaultSkills => this.Editor.HasDefaultSkills;

    /// <summary>
    ///     Gets whether a draft is markdown content and files (a skill) rather than steps.
    /// </summary>
    public bool HasContent => this.Editor.HasContent;

    /// <summary>
    ///     Gets whether the steps area is shown.
    /// </summary>
    public bool ShowsStepArea => !this.Editor.HasContent;

    /// <summary>
    ///     Gets whether the draft has exactly one step, so its editor is headed "Step".
    /// </summary>
    public bool ShowsSingleStepHeader => !this.Editor.HasSteps && !this.Editor.HasContent;

    /// <summary>
    ///     Gets whether the step properties are shown: always for a common step's one step, and beside the flow while a
    ///     step is selected and its properties are open (<see cref="FlowGraphDataContext.IsPropertiesOpen" />).
    /// </summary>
    public bool ShowsStepPanel => !this.HasSteps || (this.Flow.IsPropertiesOpen && this.SelectedStep is not null);

    /// <summary>
    ///     Gets the header over the draft's parameters (and requirements, where it can have them), with their counts.
    /// </summary>
    public string PartsHeader => this.HasRequirements
        ? $"Parameters ({this.Parameters.Count}) · Requires ({this.Requires.Count})"
        : $"Parameters ({this.Parameters.Count})";

    /// <summary>
    ///     Gets what the New button asks for, e.g. "New workflow".
    /// </summary>
    public string NewLabel => $"New {this.Editor.Noun}";

    /// <summary>
    ///     The label of the scope filter choice that shows every scope.
    /// </summary>
    public const string AllScopes = "All scopes";

    /// <summary>
    ///     Every document in every scope, before the search and filters.
    /// </summary>
    private IReadOnlyList<LibraryEntry> _allListings = [];

    /// <summary>
    ///     Gets the documents the search and filters let through, from every scope, including ones a narrower scope
    ///     overrides: ones found by name first, then ones found in their contents.
    /// </summary>
    public ObservableCollection<LibraryEntry> Listings { get; } = [];

    /// <summary>
    ///     Gets or sets the search text: every word must be in a document's id, name, description or tags, or (with
    ///     <see cref="SearchContents" />) its contents.
    /// </summary>
    public string? SearchText
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.ApplyFilters();
        }
    }

    /// <summary>
    ///     Gets or sets whether the search looks in the contents (prompts, instructions, files) too.
    /// </summary>
    public bool SearchContents
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.ApplyFilters();
        }
    } = true;

    /// <summary>
    ///     Gets the scope filter's choices: every scope, or one.
    /// </summary>
    public IReadOnlyList<string> ScopeChoices { get; } = [AllScopes, .. Enum.GetNames<WorkflowScope>()];

    /// <summary>
    ///     Gets or sets the scope filter: <see cref="AllScopes" /> or a <see cref="WorkflowScope" /> name.
    /// </summary>
    public string ScopeFilter
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value ?? AllScopes);
            this.ApplyFilters();
        }
    } = AllScopes;

    /// <summary>
    ///     Gets the tag filter: a listing must have every picked tag.
    /// </summary>
    public TagFilterDataContext Tags { get; } = new();

    /// <summary>
    ///     Gets whether the search or a filter is narrowing the list.
    /// </summary>
    public bool IsFiltered => !this.Query().IsEmpty;

    /// <summary>
    ///     Gets how much of the library the list shows, e.g. "3 of 11".
    /// </summary>
    public string FilterSummary => this.IsFiltered ? $"{this.Listings.Count} of {this._allListings.Count}" : $"{this._allListings.Count}";

    /// <summary>
    ///     Gets or sets the listing being edited. Selecting one replaces the draft.
    /// </summary>
    public LibraryEntry? SelectedListing
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                this.RaiseAndSetIfChanged(ref field, value);
                this.RaisePropertiesChanged(nameof(this.CanDelete), nameof(this.CanImprove));
                if (value is not null && !this._selectingQuietly)
                {
                    this.CloseAuthoring();
                    this.Edit(value.Draft, value.Scope == WorkflowScope.BuiltIn ? WorkflowScope.Global : value.Scope);
                }
            }
        }
    }

    /// <summary>
    ///     Gets whether a draft is open.
    /// </summary>
    public bool HasDraft
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertiesChanged(nameof(this.ShowsEditor), nameof(this.ShowsHint));
        }
    }

    /// <summary>
    ///     Gets or sets the draft's id, which is also its file name.
    /// </summary>
    public string DraftId
    {
        get;
        set
        {
            this.SetDraftField(ref field, value);
            this.ShareReferences();
        }
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the draft's name.
    /// </summary>
    public string DraftName
    {
        get;
        set => this.SetDraftField(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the draft's description.
    /// </summary>
    public string? DraftDescription
    {
        get;
        set => this.SetDraftField(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the step the draft starts at, or null for the first listed step.
    /// </summary>
    public string? DraftStart
    {
        get;
        set => this.SetDraftField(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the draft's tags, separated by commas.
    /// </summary>
    public string DraftTags
    {
        get;
        set => this.SetDraftField(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets the parameters the draft takes, filled in with <c>with</c> wherever it's used.
    /// </summary>
    public ObservableCollection<ParameterDraftDataContext> Parameters { get; } = [];

    /// <summary>
    ///     Gets the workflows the draft requires to have finished on the ticket first.
    /// </summary>
    public ObservableCollection<RequirementDraftDataContext> Requires { get; } = [];

    /// <summary>
    ///     Gets the skills the draft's AI and interactive steps all get, picked from every skill there is (or typed,
    ///     such as a <c>{{params.x}}</c> template).
    /// </summary>
    public PickListDataContext Skills { get; } = new();

    /// <summary>
    ///     Gets or sets a skill draft's instructions, in markdown.
    /// </summary>
    public string DraftContent
    {
        get;
        set => this.SetDraftField(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets a skill draft's supporting files.
    /// </summary>
    public ObservableCollection<SkillFileDraftDataContext> Files { get; } = [];

    /// <summary>
    ///     Gets the draft's steps, in order.
    /// </summary>
    public ObservableCollection<StepDraftDataContext> Steps { get; } = [];

    /// <summary>
    ///     Gets or sets the step being edited.
    /// </summary>
    public StepDraftDataContext? SelectedStep
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.ShowsStepPanel));
            this.RefreshVariables();
            this.Flow.Refresh();
        }
    }

    /// <summary>
    ///     Gets the template variables the selected step can use, as they'd be typed, e.g. <c>{{ticket.title}}</c>.
    /// </summary>
    public ObservableCollection<string> Variables { get; } = [];

    /// <summary>
    ///     Gets the draft's validation errors.
    /// </summary>
    public ObservableCollection<string> Errors { get; } = [];

    /// <summary>
    ///     Gets the draft's flow warnings: things it allows but that are probably not meant, such as steps that can
    ///     never run. They don't stop it being saved.
    /// </summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>
    ///     Gets the draft's flow as last validated, or null if it couldn't be read as one.
    /// </summary>
    internal FlowMachine? Machine { get; private set; }

    /// <summary>
    ///     Gets whether the draft is valid and can be saved.
    /// </summary>
    public bool IsValid => this.HasDraft && this.Errors.Count == 0;

    /// <summary>
    ///     Gets whether the draft has unsaved edits.
    /// </summary>
    public bool IsDirty
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the scopes the draft can be saved to: global, and the repo when one is selected.
    /// </summary>
    public IReadOnlyList<WorkflowScope> SaveScopes =>
        this.HasRepo ? [WorkflowScope.Global, WorkflowScope.Repo] : [WorkflowScope.Global];

    /// <summary>
    ///     Gets or sets the scope the draft is saved to.
    /// </summary>
    public WorkflowScope SaveScope
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.SavePath));
        }
    } = WorkflowScope.Global;

    /// <summary>
    ///     Gets the file the draft would be saved to.
    /// </summary>
    public string? SavePath
    {
        get
        {
            string? path = null;
            try
            {
                path = this.HasDraft ? this.Editor.PathFor(this.Library(), this.DraftId, this.SaveScope) : null;
            }
            catch (Exception)
            {
                // No repo for a repo-scoped path; the scope list stops that being chosen.
            }

            return path;
        }
    }

    /// <summary>
    ///     Gets whether the selected listing can be deleted (built-ins can't).
    /// </summary>
    public bool CanDelete => this.SelectedListing is { Scope: not WorkflowScope.BuiltIn };

    /// <summary>
    ///     Starts a new document with one step.
    /// </summary>
    /// <param name="id">Its id.</param>
    public void New(string id)
    {
        var workflow = this.Editor.NewDraft(id.Trim(), NewStep(1));
        this.SelectListingQuietly(null);
        this.Edit(workflow, this.HasRepo ? WorkflowScope.Repo : WorkflowScope.Global);
        foreach (var step in this.Steps)
        {
            step.MarkAsNew();
        }

        this._history.MarkUnsaved();
        this.IsDirty = true;
    }

    /// <summary>
    ///     Copies the draft under a new id.
    /// </summary>
    /// <param name="id">The new id.</param>
    public void Duplicate(string id)
    {
        if (!this.HasDraft)
        {
            return;
        }

        var copy = this.BuildDraft();
        copy.Id = id.Trim();
        copy.Name += " (copy)";
        this.SelectListingQuietly(null);
        this.Edit(copy, this.SaveScope);
        this._history.MarkUnsaved();
        this.IsDirty = true;
    }

    /// <summary>
    ///     Adds a step after the selected one, between it and the steps it went on to, and selects it; with nothing
    ///     selected the new step isn't connected to anything yet.
    /// </summary>
    public void AddStep()
    {
        this.Change(() =>
        {
            var after = this.SelectedStep;
            var index = after is null ? this.Steps.Count : this.Steps.IndexOf(after) + 1;
            var step = this.InsertStep(NewStep(this.Steps.Count + 1), index, true);
            this.LinkAfter(after, step);
        });
    }

    /// <summary>
    ///     Puts a new step after another, between it and the steps it went on to.
    /// </summary>
    /// <param name="after">The step it follows, or null to leave it unconnected.</param>
    /// <param name="step">The new step.</param>
    public void LinkAfter(StepDraftDataContext? after, StepDraftDataContext step)
    {
        if (after is null || ReferenceEquals(after, step))
        {
            return;
        }

        this.Change(() =>
        {
            var id = after.Id.Trim();
            foreach (var arrow in after.Arrows.Where(a => a.To != id).ToList())
            {
                after.RemoveArrow(arrow);
                step.AddArrow(arrow.ToDefinition());
            }

            after.AddArrow(step.Id.Trim());
        });
    }

    /// <summary>
    ///     Adds a step at an index and selects it.
    /// </summary>
    /// <param name="definition">The step.</param>
    /// <param name="index">Where it is listed in the draft's steps (which doesn't change where it goes).</param>
    /// <param name="placeholders">
    ///     Whether its name, prompt and instructions are only starting values, cleared if it's switched to a common step
    ///     or an include before they're edited.
    /// </param>
    /// <returns>The new step.</returns>
    public StepDraftDataContext InsertStep(AbstractStepDefinition definition, int index, bool placeholders)
    {
        var step = this.Track(new StepDraftDataContext(definition));
        if (placeholders)
        {
            step.MarkAsNew();
        }

        this.Change(() =>
        {
            this.Steps.Insert(Math.Clamp(index, 0, this.Steps.Count), step);
            this.SelectedStep = step;
            this.OnDraftEdited();
        });
        return step;
    }

    /// <summary>
    ///     Removes the selected step. The steps that went on to it go on to where it went instead when that was one
    ///     step, and otherwise lose that arrow.
    /// </summary>
    public void RemoveStep()
    {
        if (this.SelectedStep is not { } step)
        {
            return;
        }

        this.RemoveSteps([step]);
    }

    /// <summary>
    ///     Removes several steps as one edit, each the way <see cref="RemoveStep" /> removes one, and selects the step
    ///     listed where the first of them was.
    /// </summary>
    /// <param name="steps">The steps.</param>
    public void RemoveSteps(IReadOnlyCollection<StepDraftDataContext> steps)
    {
        var removing = steps.Where(this.Steps.Contains).ToList();
        if (removing.Count == 0)
        {
            return;
        }

        this.Change(() =>
        {
            var index = removing.Min(this.Steps.IndexOf);
            foreach (var step in removing)
            {
                var id = step.Id.Trim();
                var onward = step.Targets.Where(t => t != id).ToList();
                var onTo = onward.Count == 1 ? onward[0] : null;
                step.Edited -= this.OnStepEdited;
                this.Steps.Remove(step);
                foreach (var source in this.Steps)
                {
                    source.RemoveTarget(id, onTo);
                }

                if (this.DraftStart == id)
                {
                    this.DraftStart = onTo;
                }
            }

            this.SelectedStep = this.Steps.Count == 0 ? null : this.Steps[Math.Min(index, this.Steps.Count - 1)];
            this.OnDraftEdited();
        });
    }

    /// <summary>
    ///     Adds steps at the end of the list as one edit, e.g. pasted ones, and selects the first.
    /// </summary>
    /// <param name="definitions">The steps.</param>
    /// <returns>Their drafts.</returns>
    public IReadOnlyList<StepDraftDataContext> AddSteps(IReadOnlyList<AbstractStepDefinition> definitions)
    {
        var added = definitions.Select(d => this.Track(new StepDraftDataContext(d))).ToList();
        if (added.Count == 0)
        {
            return added;
        }

        this.Change(() =>
        {
            foreach (var step in added)
            {
                this.Steps.Add(step);
            }

            this.SelectedStep = added[0];
            this.OnDraftEdited();
        });
        return added;
    }

    /// <summary>
    ///     Makes a step the one the workflow starts at.
    /// </summary>
    /// <param name="step">The step.</param>
    public void MakeStart(StepDraftDataContext step)
    {
        var id = step.Id.Trim();
        this.Change(() => this.DraftStart = this.Steps.FirstOrDefault()?.Id.Trim() == id ? null : id);
    }

    /// <summary>
    ///     Returns whether a step can be made conditional: arrows lead forward into it, so there's somewhere to go
    ///     round it from.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>True if it has arrows in.</returns>
    public bool CanMakeConditional(StepDraftDataContext step)
    {
        return this.ArrowsInto(step).Count > 0;
    }

    /// <summary>
    ///     Makes a step run only when a condition doesn't hold: every forward arrow into it also needs <c>not</c> the
    ///     condition, and each step those arrows leave gets an arrow <c>when</c> it holds to where the step goes on
    ///     success (an arrow already there with a condition fires on either), so the run goes round it. The steps after it still start, since the arrow into the step dies.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="condition">When to go round it.</param>
    /// <returns>How many arrows were changed or added.</returns>
    public int MakeConditional(StepDraftDataContext step, ConditionDefinition condition)
    {
        return this.Change(() => this.GoRound(step, condition));
    }

    /// <summary>
    ///     Does the work of <see cref="MakeConditional" />.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="condition">When to go round it.</param>
    /// <returns>How many arrows were changed or added.</returns>
    private int GoRound(StepDraftDataContext step, ConditionDefinition condition)
    {
        var id = step.Id.Trim();
        var arrowsIn = this.ArrowsInto(step);
        var onwards = step.Arrows
            .Where(a => a.On is ArrowConstants.OnSuccess or ArrowConstants.OnAlways && a.To != ArrowConstants.End && !this.IsBack(id, a.To))
            .Select(a => a.To)
            .Distinct()
            .ToList();
        var changed = 0;
        foreach (var (from, arrow) in arrowsIn)
        {
            var definition = arrow.ToDefinition();
            var skip = new ConditionDefinition { Kind = StepConstants.ConditionNot, Conditions = [condition] };
            var already = definition.When ?? (definition.Until is { } until ? new ConditionDefinition { Kind = StepConstants.ConditionNot, Conditions = [until] } : null);
            definition.When = already is null ? skip : new ConditionDefinition { Kind = StepConstants.ConditionAll, Conditions = [already, skip] };
            definition.Until = null;
            from.ReplaceArrow(arrow, definition);
            changed++;
            foreach (var to in onwards.Where(t => t != from.Id.Trim()))
            {
                // An arrow already there is widened to fire on this condition too; one with no condition always fires.
                var bypass = from.Arrows.FirstOrDefault(a => a.To == to && a.On == arrow.On);
                if (bypass is null)
                {
                    from.AddArrow(new ArrowDefinition { To = to, On = definition.On, When = condition });
                    changed++;
                }
                else if (bypass.ToDefinition() is { When: { } when } existing)
                {
                    existing.When = new ConditionDefinition { Kind = StepConstants.ConditionAny, Conditions = [when, condition] };
                    from.ReplaceArrow(bypass, existing);
                    changed++;
                }
            }
        }

        this.OnDraftEdited();
        return changed;
    }

    /// <summary>
    ///     Gets whether there's an edit to undo.
    /// </summary>
    public bool CanUndo => this.HasDraft && (this._editPending || this._history.CanUndo);

    /// <summary>
    ///     Gets whether there's an undone edit to redo.
    /// </summary>
    public bool CanRedo => this.HasDraft && !this._editPending && this._history.CanRedo;

    /// <summary>
    ///     Makes an edit (or several) as one step of the undo history.
    /// </summary>
    /// <param name="edit">The edit.</param>
    public void Change(Action edit)
    {
        this.Change(() =>
        {
            edit();
            return true;
        });
    }

    /// <summary>
    ///     Makes an edit (or several) as one step of the undo history.
    /// </summary>
    /// <typeparam name="T">What the edit returns.</typeparam>
    /// <param name="edit">The edit.</param>
    /// <returns>What it returned.</returns>
    public T Change<T>(Func<T> edit)
    {
        this._changeDepth++;
        try
        {
            return edit();
        }
        finally
        {
            if (--this._changeDepth == 0)
            {
                this.CommitEdit();
            }
        }
    }

    /// <summary>
    ///     Records the draft in the undo history if it changed since it was last recorded. Edits typed into a field are
    ///     recorded when it loses focus, rather than one per keystroke.
    /// </summary>
    public void CommitEdit()
    {
        if (!this._editPending || !this.HasDraft)
        {
            return;
        }

        this._editPending = false;
        this._history.Record(this.BuildDraft());
        this.RaiseHistoryChanged();
    }

    /// <summary>
    ///     Puts the draft back as it was before the last edit.
    /// </summary>
    /// <returns>True if there was one to undo.</returns>
    public bool Undo()
    {
        this.CommitEdit();
        return this.Restore(this._history.Undo());
    }

    /// <summary>
    ///     Makes the last undone edit again.
    /// </summary>
    /// <returns>True if there was one to redo.</returns>
    public bool Redo()
    {
        this.CommitEdit();
        return this.Restore(this._history.Redo());
    }

    /// <summary>
    ///     Returns the id of the step the draft starts at: <see cref="DraftStart" />, or the first listed step.
    /// </summary>
    public string? StartStepId => this.DraftStart is { Length: > 0 } start ? start : this.Steps.FirstOrDefault()?.Id.Trim();

    /// <summary>
    ///     Adds a parameter to the draft.
    /// </summary>
    public void AddParameter()
    {
        this.Parameters.Add(this.TrackParameter(new ParameterDraftDataContext(
            ParameterDraftDataContext.NewParameter(this.Parameters.Select(p => p.Id)))));
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Removes a parameter from the draft.
    /// </summary>
    /// <param name="parameter">The parameter.</param>
    public void RemoveParameter(ParameterDraftDataContext parameter)
    {
        parameter.Edited -= this.OnPartEdited;
        this.Parameters.Remove(parameter);
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Adds a requirement on another workflow to the draft.
    /// </summary>
    public void AddRequirement()
    {
        var workflow = this._workflows.FirstOrDefault(w => !this._excluded.Contains(w.Id))?.Id ?? string.Empty;
        this.Requires.Add(this.TrackRequirement(new RequirementDraftDataContext(new WorkflowRequirement { Workflow = workflow })));
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Removes a requirement from the draft.
    /// </summary>
    /// <param name="requirement">The requirement.</param>
    public void RemoveRequirement(RequirementDraftDataContext requirement)
    {
        requirement.Edited -= this.OnPartEdited;
        this.Requires.Remove(requirement);
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Adds a supporting file to a skill draft.
    /// </summary>
    public void AddFile()
    {
        this.Files.Add(this.TrackFile(new SkillFileDraftDataContext(SkillFileDraftDataContext.NewPath(this.Files.Select(f => f.Path)), string.Empty)));
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Removes a supporting file from a skill draft.
    /// </summary>
    /// <param name="file">The file.</param>
    public void RemoveFile(SkillFileDraftDataContext file)
    {
        file.Edited -= this.OnPartEdited;
        this.Files.Remove(file);
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Saves the draft as JSON to <see cref="SaveScope" />.
    /// </summary>
    /// <returns>The file written, or null if it failed.</returns>
    public string? Save()
    {
        if (this.IsEditingAiDraft)
        {
            return this.SaveAiDraft();
        }

        this.CommitEdit();
        string? path = null;
        var workflow = this.BuildDraft();
        var scope = this.SaveScope;
        if (!this.Try(() => path = this.Editor.Save(this.Library(), workflow, scope)))
        {
            return null;
        }

        this._loadingDraft = true;
        try
        {
            this.DraftTags = string.Join(", ", workflow.Tags ?? []);
        }
        finally
        {
            this._loadingDraft = false;
        }

        this._history.MarkSaved();
        this.IsDirty = false;
        this.Load();
        this.SelectListingQuietly(this.Listings.FirstOrDefault(l => l.Id == workflow.Id && l.Scope == scope));
        return path;
    }

    /// <summary>
    ///     Saves the selected step as a common step, to <see cref="SaveScope" />, and bases the step on it.
    /// </summary>
    /// <param name="id">The common step's id.</param>
    /// <returns>The file written, or null if it failed.</returns>
    public string? SaveStepAsCommon(string id)
    {
        if (this.SelectedStep is not { CanSaveAsCommon: true } step)
        {
            return null;
        }

        string? path = null;
        var definition = step.ToDefinition();
        var common = new CommonStepDefinition
        {
            Id = id.Trim(),
            Name = string.IsNullOrWhiteSpace(definition.Name) ? id.Trim() : definition.Name,
            Step = Clone(definition),
        };
        common.Step.Id = common.Id;
        if (!this.Try(() => path = this.Library().Steps.Save(common, this.SaveScope)))
        {
            return null;
        }

        this.LoadReferences();
        step.Basis = StepDraftDataContext.UseChoice;
        step.Use = common.Id;
        return path;
    }

    /// <summary>
    ///     Replaces the selected step, if it uses a common step or includes a workflow, with the steps it stands for,
    ///     written out in full so they can be edited here.
    /// </summary>
    /// <returns>True if the step was replaced.</returns>
    public bool CopyStepIntoWorkflow()
    {
        if (this.SelectedStep is not { IsOwnStep: false } step)
        {
            return false;
        }

        (IReadOnlyList<AbstractStepDefinition> Steps, string? Start) expanded = ([], null);
        if (!this.Try(() => expanded = this.ExpandStep(step)))
        {
            return false;
        }

        var index = this.Steps.IndexOf(step);
        var id = step.Id.Trim();
        step.Edited -= this.OnStepEdited;
        this.Steps.RemoveAt(index);
        var added = expanded.Steps.Select(s => this.Track(new StepDraftDataContext(s))).ToList();
        for (var i = 0; i < added.Count; i++)
        {
            this.Steps.Insert(index + i, added[i]);
        }

        // Arrows to the step, and the start, now lead to where its steps begin.
        if (expanded.Start is { } start && start != id)
        {
            foreach (var other in this.Steps.Except(added))
            {
                other.RenameTarget(id, start);
            }

            if (this.DraftStart == id || (string.IsNullOrEmpty(this.DraftStart) && index == 0))
            {
                this.DraftStart = added.FirstOrDefault()?.Id.Trim() == start ? null : start;
            }
        }

        this.SelectedStep = added.FirstOrDefault();
        this.OnDraftEdited();
        return true;
    }

    /// <summary>
    ///     Deletes the selected workflow's file.
    /// </summary>
    /// <returns>True if it succeeded.</returns>
    public bool Delete()
    {
        if (this.SelectedListing is not { Scope: not WorkflowScope.BuiltIn } listing)
        {
            return false;
        }

        if (!this.Try(() => this.Editor.Delete(this.Library(), listing.Id, listing.Scope)))
        {
            return false;
        }

        this.SelectListingQuietly(null);
        this.CloseDraft();
        this.Load();
        return true;
    }

    /// <inheritdoc />
    protected override void OnRepoChanged()
    {
        // A draft may be repo-scoped, so start again from the new repo's listings.
        this.SelectListingQuietly(null);
        this.CloseDraft();
        this.RaisePropertyChanged(nameof(this.SaveScopes));
        base.OnRepoChanged();
    }

    /// <summary>
    ///     Opens the document with an id as the draft: the one in the narrowest scope, which is the one runs use. The
    ///     search and filters are cleared when they hide it.
    /// </summary>
    /// <param name="id">The document's id.</param>
    /// <returns>True if there is one with that id.</returns>
    public bool Open(string id)
    {
        if (this._allListings.Where(l => l.Id == id).MaxBy(l => l.Scope) is not { } wanted)
        {
            return false;
        }

        if (!this.Listings.Any(Matches))
        {
            this.ClearFilters();
        }

        this.SelectedListing = this.Listings.FirstOrDefault(Matches) ?? wanted;
        return true;

        bool Matches(LibraryEntry listing) => listing.Id == wanted.Id && listing.Scope == wanted.Scope;
    }

    /// <summary>
    ///     Clears the search and every filter.
    /// </summary>
    public void ClearFilters()
    {
        this._applying = true;
        try
        {
            this.SearchText = null;
            this.ScopeFilter = AllScopes;
            this.Tags.Clear();
        }
        finally
        {
            this._applying = false;
        }

        this.ApplyFilters();
    }

    /// <inheritdoc />
    protected override void Load()
    {
        var selected = this.SelectedListing;
        var library = this.Library();
        var problems = this.Editor.Problems(library);
        if (problems.Count > 0)
        {
            this.ErrorMessage = $"Skipped:\n{string.Join("\n", problems)}";
        }

        this.LoadReferences();
        this._allListings = this.Editor.List(library);
        this.ShowListings();

        var match = selected is null
            ? null
            : this.Listings.FirstOrDefault(l => l.Id == selected.Id && l.Scope == selected.Scope);
        if (match is not null || this.HasDraft || this.HasAuthoring)
        {
            this.SelectListingQuietly(match);
        }
        else
        {
            this.SelectedListing = this.Listings.FirstOrDefault();
        }

        this.LoadAiDrafts();
    }

    /// <summary>
    ///     Builds the query the search box and filters describe.
    /// </summary>
    /// <returns>The query.</returns>
    private LibraryQuery Query()
    {
        return new LibraryQuery
        {
            Text = this.SearchText,
            SearchContents = this.SearchContents,
            Tags = this.Tags.Tags,
            Scope = Enum.TryParse<WorkflowScope>(this.ScopeFilter, out var scope) ? scope : null,
        };
    }

    /// <summary>
    ///     Re-filters the list after the search or a filter changes, keeping the selection when it's still shown.
    /// </summary>
    private void ApplyFilters()
    {
        if (this._applying)
        {
            return;
        }

        var selected = this.SelectedListing;
        this.ShowListings();
        this.SelectListingQuietly(selected is null ? null : this.Listings.FirstOrDefault(l => l.Id == selected.Id && l.Scope == selected.Scope));
    }

    /// <summary>
    ///     Fills <see cref="Listings" /> with the entries the query lets through, noting where a search found each one.
    /// </summary>
    private void ShowListings()
    {
        var query = this.Query();
        var shown = query.IsEmpty
            ? this._allListings
            : query.Apply(this._allListings, l => l.Document, l => l.Scope).Select(m => m.Listing with { MatchNote = m.Match.Snippet }).ToList();
        this.Listings.Clear();
        foreach (var listing in shown)
        {
            this.Listings.Add(listing);
        }

        this.Tags.Update(this.Listings.Select(l => l.Tags));
        this.RaisePropertiesChanged(nameof(this.IsFiltered), nameof(this.FilterSummary));
    }

    /// <summary>
    ///     Creates a placeholder AI step, read-only until the author chooses otherwise.
    /// </summary>
    /// <param name="number">The step's position, for its id.</param>
    /// <returns>The step.</returns>
    private static AbstractStepDefinition NewStep(int number)
    {
        return new AiStepDefinition
        {
            Id = $"step-{number}",
            Name = $"Step {number}",
            Prompt = "Work on {{ticket.key}}: {{ticket.title}}\n\n{{ticket.body}}",
            PermissionMode = StepConstants.ReadOnlyPermissionMode,
        };
    }

    /// <summary>
    ///     Returns the library as the selected repo sees it.
    /// </summary>
    /// <returns>The library.</returns>
    internal WorkflowLibrary Library()
    {
        return this.Shell.Workflows(this.Repo);
    }

    /// <summary>
    ///     Opens a copy of a workflow as the draft.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="saveScope">The scope to save it to.</param>
    private void Edit(WorkflowDefinition workflow, WorkflowScope saveScope)
    {
        this.ForgetAuthoringDraft();
        this.LoadDraft(workflow);
        this.SaveScope = this.SaveScopes.Contains(saveScope) ? saveScope : WorkflowScope.Global;
        this.SelectedStep = this.Steps.FirstOrDefault();
        this.Flow.IsPropertiesOpen = false;
        this.Flow.ClosePicker();
        this.IsDirty = false;
        this.RefreshStepIds();
        this.Validate();
        this._editPending = false;
        this._history.Reset(this.BuildDraft());
        this.RaiseHistoryChanged();
    }

    /// <summary>
    ///     Shows a workflow in the flow graph without editing it, e.g. a run's on the Runs page: nothing is selected,
    ///     and it never counts as unsaved.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    internal void View(WorkflowDefinition workflow)
    {
        this.LoadDraft(workflow);
        this.SelectedStep = null;
        this.Flow.IsPropertiesOpen = false;
        this.IsDirty = false;
        this.RefreshStepIds();
        this.Validate();
    }

    /// <summary>
    ///     Fills the draft from a workflow, without counting it as an edit.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    private void LoadDraft(WorkflowDefinition workflow)
    {
        this.Untrack();
        this.HasDraft = true;
        this._loadingDraft = true;
        try
        {
            this.DraftId = workflow.Id;
            this.DraftName = workflow.Name;
            this.DraftDescription = workflow.Description;
            this.DraftTags = string.Join(", ", workflow.Tags ?? []);
            this.DraftStart = workflow.Start;
            this.Steps.Clear();
            foreach (var step in workflow.Steps)
            {
                this.Steps.Add(this.Track(new StepDraftDataContext(Clone(step))));
            }

            this.Parameters.Clear();
            foreach (var parameter in workflow.Parameters ?? [])
            {
                this.Parameters.Add(this.TrackParameter(new ParameterDraftDataContext(Clone(parameter))));
            }

            this.Requires.Clear();
            foreach (var requirement in workflow.Requires ?? [])
            {
                this.Requires.Add(this.TrackRequirement(new RequirementDraftDataContext(Clone(requirement))));
            }

            this.Skills.Load(workflow.Skills);
            var content = this.Editor.ContentOf(workflow);
            this.DraftContent = content?.Text ?? string.Empty;
            this.Files.Clear();
            foreach (var (path, text) in content?.Files ?? new Dictionary<string, string>())
            {
                this.Files.Add(this.TrackFile(new SkillFileDraftDataContext(path, text)));
            }
        }
        finally
        {
            this._loadingDraft = false;
        }
    }

    /// <summary>
    ///     Puts the draft back to a snapshot from the undo history, keeping the selected step and whether its
    ///     properties are open.
    /// </summary>
    /// <param name="snapshot">The snapshot, or null when there was none to go to.</param>
    /// <returns>True if there was one.</returns>
    private bool Restore(WorkflowDefinition? snapshot)
    {
        if (snapshot is null || !this.HasDraft)
        {
            return false;
        }

        var selected = this.SelectedStep?.Id.Trim();
        var propertiesOpen = this.Flow.IsPropertiesOpen;
        this.Flow.CloseArrow();
        this.Flow.CloseConditional();
        this.LoadDraft(snapshot);
        this.SelectedStep = this.Steps.FirstOrDefault(s => s.Id.Trim() == selected) ?? this.Steps.FirstOrDefault();
        this.Flow.IsPropertiesOpen = propertiesOpen;
        this.IsDirty = !this._history.IsAtSaved;
        this.RefreshStepIds();
        this.Validate();
        this._editPending = false;
        this.RaiseHistoryChanged();
        return true;
    }

    /// <summary>
    ///     Tells the undo and redo buttons the history changed.
    /// </summary>
    private void RaiseHistoryChanged()
    {
        this.RaisePropertiesChanged(nameof(this.CanUndo), nameof(this.CanRedo));
    }

    /// <summary>
    ///     Closes the draft.
    /// </summary>
    private void CloseDraft()
    {
        this.ForgetAuthoringDraft();
        this.Untrack();
        this.Steps.Clear();
        this.Parameters.Clear();
        this.Requires.Clear();
        this.Files.Clear();
        this.Skills.Load(null);
        this.SelectedStep = null;
        this.HasDraft = false;
        this.IsDirty = false;
        this._editPending = false;
        this._history.Clear();
        this.RaiseHistoryChanged();
        this.Errors.Clear();
        this.RaisePropertiesChanged(nameof(this.IsValid), nameof(this.SavePath));
    }

    /// <summary>
    ///     Builds the workflow the draft describes.
    /// </summary>
    /// <returns>The workflow.</returns>
    internal WorkflowDefinition BuildDraft()
    {
        var tags = AbstractLibraryDocument.NormalizeTags(this.DraftTags.Split(','));
        var draft = new WorkflowDefinition
        {
            Id = this.DraftId.Trim(),
            Name = this.DraftName.Trim(),
            Description = string.IsNullOrWhiteSpace(this.DraftDescription) ? null : this.DraftDescription.Trim(),
            Tags = tags.Count > 0 ? tags : null,
            Parameters = this.Parameters.Count > 0 ? this.Parameters.Select(p => p.ToDefinition()).ToList() : null,
            Requires = this.HasRequirements && this.Requires.Count > 0 ? this.Requires.Select(r => r.ToDefinition()).ToList() : null,
            Skills = this.HasDefaultSkills ? this.Skills.ToList() : null,
            Start = this.DraftStart is { Length: > 0 } start && this.Steps.FirstOrDefault()?.Id.Trim() != start ? start : null,
            Steps = this.Steps.Select(s => s.ToDefinition()).ToList(),
        };
        if (!this.HasContent)
        {
            return draft;
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in this.Files)
        {
            files[file.Path.Trim()] = file.Text;
        }

        return this.Editor.WithContent(draft, new DraftContent(this.DraftContent, files));
    }

    /// <summary>
    ///     Expands one composed step into the plain steps it stands for, keeping the draft's own <c>{{params.x}}</c>
    ///     as they are and pointing the ends of an include at the steps the step went on to.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The plain steps, and the id of the one that takes the step's place as the target of arrows.</returns>
    private (IReadOnlyList<AbstractStepDefinition> Steps, string? Start) ExpandStep(StepDraftDataContext step)
    {
        var definition = step.ToDefinition();
        var parameters = this.Parameters.Select(p => p.ToDefinition()).ToList();
        var targets = (definition.Arrows ?? []).Select(a => a.To)
            .Where(t => t.Length > 0 && t != ArrowConstants.End && t != definition.Id).Distinct().ToList();

        // Stand-ins for the steps it goes on to, so the include's ends can name them.
        List<AbstractStepDefinition> steps = [definition, .. targets.Select(t => new UserStepDefinition { Id = t, Name = t, Instructions = t })];
        var workflow = new WorkflowDefinition
        {
            Id = this.DraftId.Trim().Length > 0 ? this.DraftId.Trim() : "draft",
            Name = this.DraftName,
            Parameters = parameters.Count > 0 ? parameters : null,
            Steps = steps,
        };
        var keep = parameters.ToDictionary(p => p.Id, p => "{{params." + p.Id + "}}");
        var expanded = new WorkflowComposer(this.Library()).Expand(workflow, keep);
        var own = expanded.Steps.Where(s => !targets.Contains(s.Id)).ToList();
        return (own, expanded.Start ?? own.FirstOrDefault()?.Id);
    }

    /// <summary>
    ///     Reads the common steps and workflows the draft can refer to, and gives them to every part of the draft.
    /// </summary>
    private void LoadReferences()
    {
        var library = this.Library();
        this.Try(() =>
        {
            this._commonSteps = library.Steps.List().Select(l => l.Step).ToList();
            this._workflows = library.List().Select(l => l.Workflow).ToList();
            this._skills = library.Skills.List().Select(l => l.Skill.Id).ToList();
            this.Skills.SetChoices(this._skills);
        });
        this.ShareReferences();
    }

    /// <summary>
    ///     Gives every part of the draft the common steps and workflows it can refer to, leaving out the workflows
    ///     that would make a cycle.
    /// </summary>
    private void ShareReferences()
    {
        this._excluded = this.WorkflowsLeadingHere();
        this.Flow.SetLibrary(this._commonSteps, this._workflows, this._excluded);
        foreach (var step in this.Steps)
        {
            step.SetLibrary(this._commonSteps, this._workflows, this._excluded);
            step.SetSkills(this._skills);
        }

        foreach (var requirement in this.Requires)
        {
            requirement.SetWorkflows(this._workflows, this._excluded);
        }
    }

    /// <summary>
    ///     Returns the draft's own id and those of the workflows that lead back to it, which it can't refer to.
    /// </summary>
    /// <returns>The ids.</returns>
    private HashSet<string> WorkflowsLeadingHere()
    {
        var id = this.DraftId?.Trim() ?? string.Empty;
        if (!this.HasDraft || id.Length == 0)
        {
            return [];
        }

        var composer = new WorkflowComposer(this.Library());
        return [id, .. this._workflows.Where(w => composer.LeadsTo(w, id)).Select(w => w.Id)];
    }

    /// <summary>
    ///     Stops listening to every part of the draft.
    /// </summary>
    private void Untrack()
    {
        foreach (var step in this.Steps)
        {
            step.Edited -= this.OnStepEdited;
        }

        foreach (var parameter in this.Parameters)
        {
            parameter.Edited -= this.OnPartEdited;
        }

        foreach (var requirement in this.Requires)
        {
            requirement.Edited -= this.OnPartEdited;
        }

        foreach (var file in this.Files)
        {
            file.Edited -= this.OnPartEdited;
        }
    }

    /// <summary>
    ///     Subscribes to a skill file's edits.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <returns>The file.</returns>
    private SkillFileDraftDataContext TrackFile(SkillFileDraftDataContext file)
    {
        file.Edited += this.OnPartEdited;
        return file;
    }

    /// <summary>
    ///     Subscribes to a parameter's edits.
    /// </summary>
    /// <param name="parameter">The parameter.</param>
    /// <returns>The parameter.</returns>
    private ParameterDraftDataContext TrackParameter(ParameterDraftDataContext parameter)
    {
        parameter.Edited += this.OnPartEdited;
        return parameter;
    }

    /// <summary>
    ///     Gives a requirement the workflows it can pick and subscribes to its edits.
    /// </summary>
    /// <param name="requirement">The requirement.</param>
    /// <returns>The requirement.</returns>
    private RequirementDraftDataContext TrackRequirement(RequirementDraftDataContext requirement)
    {
        requirement.SetWorkflows(this._workflows, this._excluded);
        requirement.Edited += this.OnPartEdited;
        return requirement;
    }

    /// <summary>
    ///     Re-validates when a parameter or requirement changes.
    /// </summary>
    /// <param name="sender">The part.</param>
    /// <param name="e">The event.</param>
    private void OnPartEdited(object? sender, EventArgs e)
    {
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Sets a draft field and re-validates.
    /// </summary>
    /// <typeparam name="T">The field type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="propertyName">The property name.</param>
    private void SetDraftField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        this.RaisePropertyChanged(propertyName);
        this.OnDraftEdited();
    }

    /// <summary>
    ///     Subscribes to a step's edits.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The step.</returns>
    private StepDraftDataContext Track(StepDraftDataContext step)
    {
        step.AgentChoices = this.AgentChoices();
        var (capabilities, servers) = this.Catalog();
        step.SetCatalog(capabilities, servers);
        step.SetLibrary(this._commonSteps, this._workflows, this._excluded);
        step.SetSkills(this._skills);
        step.Edited += this.OnStepEdited;
        return step;
    }

    /// <summary>
    ///     Re-validates when a step changes.
    /// </summary>
    /// <param name="sender">The step.</param>
    /// <param name="e">The event.</param>
    private void OnStepEdited(object? sender, EventArgs e)
    {
        this.OnDraftEdited();
        this.RefreshVariables();
    }

    /// <summary>
    ///     Marks the draft dirty and re-validates it.
    /// </summary>
    private void OnDraftEdited()
    {
        if (this._loadingDraft || !this.HasDraft)
        {
            return;
        }

        this.IsDirty = true;
        if (!this._editPending)
        {
            this._editPending = true;
            this.RaiseHistoryChanged();
        }

        this.RefreshStepIds();
        this.Validate();
    }

    /// <summary>
    ///     Gives every step the current step ids, for its go-to and loop pickers, and the steps whose arrows lead into
    ///     it. A step whose id changed since last time has the arrows to it (and the start) follow it, unless the old or
    ///     new id is another step's.
    /// </summary>
    private void RefreshStepIds()
    {
        var ids = this.Steps.Select(s => s.Id.Trim()).Where(id => id.Length > 0).ToList();
        foreach (var step in this.Steps)
        {
            var id = step.Id.Trim();
            if (this._knownIds.TryGetValue(step, out var old) && old != id && old.Length > 0 && id.Length > 0 &&
                ids.Count(i => i == id) == 1 && !ids.Contains(old))
            {
                foreach (var other in this.Steps)
                {
                    other.RenameTarget(old, id);
                }

                if (this.DraftStart == old)
                {
                    this.DraftStart = id;
                }
            }

            this._knownIds[step] = id;
        }

        foreach (var gone in this._knownIds.Keys.Where(k => !this.Steps.Contains(k)).ToList())
        {
            this._knownIds.Remove(gone);
        }

        foreach (var step in this.Steps)
        {
            var id = step.Id.Trim();
            step.StepIds = ids;
            step.IncomingIds = this.Steps.Where(s => s != step && s.Targets.Contains(id)).Select(s => s.Id.Trim()).Distinct().ToList();
        }
    }

    /// <summary>
    ///     Validates the draft with the same rules the CLI and saving use, resolving what it uses against the library.
    /// </summary>
    private void Validate()
    {
        this.Errors.Clear();
        IReadOnlyList<string> errors;
        try
        {
            errors = this.Editor.Validate(this.Library(), this.BuildDraft());
        }
        catch (Exception ex)
        {
            errors = [ex.Message];
        }

        foreach (var error in errors)
        {
            this.Errors.Add(error);
        }

        this.Warnings.Clear();
        try
        {
            this.Machine = new FlowMachine(this.BuildDraft());
            foreach (var warning in this.Machine.Warnings())
            {
                this.Warnings.Add(warning);
            }
        }
        catch (Exception)
        {
            // A draft that can't be read as a flow yet has its errors listed already.
            this.Machine = null;
        }

        this.RaisePropertiesChanged(nameof(this.IsValid), nameof(this.SavePath));
        this.Flow.Refresh();
    }

    /// <summary>
    ///     Lists the variables available to the selected step: the ticket and repo, its own inputs, the outputs and
    ///     input answers of the steps that always come before it (and, in the optional form, of those that may), the
    ///     loop iteration, and every step's previous output.
    /// </summary>
    private void RefreshVariables()
    {
        this.Variables.Clear();
        if (this.SelectedStep is not { } step)
        {
            return;
        }

        var workflow = this.BuildDraft();
        var id = step.Id.Trim();
        if (workflow.Steps.FirstOrDefault(s => s.Id == id) is not { } current)
        {
            return;
        }

        IReadOnlyList<AbstractStepDefinition> earlier = [];
        IReadOnlyList<AbstractStepDefinition> possible = [];
        try
        {
            var machine = new FlowMachine(workflow);
            earlier = TemplateVariables.StepsBefore(machine, current);
            possible = TemplateVariables.StepsMaybeBefore(machine, current);
        }
        catch (Exception)
        {
            // A draft that can't be read as a flow yet offers only the variables every step has.
        }
        foreach (var name in TemplateVariables.Available(earlier, workflow.Steps, current))
        {
            this.Variables.Add("{{" + name + "}}");
        }

        foreach (var name in TemplateVariables.Available(possible).Except(TemplateVariables.Available([])))
        {
            this.Variables.Add("{{" + name + "?}}");
        }
    }

    /// <summary>
    ///     Selects a listing without replacing the draft.
    /// </summary>
    /// <param name="listing">The listing, or null.</param>
    private void SelectListingQuietly(LibraryEntry? listing)
    {
        this._selectingQuietly = true;
        try
        {
            this.SelectedListing = listing;
        }
        finally
        {
            this._selectingQuietly = false;
        }

        this.RaisePropertiesChanged(nameof(this.CanDelete), nameof(this.CanImprove));
    }

    /// <summary>
    ///     Deep-copies a part of a document so edits never touch the library's instance.
    /// </summary>
    /// <typeparam name="T">The part's type.</typeparam>
    /// <param name="value">The part.</param>
    /// <returns>The copy.</returns>
    private static T Clone<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonConstants.Options), JsonConstants.Options)!;
    }

    /// <summary>
    ///     Returns the agents a step can pick: the run's default, then every agent.
    /// </summary>
    /// <returns>The choices.</returns>
    private IReadOnlyList<string> AgentChoices()
    {
        IReadOnlyList<string> agents = [];
        this.Try(() => agents = this.Shell.Agents.All().Select(a => a.Id).ToList());
        return [StepDraftDataContext.RunAgentChoice, .. agents];
    }

    /// <summary>
    ///     Returns the capabilities and MCP servers a step can pick.
    /// </summary>
    /// <returns>The capability ids and server ids.</returns>
    private (IReadOnlyList<string> Capabilities, IReadOnlyList<string> Servers) Catalog()
    {
        IReadOnlyList<string> capabilities = [];
        IReadOnlyList<string> servers = [];
        this.Try(() =>
        {
            capabilities = CapabilityCatalog.For(this.Shell.Settings).All.Select(c => c.Id).ToList();
            servers = this.Shell.Servers.All().Select(s => s.Id).ToList();
        });
        return (capabilities, servers);
    }

    /// <summary>
    ///     Refreshes every step's agent, capability and server choices after agents, servers or settings change.
    /// </summary>
    private void RefreshAgentChoices()
    {
        var choices = this.AgentChoices();
        var (capabilities, servers) = this.Catalog();
        foreach (var step in this.Steps)
        {
            step.AgentChoices = choices;
            step.SetCatalog(capabilities, servers);
        }
    }

    /// <summary>
    ///     Returns the forward arrows into a step, with the steps they leave.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The arrows.</returns>
    private List<(StepDraftDataContext From, ArrowDraftDataContext Arrow)> ArrowsInto(StepDraftDataContext step)
    {
        var id = step.Id.Trim();
        return [.. this.Steps
            .Where(s => s != step)
            .SelectMany(s => s.Arrows.Where(a => a.To == id && !this.IsBack(s.Id.Trim(), id)).Select(a => (s, a)))];
    }

    /// <summary>
    ///     Returns whether an arrow goes back round a loop, by the flow as last validated.
    /// </summary>
    /// <param name="from">The step it leaves.</param>
    /// <param name="to">The step it goes to.</param>
    /// <returns>True if it goes back.</returns>
    private bool IsBack(string from, string to)
    {
        return from == to || (this.Machine?.IsBackEdge(from, to) ?? false);
    }
}
