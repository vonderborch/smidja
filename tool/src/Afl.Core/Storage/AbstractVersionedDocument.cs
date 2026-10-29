using System.Reflection;
using System.Text.Json.Serialization;
using Afl.Core.Constants;

namespace Afl.Core.Storage;

/// <summary>
///     Declares the current schema version of a <see cref="AbstractVersionedDocument" /> subclass. When the format of a
///     document changes, bump this; afl doesn't upgrade older files, so a document from before the change is rewritten by
///     hand.
/// </summary>
/// <param name="major">The major version.</param>
/// <param name="minor">The minor version.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class DocumentVersionAttribute(int major, int minor) : Attribute
{
    /// <summary>
    ///     Gets the schema version.
    /// </summary>
    public Version Version { get; } = new(major, minor);
}

/// <summary>
///     Base for every document afl persists. Each document records the schema version it was written with, so a store
///     can refuse one from a newer afl.
/// </summary>
public abstract class AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the schema version the document was written with.
    /// </summary>
    [JsonPropertyName(JsonConstants.SchemaVersionPropertyName)]
    [JsonPropertyOrder(-100)]
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>
    ///     Returns the current schema version of a document type, from its <see cref="DocumentVersionAttribute" />.
    /// </summary>
    /// <param name="documentType">The document type.</param>
    /// <returns>The version; 1.0 when the type isn't annotated.</returns>
    public static Version CurrentVersionOf(Type documentType)
    {
        return documentType.GetCustomAttribute<DocumentVersionAttribute>()?.Version ?? new Version(1, 0);
    }
}
