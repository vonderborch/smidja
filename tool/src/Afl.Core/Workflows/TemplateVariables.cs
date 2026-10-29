using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Tickets;

namespace Afl.Core.Workflows;

/// <summary>
///     The variables available to step prompts and instructions, and how their values are built for a run.
/// </summary>
public static class TemplateVariables
{
    /// <summary>
    ///     The variable holding the user's latest rejection feedback for the current step.
    /// </summary>
    public const string Feedback = "feedback";

    /// <summary>
    ///     The variable holding every result of a multi-agent step under its agent's heading, for its merge prompt.
    /// </summary>
    public const string Candidates = "candidates";

    /// <summary>
    ///     The variable holding which time this is that the current step runs, from 1 (more than 1 in a loop).
    /// </summary>
    public const string LoopIteration = "loop.iteration";

    /// <summary>
    ///     The variable holding the most times the current step's loop may run it, or empty when no loop can bring it
    ///     back.
    /// </summary>
    public const string LoopMax = "loop.max";

    /// <summary>
    ///     The variable holding the id of the step whose arrow started the current step, or empty for the start.
    /// </summary>
    public const string ArrivedFrom = "arrived.from";

    /// <summary>
    ///     The variable holding the name of the step whose arrow started the current step, or empty for the start.
    /// </summary>
    public const string ArrivedFromName = "arrived.fromName";

    /// <summary>
    ///     The variables every step has, whatever came before it.
    /// </summary>
    public static readonly IReadOnlyList<string> StepContextVariables = [LoopIteration, LoopMax, ArrivedFrom, ArrivedFromName, Feedback];

    /// <summary>
    ///     The variable holding what a step produced, in its <see cref="AbstractStepDefinition.OutputTemplate" />.
    /// </summary>
    public const string Output = "output";

    /// <summary>
    ///     The ticket variables.
    /// </summary>
    public static readonly IReadOnlyList<string> TicketVariables =
        ["ticket.key", "ticket.slug", "ticket.title", "ticket.body", "ticket.url", "ticket.source", "ticket.status"];

    /// <summary>
    ///     The repo variables.
    /// </summary>
    public static readonly IReadOnlyList<string> RepoVariables = ["repo.name", "repo.path", "repo.github", "repo.host", "repo.project"];

    /// <summary>
    ///     Returns the variable holding an earlier step's output.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The variable name.</returns>
    public static string StepOutput(string stepId)
    {
        return $"steps.{stepId}.output";
    }

    /// <summary>
    ///     Returns the variable holding where an earlier step's output was saved (its artifact), relative to the repo.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The variable name.</returns>
    public static string StepArtifact(string stepId)
    {
        return $"steps.{stepId}.artifact";
    }

