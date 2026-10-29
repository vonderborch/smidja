using System.Collections.ObjectModel;
using Afl.Core.Permissions;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An editable list of permission rules, shown by <c>Views/PermissionRulesView</c>: the rules (allow or deny), a box
///     to add one, and Remove and "Change to allow/deny" on the picked rule. Where the rules are kept is up to the
///     subclass; the owner passes its <c>Try</c> so a failure lands in its error message.
/// </summary>
/// <param name="store">The permission rule store.</param>
/// <param name="attempt">Runs a change, showing any failure; returns whether it worked.</param>
public abstract class AbstractPermissionRuleListDataContext(PermissionRuleStore store, Func<Action, bool> attempt)
    : ReactiveObject
{
    /// <summary>
    ///     Gets the permission rule store.
    /// </summary>
    public PermissionRuleStore Store { get; } = store;

    /// <summary>
    ///     Gets the rules.
    /// </summary>
    public ObservableCollection<PermissionRule> Rules { get; } = [];

    /// <summary>
    ///     Gets a value indicating whether a rule added here can be an allow rule; false for lists of deny rules only.
    /// </summary>
    public virtual bool CanAllow => true;

    /// <summary>
    ///     Gets a value indicating whether the list can be put back to afl's defaults.
    /// </summary>
    public virtual bool CanReset => false;

    /// <summary>
    ///     Gets the text shown when the list is empty.
    /// </summary>
    public abstract string EmptyText { get; }

    /// <summary>
    ///     Gets a value indicating whether the list is empty.
    /// </summary>
    public bool IsEmpty => this.Rules.Count == 0;

    /// <summary>
    ///     Gets or sets the picked rule.
    /// </summary>
    public PermissionRule? Selected
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.HasSelected));
            this.RaisePropertyChanged(nameof(this.ToggleText));
        }
    }

    /// <summary>
    ///     Gets a value indicating whether a rule is picked.
    /// </summary>
    public bool HasSelected => this.Selected is not null;

    /// <summary>
    ///     Gets the label of the action that turns the picked rule into the other kind.
    /// </summary>
    public string ToggleText => this.Selected?.Kind == PermissionRuleKind.Deny ? "Change to allow" : "Change to deny";

    /// <summary>
    ///     Gets or sets the rule to add.
    /// </summary>
    public string? NewRule
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the rule to add denies rather than allows.
    /// </summary>
    public bool NewRuleDenies
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the outcome of the last change.
    /// </summary>
    public string? Note
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Reads the rules again, keeping the picked one when it's still there.
    /// </summary>
    public void Load()
    {
        var picked = this.Selected?.Rule;
        IReadOnlyList<PermissionRule> rules = [];
        attempt(() => rules = this.Read());
        this.Rules.Clear();
        foreach (var rule in rules)
        {
            this.Rules.Add(rule);
        }

        this.Selected = this.Rules.FirstOrDefault(r => r.Rule == picked);
        this.RaisePropertyChanged(nameof(this.IsEmpty));
    }

    /// <summary>
    ///     Adds <see cref="NewRule" />.
    /// </summary>
    /// <returns>True if it was added.</returns>
    public bool Add()
    {
        var rule = this.NewRule?.Trim() ?? string.Empty;
        var kind = this.NewRuleDenies || !this.CanAllow ? PermissionRuleKind.Deny : PermissionRuleKind.Allow;
        if (!this.Change(() => this.Write(rule, kind), $"Added {rule}."))
        {
            return false;
        }

        this.NewRule = null;
        return true;
    }

    /// <summary>
    ///     Removes the picked rule.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool Remove()
    {
        return this.Selected is { } rule && this.Change(() => this.Delete(rule.Rule), $"Removed {rule.Rule}.");
    }

    /// <summary>
    ///     Turns the picked rule from allow into deny, or back.
    /// </summary>
    /// <returns>True if it changed.</returns>
    public bool Toggle()
    {
        if (this.Selected is not { } rule || !this.CanAllow)
        {
            return false;
        }

        var kind = rule.Kind == PermissionRuleKind.Deny ? PermissionRuleKind.Allow : PermissionRuleKind.Deny;
        return this.Change(() => this.Write(rule.Rule, kind), $"{rule.Rule} now {(kind == PermissionRuleKind.Deny ? "denies" : "allows")}.");
    }

    /// <summary>
    ///     Puts the list back to afl's defaults, where it has any.
    /// </summary>
    /// <returns>True if it was reset.</returns>
    public bool Reset()
    {
        return this.CanReset && this.Change(this.ResetRules, "Back to afl's defaults.");
    }

    /// <summary>
    ///     Reads the rules from where they're kept.
    /// </summary>
    /// <returns>The rules.</returns>
    protected abstract IReadOnlyList<PermissionRule> Read();

    /// <summary>
    ///     Adds a rule, or changes its kind.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="kind">Allow or deny.</param>
    protected abstract void Write(string rule, PermissionRuleKind kind);

    /// <summary>
    ///     Removes a rule.
    /// </summary>
    /// <param name="rule">The rule.</param>
    protected abstract void Delete(string rule);

    /// <summary>
    ///     Puts the list back to afl's defaults; only called when <see cref="CanReset" />.
    /// </summary>
    protected virtual void ResetRules()
    {
    }

    /// <summary>
    ///     Makes a change and reloads, noting what happened.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="done">The note when it worked.</param>
    /// <returns>True if it worked.</returns>
    private bool Change(Action change, string done)
    {
        if (!attempt(change))
        {
            this.Note = null;
            return false;
        }

        this.Load();
        this.Note = done;
        return true;
    }
}

