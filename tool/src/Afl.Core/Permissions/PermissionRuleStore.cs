using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Repos;
using Afl.Core.Settings;
using Afl.Core.Storage;

namespace Afl.Core.Permissions;

/// <summary>
///     Reads and writes permission rules: the global ones (<c>~/.afl/permissions.json</c>) here, a repo's through
///     <see cref="RepoRecord.PermissionRules" />, and the guard rails through <see cref="AflSettings.GuardRules" />.
///     A run's own rules live on the run and are changed by <see cref="Runs.RunEngine" />.
/// </summary>
/// <param name="paths">The afl home paths.</param>
public class PermissionRuleStore(AflPaths paths) : AbstractJsonStore<PermissionRuleDocument>(paths.PermissionsFile)
{
    /// <summary>
    ///     Gets the registered repos, for repo rules.
    /// </summary>
    public RepoRegistry Repos { get; init; } = new(paths);

    /// <summary>
    ///     Gets the settings, for the guard rails.
    /// </summary>
    public SettingsStore Settings { get; init; } = new(paths);

    /// <summary>
    ///     Gets the global rules.
    /// </summary>
    /// <returns>The rules.</returns>
    public IReadOnlyList<PermissionRule> Global()
    {
        return this.Load().Rules;
    }

    /// <summary>
    ///     Gets the rules saved for a scope. Run rules aren't kept here; ask the run.
    /// </summary>
    /// <param name="scope">The scope: repo or global.</param>
    /// <param name="repoId">The repo, for the repo scope.</param>
    /// <returns>The rules.</returns>
    public IReadOnlyList<PermissionRule> In(PermissionScope scope, string? repoId)
    {
        return scope switch
        {
            PermissionScope.Global => this.Global(),
            PermissionScope.Repo => this.Repos.GetRequired(RequireRepo(repoId)).PermissionRules,
            _ => throw new AflException(AflErrorKind.InvalidInput, "A run's rules are listed with the run."),
        };
    }

    /// <summary>
    ///     Saves a rule to the repo or global scope.
    /// </summary>
    /// <param name="scope">Repo or global.</param>
    /// <param name="repoId">The repo, for the repo scope.</param>
    /// <param name="rule">The rule.</param>
    /// <param name="kind">Allow or deny.</param>
    /// <param name="note">Why it was added.</param>
    /// <returns>True if it was added or changed.</returns>
    public bool Add(PermissionScope scope, string? repoId, string rule, PermissionRuleKind kind, string? note = null)
    {
        if (scope == PermissionScope.Global)
        {
            return this.Update(document => PermissionRule.AddTo(document.Rules, rule, kind, note));
        }

        if (scope != PermissionScope.Repo)
        {
            throw new AflException(AflErrorKind.InvalidInput, "A run's rules are added through the run.");
        }

        var changed = false;
        this.Repos.Modify(this.Repos.GetRequired(RequireRepo(repoId)).Id,
            repo => changed = PermissionRule.AddTo(repo.PermissionRules, rule, kind, note));
        return changed;
    }

    /// <summary>
    ///     Removes a rule from the repo or global scope.
    /// </summary>
    /// <param name="scope">Repo or global.</param>
    /// <param name="repoId">The repo, for the repo scope.</param>
    /// <param name="rule">The rule.</param>
    /// <returns>True if it was there.</returns>
    public bool Remove(PermissionScope scope, string? repoId, string rule)
    {
        if (scope == PermissionScope.Global)
        {
            return this.Update(document => document.Rules.RemoveAll(r => r.Rule == rule.Trim()) > 0);
        }

        if (scope != PermissionScope.Repo)
        {
            throw new AflException(AflErrorKind.InvalidInput, "A run's rules are removed through the run.");
        }

        var removed = false;
        this.Repos.Modify(this.Repos.GetRequired(RequireRepo(repoId)).Id,
            repo => removed = repo.PermissionRules.RemoveAll(r => r.Rule == rule.Trim()) > 0);
        return removed;
    }

