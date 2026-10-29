using Afl.Core.Constants;
using Afl.Core.Workflows;

namespace Afl.UI.DataContexts;

/// <summary>
///     One document in the builder's list: a workflow or a common step, with where it was found.
/// </summary>
/// <param name="Id">The document's id.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Scope">Where it came from.</param>
/// <param name="Path">The file it was loaded from, or null for built-ins.</param>
/// <param name="Summary">What it holds, e.g. "4 steps".</param>
/// <param name="Draft">The document as the builder edits it: a workflow (a common step is one with a single step).</param>
/// <param name="Document">The document as saved, which searches look in.</param>
public record LibraryEntry(string Id, string Name, WorkflowScope Scope, string? Path, string Summary, WorkflowDefinition Draft,
    AbstractLibraryDocument Document)
{
    /// <summary>
    ///     Gets the document's tags.
    /// </summary>
    public IReadOnlyList<string> Tags => this.Document.Tags ?? [];

    /// <summary>
    ///     Gets whether the document has any tags.
    /// </summary>
    public bool HasTags => this.Tags.Count > 0;

    /// <summary>
    ///     Gets the tags as one line, e.g. "#review #planning".
    /// </summary>
    public string TagLine => string.Join(" ", this.Tags.Select(t => "#" + t));

    /// <summary>
    ///     Gets the line of the contents a search found, when it didn't find the entry by name; otherwise null.
    /// </summary>
    public string? MatchNote { get; init; }

    /// <summary>
    ///     Gets whether <see cref="MatchNote" /> is set.
    /// </summary>
    public bool HasMatchNote => this.MatchNote is not null;
}

/// <summary>
///     The text a document holds besides steps, edited in the builder's content box: a skill's instructions and files.
/// </summary>
/// <param name="Text">The markdown.</param>
/// <param name="Files">Supporting files, by relative path.</param>
public record DraftContent(string Text, IReadOnlyDictionary<string, string> Files);

/// <summary>
///     What the builder edits: workflows, or the common steps workflows use. The builder always edits a
///     <see cref="WorkflowDefinition" /> draft; an editor turns its documents into drafts and back, and says which parts
///     of the builder apply. A new kind of document is one subclass, and a
///     <see cref="Afl.UI.Pages.AbstractLibraryPage{TEditor}" /> subclass registering its page in the sidebar.
/// </summary>
public abstract class AbstractLibraryEditor
{
    /// <summary>
    ///     Gets the label of the builder's list, e.g. "Workflows".
    /// </summary>
    public abstract string Label { get; }

    /// <summary>
    ///     Gets what one document is called, e.g. "workflow".
    /// </summary>
    public abstract string Noun { get; }

    /// <summary>
    ///     Gets the kind of document the AI helper drafts for this editor (an <c>AuthoringKindRegistry</c> name).
    /// </summary>
    public abstract string AuthoringKind { get; }

    /// <summary>
    ///     Gets whether a document has a list of steps (otherwise exactly one).
    /// </summary>
    public virtual bool HasSteps => true;

    /// <summary>
    ///     Gets whether a document can require other workflows to have finished first.
    /// </summary>
    public virtual bool HasRequirements => false;

    /// <summary>
    ///     Gets whether a document takes parameters.
    /// </summary>
    public virtual bool HasParameters => true;

    /// <summary>
    ///     Gets whether a document can name skills every AI step of it gets.
    /// </summary>
    public virtual bool HasDefaultSkills => false;

    /// <summary>
    ///     Gets whether a document is markdown content (with files) rather than steps.
    /// </summary>
    public virtual bool HasContent => false;

    /// <summary>
    ///     Returns the content a draft holds, for a document with <see cref="HasContent" />.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <returns>The content, or null when the document has none.</returns>
    public virtual DraftContent? ContentOf(WorkflowDefinition draft)
    {
        return null;
    }

    /// <summary>
    ///     Returns a draft carrying the given content, for a document with <see cref="HasContent" />.
    /// </summary>
    /// <param name="draft">The draft the builder rebuilt from its fields.</param>
    /// <param name="content">The content.</param>
    /// <returns>The draft with the content.</returns>
    public virtual WorkflowDefinition WithContent(WorkflowDefinition draft, DraftContent content)
    {
        return draft;
    }

    /// <summary>
    ///     Lists every document in every scope, including ones a narrower scope overrides.
    /// </summary>
    /// <param name="library">The workflow library as the selected repo sees it.</param>
    /// <returns>The entries.</returns>
    public abstract IReadOnlyList<LibraryEntry> List(WorkflowLibrary library);