    /// <summary>
    ///     Returns a ticket key made safe for a file or folder name: lowercase letters, digits and dashes (e.g.
    ///     <c>owner/repo#12</c> becomes <c>owner-repo-12</c>).
    /// </summary>
    /// <param name="key">The ticket key.</param>
    /// <returns>The slug; <c>ticket</c> when nothing is left.</returns>
    public static string Slug(string key)
    {
        var slug = new string(key.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return slug.Length == 0 ? "ticket" : slug;
    }

    /// <summary>
    ///     Returns the variable holding one agent's result for a multi-agent step.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <param name="agentId">The agent id.</param>
    /// <returns>The variable name.</returns>
    public static string StepCandidate(string stepId, string agentId)
    {
        return $"steps.{stepId}.candidates.{agentId}";
    }

    /// <summary>
    ///     Returns the variable holding the output of a step's latest earlier attempt, e.g. the last time round a loop.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <returns>The variable name.</returns>
    public static string StepPrevious(string stepId)
    {
        return $"steps.{stepId}.previous";
    }

    /// <summary>
    ///     Returns the variable holding the answer to one of a step's inputs.
    /// </summary>
    /// <param name="stepId">The step id.</param>
    /// <param name="inputId">The input id.</param>
    /// <returns>The variable name.</returns>
    public static string StepInput(string stepId, string inputId)
    {
        return $"steps.{stepId}.inputs.{inputId}";
    }

    /// <summary>
    ///     Returns the variable holding the answer to one of the current step's own inputs.
    /// </summary>
    /// <param name="inputId">The input id.</param>
    /// <returns>The variable name.</returns>
    public static string OwnInput(string inputId)
    {
        return $"inputs.{inputId}";
    }

    /// <summary>
    ///     Returns the steps whose variables a step can always use: those that have always run (or been skipped) by the
    ///     time it starts, in the order they're listed.
    /// </summary>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="step">The step.</param>
    /// <returns>The steps.</returns>
    public static IReadOnlyList<AbstractStepDefinition> StepsBefore(FlowMachine machine, AbstractStepDefinition step)
    {
        var ids = machine.Guaranteed(step.Id);
        return machine.Workflow.Steps.Where(s => ids.Contains(s.Id)).ToList();
    }

    /// <summary>
    ///     Returns the steps that can come before a step but may not have run by then, whose variables it can use only in
    ///     the optional form (<c>{{steps.x.output?}}</c>).
    /// </summary>
    /// <param name="machine">The workflow's flow.</param>
    /// <param name="step">The step.</param>
    /// <returns>The steps, in the order they're listed.</returns>
    public static IReadOnlyList<AbstractStepDefinition> StepsMaybeBefore(FlowMachine machine, AbstractStepDefinition step)
    {
        var ids = machine.Possible(step.Id);
        return machine.Workflow.Steps.Where(s => ids.Contains(s.Id)).ToList();
    }

    /// <summary>
    ///     Returns whether a variable can be used in a step that follows <paramref name="earlierSteps" />. A name in the
    ///     optional form (<c>name?</c>) can also be one of <paramref name="possibleSteps" />.
    /// </summary>
    /// <param name="name">The variable name, perhaps in the optional form.</param>
    /// <param name="earlierSteps">The steps that have always run before this one.</param>
    /// <param name="allSteps">Every step in the workflow, whose earlier attempts are available; none when null.</param>
    /// <param name="current">The step itself, whose <c>before</c> inputs are available; none when null.</param>
    /// <param name="possibleSteps">The steps that may have run before this one; none when null.</param>
    /// <returns>True if available.</returns>
    public static bool IsAvailable(
        string name,
        IEnumerable<AbstractStepDefinition> earlierSteps,
        IEnumerable<AbstractStepDefinition>? allSteps = null,
        AbstractStepDefinition? current = null,
        IEnumerable<AbstractStepDefinition>? possibleSteps = null)
    {
        var steps = Helpers.PromptTemplate.IsOptional(name) ? earlierSteps.Concat(possibleSteps ?? []) : earlierSteps;
        return Available(steps, allSteps, current).Contains(Helpers.PromptTemplate.NameOf(name));
    }

    /// <summary>
    ///     Lists every variable available to a step that follows <paramref name="earlierSteps" />.
    /// </summary>
    /// <param name="earlierSteps">The steps before this one.</param>
    /// <param name="allSteps">
    ///     Every step in the workflow, whose earlier attempts (<c>steps.&lt;id&gt;.previous</c>) are available
    ///     wherever a loop or route could bring the run back; none when null.
    /// </param>
    /// <param name="current">
    ///     The step itself, whose <c>before</c> inputs (<c>inputs.&lt;id&gt;</c>) are answered by the time its prompt
    ///     is rendered; none when null.
    /// </param>
    /// <returns>The variable names.</returns>
    public static IReadOnlyList<string> Available(
        IEnumerable<AbstractStepDefinition> earlierSteps,
        IEnumerable<AbstractStepDefinition>? allSteps = null,
        AbstractStepDefinition? current = null)
    {
        var previous = (allSteps ?? []).Select(s => StepPrevious(s.Id));
        var own = (current?.InputsAsked(Constants.StepConstants.WhenBefore) ?? []).Select(i => OwnInput(i.Id));
        return TicketVariables.Concat(RepoVariables).Concat(ScratchArea.Variables).Concat(own).Concat(StepVariables(earlierSteps)).Concat(previous)
            .Concat(StepContextVariables).Distinct().ToList();
    }

    /// <summary>
    ///     Describes the available variables, for validation messages.
    /// </summary>
    /// <param name="earlierSteps">The steps that have always run before this one.</param>
    /// <param name="allSteps">Every step in the workflow, whose earlier attempts are available; none when null.</param>
    /// <param name="current">The step itself, whose <c>before</c> inputs are available; none when null.</param>
    /// <param name="possibleSteps">The steps that may have run before this one; none when null.</param>
    /// <returns>The description.</returns>
    public static string Describe(
        IEnumerable<AbstractStepDefinition> earlierSteps,
        IEnumerable<AbstractStepDefinition>? allSteps = null,
        AbstractStepDefinition? current = null,
        IEnumerable<AbstractStepDefinition>? possibleSteps = null)
    {
        var optional = StepVariables(possibleSteps ?? []).Select(v => v + Helpers.PromptTemplate.OptionalMark).ToList();
        return "Available: " + string.Join(", ", Available(earlierSteps, allSteps, current)) +
               (optional.Count == 0 ? string.Empty : $"; only in the optional form, as those steps may not have run: {string.Join(", ", optional)}");
    }

    /// <summary>
    ///     Lists the variables steps' results give.
    /// </summary>
    /// <param name="steps">The steps.</param>
    /// <returns>The variable names.</returns>
    private static IEnumerable<string> StepVariables(IEnumerable<AbstractStepDefinition> steps)
    {
        return steps.SelectMany(s =>
            new[] { StepOutput(s.Id) }
                .Concat(s.Artifact is null ? [] : [StepArtifact(s.Id)])
                .Concat(((s as AbstractAgentStepDefinition)?.Agents ?? []).Select(a => StepCandidate(s.Id, a)))
                .Concat((s.Inputs ?? []).Select(i => StepInput(s.Id, i.Id))));
    }

    /// <summary>
    ///     Builds the variable values for the current step of a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="stepState">The current step's state.</param>
    /// <returns>The values by name.</returns>
    public static IReadOnlyDictionary<string, string> Build(Run run, RunStep stepState)
    {
        var ticket = run.Ticket;
        var values = new Dictionary<string, string>
        {
            ["ticket.key"] = ticket.Key,
            ["ticket.slug"] = Slug(ticket.Key),
            ["ticket.title"] = ticket.Title,
            ["ticket.body"] = ticket.Body ?? string.Empty,
            ["ticket.url"] = ticket.Url ?? string.Empty,
            ["ticket.source"] = ticket.Source,
            ["ticket.status"] = ticket.Status ?? string.Empty,
            ["repo.name"] = run.RepoName,
            ["repo.path"] = run.RepoPath,
            ["repo.github"] = run.RepoHost is { Kind: TicketRef.GitHubSource } gitHub ? gitHub.Project : string.Empty,
            ["repo.host"] = run.RepoHost?.ConnectionId ?? string.Empty,
            ["repo.project"] = run.RepoHost?.Project ?? string.Empty,
            [Feedback] = stepState.Feedback.LastOrDefault() ?? string.Empty,
        };
        foreach (var (name, value) in ScratchArea.Values(run))
        {
            values[name] = value;
        }

        values[Candidates] = stepState.CombineCandidates();
        values[LoopIteration] = (stepState.Runs + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        values[LoopMax] = Flow.FlowMachine.Of(run.Workflow).LimitOf(stepState.StepId)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        values[ArrivedFrom] = stepState.ArrivedFrom ?? string.Empty;
        values[ArrivedFromName] = stepState.ArrivedFrom is { } from ? run.Workflow.Steps.FirstOrDefault(s => s.Id == from)?.Name ?? from : string.Empty;
        foreach (var input in run.DefinitionOf(stepState).Inputs ?? [])
        {
            values[OwnInput(input.Id)] = stepState.Answers.GetValueOrDefault(input.Id) ?? string.Empty;
        }

        foreach (var step in run.Steps)
        {
            values[StepPrevious(step.StepId)] = step.PreviousOutput ?? string.Empty;
            foreach (var candidate in step.Candidates ?? [])
            {
                values[StepCandidate(step.StepId, candidate.Agent)] = candidate.Output ?? string.Empty;
            }
        }

        foreach (var step in run.Steps.Where(s => s.ArtifactPath is not null))
        {
            values[StepArtifact(step.StepId)] = step.ArtifactPath!;
        }

        foreach (var step in run.Steps.Where(s => s.Output is not null))
        {
            values[StepOutput(step.StepId)] = step.Output!;
            foreach (var input in run.DefinitionOf(step).Inputs ?? [])
            {
                values[StepInput(step.StepId, input.Id)] = step.Answers.GetValueOrDefault(input.Id) ?? string.Empty;
            }
        }

        return values;
    }
}