    /// <summary>
    ///     Gets the guard rails: deny rules every run gets, whatever its policy.
    /// </summary>
    /// <returns>The rules.</returns>
    public IReadOnlyList<string> Guards()
    {
        return this.Settings.Load().GuardRules;
    }

    /// <summary>
    ///     Replaces the guard rails.
    /// </summary>
    /// <param name="rules">The new rules; null for afl's defaults.</param>
    /// <returns>The rules saved.</returns>
    public IReadOnlyList<string> SetGuards(IEnumerable<string>? rules)
    {
        var list = (rules ?? PermissionConstants.DefaultGuardRules).Select(r => r.Trim()).Distinct().ToList();
        var problem = list.Select(PermissionRule.Problem).FirstOrDefault(p => p is not null);
        if (problem is not null)
        {
            throw new AflException(AflErrorKind.InvalidInput, problem);
        }

        return this.Settings.Update(document =>
        {
            document.GuardRules = list;
            return document.GuardRules;
        });
    }

    /// <summary>
    ///     Gets every rule that applies to a run: its own, its repo's, the global ones and the guard rails.
    /// </summary>
    /// <param name="runRules">The run's own rules.</param>
    /// <param name="repoId">The run's repo.</param>
    /// <param name="once">Rules allowed for the next turn only.</param>
    /// <returns>The merged rules.</returns>
    public PermissionRuleSet For(IEnumerable<PermissionRule> runRules, string? repoId, IEnumerable<string>? once = null)
    {
        var repo = repoId is null ? null : this.Repos.Find(repoId);
        return PermissionRuleSet.Merge(this.Guards(), runRules, repo?.PermissionRules ?? [], this.Global(), once ?? []);
    }

    /// <summary>
    ///     Insists on a repo for the repo scope.
    /// </summary>
    /// <param name="repoId">The repo, or null.</param>
    /// <returns>The repo.</returns>
    private static string RequireRepo(string? repoId)
    {
        return string.IsNullOrWhiteSpace(repoId)
            ? throw new AflException(AflErrorKind.InvalidInput, "Say which repo the rule is for.")
            : repoId;
    }
}

/// <summary>
///     The permission rules that apply to one turn of an agent, merged from every scope.
/// </summary>
/// <param name="Allowed">The allow rules, run scope first.</param>
/// <param name="Denied">The deny rules, guard rails first.</param>
public record PermissionRuleSet(IReadOnlyList<string> Allowed, IReadOnlyList<string> Denied)
{
    /// <summary>
    ///     Gets a rule set with nothing in it.
    /// </summary>
    public static PermissionRuleSet Empty { get; } = new([], []);

    /// <summary>
    ///     Merges rules from every scope.
    /// </summary>
    /// <param name="guards">The guard rails.</param>
    /// <param name="run">The run's rules.</param>
    /// <param name="repo">The repo's rules.</param>
    /// <param name="global">The global rules.</param>
    /// <param name="once">Rules allowed for the next turn only.</param>
    /// <returns>The merged set.</returns>
    public static PermissionRuleSet Merge(
        IEnumerable<string> guards,
        IEnumerable<PermissionRule> run,
        IEnumerable<PermissionRule> repo,
        IEnumerable<PermissionRule> global,
        IEnumerable<string> once)
    {
        var rules = run.Concat(repo).Concat(global).ToList();
        var allowed = once.Concat(rules.Where(r => r.Kind == PermissionRuleKind.Allow).Select(r => r.Rule));
        var denied = guards.Concat(rules.Where(r => r.Kind == PermissionRuleKind.Deny).Select(r => r.Rule));
        return new PermissionRuleSet(
            allowed.Where(PermissionRule.IsValid).Distinct().ToList(),
            denied.Where(PermissionRule.IsValid).Distinct().ToList());
    }
}
