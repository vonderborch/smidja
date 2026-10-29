using Afl.Core.Exceptions;

namespace Afl.Core.Runs;

/// <summary>
///     What a run is doing, as front-ends list it: its <see cref="RunStatus" />, with a run waiting on a person split
///     out of the waiting ones.
/// </summary>
public enum RunState
{
    /// <summary>
    ///     The run is stopped at a step waiting on a person (<see cref="Run.WaitsForUser" />), other than to deal with a
    ///     failure.
    /// </summary>
    NeedsYou,

    /// <summary>
    ///     A step is executing.
    /// </summary>
    Running,

    /// <summary>
    ///     The run is waiting, but not on a person: e.g. on the calling Claude session or the runs it started.
    /// </summary>
    Waiting,

    /// <summary>
    ///     The current step failed; it can be retried.
    /// </summary>
    Failed,

    /// <summary>
    ///     Every step is done.
    /// </summary>
    Done,

    /// <summary>
    ///     The run was cancelled.
    /// </summary>
    Cancelled,
}

/// <summary>
///     How a run state is shown: a symbol for lists and the words for it.
/// </summary>
/// <param name="State">The state.</param>
/// <param name="Symbol">The symbol shown beside a run in that state.</param>
/// <param name="Label">The state's name, e.g. "Needs you".</param>
/// <param name="Description">What it means, for a tooltip.</param>
public record RunStateInfo(RunState State, string Symbol, string Label, string Description)
{
    /// <summary>
    ///     Gets the name the state is typed as on the command line, e.g. <c>needs-you</c>.
    /// </summary>
    public string CliName => this.Label.ToLowerInvariant().Replace(' ', '-');

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Label;
    }
}

/// <summary>
///     The run states, how each is shown, and which a run is in.
/// </summary>
public static class RunStates
{
    /// <summary>
    ///     Every state, in the order lists and filters show them: what wants attention first.
    /// </summary>
    public static readonly IReadOnlyList<RunStateInfo> All =
    [
        new(RunState.NeedsYou, "●", "Needs you", "Stopped at a step waiting on you"),
        new(RunState.Running, "▶", "Running", "A step is running"),
        new(RunState.Waiting, "◐", "Waiting", "Waiting on Claude or the runs it started"),
        new(RunState.Failed, "✖", "Failed", "A step failed; retry, skip or change it"),
        new(RunState.Done, "✓", "Done", "Every step is done"),
        new(RunState.Cancelled, "⊘", "Cancelled", "The run was cancelled"),
    ];

    /// <summary>
    ///     The state of a run with each status that isn't waiting on a person.
    /// </summary>
    private static readonly IReadOnlyDictionary<RunStatus, RunState> StateOfStatus = new Dictionary<RunStatus, RunState>
    {
        [RunStatus.Running] = RunState.Running,
        [RunStatus.Waiting] = RunState.Waiting,
        [RunStatus.Done] = RunState.Done,
        [RunStatus.Failed] = RunState.Failed,
        [RunStatus.Cancelled] = RunState.Cancelled,
    };

    /// <summary>
    ///     Gets the state a run is in.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The state.</returns>
    public static RunState Of(Run run)
    {
        // A failed step waits on a person too, but is shown as the failure it is.
        return run.WaitsForUser && run.Status != RunStatus.Failed ? RunState.NeedsYou : StateOfStatus[run.Status];
    }

    /// <summary>
    ///     Gets how a state is shown.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The state's symbol and words.</returns>
    public static RunStateInfo Info(RunState state)
    {
        return All.First(s => s.State == state);
    }

    /// <summary>
    ///     Reads states typed on the command line, e.g. <c>needs-you</c>, <c>failed</c> or <c>NeedsYou</c>.
    /// </summary>
    /// <param name="names">The names.</param>
    /// <returns>The states, once each.</returns>
    /// <exception cref="AflException">A name isn't a state.</exception>
    public static List<RunState> Parse(IEnumerable<string> names)
    {
        var states = new List<RunState>();
        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            var typed = Squash(name);
            var info = All.FirstOrDefault(s => Squash(s.CliName) == typed)
                       ?? throw new AflException(AflErrorKind.InvalidInput,
                           $"'{name}' isn't a run state; use {string.Join(", ", All.Select(s => s.CliName))}.");
            if (!states.Contains(info.State))
            {
                states.Add(info.State);
            }
        }

        return states;
    }

    /// <summary>
    ///     Gets whether a run in a state has finished, so is left out of lists of what's going on.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>True for done and cancelled.</returns>
    public static bool IsFinished(RunState state)
    {
        return state is RunState.Done or RunState.Cancelled;
    }

    /// <summary>
    ///     Lowers a state's name and drops what's between its words, so <c>needs-you</c> and <c>NeedsYou</c> match.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The squashed name.</returns>
    private static string Squash(string name)
    {
        return new string([.. name.Where(char.IsLetter).Select(char.ToLowerInvariant)]);
    }
}
