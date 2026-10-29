using Afl.Core.Agents;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Settings;

namespace Afl.Cli.Output;

/// <summary>
///     afl's global settings as the CLI shows them.
/// </summary>
/// <param name="DefaultAgent">The main agent for new runs (the effective one when none is set).</param>
/// <param name="DefaultRunMode">How agents' commands run by default.</param>
/// <param name="DefaultSelector">How a step picks between qualifying agents by default.</param>
/// <param name="AgentPreference">The order agents are preferred in.</param>
/// <param name="Capabilities">The user's own capabilities.</param>
/// <param name="ScratchDirectory">The scratch directory at each repo root.</param>
/// <param name="DefaultPermissions">The permission policy new runs get unless they pick one.</param>
/// <param name="GuardRules">The deny rules every run gets, whatever its policy.</param>
/// <param name="NotifyOn">The run states that show a desktop notification (CLI names).</param>
public record SettingsView(
    string DefaultAgent,
    string DefaultRunMode,
    string DefaultSelector,
    IReadOnlyList<string> AgentPreference,
    IReadOnlyList<CapabilityView> Capabilities,
    string ScratchDirectory,
    string DefaultPermissions,
    IReadOnlyList<string> GuardRules,
    IReadOnlyList<string> NotifyOn)
{
    /// <summary>
    ///     Creates the view for the settings.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="defaultAgent">The effective main agent.</param>
    /// <returns>The view.</returns>
    public static SettingsView From(AflSettings settings, string defaultAgent)
    {
        return new SettingsView(defaultAgent, settings.DefaultRunMode, settings.DefaultSelector, settings.AgentPreference,
            settings.Capabilities.Select(CapabilityView.From).ToList(), settings.ScratchDirectory,
            settings.DefaultPermissionPolicy, settings.GuardRules, settings.NotifyOn.Select(s => RunStates.Info(s).CliName).ToList());
    }
}

/// <summary>
///     A capability as the CLI lists it.
/// </summary>
/// <param name="Id">The id.</param>
/// <param name="Description">What an agent with it is good at.</param>
/// <param name="BuiltIn">Whether afl defines it.</param>
public record CapabilityView(string Id, string Description, bool BuiltIn)
{
    /// <summary>
    ///     Creates the view for a capability.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <returns>The view.</returns>
    public static CapabilityView From(CapabilityDefinition capability)
    {
        return new CapabilityView(capability.Id, capability.Description, CapabilityCatalog.BuiltIns.Any(c => c.Id == capability.Id));
    }
}

/// <summary>
///     The permission rules the CLI lists: the default policy, the guard rails, the global rules and, inside a repo,
///     the repo's.
/// </summary>
/// <param name="DefaultPolicy">The permission policy new runs get unless they pick one.</param>
/// <param name="Policies">Every policy, by name, with its description.</param>
/// <param name="GuardRules">The deny rules every run gets.</param>
/// <param name="NotifyOn">The run states that show a desktop notification (CLI names).</param>
/// <param name="Global">The rules for every repo.</param>
/// <param name="Repo">The repo the repo rules belong to, or null outside a repo.</param>
/// <param name="RepoRules">The repo's rules.</param>
public record PermissionsView(
    string DefaultPolicy,
    IReadOnlyList<PermissionPolicyView> Policies,
    IReadOnlyList<string> GuardRules,
    IReadOnlyList<PermissionRule> Global,
    string? Repo,
    IReadOnlyList<PermissionRule> RepoRules)
{
    /// <summary>
    ///     Creates the view from the stores.
    /// </summary>
    /// <param name="store">The rule store.</param>
    /// <param name="repo">The repo, or null.</param>
    /// <returns>The view.</returns>
    public static PermissionsView From(PermissionRuleStore store, RepoRecord? repo)
    {
        var policies = PermissionPolicyRegistry.Instance.All
            .Select(p => new PermissionPolicyView(p.Name, p.DisplayName, p.Description, p.AllowedAsDefault))
            .ToList();
        return new PermissionsView(store.Settings.Load().DefaultPermissionPolicy, policies, store.Guards(), store.Global(),
            repo?.Name, repo is null ? [] : store.In(PermissionScope.Repo, repo.Id));
    }
}

/// <summary>
///     A permission policy as the CLI lists it.
/// </summary>
/// <param name="Name">The name <c>--permissions</c> takes.</param>
/// <param name="DisplayName">The name front-ends show.</param>
/// <param name="Description">What it does.</param>
/// <param name="AllowedAsDefault">Whether it can be the default for new runs.</param>
public record PermissionPolicyView(string Name, string DisplayName, string Description, bool AllowedAsDefault);
