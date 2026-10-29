using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for the commands that list and change saved permission rules and guard rails. Each prints the rules as they
///     now are.
/// </summary>
public abstract class AbstractPermissionCommand : AbstractCommand
{
    /// <summary>
    ///     Gets the rule store.
    /// </summary>
    protected PermissionRuleStore Store => new(this.Paths);

    /// <summary>
    ///     Reads a scope a user typed.
    /// </summary>
    /// <param name="value">The scope: run, repo or global.</param>
    /// <param name="allowRun">Whether the run scope is allowed.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="AflException">The scope is unknown or not allowed here.</exception>
    public static PermissionScope ParseScope(string value, bool allowRun = true)
    {
        var names = Enum.GetNames<PermissionScope>().Where(n => allowRun || n != nameof(PermissionScope.Run))
            .Select(n => n.ToLowerInvariant()).ToList();
        return Enum.TryParse<PermissionScope>(value.Trim(), true, out var scope) && names.Contains(scope.ToString().ToLowerInvariant())
            ? scope
            : throw new AflException(AflErrorKind.InvalidInput, $"There's no scope '{value}' here. Use {string.Join(" or ", names)}.");
    }

    /// <summary>
    ///     Writes the rules as they now are, with the repo's when there is one.
    /// </summary>
    /// <param name="repo">The repo, or null.</param>
    protected void WriteRules(RepoRecord? repo)
    {
        this.Output.Result(PermissionsView.From(this.Store, repo), TextRendering.Permissions);
    }

    /// <summary>
    ///     Returns the repo a repo-scoped rule is for, or null for the global scope.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The repo, or null.</returns>
    protected RepoRecord? RepoFor(PermissionScope scope)
    {
        return scope == PermissionScope.Repo ? this.RequireRepo() : this.OptionalRepo();
    }

    /// <summary>
    ///     Returns <c>--repo</c> or the current directory's repo, or null when there's neither.
    /// </summary>
    /// <returns>The repo, or null.</returns>
    protected RepoRecord? OptionalRepo()
    {
        return this.RepoOption is not null ? this.Repos.GetRequired(this.RepoOption) : this.CurrentRepo;
    }
}

