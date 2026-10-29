using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Storage;
using Afl.Core.Workflows;

namespace Afl.Core.Runs;

/// <summary>
///     The saved runs: <c>~/.afl/runs/&lt;repoId&gt;/&lt;runId&gt;.json</c>, with step logs in
///     <c>&lt;runId&gt;/&lt;stepId&gt;.log</c> beside them. Runs can be looked up by a unique prefix or suffix of their id.
/// </summary>
/// <param name="paths">The afl paths.</param>
public class RunStore(AflPaths paths) : AbstractJsonCollectionStore<Run, string>(paths.RunsDirectory)
{
    /// <summary>
    ///     How long to wait for another process to release a run.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Gets the afl paths.
    /// </summary>
    public AflPaths Paths { get; } = paths;

    /// <inheritdoc />
    protected override bool WatchSubdirectories => true;

    /// <inheritdoc />
    public override string GetKey(Run item)
    {
        return item.Id;
    }

    /// <summary>
    ///     Gets a run by id or a unique prefix or suffix of it, or throws.
    /// </summary>
    /// <param name="idOrPrefix">The id or prefix.</param>
    /// <returns>The run.</returns>
    public Run GetRequired(string idOrPrefix)
    {
        return this.Get(idOrPrefix) ?? throw new AflException(AflErrorKind.NotFound, $"No run '{idOrPrefix}'.");
    }

    /// <summary>
    ///     Lists the steps a run is on: its current step, or, while its paths run in parallel, the steps its unfinished
    ///     path runs are on (following their own paths down).
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>Each run holding a step, and the step.</returns>
    public List<(Run Run, RunStep Step)> WaitingSteps(Run run)
    {
        var waiting = new List<(Run Run, RunStep Step)>();
        this.CollectWaiting(run, waiting);
        return waiting;
    }

    /// <summary>
    ///     Lists runs, newest first.
    /// </summary>
    /// <param name="repoId">Only runs in this repo, or null for every repo.</param>
    /// <returns>The runs.</returns>
    public IReadOnlyList<Run> List(string? repoId = null)
    {
        return this.All().Where(r => repoId is null || r.RepoId == repoId).OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>
    ///     Orders runs (or views of them) so each child run follows its parent, keeping the given order otherwise, with
    ///     each one's nesting level. A child whose parent isn't in the list stays at the top level.
    /// </summary>
    /// <typeparam name="T">The run or view type.</typeparam>
    /// <param name="items">The runs.</param>
    /// <param name="id">Returns a run's id.</param>
    /// <param name="parentId">Returns the id of the run that started it, if any.</param>
    /// <returns>The runs with their levels, parents first.</returns>
    public static IReadOnlyList<(T Item, int Level)> Nest<T>(IReadOnlyList<T> items, Func<T, string> id, Func<T, string?> parentId)
    {
        var listed = items.Select(id).ToHashSet();
        var children = items.Where(i => parentId(i) is { } parent && listed.Contains(parent)).ToLookup(i => parentId(i)!);
        var nested = new List<(T Item, int Level)>(items.Count);
        var seen = new HashSet<string>();

        void Add(T item, int level)
        {
            if (!seen.Add(id(item)))
            {
                return;
            }

            nested.Add((item, level));
            foreach (var child in children[id(item)])
            {
                Add(child, level + 1);
            }
        }

        foreach (var item in items.Where(i => parentId(i) is not { } parent || !listed.Contains(parent)))
        {
            Add(item, 0);
        }

        return nested;
    }

    /// <inheritdoc />
    public override void Save(Run item)
    {
        item.UpdatedAt = DateTimeOffset.Now;
        base.Save(item);
    }

    /// <summary>
    ///     Creates a new run id: the date plus a random suffix, e.g. <c>260924-3fa2</c>.
    /// </summary>
    /// <returns>The id.</returns>
    public string NewId()
    {
        while (true)
        {
            var id = $"{DateTime.Now:yyMMdd}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
            if (this.FindExact(id) is null)
            {
                return id;
            }
        }
    }

    /// <summary>
    ///     Returns the log file for a step of a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="stepId">The step id.</param>
    /// <returns>The path.</returns>
    public string LogPath(Run run, string stepId)
    {
        return Path.Combine(this.Paths.RunsDirectoryFor(run.RepoId), run.Id, stepId + PathConstants.LogExtension);
    }

    /// <summary>
    ///     Locks a run so only one process executes it at a time.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The lock; dispose it to release.</returns>
    public FileLock Lock(Run run)
    {
        return FileLock.Acquire(this.LockPath(run), LockTimeout, $"Run {run.Id}");
    }

    /// <summary>
    ///     Returns the file that locks a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The path.</returns>
    public string LockPath(Run run)
    {
        return this.GetPath(run) + PathConstants.LockExtension;
    }

    /// <inheritdoc />
    protected override string GetPath(Run item)
    {
        return Path.Combine(this.Paths.RunsDirectoryFor(item.RepoId), item.Id + PathConstants.JsonExtension);
    }

    /// <inheritdoc />
    protected override string? FindPath(string key)
    {
        var exact = this.FindExact(key);
        if (exact is not null)
        {
            return exact;
        }

        var matches = this.EnumerateFiles().Where(p =>
        {
            var id = Path.GetFileNameWithoutExtension(p);
            return id.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                   id.EndsWith(key, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new AflException(AflErrorKind.InvalidInput,
                $"'{key}' matches several runs: {string.Join(", ", matches.Select(Path.GetFileNameWithoutExtension))}."),
        };
    }

    /// <summary>
    ///     Finds the file of the run with exactly this id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The path, or null.</returns>
    private string? FindExact(string id)
    {
        return this.EnumerateFiles().FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == id);
    }

    /// <summary>
    ///     Collects the steps a run is on: its current step, or, while its paths run in parallel, the step each path run
    ///     is on.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="waiting">Receives each run and its step.</param>
    private void CollectWaiting(Run run, List<(Run Run, RunStep Step)> waiting)
    {
        if (run.CurrentStep is not { } step)
        {
            return;
        }

        if (step.Status == StepStatus.AwaitingPaths)
        {
            foreach (var path in run.Paths.Where(p => p.RunId is not null))
            {
                if (this.Get(path.RunId!) is { IsFinished: false } pathRun)
                {
                    this.CollectWaiting(pathRun, waiting);
                }
            }

            return;
        }

        waiting.Add((run, step));
    }
}
