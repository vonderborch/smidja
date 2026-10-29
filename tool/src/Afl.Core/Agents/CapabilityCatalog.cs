using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Settings;
using Afl.Core.Storage;

namespace Afl.Core.Agents;

/// <summary>
///     Something an agent can do or is good at, such as <c>edit-code</c> or <c>review</c>. Steps ask for capabilities
///     instead of naming agents, so a workflow works with whatever agents a machine has.
/// </summary>
public class CapabilityDefinition
{
    /// <summary>
    ///     Gets or sets the id, used by agents and steps.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets what it means; shown to people and to the main agent when it chooses an agent.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
///     The embedded capability list (<c>agents/capabilities.json</c>).
/// </summary>
[DocumentVersion(1, 0)]
public class CapabilityDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the capabilities.
    /// </summary>
    public List<CapabilityDefinition> Capabilities { get; set; } = [];
}

/// <summary>
///     The capabilities afl knows: the built-in ones (<c>agents/capabilities.json</c>) and the user's own
///     (<see cref="AflSettings.Capabilities" />).
/// </summary>
/// <param name="custom">The user's own capabilities.</param>
public class CapabilityCatalog(IEnumerable<CapabilityDefinition> custom)
{
    /// <summary>
    ///     The capability afl adds to agents that can open interactive sessions.
    /// </summary>
    public const string Interactive = "interactive";

    /// <summary>
    ///     The capability afl adds to agents that can continue a conversation.
    /// </summary>
    public const string Resume = "resume";

    /// <summary>
    ///     The capability afl adds to agents that honour permission modes and allowed tools.
    /// </summary>
    public const string Permissions = "permissions";

    /// <summary>
    ///     The capability afl adds to agents that can be given MCP servers as tools.
    /// </summary>
    public const string McpTools = "mcp-tools";

    /// <summary>
    ///     The built-in capabilities, read once.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<CapabilityDefinition>> BuiltInList = new(ReadBuiltIns);

    /// <summary>
    ///     Gets the built-in capabilities.
    /// </summary>
    public static IReadOnlyList<CapabilityDefinition> BuiltIns => BuiltInList.Value;

    /// <summary>
    ///     Gets every capability: the built-ins, then the user's.
    /// </summary>
    public IReadOnlyList<CapabilityDefinition> All { get; } =
        BuiltInList.Value.Concat(custom.Where(c => BuiltInList.Value.All(b => b.Id != c.Id))).ToList();

    /// <summary>
    ///     Creates the catalogue for an afl home, with the user's capabilities from its settings.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <returns>The catalogue.</returns>
    public static CapabilityCatalog For(SettingsStore settings)
    {
        return new CapabilityCatalog(settings.Load().Capabilities);
    }

    /// <summary>
    ///     Returns whether a capability is known.
    /// </summary>
    /// <param name="id">The capability id.</param>
    /// <returns>True if it's built in or the user's.</returns>
    public bool Contains(string id)
    {
        return this.All.Any(c => c.Id == id);
    }

    /// <summary>
    ///     Returns a capability's description, or the id when it's unknown.
    /// </summary>
    /// <param name="id">The capability id.</param>
    /// <returns>The description.</returns>
    public string Describe(string id)
    {
        return this.All.FirstOrDefault(c => c.Id == id)?.Description ?? id;
    }

    /// <summary>
    ///     Reads the embedded built-in capabilities.
    /// </summary>
    /// <returns>The capabilities.</returns>
    private static IReadOnlyList<CapabilityDefinition> ReadBuiltIns()
    {
        using var stream = typeof(CapabilityCatalog).Assembly.GetManifestResourceStream(PathConstants.CapabilitiesResourceName);
        if (stream is null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<CapabilityDocument>(stream, JsonConstants.Options)?.Capabilities ?? [];
    }
}
