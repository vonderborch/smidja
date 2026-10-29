using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Workflows;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Lists common steps.
/// </summary>
[Verb("step-list", HelpText = "List the common steps workflows can use (built-in, global and repo), optionally by tag or search.")]
public class CommonStepListCommand : AbstractLibraryListCommand<CommonStepDefinition, CommonStepListing>
{
    /// <inheritdoc />
    protected override AbstractLayeredLibrary<CommonStepDefinition, CommonStepListing> LayerOf(WorkflowLibrary library)
    {
        return library.Steps;
    }

    /// <inheritdoc />
    protected override void Render(IReadOnlyList<CommonStepListing> listings, TextWriter writer, IReadOnlyDictionary<string, LibraryMatch>? matches)
    {
        TextRendering.CommonStepList(listings, writer, matches);
    }
}

/// <summary>
///     Shows a common step.
/// </summary>
[Verb("step-show", HelpText = "Show a common step: its parameters and what it does.")]
public class CommonStepShowCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the common step id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The common step id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.Library.Steps.GetRequired(this.Id), TextRendering.CommonStep);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Validates a common step file.
/// </summary>
[Verb("step-validate", HelpText = "Check a common step file for problems.")]
public class CommonStepValidateCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the file.
    /// </summary>
    [Value(0, MetaName = "file", Required = true, HelpText = "The common step JSON file.")]
    public string File { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        return Task.FromResult(this.ReportProblems(this.File, CommonStepLibrary.Validate(this.Library.Steps.ReadExternal(this.File))));
    }
}

/// <summary>
///     Saves a step of an existing workflow as a common step.
/// </summary>
[Verb("step-new", HelpText = "Save a step of a workflow as a common step other workflows can use.")]
public class CommonStepNewCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the new common step id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The new common step id (lowercase, digits and dashes).")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the workflow to take the step from.
    /// </summary>
    [Option("from-workflow", Required = true, HelpText = "The workflow the step is in.")]
    public string FromWorkflow { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the step to copy.
    /// </summary>
    [Option("step", Required = true, HelpText = "The id of the step to copy (as shown by afl workflow-show --expanded).")]
    public string Step { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether to save it in the repo rather than globally.
    /// </summary>
    [Option("in-repo", HelpText = "Save to <repo>/.afl/steps (shared through git) instead of ~/.afl/steps.")]
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

        if (library.Steps.ListAllScopes().Any(l => l.Scope == scope && l.Step.Id == this.Id))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"A {scope} common step '{this.Id}' already exists.");
        }

        // The expanded workflow, so a step that itself uses a common step (or came from an include) is copied whole.
        var workflow = new WorkflowComposer(library).Expand(library.GetRequired(this.FromWorkflow),
            WorkflowComposer.ValuesWithDefaults(library.GetRequired(this.FromWorkflow).Parameters));
        var step = workflow.Steps.FirstOrDefault(s => s.Id == this.Step)
                   ?? throw new AflException(AflErrorKind.NotFound,
                       $"Workflow '{this.FromWorkflow}' has no step '{this.Step}'. Steps: {string.Join(", ", workflow.Steps.Select(s => s.Id))}.");
        var common = new CommonStepDefinition
        {
            Id = this.Id,
            Name = step.Name,
            Step = WorkflowComposer.FromNode<AbstractStepDefinition>(WorkflowComposer.ToNode(step)),
        };
        var path = library.Steps.Save(common, scope);
        this.Output.Result(new { id = this.Id, scope, path },
            (r, w) => w.WriteLine($"Created {r.path}. Add parameters ({{{{params.<id>}}}}) as needed, then: afl step-validate {r.path}"));
        return Task.FromResult(ExitSuccess);
    }
}
