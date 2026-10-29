using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Repos;
using Afl.Core.Tickets;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for every afl verb. Handles the shared <c>--json</c> and <c>--repo</c> options, registers the repo afl is
///     run in (so the UI sees it without setup), picks the output writer, and turns errors into exit codes.
/// </summary>
public abstract class AbstractCommand
{
    /// <summary>
    ///     Exit code for success.
    /// </summary>
    public const int ExitSuccess = 0;

    /// <summary>
    ///     Exit code for bad arguments.
    /// </summary>
    public const int ExitUsage = 1;

    /// <summary>
    ///     Exit code for an afl error (not found, invalid state, a tool failed, ...).
    /// </summary>
    public const int ExitError = 2;

    /// <summary>
    ///     Exit code when a run stopped because a step failed.
    /// </summary>
    public const int ExitRunFailed = 3;

    /// <summary>
    ///     The repo afl is running inside, resolved on first use.
    /// </summary>
    private RepoRecord? _currentRepo;

    /// <summary>
    ///     Whether <see cref="_currentRepo" /> has been resolved.
    /// </summary>
    private bool _currentRepoResolved;

    /// <summary>
    ///     Gets or sets whether to write JSON.
    /// </summary>
    [Option("json", HelpText = "Write JSON to stdout instead of text (progress goes to stderr).")]
    public bool Json { get; set; }

    /// <summary>
    ///     Gets or sets the repo to use instead of the current directory's.
    /// </summary>
    [Option("repo", HelpText = "Repo id, name or path to use instead of the current directory's repo.")]
    public string? RepoOption { get; set; }

    /// <summary>
    ///     Gets the afl paths.
    /// </summary>
    protected AflPaths Paths { get; } = AflPaths.FromEnvironment();

    /// <summary>
    ///     Gets the repo registry.
    /// </summary>
    protected RepoRegistry Repos => new(this.Paths);

    /// <summary>
    ///     Gets the output writer.
    /// </summary>
    protected AbstractOutputWriter Output { get; private set; } = new TextOutputWriter();

    /// <summary>
    ///     Gets whether running this command registers the current directory's repo. Commands that manage the registry
    ///     itself turn this off.
    /// </summary>
    protected virtual bool RegistersCurrentRepo => true;

    /// <summary>
    ///     Gets the repo afl is running inside (registering it if needed), or null outside a git repo.
    /// </summary>
    protected RepoRecord? CurrentRepo
    {
        get
        {
            if (!this._currentRepoResolved)
            {
                this._currentRepoResolved = true;
                this._currentRepo = GitHelpers.FindGitRoot(Environment.CurrentDirectory) is null
                    ? null
                    : this.Repos.EnsureRegistered(Environment.CurrentDirectory);
            }

            return this._currentRepo;
        }
    }

    /// <summary>
    ///     Runs the command.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The exit code.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        this.Output = this.Json ? new JsonOutputWriter() : new TextOutputWriter();
        try
        {
            if (this.RegistersCurrentRepo)
            {
                _ = this.CurrentRepo;
            }

            return await this.ExecuteAsync(ct);
        }
        catch (AflException ex)
        {
            this.Output.Error(ex.Kind, ex.Message);
            return ExitError;
        }
        catch (OperationCanceledException)
        {
            this.Output.Error(null, "Cancelled.");
            return ExitError;
        }
    }

    /// <summary>
    ///     Does the command's work.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The exit code.</returns>
    protected abstract Task<int> ExecuteAsync(CancellationToken ct);

    /// <summary>
    ///     Returns the repo to work on: <c>--repo</c> if given, else the current directory's.
    /// </summary>
    /// <returns>The repo.</returns>
    /// <exception cref="AflException">Neither is available.</exception>
    protected RepoRecord RequireRepo()
    {
        if (this.RepoOption is not null)
        {
            return this.Repos.GetRequired(this.RepoOption);
        }

        return this.CurrentRepo ?? throw new AflException(AflErrorKind.InvalidInput,
            "Not inside a git repo. Run this from a repo, or pass --repo <id|name|path>.");
    }

    /// <summary>
    ///     Returns the repo to use a ticket in: <c>--repo</c> or the current directory's (noting when the ticket's Jira
    ///     project is associated with a different repo), else the repo associated with the ticket's Jira project.
    /// </summary>
    /// <param name="ticket">The ticket reference as typed.</param>
    /// <returns>The repo.</returns>
    /// <exception cref="AflException">The reference is invalid, or no repo fits.</exception>
    protected RepoRecord RequireRepoFor(string ticket)
    {
        var current = this.RepoOption is not null ? this.Repos.GetRequired(this.RepoOption) : this.CurrentRepo;
        var choice = this.Repos.ResolveForTicket(TicketRef.Parse(ticket), current);
        if (choice.Note is not null)
        {
            this.Output.Info(choice.Note);
        }

        return choice.Repo;
    }

    /// <summary>
    ///     Parses repeated <c>id=value</c> options (values may be <c>@file</c> or <c>-</c>) into a dictionary.
    /// </summary>
    /// <param name="pairs">The options as given.</param>
    /// <param name="option">The option's name, for the error.</param>
    /// <returns>The values, by id; empty when none were given.</returns>
    /// <exception cref="AflException">One isn't <c>id=value</c>.</exception>
    protected static Dictionary<string, string> ParsePairs(IEnumerable<string> pairs, string option)
    {
        var values = new Dictionary<string, string>();
        foreach (var pair in pairs)
        {
            var split = pair.IndexOf('=');
            if (split <= 0)
            {
                throw new AflException(AflErrorKind.InvalidInput, $"{option} '{pair}' should be id=value.");
            }

            values[pair[..split].Trim()] = ReadTextArgument(pair[(split + 1)..]);
        }

        return values;
    }

    /// <summary>
    ///     Reads a text argument that may be <c>@path</c> (a file) or <c>-</c> (stdin).
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <returns>The text.</returns>
    protected static string ReadTextArgument(string value)
    {
        if (value == "-")
        {
            return Console.In.ReadToEnd();
        }

        if (value.StartsWith('@'))
        {
            var path = value[1..];
            if (!File.Exists(path))
            {
                throw new AflException(AflErrorKind.NotFound, $"File not found: {path}");
            }

            return File.ReadAllText(path);
        }

        return value;
    }
}