/// <summary>
///     The rules saved for every run in one repo, or for every run anywhere.
/// </summary>
/// <param name="store">The permission rule store.</param>
/// <param name="attempt">Runs a change, showing any failure.</param>
/// <param name="scope">Repo or global.</param>
/// <param name="repoId">The repo, for the repo scope.</param>
public class ScopedPermissionRuleListDataContext(
    PermissionRuleStore store,
    Func<Action, bool> attempt,
    PermissionScope scope,
    string? repoId = null) : AbstractPermissionRuleListDataContext(store, attempt)
{
    /// <summary>
    ///     Gets where the rules are saved.
    /// </summary>
    public PermissionScope Scope { get; } = scope;

    /// <summary>
    ///     Gets the repo, for the repo scope.
    /// </summary>
    public string? RepoId { get; } = repoId;

    /// <inheritdoc />
    public override string EmptyText => this.Scope == PermissionScope.Global
        ? "No rules for every repo yet. Allow a command with Always allow, or add one here."
        : "No rules for this repo yet. Allow a command with Always allow, or add one here.";

    /// <inheritdoc />
    protected override IReadOnlyList<PermissionRule> Read()
    {
        return this.Store.In(this.Scope, this.RepoId);
    }

    /// <inheritdoc />
    protected override void Write(string rule, PermissionRuleKind kind)
    {
        this.Store.Add(this.Scope, this.RepoId, rule, kind, "Added in the app");
    }

    /// <inheritdoc />
    protected override void Delete(string rule)
    {
        this.Store.Remove(this.Scope, this.RepoId, rule);
    }
}

/// <summary>
///     The guard rails: deny rules every run gets, whatever its permission policy.
/// </summary>
/// <param name="store">The permission rule store.</param>
/// <param name="attempt">Runs a change, showing any failure.</param>
public class GuardRuleListDataContext(PermissionRuleStore store, Func<Action, bool> attempt)
    : AbstractPermissionRuleListDataContext(store, attempt)
{
    /// <inheritdoc />
    public override bool CanAllow => false;

    /// <inheritdoc />
    public override bool CanReset => true;

    /// <inheritdoc />
    public override string EmptyText => "No guard rails: Full auto runs may do anything. Reset to get afl's defaults back.";

    /// <inheritdoc />
    protected override IReadOnlyList<PermissionRule> Read()
    {
        return this.Store.Guards().Select(g => new PermissionRule { Rule = g, Kind = PermissionRuleKind.Deny }).ToList();
    }

    /// <inheritdoc />
    protected override void Write(string rule, PermissionRuleKind kind)
    {
        this.Store.SetGuards([.. this.Store.Guards(), rule]);
    }

    /// <inheritdoc />
    protected override void Delete(string rule)
    {
        this.Store.SetGuards(this.Store.Guards().Where(g => g != rule));
    }

    /// <inheritdoc />
    protected override void ResetRules()
    {
        this.Store.SetGuards(null);
    }
}