/// <summary>
///     <c>afl permission list</c>.
/// </summary>
[Verb("permission-list", HelpText = "List the permission policies, the guard rails, and the saved rules: global ones and, inside a repo, the repo's.")]
public class PermissionListCommand : AbstractPermissionCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.WriteRules(this.OptionalRepo());
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl permission add</c>: saves a rule that lets agents use something without asking, or refuses it.
/// </summary>
[Verb("permission-add", HelpText = "Save a permission rule for this repo or every repo: e.g. afl permission add \"Bash(dotnet test:*)\" [--deny] [--scope repo|global].")]
public class PermissionAddCommand : AbstractPermissionCommand
{
    /// <summary>
    ///     Gets or sets the rule.
    /// </summary>
    [Value(0, MetaName = "rule", Required = true,
        HelpText = "The rule: a tool name, optionally with a specifier, such as Bash(dotnet test:*), WebFetch(domain:github.com), Edit(src/**) or mcp__jira__search.")]
    public string Rule { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether the rule refuses rather than allows.
    /// </summary>
    [Option("deny", HelpText = "Refuse what the rule matches, whatever else allows it.")]
    public bool Deny { get; set; }

    /// <summary>
    ///     Gets or sets the scope.
    /// </summary>
    [Option("scope", Default = "repo", HelpText = "repo (this repo's runs, the default) or global (every repo's).")]
    public string Scope { get; set; } = "repo";

    /// <summary>
    ///     Gets or sets why the rule was added.
    /// </summary>
    [Option("note", HelpText = "Why the rule was added.")]
    public string? Note { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var scope = ParseScope(this.Scope, false);
        var repo = this.RepoFor(scope);
        this.Store.Add(scope, repo?.Id, this.Rule, this.Deny ? PermissionRuleKind.Deny : PermissionRuleKind.Allow, this.Note);
        this.WriteRules(repo);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl permission remove</c>.
/// </summary>
[Verb("permission-remove", HelpText = "Remove a saved permission rule from this repo or the global rules.")]
public class PermissionRemoveCommand : AbstractPermissionCommand
{
    /// <summary>
    ///     Gets or sets the rule.
    /// </summary>
    [Value(0, MetaName = "rule", Required = true, HelpText = "The rule, exactly as afl permission list shows it (without 'deny').")]
    public string Rule { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the scope.
    /// </summary>
    [Option("scope", Default = "repo", HelpText = "repo (the default) or global.")]
    public string Scope { get; set; } = "repo";

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var scope = ParseScope(this.Scope, false);
        var repo = this.RepoFor(scope);
        if (!this.Store.Remove(scope, repo?.Id, this.Rule))
        {
            throw new AflException(AflErrorKind.NotFound, $"There's no {scope.ToString().ToLowerInvariant()} rule '{this.Rule.Trim()}'.");
        }

        this.WriteRules(repo);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl permission guards</c>: shows or changes the guard rails, the deny rules every run gets.
/// </summary>
[Verb("permission-guards", HelpText = "Show or change the guard rails: deny rules every run gets, even in full auto (--add, --remove, --reset).")]
public class PermissionGuardsCommand : AbstractPermissionCommand
{
    /// <summary>
    ///     Gets or sets the rules to add.
    /// </summary>
    [Option("add", HelpText = "Add a guard rule (repeatable).")]
    public IEnumerable<string> Add { get; set; } = [];

    /// <summary>
    ///     Gets or sets the rules to remove.
    /// </summary>
    [Option("remove", HelpText = "Remove a guard rule (repeatable).")]
    public IEnumerable<string> Remove { get; set; } = [];

    /// <summary>
    ///     Gets or sets whether to go back to afl's guard rails.
    /// </summary>
    [Option("reset", HelpText = "Go back to afl's own guard rails (before any --add or --remove).")]
    public bool Reset { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Store;
        var add = this.Add.Select(r => r.Trim()).ToList();
        var remove = this.Remove.Select(r => r.Trim()).ToHashSet();
        if (this.Reset || add.Count > 0 || remove.Count > 0)
        {
            var guards = this.Reset ? store.SetGuards(null) : store.Guards();
            var missing = remove.Where(r => !guards.Contains(r)).ToList();
            if (missing.Count > 0)
            {
                throw new AflException(AflErrorKind.NotFound, $"There's no guard rule {string.Join(", ", missing.Select(r => $"'{r}'"))}.");
            }

            store.SetGuards(guards.Where(r => !remove.Contains(r)).Concat(add));
        }

        this.WriteRules(this.OptionalRepo());
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl run permit</c>: decides on the tool calls the AI was refused, for a step that needs permission. The step
///     carries on once every request is decided.
/// </summary>
[Verb("run-permit", HelpText = "Decide on what the AI was refused, for a step that needs permission: --once N, --always N[=rule], --deny N, --all once|always|deny, or --accept-output.")]
public class RunPermitCommand : AbstractStepRunCommand
{
    /// <summary>
    ///     The answers the terminal prompt takes, by their first letter.
    /// </summary>
    private static readonly Dictionary<char, PermissionDecision> Answers = new()
    {
        ['o'] = PermissionDecision.Once,
        ['a'] = PermissionDecision.Always,
        ['d'] = PermissionDecision.Deny,
    };

    /// <summary>
    ///     Gets or sets the requests to allow for the agent's next turn only, as <c>N</c> or <c>N=rule</c>.
    /// </summary>
    [Option("once", HelpText = "Allow request N for the agent's next turn only: --once 1 or --once 1=\"Bash(git status)\" (repeatable).")]
    public IEnumerable<string> Once { get; set; } = [];

    /// <summary>
    ///     Gets or sets the requests to allow from now on, as <c>N</c> or <c>N=rule</c>.
    /// </summary>
    [Option("always", HelpText = "Always allow request N, saving its first suggested rule (or N=rule) to --scope (repeatable).")]
    public IEnumerable<string> Always { get; set; } = [];

    /// <summary>
    ///     Gets or sets the requests to keep refusing.
    /// </summary>
    [Option("deny", HelpText = "Keep refusing request N (repeatable).")]
    public IEnumerable<string> Deny { get; set; } = [];

    /// <summary>
    ///     Gets or sets what to decide for every request not named.
    /// </summary>
    [Option("all", HelpText = "Decide every request not named otherwise: once, always or deny.")]
    public string? All { get; set; }

    /// <summary>
    ///     Gets or sets where <c>--always</c> rules are saved.
    /// </summary>
    [Option("scope", Default = "repo", HelpText = "Where --always rules are saved: run (this run only), repo (the default) or global.")]
    public string Scope { get; set; } = "repo";

    /// <summary>
    ///     Gets or sets whether to keep the AI's output as it is, refusing whatever is undecided.
    /// </summary>
    [Option("accept-output", HelpText = "Keep what the AI did without what it was refused: undecided requests are denied and the step finishes.")]
    public bool AcceptOutput { get; set; }

    /// <summary>
    ///     Works out the decisions from the permit options, asking in a terminal when none were given.
    /// </summary>
    /// <param name="output">The output, for asking.</param>
    /// <param name="open">The requests still open.</param>
    /// <param name="once">The <c>--once</c> values.</param>
    /// <param name="always">The <c>--always</c> values.</param>
    /// <param name="deny">The <c>--deny</c> values.</param>
    /// <param name="all">The <c>--all</c> value, or null.</param>
    /// <param name="scope">Where always rules are saved.</param>
    /// <param name="acceptOutput">Whether <c>--accept-output</c> was given.</param>
    /// <param name="showCommand">The command that lists the requests, for errors.</param>
    /// <returns>The decisions.</returns>
    /// <exception cref="AflException">An option is malformed, or nothing was decided.</exception>
    public static List<PermissionChoice> ChoicesFrom(
        AbstractOutputWriter output,
        IReadOnlyList<PermissionRequest> open,
        IEnumerable<string> once,
        IEnumerable<string> always,
        IEnumerable<string> deny,
        string? all,
        PermissionScope scope,
        bool acceptOutput,
        string showCommand)
    {
        var choices = new Dictionary<int, PermissionChoice>();
        Collect(choices, once, PermissionDecision.Once, scope, showCommand);
        Collect(choices, always, PermissionDecision.Always, scope, showCommand);
        Collect(choices, deny, PermissionDecision.Deny, scope, showCommand);
        if (all is not null)
        {
            var decision = Enum.TryParse<PermissionDecision>(all.Trim(), true, out var parsed)
                ? parsed
                : throw new AflException(AflErrorKind.InvalidInput, $"--all takes once, always or deny, not '{all}'.");
            foreach (var request in open.Where(r => !choices.ContainsKey(r.Index)))
            {
                choices[request.Index] = new PermissionChoice(request.Index, decision, null, scope);
            }
        }

        if (choices.Count == 0 && !acceptOutput)
        {
            AskInTerminal(output, choices, open, scope);
        }

        if (choices.Count == 0 && !acceptOutput)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"Say what to allow: --once N, --always N[=rule], --deny N, --all once|always|deny, or --accept-output (see {showCommand}).");
        }

        return choices.Values.ToList();
    }

    /// <inheritdoc />
    protected override Task<Run> ActAsync(RunEngine engine, CancellationToken ct)
    {
        var target = this.TargetOf(engine);
        var open = engine.Store.GetRequired(target).CurrentStep?.OpenPermissionRequests ?? [];
        var choices = ChoicesFrom(this.Output, open, this.Once, this.Always, this.Deny, this.All,
            AbstractPermissionCommand.ParseScope(this.Scope), this.AcceptOutput, $"afl run show {this.RunId}");
        return engine.PermitAsync(target, choices, this.AcceptOutput, this.Output, ct);
    }

    /// <summary>
    ///     Adds the choices from one option, each <c>N</c> or <c>N=rule</c>.
    /// </summary>
    /// <param name="choices">The choices so far, by request number.</param>
    /// <param name="values">The option's values.</param>
    /// <param name="decision">The decision the option makes.</param>
    /// <param name="scope">Where an always rule is saved.</param>
    /// <param name="showCommand">The command that lists the requests, for errors.</param>
    /// <exception cref="AflException">A value isn't a request number, or a request is named twice.</exception>
    private static void Collect(
        Dictionary<int, PermissionChoice> choices,
        IEnumerable<string> values,
        PermissionDecision decision,
        PermissionScope scope,
        string showCommand)
    {
        foreach (var value in values)
        {
            var equals = value.IndexOf('=');
            var number = (equals < 0 ? value : value[..equals]).Trim().TrimStart('#');
            var rule = equals < 0 ? null : value[(equals + 1)..].Trim();
            if (!int.TryParse(number, out var index) || index < 1)
            {
                throw new AflException(AflErrorKind.InvalidInput,
                    $"'{value}' should start with a request number (see {showCommand}).");
            }

            if (decision == PermissionDecision.Deny && rule is not null)
            {
                throw new AflException(AflErrorKind.InvalidInput, $"--deny takes only a request number, not '{value}'.");
            }

            if (!choices.TryAdd(index, new PermissionChoice(index, decision, string.IsNullOrEmpty(rule) ? null : rule, scope)))
            {
                throw new AflException(AflErrorKind.InvalidInput, $"Request {index} is decided twice.");
            }
        }
    }

    /// <summary>
    ///     Asks about each open request in a terminal: allow once, always (with which rule) or deny.
    /// </summary>
    /// <param name="output">The output, for asking.</param>
    /// <param name="choices">The choices, added to.</param>
    /// <param name="open">The open requests.</param>
    /// <param name="scope">Where always rules are saved.</param>
    private static void AskInTerminal(AbstractOutputWriter output, Dictionary<int, PermissionChoice> choices, IReadOnlyList<PermissionRequest> open, PermissionScope scope)
    {
        if (!output.IsInteractive || Console.IsInputRedirected)
        {
            return;
        }

        foreach (var request in open)
        {
            output.Info($"{request.Index}. {request.Denial.Display}");
            var answer = output.Ask("Allow [o]nce, [a]lways, or [d]eny? (enter to stop)")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(answer) || !Answers.TryGetValue(answer[0], out var decision))
            {
                return;
            }

            string? rule = null;
            if (decision != PermissionDecision.Deny && request.Denial.SuggestedRules.Count > 1)
            {
                var rules = request.Denial.SuggestedRules;
                output.Info(string.Join("\n", rules.Select((r, i) => $"   {i + 1}) {r}")));
                var pick = output.Ask("Which rule? (number or your own rule, enter for 1)")?.Trim();
                rule = string.IsNullOrEmpty(pick) ? null
                    : int.TryParse(pick, out var n) && n >= 1 && n <= rules.Count ? rules[n - 1]
                    : pick;
            }

            choices[request.Index] = new PermissionChoice(request.Index, decision, rule, scope);
        }
    }
}
