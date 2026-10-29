using Afl.Core.Authoring;
using Afl.Core.Workflows;

namespace Afl.Cli.Output;

/// <summary>
///     An AI draft as the CLI shows it: what was asked for, where it is, what the draft check found, the drafting step
///     (questions, permission requests, the agent's summary) and what to do next.
/// </summary>
public class AuthoringSessionView
{
    /// <summary>
    ///     Gets or sets the draft's id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the kind of document: skill, step or workflow.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets what the user asked for.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the id of the document being improved, or null for a new one.
    /// </summary>
    public string? BaseId { get; set; }

    /// <summary>
    ///     Gets or sets the scope the document being improved is in.
    /// </summary>
    public WorkflowScope? BaseScope { get; set; }

    /// <summary>
    ///     Gets or sets the repo the agent may read, or null for none.
    /// </summary>
    public string? Repo { get; set; }

    /// <summary>
    ///     Gets or sets the agent drafting it.
    /// </summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the hidden run drafting it (<c>afl run show</c> works on it).
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets where the draft is: working, needs-you, review, failed, saved or ended.
    /// </summary>
    public AuthoringStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets when the draft was started.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     Gets or sets the draft file.
    /// </summary>
    public string DraftPath { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets what the draft check found: the draft's id, name, description, tags, a detail line and its
    ///     problems (Approve needs none).
    /// </summary>
    public AuthoringDraftCheck Check { get; set; } = new(null, null, null, [], null, []);

    /// <summary>
    ///     Gets or sets the id the document was saved under, once approved.
    /// </summary>
    public string? SavedId { get; set; }

    /// <summary>
    ///     Gets or sets the scope it was saved to, once approved.
    /// </summary>
    public WorkflowScope? SavedScope { get; set; }

    /// <summary>
    ///     Gets or sets the file it was saved to, once approved.
    /// </summary>
    public string? SavedPath { get; set; }

    /// <summary>
    ///     Gets or sets the drafting step: the agent's summary (<c>output</c>), its questions (<c>inputs</c>), its
    ///     permission requests and notes; null once the run has gone.
    /// </summary>
    public StepView? Step { get; set; }

    /// <summary>
    ///     Gets or sets the draft's JSON, when it was asked for (<c>afl author-show</c>).
    /// </summary>
    public string? Draft { get; set; }

    /// <summary>
    ///     Gets or sets the commands that move the draft on.
    /// </summary>
    public List<string> Next { get; set; } = [];

    /// <summary>
    ///     Creates the view of a draft.
    /// </summary>
    /// <param name="view">The draft as the service sees it.</param>
    /// <param name="draft">The draft's JSON to include, or null.</param>
    /// <returns>The view.</returns>
    public static AuthoringSessionView From(AuthoringView view, string? draft = null)
    {
        var session = view.Session;
        var result = new AuthoringSessionView
        {
            Id = session.Id,
            Kind = session.Kind,
            Description = session.Description,
            BaseId = session.BaseId,
            BaseScope = session.BaseScope,
            Repo = session.RepoId,
            Agent = session.Agent,
            RunId = session.RunId,
            Status = view.Status,
            CreatedAt = session.CreatedAt,
            DraftPath = view.DraftPath,
            Check = view.Check,
            SavedId = session.SavedId,
            SavedScope = session.SavedScope,
            SavedPath = session.SavedPath,
            Step = view.Run is { } run && view.Step is { } step ? StepView.From(run, step) : null,
            Draft = draft,
        };

        result.Next = NextCommands(result);
        return result;
    }

    /// <summary>
    ///     Renders a draft as text.
    /// </summary>
    /// <param name="view">The draft.</param>
    /// <param name="writer">The writer.</param>
    public static void Render(AuthoringSessionView view, TextWriter writer)
    {
        var what = view.BaseId is null ? $"new {view.Kind}" : $"improving {view.Kind} {view.BaseId}";
        writer.WriteLine($"AI draft {view.Id}  [{TextRendering.Name(view.Status)}]  {what}");
        writer.WriteLine($"  agent {view.Agent}, run {view.RunId}{(view.Repo is null ? string.Empty : $", reads repo {view.Repo}")}");
        writer.WriteLine($"  draft {view.DraftPath}");
        if (!string.IsNullOrWhiteSpace(view.Description))
        {
            writer.WriteLine();
            writer.WriteLine("Asked for:");
            writer.WriteLine($"  {view.Description.ReplaceLineEndings("\n  ")}");
        }

        if (view.Check.Id is not null || view.Check.Name is not null)
        {
            writer.WriteLine();
            writer.WriteLine($"Draft: {view.Check.Id} — {view.Check.Name}{(view.Check.Detail is null ? string.Empty : $" ({view.Check.Detail})")}");
            if (!string.IsNullOrWhiteSpace(view.Check.Description))
            {
                writer.WriteLine($"  {view.Check.Description}");
            }

            if (view.Check.Tags.Count > 0)
            {
                writer.WriteLine($"  tags {string.Join(", ", view.Check.Tags)}");
            }
        }

        if (view.Check.Problems.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Problems (fix these before approving):");
            foreach (var problem in view.Check.Problems)
            {
                writer.WriteLine($"  - {problem}");
            }
        }

        if (view.SavedPath is not null)
        {
            writer.WriteLine();
            writer.WriteLine($"Saved as {view.SavedId} ({(view.SavedScope is { } scope ? TextRendering.Name(scope) : string.Empty)}) in {view.SavedPath}");
        }

        if (view.Step is { } step && view.Status != AuthoringStatus.Saved)
        {
            TextRendering.StepBody(step, writer);
        }

        if (view.Draft is not null)
        {
            writer.WriteLine();
            writer.WriteLine("Draft JSON:");
            writer.WriteLine(view.Draft);
        }

        if (view.Next.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Next:");
            foreach (var command in view.Next)
            {
                writer.WriteLine($"  {command}");
            }
        }
    }

    /// <summary>
    ///     Renders a list of drafts, one per line.
    /// </summary>
    /// <param name="views">The drafts.</param>
    /// <param name="writer">The writer.</param>
    public static void RenderList(IReadOnlyList<AuthoringSessionView> views, TextWriter writer)
    {
        if (views.Count == 0)
        {
            writer.WriteLine("No AI drafts. Start one with: afl author-new <skill|step|workflow> --describe \"...\"");
            return;
        }

        foreach (var view in views)
        {
            var what = view.BaseId is null ? $"new {view.Kind}" : $"improve {view.Kind} {view.BaseId}";
            var name = view.Check.Name ?? view.Description.Split('\n')[0];
            writer.WriteLine($"{view.Id}  {TextRendering.Name(view.Status),-9}  {what,-24}  {name}");
        }
    }

    /// <summary>
    ///     Returns the commands that move a draft on from where it is.
    /// </summary>
    /// <param name="view">The draft.</param>
    /// <returns>The commands.</returns>
    private static List<string> NextCommands(AuthoringSessionView view)
    {
        var id = view.Id;
        var discard = $"afl author-discard {id}";
        return view.Status switch
        {
            AuthoringStatus.Review =>
            [
                view.Check.Problems.Count == 0
                    ? $"afl author-approve {id} --scope global|repo [--overwrite]"
                    : $"(fix the problems first: afl author-changes {id} --feedback \"...\", or edit {view.DraftPath} and run afl author-show {id})",
                $"afl author-changes {id} --feedback \"...\"",
                discard,
            ],
            AuthoringStatus.NeedsYou when view.Step is { PermissionRequests.Count: > 0 } =>
                [$"afl author-permit {id} --once N | --always N[=rule] | --deny N | --all once|always|deny | --accept-output", discard],
            AuthoringStatus.NeedsYou when view.Step is { AsksInputs: true } step =>
                [$"afl author-answer {id} {string.Join(" ", step.Inputs.Select(i => $"--input {i.Id}=\"...\""))}", discard],
            AuthoringStatus.NeedsYou or AuthoringStatus.Working => [$"afl author-resume {id}", discard],
            AuthoringStatus.Failed => [$"afl author-retry {id}", discard],
            _ => [],
        };
    }
}
