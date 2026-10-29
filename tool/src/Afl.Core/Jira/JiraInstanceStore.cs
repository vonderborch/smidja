using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Storage;
using Niddy.Core.Security;

namespace Afl.Core.Jira;

/// <summary>
///     The Jira instances afl knows about (<c>jira.json</c>), and their tokens. Instance details are plain JSON; tokens go
///     to the OS secure store (Keychain, Credential Manager, Secret Service or an encrypted file) under
///     <c>jira:&lt;instanceId&gt;</c>, and an <c>AFL_JIRA_TOKEN_&lt;ID&gt;</c> environment variable overrides the stored one.
/// </summary>
public partial class JiraInstanceStore : AbstractJsonStore<JiraInstanceDocument>
{
    /// <summary>
    ///     The service name tokens are stored under.
    /// </summary>
    public const string SecureStorageService = "afl";

    /// <summary>
    ///     One HTTP client for the process, as recommended for <see cref="HttpClient" />.
    /// </summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

    /// <summary>
    ///     The secure store, created on first use so listing instances never touches the keychain.
    /// </summary>
    private readonly Lazy<ISecureStorage> _secrets;

    /// <summary>
    ///     Initializes a new instance of the <see cref="JiraInstanceStore" /> class.
    /// </summary>
    /// <param name="paths">The afl home paths.</param>
    /// <param name="secrets">The secure store to keep tokens in; defaults to the platform's.</param>
    public JiraInstanceStore(AflPaths paths, ISecureStorage? secrets = null) : base(paths.JiraFile)
    {
        this._secrets = new Lazy<ISecureStorage>(() => secrets ?? SecureStorageFactory.Create(paths.Root, SecureStorageService));
    }

