namespace Afl.Core.Registration;

/// <summary>
///     Base for attributes that register a concrete class with a <see cref="TypeRegistry{TBase, TAttribute}" />.
/// </summary>
/// <param name="key">The key the class is registered under.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public abstract class AbstractRegistrationAttribute(string key) : Attribute
{
    /// <summary>
    ///     Gets the key the class is registered under.
    /// </summary>
    public string Key { get; } = key;
}
