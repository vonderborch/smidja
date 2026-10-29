using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Workflows;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for workflow commands: gives access to the layered workflow library for the current repo, if any.
/// </summary>
public abstract class AbstractWorkflowCommand : AbstractCommand
{
    /// <summary>
    ///     Gets the workflow library, including the repo layer when afl is running in (or given) a repo.
    /// </summary>
    protected WorkflowLibrary Library =>
        new(this.Paths, this.RepoOption is not null ? this.RequireRepo().Path : this.CurrentRepo?.Path);

    /// <summary>
    ///     Prints whether a file is valid, with its problems.
    /// </summary>
    /// <param name="file">The file checked.</param>
    /// <param name="errors">Its problems.</param>
    /// <returns>The exit code: success when there are none.</returns>
    protected int ReportProblems(string file, IReadOnlyList<string> errors, IReadOnlyList<string>? warnings = null)
    {
        this.Output.Result(new { valid = errors.Count == 0, errors, warnings = warnings ?? [] }, (r, w) =>
        {
            w.WriteLine(r.valid ? $"{file} is valid." : $"{file} has problems:");
            foreach (var error in r.errors)
            {
                w.WriteLine($"  - {error}");
            }

            foreach (var warning in r.warnings)
            {
                w.WriteLine($"  warning: {warning}");
            }
        });
        return errors.Count == 0 ? ExitSuccess : ExitError;
    }
}

/// <summary>
///     Base for listing a layered library: every effective document, or only the ones matching <c>--tag</c> and
///     <c>--search</c>, then any files that were skipped.
/// </summary>
/// <typeparam name="TDocument">The document type.</typeparam>
/// <typeparam name="TListing">The library's listing type.</typeparam>
public abstract class AbstractLibraryListCommand<TDocument, TListing> : AbstractWorkflowCommand
    where TDocument : AbstractLibraryDocument
    where TListing : LibraryListing<TDocument>
{
    /// <summary>
    ///     Gets or sets the tags a document must all have.
    /// </summary>
    [Option('t', "tag", HelpText = "Only list ones with this tag (repeatable; they must have every tag given).")]
    public IEnumerable<string> Tags { get; set; } = [];

    /// <summary>
    ///     Gets or sets the search text.
    /// </summary>
    [Option('s', "search", HelpText = "Only list ones whose id, name, description, tags or contents contain every word of this text.")]
    public string? Search { get; set; }

    /// <summary>
    ///     Gets or sets whether the search skips the contents.
    /// </summary>
    [Option("names-only", HelpText = "With --search, look only in the id, name, description and tags, not the contents.")]
    public bool NamesOnly { get; set; }

    /// <summary>
    ///     Gets or sets the scope to list.
    /// </summary>
    [Option("scope", HelpText = "Only list ones from this scope: builtin, global or repo.")]
    public WorkflowScope? Scope { get; set; }

    /// <summary>
    ///     Returns the library to list.
    /// </summary>
    /// <param name="library">The workflow library.</param>
    /// <returns>The library.</returns>
    protected abstract AbstractLayeredLibrary<TDocument, TListing> LayerOf(WorkflowLibrary library);

    /// <summary>
    ///     Renders the listings as text.
    /// </summary>
    /// <param name="listings">The listings.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="matches">How each matched the query, by id; null when nothing was asked for.</param>
    protected abstract void Render(IReadOnlyList<TListing> listings, TextWriter writer, IReadOnlyDictionary<string, LibraryMatch>? matches);

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var layer = this.LayerOf(this.Library);
        var query = new LibraryQuery
        {
            Text = this.Search, SearchContents = !this.NamesOnly, Tags = AbstractLibraryDocument.NormalizeTags(this.Tags), Scope = this.Scope,
        };
        if (query.IsEmpty)
        {
            this.Output.Result(layer.List(), (r, w) => this.Render(r, w, null));
        }
        else
        {
            var hits = layer.Search(query);
            var matches = hits.ToDictionary(h => h.Listing.Document.Id, h => h.Match, StringComparer.Ordinal);
            this.Output.Result(hits.Select(h => h.Listing).ToList(), (r, w) => this.Render(r, w, matches));
        }

        foreach (var problem in layer.Problems)
        {
            this.Output.Info($"Skipped {problem}");
        }

        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Lists workflows.
/// </summary>
[Verb("workflow-list", HelpText = "List the workflows available here (built-in, global and repo), optionally by tag or search.")]
public class WorkflowListCommand : AbstractLibraryListCommand<WorkflowDefinition, WorkflowListing>
{
    /// <inheritdoc />
    protected override AbstractLayeredLibrary<WorkflowDefinition, WorkflowListing> LayerOf(WorkflowLibrary library)
    {
        return library;
    }

    /// <inheritdoc />
    protected override void Render(IReadOnlyList<WorkflowListing> listings, TextWriter writer, IReadOnlyDictionary<string, LibraryMatch>? matches)
    {
        TextRendering.WorkflowList(listings, writer, matches);
    }
}

/// <summary>
///     Shows a workflow.
/// </summary>
[Verb("workflow-show", HelpText = "Show a workflow's steps.")]
public class WorkflowShowCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the workflow id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The workflow id.")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether to show the workflow as a run would follow it.
    /// </summary>
    [Option("expanded", HelpText = "Show the steps a run follows: common steps filled in, included workflows spliced in, requirements added.")]
    public bool Expanded { get; set; }

    /// <summary>
    ///     Gets or sets the workflow's parameters, for <c>--expanded</c>.
    /// </summary>
    [Option('p', "param", HelpText = "With --expanded, a value for one of the workflow's parameters: --param id=value (repeatable).")]
    public IEnumerable<string> Parameters { get; set; } = [];

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var library = this.Library;
        var workflow = library.GetRequired(this.Id);
        if (this.Expanded)
        {
            workflow = new WorkflowComposer(library).Expand(workflow, ParsePairs(this.Parameters, "--param"));
        }

        this.Output.Result(workflow, TextRendering.Workflow);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Validates a workflow file.
/// </summary>
[Verb("workflow-validate", HelpText = "Check a workflow file for problems.")]
public class WorkflowValidateCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the file.
    /// </summary>
    [Value(0, MetaName = "file", Required = true, HelpText = "The workflow JSON file.")]
    public string File { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var library = this.Library;
        var composer = new WorkflowComposer(library);
        var workflow = library.ReadExternal(this.File);
        var errors = composer.Validate(workflow);
        IReadOnlyList<string>? warnings = null;
        try
        {
            // Warnings need the expanded flow; a workflow that needs parameters can't be expanded without them.
            warnings = errors.Count == 0 ? FlowMachine.Of(composer.Expand(workflow)).Warnings() : null;
        }
        catch (AflException)
        {
        }

        return Task.FromResult(this.ReportProblems(this.File, errors, warnings));
    }
}