    /// <summary>
    ///     Returns every instance, by id.
    /// </summary>
    /// <returns>The instances.</returns>
    public IReadOnlyList<JiraInstance> All()
    {
        return this.Load().Instances.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     Finds an instance by id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The instance, or null.</returns>
    public JiraInstance? Find(string id)
    {
        return this.Load().Instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Finds an instance by id, or fails.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The instance.</returns>
    /// <exception cref="AflException">No instance has that id.</exception>
    public JiraInstance GetRequired(string id)
    {
        return this.Find(id) ?? throw new AflException(AflErrorKind.NotFound,
            $"No Jira instance '{id}'. Known: {string.Join(", ", this.All().Select(i => i.Id).DefaultIfEmpty("none"))}.");
    }

    /// <summary>
    ///     Finds the instance serving a URL (for tickets given as Jira links).
    /// </summary>
    /// <param name="url">A URL on the instance.</param>
    /// <returns>The instance, or null.</returns>
    public JiraInstance? FindByUrl(string url)
    {
        var target = NormalizeUrl(url);
        return this.Load().Instances
            .Where(i => target.StartsWith(NormalizeUrl(i.BaseUrl), StringComparison.OrdinalIgnoreCase))
            .MaxBy(i => i.BaseUrl.Length);
    }

    /// <summary>
    ///     Adds or replaces an instance, and stores its token when given one.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="token">The token, or null to keep the stored one.</param>
    /// <returns>The saved instance.</returns>
    /// <exception cref="AflException">The instance is invalid.</exception>
    public JiraInstance Save(JiraInstance instance, string? token)
    {
        Validate(instance);
        instance.BaseUrl = instance.BaseUrl.Trim().TrimEnd('/');
        var saved = this.Update(document =>
        {
            var existing = document.Instances.FindIndex(i => string.Equals(i.Id, instance.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                instance.AddedAt = document.Instances[existing].AddedAt;
                document.Instances[existing] = instance;
            }
            else
            {
                instance.AddedAt = DateTimeOffset.Now;
                document.Instances.Add(instance);
            }

            return instance;
        });

        if (!string.IsNullOrWhiteSpace(token))
        {
            this.SetToken(saved.Id, token.Trim());
        }

        return saved;
    }

    /// <summary>
    ///     Removes an instance and its stored token.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True if it existed.</returns>
    public bool Remove(string id)
    {
        var removed = this.Update(document =>
            document.Instances.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)) > 0);
        if (removed)
        {
            this.SetToken(id, null);
        }

        return removed;
    }

    /// <summary>
    ///     Stores, or with null deletes, an instance's token.
    /// </summary>
    /// <param name="id">The instance id.</param>
    /// <param name="token">The token.</param>
    public void SetToken(string id, string? token)
    {
        if (token is null && !this._secrets.Value.TokenExists(SecretKey(id)))
        {
            return;
        }

        this._secrets.Value.SetToken(SecretKey(id), token);
    }

    /// <summary>
    ///     Returns whether a token is available for an instance, from the environment or the secure store.
    /// </summary>
    /// <param name="id">The instance id.</param>
    /// <returns>True if there is a token.</returns>
    public bool HasToken(string id)
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenVariable(id))) ||
               this._secrets.Value.TokenExists(SecretKey(id));
    }

    /// <summary>
    ///     Returns an instance's token: the environment override if set, otherwise the stored one.
    /// </summary>
    /// <param name="id">The instance id.</param>
    /// <returns>The token.</returns>
    /// <exception cref="AflException">There is no token.</exception>
    public string ResolveToken(string id)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(TokenVariable(id));
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return this._secrets.Value.GetToken(SecretKey(id)) ?? throw new AflException(AflErrorKind.InvalidState,
            $"No token for Jira '{id}'. Add one with: afl jira instance add {id} … (or set {TokenVariable(id)}).");
    }

    /// <summary>
    ///     Creates the client for an instance, using its token.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="http">The HTTP client to use; defaults to a shared one.</param>
    /// <returns>The client.</returns>
    public AbstractJiraClient CreateClient(JiraInstance instance, HttpClient? http = null)
    {
        return this.CreateClient(instance, this.ResolveToken(instance.Id), http);
    }

    /// <summary>
    ///     Creates the client for an instance with a given token (e.g. to test a token before saving it).
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="token">The token.</param>
    /// <param name="http">The HTTP client to use; defaults to a shared one.</param>
    /// <returns>The client.</returns>
    public virtual AbstractJiraClient CreateClient(JiraInstance instance, string token, HttpClient? http = null)
    {
        return JiraClientRegistry.Instance.Create(instance, token, http ?? SharedHttp.Value);
    }

    /// <summary>
    ///     Returns the environment variable that overrides an instance's token.
    /// </summary>
    /// <param name="id">The instance id.</param>
    /// <returns>The variable name, e.g. <c>AFL_JIRA_TOKEN_WORK</c>.</returns>
    public static string TokenVariable(string id)
    {
        return EnvironmentConstants.JiraTokenPrefix + NonIdentifierRegex().Replace(id.ToUpperInvariant(), "_");
    }

    /// <summary>
    ///     Checks an instance's fields.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <exception cref="AflException">A field is missing or malformed.</exception>
    public static void Validate(JiraInstance instance)
    {
        if (!IdRegex().IsMatch(instance.Id))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"'{instance.Id}' isn't a usable instance id. Use letters, digits, '-' and '_'.");
        }

        if (!Uri.TryCreate(instance.BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"'{instance.BaseUrl}' isn't an http(s) URL.");
        }

        if (instance.Kind == JiraInstanceKind.Cloud && string.IsNullOrWhiteSpace(instance.Email))
        {
            throw new AflException(AflErrorKind.InvalidInput, "Jira Cloud needs the account email (--email) to go with the API token.");
        }
    }

    /// <summary>
    ///     Returns the secure-store key for an instance's token.
    /// </summary>
    /// <param name="id">The instance id.</param>
    /// <returns>The key.</returns>
    private static string SecretKey(string id)
    {
        return $"jira:{id.ToLowerInvariant()}";
    }

    /// <summary>
    ///     Normalizes a URL for prefix comparison: no scheme, no trailing slash.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The normalized URL.</returns>
    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        return (scheme >= 0 ? trimmed[(scheme + 3)..] : trimmed).TrimEnd('/') + "/";
    }

    /// <summary>
    ///     Matches a valid instance id.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex IdRegex();

    /// <summary>
    ///     Matches characters that can't appear in an environment variable name.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("[^A-Z0-9_]")]
    private static partial Regex NonIdentifierRegex();
}
