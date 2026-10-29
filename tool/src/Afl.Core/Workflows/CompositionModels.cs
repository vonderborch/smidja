using System.Text.Json.Serialization;
using Afl.Core.Storage;

namespace Afl.Core.Workflows;

/// <summary>
///     A value a common step or workflow takes from whatever uses it, written <c>{{params.&lt;id&gt;}}</c> in its text
///     and filled in from the user's <c>with</c> when it is expanded.
/// </summary>
public class ParameterDefinition
{
    /// <summary>
    ///     Gets or sets the id, used as <c>{{params.&lt;id&gt;}}</c> and as the key in <c>with</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the label shown to people filling it in; the id when unset.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    ///     Gets or sets what the parameter is for.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Gets or sets the value used when <c>with</c> doesn't give one. A parameter without a default must be given.
    /// </summary>
    public string? Default { get; set; }

    /// <summary>
    ///     Gets whether the parameter must be given: it has no default.
    /// </summary>
    [JsonIgnore]
    public bool IsRequired => this.Default is null;
}

/// <summary>
///     A step written once and used by any number of workflows (<see cref="AbstractStepDefinition.Use" />), customised with
///     <see cref="Parameters" /> and by overriding its fields.
/// </summary>
[DocumentVersion(2, 0)]
public class CommonStepDefinition : AbstractLibraryDocument
{
    /// <summary>
    ///     Gets or sets the values the step takes from whatever uses it.
    /// </summary>
    public List<ParameterDefinition>? Parameters { get; set; }

    /// <summary>
    ///     Gets or sets the step itself. Its id, when set, is what a using step's id defaults to.
    /// </summary>
    public AbstractStepDefinition Step { get; set; } = new AiStepDefinition();
}

/// <summary>
///     Another workflow that must have finished on the ticket before a workflow starts (<see cref="WorkflowDefinition.Requires" />).
/// </summary>
public class WorkflowRequirement
{
    /// <summary>
    ///     Gets or sets the id of the workflow required.
    /// </summary>
    public string Workflow { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the values for its parameters, if it is started.
    /// </summary>
    public Dictionary<string, string>? With { get; set; }
}

/// <summary>
///     A workflow together with where it was found.
/// </summary>
/// <param name="Workflow">The workflow.</param>
/// <param name="Scope">Where it came from.</param>
/// <param name="Path">The file it was loaded from, or null for built-ins.</param>
public record WorkflowListing(WorkflowDefinition Workflow, WorkflowScope Scope, string? Path)
    : LibraryListing<WorkflowDefinition>(Workflow, Scope, Path);

/// <summary>
///     A common step together with where it was found.
/// </summary>
/// <param name="Step">The common step.</param>
/// <param name="Scope">Where it came from.</param>
/// <param name="Path">The file it was loaded from, or null for built-ins.</param>
public record CommonStepListing(CommonStepDefinition Step, WorkflowScope Scope, string? Path)
    : LibraryListing<CommonStepDefinition>(Step, Scope, Path);