    /// <summary>
    ///     Returns the files skipped while listing.
    /// </summary>
    /// <param name="library">The workflow library.</param>
    /// <returns>The problems.</returns>
    public abstract IReadOnlyList<string> Problems(WorkflowLibrary library);

    /// <summary>
    ///     Starts a new document.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <param name="step">The step to start with.</param>
    /// <returns>The draft.</returns>
    public virtual WorkflowDefinition NewDraft(string id, AbstractStepDefinition step)
    {
        return new WorkflowDefinition { Id = id, Name = id, Steps = [step] };
    }

    /// <summary>
    ///     Checks a draft with the same rules saving uses.
    /// </summary>
    /// <param name="library">The workflow library, for the documents the draft refers to.</param>
    /// <param name="draft">The draft.</param>
    /// <returns>The problems; empty when valid.</returns>
    public abstract IReadOnlyList<string> Validate(WorkflowLibrary library, WorkflowDefinition draft);

    /// <summary>
    ///     Saves a draft.
    /// </summary>
    /// <param name="library">The workflow library.</param>
    /// <param name="draft">The draft.</param>
    /// <param name="scope">The scope to save to.</param>
    /// <returns>The file written.</returns>
    public abstract string Save(WorkflowLibrary library, WorkflowDefinition draft, WorkflowScope scope);

    /// <summary>
    ///     Deletes a document's file.
    /// </summary>
    /// <param name="library">The workflow library.</param>
    /// <param name="id">The id.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>True if a file was deleted.</returns>
    public abstract bool Delete(WorkflowLibrary library, string id, WorkflowScope scope);

    /// <summary>
    ///     Returns the file a document would be saved to.
    /// </summary>
    /// <param name="library">The workflow library.</param>
    /// <param name="id">The id.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The path.</returns>
    public abstract string PathFor(WorkflowLibrary library, string id, WorkflowScope scope);

    /// <summary>
    ///     Reads a document from a file outside the library, e.g. an AI draft, as a draft.
    /// </summary>
    /// <param name="library">The workflow library as the selected repo sees it.</param>
    /// <param name="path">The file.</param>
    /// <returns>The draft.</returns>
    public abstract WorkflowDefinition ReadExternal(WorkflowLibrary library, string path);

    /// <summary>
    ///     Writes a draft as the JSON its library saves.
    /// </summary>
    /// <param name="library">The workflow library as the selected repo sees it.</param>
    /// <param name="draft">The draft.</param>
    /// <returns>The JSON.</returns>
    public abstract string Serialize(WorkflowLibrary library, WorkflowDefinition draft);

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Label;
    }
}

