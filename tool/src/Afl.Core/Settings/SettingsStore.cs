using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Permissions;
using Afl.Core.Storage;
using Afl.Core.Workflows;

namespace Afl.Core.Settings;

/// <summary>
///     Reads and writes afl's global settings (<c>settings.json</c>).
/// </summary>
/// <param name="paths">The afl home paths.</param>
public class SettingsStore(AflPaths paths) : AbstractJsonStore<AflSettings>(paths.SettingsFile)
{
    /// <summary>
    ///     Gets the main agent for new runs.
    /// </summary>
    /// <returns>The agent id.</returns>
    public string DefaultAgent()
    {
        return this.Load().DefaultAgent ?? ExecutorRegistry.DefaultExecutor;
    }

    /// <summary>
    ///     Checks and saves the settings.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The saved settings.</returns>
    /// <exception cref="AflException">A setting is invalid.</exception>
    public AflSettings Save(AflSettings settings)
    {
        var problems = Validate(settings);
        if (problems.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput, string.Join(" ", problems));
        }

        return this.Update(document =>
        {
            document.DefaultAgent = settings.DefaultAgent;
            document.DefaultRunMode = settings.DefaultRunMode;
            document.DefaultSelector = settings.DefaultSelector;
            document.AgentPreference = settings.AgentPreference.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            document.Capabilities = settings.Capabilities;
            document.ScratchDirectory = settings.ScratchDirectory;
            document.DefaultPermissionPolicy = settings.DefaultPermissionPolicy;
            document.GuardRules = settings.GuardRules.Select(r => r.Trim()).Distinct().ToList();
            document.NotifyOn = settings.NotifyOn.Distinct().ToList();
            return document;
        });
    }

    /// <summary>
    ///     Checks settings, returning what's wrong with them.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The problems; empty when they're valid.</returns>
    public static IReadOnlyList<string> Validate(AflSettings settings)
    {
        var problems = new List<string>();
        if (!RunModeRegistry.Instance.Contains(settings.DefaultRunMode))
        {
            problems.Add($"defaultRunMode must be one of {string.Join(", ", RunModeRegistry.Instance.Keys.Order())}.");
        }

        if (!AgentSelectorRegistry.Instance.Contains(settings.DefaultSelector))
        {
            problems.Add($"defaultSelector must be one of {string.Join(", ", AgentSelectorRegistry.Instance.Keys.Order())}.");
        }

        if (!PermissionPolicyRegistry.Instance.Contains(settings.DefaultPermissionPolicy))
        {
            problems.Add($"defaultPermissionPolicy must be one of {string.Join(", ", PermissionPolicyRegistry.Instance.Keys.Order())}.");
        }
        else if (!PermissionPolicyRegistry.Instance.Create(settings.DefaultPermissionPolicy).AllowedAsDefault)
        {
            problems.Add($"'{settings.DefaultPermissionPolicy}' can't be the default permission policy; choose it when you start a run.");
        }

        problems.AddRange(settings.GuardRules.Select(PermissionRule.Problem).OfType<string>());

        foreach (var capability in settings.Capabilities)
        {
            if (!WorkflowLibrary.IsValidId(capability.Id))
            {
                problems.Add($"'{capability.Id}' isn't a usable capability id. Use lowercase letters, digits and dashes.");
            }
            else if (CapabilityCatalog.BuiltIns.Any(c => c.Id == capability.Id))
            {
                problems.Add($"'{capability.Id}' is a built-in capability.");
            }
        }

        var duplicates = settings.Capabilities.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            problems.Add($"capabilities listed twice: {string.Join(", ", duplicates)}.");
        }

        return problems;
    }
}