/// <summary>
///     Creates a workflow from an existing one.
/// </summary>
[Verb("workflow-new", HelpText = "Create a workflow by copying another, in the global library or this repo.")]
public class WorkflowNewCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the new workflow id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The new workflow id (lowercase, digits and dashes).")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the workflow to copy.
    /// </summary>
    [Option("from", Default = "feature", HelpText = "The workflow to copy.")]
    public string From { get; set; } = "feature";

    /// <summary>
    ///     Gets or sets whether to save it in the repo rather than globally.
    /// </summary>
    [Option("in-repo", HelpText = "Save to <repo>/.afl/workflows (shared through git) instead of ~/.afl/workflows.")]
    public bool InRepo { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var library = this.Library;
        var scope = this.InRepo ? WorkflowScope.Repo : WorkflowScope.Global;
        if (this.InRepo)
        {
            this.RequireRepo();
        }

        if (library.ListAllScopes().Any(l => l.Scope == scope && l.Workflow.Id == this.Id))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"A {scope} workflow '{this.Id}' already exists.");
        }

        var workflow = library.GetRequired(this.From);
        workflow.Id = this.Id;
        workflow.Name = this.Id;
        var path = library.Save(workflow, scope);
        this.Output.Result(new { id = this.Id, scope, path }, (r, w) => w.WriteLine($"Created {r.path}. Edit it, then: afl workflow validate {r.path}"));
        return Task.FromResult(ExitSuccess);
    }
}