/// <summary>
///     Base for an editor over one layered library: listing, saving, deleting and paths come from the library, and a
///     subclass converts its documents to and from drafts.
/// </summary>
/// <typeparam name="TDocument">The document type.</typeparam>
/// <typeparam name="TListing">The library's listing type.</typeparam>
public abstract class AbstractLibraryEditor<TDocument, TListing> : AbstractLibraryEditor
    where TDocument : AbstractLibraryDocument
    where TListing : LibraryListing<TDocument>
{
    /// <summary>
    ///     Returns the layered library the editor works on.
    /// </summary>
    /// <param name="library">The workflow library.</param>
    /// <returns>The library holding the editor's documents.</returns>
    protected abstract AbstractLayeredLibrary<TDocument, TListing> LayerOf(WorkflowLibrary library);

    /// <summary>
    ///     Turns a document into a draft. The draft must be a copy, so edits never touch the library's instance.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The draft.</returns>
    protected abstract WorkflowDefinition ToDraft(TDocument document);

    /// <summary>
    ///     Turns a draft back into a document.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <returns>The document.</returns>
    protected abstract TDocument FromDraft(WorkflowDefinition draft);

    /// <summary>
    ///     Describes what a document holds, for the list.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The summary.</returns>
    protected abstract string Summarize(TDocument document);

    /// <summary>
    ///     Checks a document with the same rules saving uses.
    /// </summary>
    /// <param name="library">The workflow library.</param>
    /// <param name="document">The document.</param>
    /// <returns>The problems; empty when valid.</returns>
    protected virtual IReadOnlyList<string> ValidateDocument(WorkflowLibrary library, TDocument document)
    {
        return this.LayerOf(library).ValidateForSave(document);
    }

    /// <inheritdoc />
    public override IReadOnlyList<LibraryEntry> List(WorkflowLibrary library)
    {
        var layer = this.LayerOf(library);
        return layer.ListAllScopes()
            .Select(l =>
            {
                var draft = this.ToDraft(l.Document);
                return new LibraryEntry(draft.Id, draft.Name, l.Scope, l.Path, this.Summarize(l.Document), draft, l.Document);
            })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Scope)
            .ToList();
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> Problems(WorkflowLibrary library)
    {
        return this.LayerOf(library).Problems;
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> Validate(WorkflowLibrary library, WorkflowDefinition draft)
    {
        return this.ValidateDocument(library, this.FromDraft(draft));
    }

    /// <inheritdoc />
    public override string Save(WorkflowLibrary library, WorkflowDefinition draft, WorkflowScope scope)
    {
        return this.LayerOf(library).Save(this.FromDraft(draft), scope);
    }

    /// <inheritdoc />
    public override bool Delete(WorkflowLibrary library, string id, WorkflowScope scope)
    {
        return this.LayerOf(library).Delete(id, scope);
    }

    /// <inheritdoc />
    public override string PathFor(WorkflowLibrary library, string id, WorkflowScope scope)
    {
        return this.LayerOf(library).PathFor(id, scope);
    }

    /// <inheritdoc />
    public override WorkflowDefinition ReadExternal(WorkflowLibrary library, string path)
    {
        return this.ToDraft(this.LayerOf(library).ReadExternal(path));
    }

    /// <inheritdoc />
    public override string Serialize(WorkflowLibrary library, WorkflowDefinition draft)
    {
        return this.LayerOf(library).Serialize(this.FromDraft(draft));
    }

    /// <summary>
    ///     Deep-copies a value through JSON.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The copy.</returns>
    protected static T Clone<T>(T value) where T : class
    {
        return WorkflowComposer.FromNode<T>(WorkflowComposer.ToNode(value));
    }
}

/// <summary>
///     Edits workflows, checked with <see cref="WorkflowComposer" /> so references to common steps and other workflows
///     are resolved.
/// </summary>
public class WorkflowLibraryEditor : AbstractLibraryEditor<WorkflowDefinition, WorkflowListing>
{
    /// <inheritdoc />
    public override string Label => "Workflows";

    /// <inheritdoc />
    public override string Noun => "workflow";

    /// <inheritdoc />
    public override string AuthoringKind => AuthoringConstants.KindWorkflow;

    /// <inheritdoc />
    public override bool HasRequirements => true;

    /// <inheritdoc />
    public override bool HasDefaultSkills => true;

    /// <inheritdoc />
    protected override AbstractLayeredLibrary<WorkflowDefinition, WorkflowListing> LayerOf(WorkflowLibrary library)
    {
        return library;
    }

    /// <inheritdoc />
    protected override WorkflowDefinition ToDraft(WorkflowDefinition document)
    {
        return Clone(document);
    }

    /// <inheritdoc />
    protected override WorkflowDefinition FromDraft(WorkflowDefinition draft)
    {
        return draft;
    }

    /// <inheritdoc />
    protected override string Summarize(WorkflowDefinition document)
    {
        return document.Steps.Count == 1 ? "1 step" : $"{document.Steps.Count} steps";
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> ValidateDocument(WorkflowLibrary library, WorkflowDefinition document)
    {
        return new WorkflowComposer(library).Validate(document);
    }
}

/// <summary>
///     Edits common steps: a draft with exactly one step, whose parameters are the common step's.
/// </summary>
public class CommonStepLibraryEditor : AbstractLibraryEditor<CommonStepDefinition, CommonStepListing>
{
    /// <inheritdoc />
    public override string Label => "Common steps";

    /// <inheritdoc />
    public override string Noun => "common step";

    /// <inheritdoc />
    public override string AuthoringKind => AuthoringConstants.KindStep;

    /// <inheritdoc />
    public override bool HasSteps => false;

    /// <inheritdoc />
    public override WorkflowDefinition NewDraft(string id, AbstractStepDefinition step)
    {
        step.Id = id;
        return base.NewDraft(id, step);
    }

    /// <inheritdoc />
    protected override AbstractLayeredLibrary<CommonStepDefinition, CommonStepListing> LayerOf(WorkflowLibrary library)
    {
        return library.Steps;
    }

    /// <inheritdoc />
    protected override WorkflowDefinition ToDraft(CommonStepDefinition document)
    {
        var step = Clone(document.Step);
        if (string.IsNullOrEmpty(step.Id))
        {
            step.Id = document.Id;
        }

        return new WorkflowDefinition
        {
            Id = document.Id,
            Name = document.Name,
            Description = document.Description,
            Tags = document.Tags is null ? null : [.. document.Tags],
            Parameters = document.Parameters is null ? null : [.. document.Parameters.Select(Clone)],
            Steps = [step],
        };
    }

    /// <inheritdoc />
    protected override CommonStepDefinition FromDraft(WorkflowDefinition draft)
    {
        return new CommonStepDefinition
        {
            Id = draft.Id,
            Name = draft.Name,
            Description = draft.Description,
            Tags = draft.Tags,
            Parameters = draft.Parameters,
            Step = draft.Steps.FirstOrDefault() ?? new AiStepDefinition {},
        };
    }

    /// <inheritdoc />
    protected override string Summarize(CommonStepDefinition document)
    {
        var count = document.Parameters?.Count ?? 0;
        return $"{document.Step.Type}, {(count == 1 ? "1 parameter" : $"{count} parameters")}";
    }
}

/// <summary>
///     Edits common skills: no steps or parameters, just a name, description, markdown instructions and supporting
///     files. The draft is a <see cref="SkillDraft" />, which carries the content alongside the builder's usual fields.
/// </summary>
public class SkillLibraryEditor : AbstractLibraryEditor<SkillDefinition, SkillListing>
{
    /// <inheritdoc />
    public override string Label => "Skills";

    /// <inheritdoc />
    public override string Noun => "skill";

    /// <inheritdoc />
    public override string AuthoringKind => AuthoringConstants.KindSkill;

    /// <inheritdoc />
    public override bool HasSteps => false;

    /// <inheritdoc />
    public override bool HasParameters => false;

    /// <inheritdoc />
    public override bool HasContent => true;

    /// <inheritdoc />
    public override WorkflowDefinition NewDraft(string id, AbstractStepDefinition step)
    {
        return new SkillDraft
        {
            Id = id, Name = id, Description = "What the skill does, and when to use it.",
            Content = new DraftContent("# " + id + "\n\nWhat the agent should do.\n", new Dictionary<string, string>()),
        };
    }

    /// <inheritdoc />
    public override DraftContent? ContentOf(WorkflowDefinition draft)
    {
        return (draft as SkillDraft)?.Content ?? new DraftContent(string.Empty, new Dictionary<string, string>());
    }

    /// <inheritdoc />
    public override WorkflowDefinition WithContent(WorkflowDefinition draft, DraftContent content)
    {
        return new SkillDraft { Id = draft.Id, Name = draft.Name, Description = draft.Description, Tags = draft.Tags, Content = content };
    }

    /// <inheritdoc />
    protected override AbstractLayeredLibrary<SkillDefinition, SkillListing> LayerOf(WorkflowLibrary library)
    {
        return library.Skills;
    }

    /// <inheritdoc />
    protected override WorkflowDefinition ToDraft(SkillDefinition document)
    {
        return new SkillDraft
        {
            Id = document.Id, Name = document.Name, Description = document.Description,
            Tags = document.Tags is null ? null : [.. document.Tags],
            Content = new DraftContent(document.Content, new Dictionary<string, string>(document.Files ?? [], StringComparer.Ordinal)),
        };
    }

    /// <inheritdoc />
    protected override SkillDefinition FromDraft(WorkflowDefinition draft)
    {
        var content = this.ContentOf(draft)!;
        return new SkillDefinition
        {
            Id = draft.Id,
            Name = draft.Name,
            Description = draft.Description ?? string.Empty,
            Tags = draft.Tags,
            Content = content.Text,
            Files = content.Files.Count > 0 ? new Dictionary<string, string>(content.Files, StringComparer.Ordinal) : null,
        };
    }

    /// <inheritdoc />
    protected override string Summarize(SkillDefinition document)
    {
        var count = document.Files?.Count ?? 0;
        return count == 0 ? "skill" : count == 1 ? "skill, 1 file" : $"skill, {count} files";
    }
}

/// <summary>
///     A skill as the builder edits it: the builder's usual draft fields, plus the skill's instructions and files.
/// </summary>
public class SkillDraft : WorkflowDefinition
{
    /// <summary>
    ///     Gets or sets the skill's instructions and files.
    /// </summary>
    public DraftContent Content { get; set; } = new(string.Empty, new Dictionary<string, string>());
}
