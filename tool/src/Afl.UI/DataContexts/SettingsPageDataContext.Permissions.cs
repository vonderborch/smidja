using Afl.Core.Permissions;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     The Permissions tab of <see cref="SettingsPageDataContext" />: the policy new runs start with, the rules for every
///     repo, and the guard rails every run gets.
/// </summary>
public partial class SettingsPageDataContext
{
    /// <summary>
    ///     Gets the policies that can be the default; those that let agents do anything are chosen per run.
    /// </summary>
    public IReadOnlyList<SettingChoice> DefaultPolicies { get; } = PermissionPolicyRegistry.Instance.All
        .Where(p => p.AllowedAsDefault).Select(p => new SettingChoice(p.Name, p.DisplayName, p.Description)).ToList();

    /// <summary>
    ///     Gets the policies chosen per run only, described for the tab.
    /// </summary>
    public string PerRunPolicies => string.Join("\n", PermissionPolicyRegistry.Instance.All.Where(p => !p.AllowedAsDefault)
        .Select(p => $"{p.DisplayName} is chosen when you start a run: {p.Description}"));

    /// <summary>
    ///     Gets or sets the policy new runs start with.
    /// </summary>
    public SettingChoice? DefaultPolicy
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (!this._loading && value is not null)
            {
                this.SavePermissionPolicy();
            }
        }
    }

    /// <summary>
    ///     Gets the rules for every run in every repo.
    /// </summary>
    public AbstractPermissionRuleListDataContext GlobalRules => field ??=
        new ScopedPermissionRuleListDataContext(this.Shell.Permissions, this.Try, PermissionScope.Global);

    /// <summary>
    ///     Gets the guard rails.
    /// </summary>
    public AbstractPermissionRuleListDataContext GuardRules => field ??=
        new GuardRuleListDataContext(this.Shell.Permissions, this.Try);

    /// <summary>
    ///     Saves the default policy.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public bool SavePermissionPolicy()
    {
        var name = this.DefaultPolicy?.Value;
        return name is not null && this.SaveSettings(settings => settings.DefaultPermissionPolicy = name);
    }

    /// <summary>
    ///     Loads the permission settings and rules.
    /// </summary>
    private void LoadPermissions()
    {
        var current = PermissionPolicyRegistry.Instance.Resolve(this.Shell.Settings.Load().DefaultPermissionPolicy).Name;
        this.DefaultPolicy = this.DefaultPolicies.FirstOrDefault(p => p.Value == current) ?? this.DefaultPolicies.FirstOrDefault();
        this.GlobalRules.Load();
        this.GuardRules.Load();
    }
}
