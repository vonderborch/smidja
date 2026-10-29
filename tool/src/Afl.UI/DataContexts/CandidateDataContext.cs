using Afl.Core.Runs;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     One agent's result on the Runs page, for a step several agents did that waits for a choice: the result as the
///     agent gave it, and the text the user may edit before using it.
/// </summary>
public class CandidateDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CandidateDataContext" /> class.
    /// </summary>
    /// <param name="candidate">The agent's result.</param>
    public CandidateDataContext(StepCandidate candidate)
    {
        this.Candidate = candidate;
        this.Text = candidate.Output ?? string.Empty;
    }

    /// <summary>
    ///     Gets the agent's result.
    /// </summary>
    public StepCandidate Candidate { get; }

    /// <summary>
    ///     Gets the agent id.
    /// </summary>
    public string Agent => this.Candidate.Agent;

    /// <summary>
    ///     Gets the tab header: the agent, and whether it failed.
    /// </summary>
    public string Header => this.Candidate.Status == StepStatus.Done ? this.Agent : $"{this.Agent} (failed)";

    /// <summary>
    ///     Gets whether the agent has a result that can be used.
    /// </summary>
    public bool CanUse => this.Candidate.Status == StepStatus.Done;

    /// <summary>
    ///     Gets why the agent failed, if it did.
    /// </summary>
    public string? Error => this.Candidate.Error;

    /// <summary>
    ///     Gets or sets the result as the user has edited it.
    /// </summary>
    public string Text
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsEdited));
        }
    } = string.Empty;

    /// <summary>
    ///     Gets whether the user changed the result.
    /// </summary>
    public bool IsEdited => this.Text != (this.Candidate.Output ?? string.Empty);

    /// <summary>
    ///     Gets the edited result, or null when it's unchanged.
    /// </summary>
    public string? EditedOutput => this.IsEdited ? this.Text : null;
}
