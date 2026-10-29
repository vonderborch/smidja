using System.Reflection;
using Afl.Core.Exceptions;

namespace Afl.Core.Registration;

/// <summary>
///     Discovers every concrete subclass of <typeparamref name="TBase" /> tagged with <typeparamref name="TAttribute" />
///     across the loaded assemblies, and creates instances by key. Adding a new ticket source, step handler, or executor
///     is a matter of writing the subclass and tagging it; nothing else needs to change.
/// </summary>
/// <typeparam name="TBase">The abstract base class.</typeparam>
/// <typeparam name="TAttribute">The registration attribute.</typeparam>
public class TypeRegistry<TBase, TAttribute>
    where TBase : class
    where TAttribute : AbstractRegistrationAttribute
{
    /// <summary>
    ///     The registered types by key, built on first use.
    /// </summary>
    private readonly Lazy<IReadOnlyDictionary<string, (TAttribute Attribute, Type Type)>> _types;

    /// <summary>
    ///     A human-readable name for the kind of thing registered, used in error messages.
    /// </summary>
    private readonly string _kindName;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TypeRegistry{TBase, TAttribute}" /> class.
    /// </summary>
    /// <param name="kindName">A human-readable name for the kind of thing registered, e.g. <c>"executor"</c>.</param>
    public TypeRegistry(string kindName)
    {
        this._kindName = kindName;
        this._types = new Lazy<IReadOnlyDictionary<string, (TAttribute, Type)>>(Discover);
    }

    /// <summary>
    ///     Gets the registered keys.
    /// </summary>
    public IReadOnlyCollection<string> Keys => this._types.Value.Keys.ToList();

    /// <summary>
    ///     Gets the registration attributes.
    /// </summary>
    public IReadOnlyCollection<TAttribute> Registrations => this._types.Value.Values.Select(v => v.Attribute).ToList();

    /// <summary>
    ///     Returns whether a class is registered under <paramref name="key" />.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True if registered.</returns>
    public bool Contains(string key)
    {
        return this._types.Value.ContainsKey(key);
    }

    /// <summary>
    ///     Creates a new instance of the class registered under <paramref name="key" />.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="arguments">Constructor arguments, for classes that need them (none for a parameterless constructor).</param>
    /// <returns>The new instance.</returns>
    /// <exception cref="AflException">No class is registered under the key.</exception>
    public TBase Create(string key, params object?[] arguments)
    {
        if (!this._types.Value.TryGetValue(key, out var entry))
        {
            throw new AflException(AflErrorKind.NotFound,
                $"Unknown {this._kindName} '{key}'. Known: {string.Join(", ", this.Keys.Order())}.");
        }

        return this.OnCreated((TBase)Activator.CreateInstance(entry.Type, arguments)!);
    }

    /// <summary>
    ///     Creates one instance of every registered class.
    /// </summary>
    /// <returns>The instances, ordered by key.</returns>
    public IReadOnlyList<TBase> CreateAll()
    {
        return this.Keys.Order().Select(k => this.Create(k)).ToList();
    }

    /// <summary>
    ///     Called with every instance <see cref="Create" /> makes, before it is returned. Override to hand instances
    ///     what this registry was configured with, e.g. the stores they should use.
    /// </summary>
    /// <param name="instance">The new instance.</param>
    /// <returns>The instance to return.</returns>
    protected virtual TBase OnCreated(TBase instance)
    {
        return instance;
    }

    /// <summary>
    ///     Scans the loaded assemblies for registered classes.
    /// </summary>
    /// <returns>The registered types by key.</returns>
    private static IReadOnlyDictionary<string, (TAttribute, Type)> Discover()
    {
        var result = new Dictionary<string, (TAttribute, Type)>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(LoadableTypes))
        {
            if (type.IsAbstract || !typeof(TBase).IsAssignableFrom(type))
            {
                continue;
            }

            var attribute = type.GetCustomAttribute<TAttribute>(false);
            if (attribute is not null)
            {
                result[attribute.Key] = (attribute, type);
            }
        }

        return result;
    }

    /// <summary>
    ///     Returns the types of an assembly, skipping any that fail to load.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>The loadable types.</returns>
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return [];
        }

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
