using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Runs;
using Afl.Core.Storage;

namespace Afl.Core.Settings;

/// <summary>
///     afl's global settings (<c>settings.json</c>): which agent runs a run by default, how agents' commands run, how
///     steps choose between agents, and the user's own capabilities.
/// </summary>
[DocumentVersion(1, 0)]
public class AflSettings : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the main agent for new runs; null means afl's default (<c>claude</c>).
    /// </summary>
    public string? DefaultAgent { get; set; }

    /// <summary>
    ///     Gets or sets how agents' commands run by default: <c>background</c> or <c>terminal</c>.
    /// </summary>
    public string DefaultRunMode { get; set; } = AgentConstants.RunModeBackground;

    /// <summary>
    ///     Gets or sets how a step picks between the agents that can do it when it doesn't say: <c>ai</c> (the main agent
    ///     chooses) or <c>first</c> (the first in <see cref="AgentPreference" /> order).
    /// </summary>
    public string DefaultSelector { get; set; } = AgentConstants.SelectorAi;

    /// <summary>
    ///     Gets or sets the agent ids in order of preference, for ties and the <c>first</c> selector. Agents not listed
    ///     come after, in the order they're defined.
    /// </summary>
    public List<string> AgentPreference { get; set; } = [];

    /// <summary>
    ///     Gets or sets the user's own capabilities, added to the built-in catalogue.
    /// </summary>
    public List<CapabilityDefinition> Capabilities { get; set; } = [];

    /// <summary>
    ///     Gets or sets the directory at each repo's root where runs keep their working documents (one folder per
    ///     ticket or piece of work); afl makes git ignore it.
    /// </summary>
    public string ScratchDirectory { get; set; } = PathConstants.DefaultScratchDirectory;

    /// <summary>
    ///     Gets or sets the permission policy for new runs that don't choose one: <c>ask</c> or <c>strict</c>. Full auto
    ///     can't be the default; it is chosen per run.
    /// </summary>
    public string DefaultPermissionPolicy { get; set; } = PermissionConstants.DefaultPolicy;

    /// <summary>
    ///     Gets or sets the guard rails: deny rules every run gets, whatever its policy (Claude Code applies them even when
    ///     it bypasses permissions).
    /// </summary>
    public List<string> GuardRules { get; set; } = [..PermissionConstants.DefaultGuardRules];

    /// <summary>
    ///     Gets or sets the run states that show a desktop notification when a run reaches them: by default when a run
    ///     needs you, and when a run you started is done or fails. Empty turns notifications off.
    /// </summary>
    public List<RunState> NotifyOn { get; set; } = [..DefaultNotifyOn];

    /// <summary>
    ///     The states <see cref="NotifyOn" /> starts with.
    /// </summary>
    public static IReadOnlyList<RunState> DefaultNotifyOn { get; } = [RunState.NeedsYou, RunState.Done, RunState.Failed];
}
