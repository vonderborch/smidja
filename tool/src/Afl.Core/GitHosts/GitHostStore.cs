using System.Text.RegularExpressions;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Storage;
using Niddy.Core.Security;

namespace Afl.Core.GitHosts;

/// <summary>
///     The GitHub and GitLab sites afl reads issues from (<c>githosts.json</c>), and their tokens. github.com and
///     gitlab.com are always there; Enterprise and self-managed sites are added. Connection details are plain JSON; tokens
///     go to the OS secure store under <c>githost:&lt;id&gt;</c>, and an <c>AFL_GITHOST_TOKEN_&lt;ID&gt;</c> environment
///     variable overrides the stored one. With neither, the cloud sites' standard variables (<c>GH_TOKEN</c>,
///     <c>GITLAB_TOKEN</c>) and then the host's CLI (<c>gh</c>, <c>glab</c>) can lend a sign-in, held only in memory.
/// </summary>
public partial class GitHostStore : AbstractJsonStore<GitHostDocument>
{
    /// <summary>
    ///     The id of the built-in github.com connection.
    /// </summary>
    public const string GitHubId = "github";

    /// <summary>
    ///     The id of the built-in gitlab.com connection.
    /// </summary>
    public const string GitLabId = "gitlab";

    /// <summary>
    ///     One HTTP client for the process, as recommended for <see cref="HttpClient" />.
    /// </summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

    /// <summary>
    ///     The secure store, created on first use so listing connections never touches the keychain.
    /// </summary>
    private readonly Lazy<ISecureStorage> _secrets;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GitHostStore" /> class.
    /// </summary>
    /// <param name="paths">The afl home paths.</param>
    /// <param name="secrets">The secure store to keep tokens in; defaults to the platform's.</param>
    /// <param name="borrowSignIns">
    ///     Whether a sign-in can be borrowed from the hosts' CLIs and their standard variables such as <c>GH_TOKEN</c>
    ///     (off in tests, so they never pick up yours).
    /// </param>
    public GitHostStore(AflPaths paths, ISecureStorage? secrets = null, bool borrowSignIns = true) : base(paths.GitHostsFile)
    {
        this._secrets = new Lazy<ISecureStorage>(() =>
            secrets ?? SecureStorageFactory.Create(paths.Root, Jira.JiraInstanceStore.SecureStorageService));
        this.BorrowsSignIns = borrowSignIns;
        this.CacheDirectory = paths.CacheDirectory;
    }

    /// <summary>
    ///     Gets whether a sign-in can be borrowed from the hosts' CLIs and standard variables.
    /// </summary>
    public bool BorrowsSignIns { get; }

    /// <summary>
    ///     Gets the directory the clients cache responses under, one folder per kind.
    /// </summary>
    public string CacheDirectory { get; }

    /// <summary>
    ///     Returns every connection: the built-in ones first, then the added ones by id.
    /// </summary>
    /// <returns>The connections.</returns>
    public IReadOnlyList<GitHostConnection> All()
    {
        return [.. BuiltIns(), .. this.Load().Connections.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    ///     Finds a connection by id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The connection, or null.</returns>
    public GitHostConnection? Find(string id)
    {
        return this.All().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Finds a connection by id, or fails.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="AflException">No connection has that id.</exception>
    public GitHostConnection GetRequired(string id)
    {
        return this.Find(id) ?? throw new AflException(AflErrorKind.NotFound,
            $"No git host connection '{id}'. Known: {string.Join(", ", this.All().Select(c => c.Id))}.");
    }

    /// <summary>
    ///     Finds the connection serving a URL (for issues given as links): the one whose address is the longest prefix.
    /// </summary>
    /// <param name="url">A URL on the site.</param>
    /// <returns>The connection, or null.</returns>
    public GitHostConnection? FindByUrl(string url)
    {
        var target = NormalizeUrl(url);
        return this.All()
            .Where(c => target.StartsWith(NormalizeUrl(c.BaseUrl), StringComparison.OrdinalIgnoreCase))
            .MaxBy(c => c.BaseUrl.Length);
    }

    /// <summary>
    ///     Finds the connections on a host (for git remotes, which only name the host).
    /// </summary>
    /// <param name="host">The host name.</param>
    /// <returns>The connections, most specific address first.</returns>
    public IReadOnlyList<GitHostConnection> FindByHost(string host)
    {
        return this.All()
            .Where(c => string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.PathPrefix.Length)
            .ToList();
    }

    /// <summary>
    ///     Works out which project a repo's issues come from by matching its remotes (<c>origin</c> first) against the
    ///     connections' hosts.
    /// </summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <returns>The link, or null when no remote is on a known host.</returns>
    public RepoHostLink? DetectLink(string repoRoot)
    {
        foreach (var (_, url) in GitHelpers.GetRemoteUrls(repoRoot))
        {
            if (this.LinkForRemote(url) is { } link)
            {
                return link;
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns the link for one remote URL, if it's on a known host.
    /// </summary>
    /// <param name="remoteUrl">The remote URL.</param>
    /// <returns>The link, or null.</returns>
    public RepoHostLink? LinkForRemote(string remoteUrl)
    {
        if (GitHelpers.ParseRemote(remoteUrl) is not var (host, path))
        {
            return null;
        }

        foreach (var connection in this.FindByHost(host))
        {
            // A site served under a path (https://acme.com/gitlab) has it in front of every project's path.
            var prefix = connection.PathPrefix.Trim('/');
            if (prefix.Length > 0 && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var project = prefix.Length > 0 ? path[(prefix.Length + 1)..] : path;
            if (IsProjectFor(connection, project))
            {
                return new RepoHostLink { ConnectionId = connection.Id, Kind = connection.Kind, Project = project };
            }
        }

        return null;
    }

    /// <summary>
    ///     Builds a link to a project on a connection, checking the project's shape.
    /// </summary>
    /// <param name="connectionId">The connection id.</param>
    /// <param name="project">The project path, e.g. <c>owner/repo</c>; a web URL to it works too.</param>
    /// <returns>The link.</returns>
    /// <exception cref="AflException">The connection is unknown or the project isn't a path on it.</exception>
    public RepoHostLink CreateLink(string connectionId, string project)
    {
        var connection = this.GetRequired(connectionId);
        var path = project.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            var prefix = connection.PathPrefix.Trim('/');
            path = prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? path[(prefix.Length + 1)..] : path;
        }

        path = path.Trim('/');
        path = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        if (!IsProjectFor(connection, path))
        {
            throw new AflException(AflErrorKind.InvalidInput, connection.Kind == Tickets.TicketRef.GitHubSource
                ? $"'{project}' isn't a GitHub repository. Use owner/name."
                : $"'{project}' isn't a GitLab project. Use group/project or group/subgroup/project.");
        }

        return new RepoHostLink { ConnectionId = connection.Id, Kind = connection.Kind, Project = path };
    }

    /// <summary>
    ///     Adds or replaces a connection, and stores its token when given one. For a built-in connection only the token
    ///     is saved.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="token">The token, or null to keep the stored one.</param>
    /// <returns>The saved connection.</returns>
    /// <exception cref="AflException">The connection is invalid.</exception>
    public GitHostConnection Save(GitHostConnection connection, string? token)
    {
        var saved = BuiltIns().FirstOrDefault(c => string.Equals(c.Id, connection.Id, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            Validate(connection);
            connection.BaseUrl = connection.BaseUrl.Trim().TrimEnd('/');
            connection.Kind = connection.Kind.ToLowerInvariant();
            saved = this.Update(document =>
            {
                var existing = document.Connections.FindIndex(c => string.Equals(c.Id, connection.Id, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                {
                    connection.AddedAt = document.Connections[existing].AddedAt;
                    document.Connections[existing] = connection;
                }
                else
                {
                    connection.AddedAt = DateTimeOffset.Now;
                    document.Connections.Add(connection);
                }

                return connection;
            });
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            this.SetToken(saved.Id, token.Trim());
        }

        return saved;
    }

    /// <summary>
    ///     Removes an added connection and its stored token.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True if it existed.</returns>
    /// <exception cref="AflException">The connection is built in.</exception>
    public bool Remove(string id)
    {
        if (BuiltIns().Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"'{id}' is built in and can't be removed. Clear its token with: afl host forget {id}.");
        }

        var removed = this.Update(document =>
            document.Connections.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) > 0);
        if (removed)
        {
            this.SetToken(id, null);
        }

        return removed;
    }

    /// <summary>
    ///     Stores, or with null deletes, a connection's token.
    /// </summary>
    /// <param name="id">The connection id.</param>
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
    ///     Returns whether a token is saved for a connection, or set in its override variable. Doesn't ask the CLIs.
    /// </summary>
    /// <param name="id">The connection id.</param>
    /// <returns>True if there is one.</returns>
    public bool HasToken(string id)
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenVariable(id))) ||
               this._secrets.Value.TokenExists(SecretKey(id));
    }

    /// <summary>
    ///     Finds a connection's token: its override variable, then the stored token, then (for the cloud site) the
    ///     standard variables, then the host's CLI. A token from the CLI is only held in memory.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="ct">Cancels asking the CLI.</param>
    /// <returns>The token, or null when there's none (requests are then anonymous).</returns>
    public async Task<GitHostToken?> ResolveTokenAsync(GitHostConnection connection, CancellationToken ct = default)
    {
        var variable = TokenVariable(connection.Id);
        if (Environment.GetEnvironmentVariable(variable) is { } overridden && !string.IsNullOrWhiteSpace(overridden))
        {
            return new GitHostToken(overridden.Trim(), GitHostTokenSource.Environment, variable);
        }

        if (this._secrets.Value.GetToken(SecretKey(connection.Id)) is { } stored && !string.IsNullOrWhiteSpace(stored))
        {
            return new GitHostToken(stored, GitHostTokenSource.Keychain, "keychain");
        }

        // An anonymous client is enough to ask the kind where else a sign-in can come from.
        var probe = GitHostClientRegistry.Instance.Create(connection, null, SharedHttp.Value);
        if (this.BorrowsSignIns && probe.IsCloud)
        {
            foreach (var name in probe.EnvironmentTokens)
            {
                if (Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value))
                {
                    return new GitHostToken(value.Trim(), GitHostTokenSource.Environment, name);
                }
            }
        }

        return this.BorrowsSignIns && await probe.CliTokenAsync(ct) is { } borrowed
            ? new GitHostToken(borrowed, GitHostTokenSource.Cli, probe.CliName)
            : null;
    }

    /// <summary>
    ///     Describes how afl signs in to a connection, e.g. "Token set", "Using gh's sign-in" or "Not signed in". It may
    ///     ask the host's CLI, so the UI calls it in the background.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="ct">Cancels asking the CLI.</param>
    /// <returns>The description.</returns>
    public async Task<string> TokenStatusAsync(GitHostConnection connection, CancellationToken ct = default)
    {
        return (await this.ResolveTokenAsync(connection, ct))?.Description ?? "Not signed in";
    }

    /// <summary>
    ///     Creates the client for a connection, signed in with whatever token <see cref="ResolveTokenAsync" /> finds.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="http">The HTTP client to use; defaults to a shared one.</param>
    /// <param name="ct">Cancels asking the CLI.</param>
    /// <returns>The client, anonymous when there's no token.</returns>
    public async Task<AbstractGitHostClient> CreateClientAsync(GitHostConnection connection, HttpClient? http = null, CancellationToken ct = default)
    {
        return this.CreateClient(connection, (await this.ResolveTokenAsync(connection, ct))?.Value, http);
    }

    /// <summary>
    ///     Creates the client for a connection with a given token (e.g. to test a token before saving it).
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="token">The token, or null for anonymous requests.</param>
    /// <param name="http">The HTTP client to use; defaults to a shared one.</param>
    /// <returns>The client.</returns>
    public virtual AbstractGitHostClient CreateClient(GitHostConnection connection, string? token, HttpClient? http = null)
    {
        var client = GitHostClientRegistry.Instance.Create(connection, token, http ?? SharedHttp.Value);
        client.CacheDirectory = Path.Combine(this.CacheDirectory, connection.Kind);
        return client;
    }

    /// <summary>
    ///     Returns the environment variable that overrides a connection's token.
    /// </summary>
    /// <param name="id">The connection id.</param>
    /// <returns>The variable name, e.g. <c>AFL_GITHOST_TOKEN_GITHUB</c>.</returns>
    public static string TokenVariable(string id)
    {
        return EnvironmentConstants.GitHostTokenPrefix + NonIdentifierRegex().Replace(id.ToUpperInvariant(), "_");
    }

    /// <summary>
    ///     Checks a connection's fields.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <exception cref="AflException">A field is missing or malformed.</exception>
    public static void Validate(GitHostConnection connection)
    {
        if (!IdRegex().IsMatch(connection.Id))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"'{connection.Id}' isn't a usable connection id. Use letters, digits, '-' and '_'.");
        }

        if (!GitHostClientRegistry.Instance.Contains(connection.Kind))
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"'{connection.Kind}' isn't a kind of git host. Use {string.Join(" or ", GitHostClientRegistry.Instance.Keys.Order())}.");
        }

        if (!Uri.TryCreate(connection.BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"'{connection.BaseUrl}' isn't an http(s) URL.");
        }
    }

    /// <summary>
    ///     Returns the connections afl always has: github.com and gitlab.com.
    /// </summary>
    /// <returns>Fresh copies, so callers can't change the originals.</returns>
    private static IEnumerable<GitHostConnection> BuiltIns()
    {
        yield return new GitHostConnection
        {
            Id = GitHubId, Name = "GitHub", Kind = Tickets.TicketRef.GitHubSource, BaseUrl = "https://github.com", IsBuiltIn = true,
        };
        yield return new GitHostConnection
        {
            Id = GitLabId, Name = "GitLab", Kind = Tickets.TicketRef.GitLabSource, BaseUrl = "https://gitlab.com", IsBuiltIn = true,
        };
    }

    /// <summary>
    ///     Returns whether a path can be a project on a connection: <c>owner/name</c> on GitHub, two or more segments on
    ///     GitLab.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="project">The path.</param>
    /// <returns>True if it can.</returns>
    private static bool IsProjectFor(GitHostConnection connection, string project)
    {
        var segments = project.Split('/');
        return segments.All(s => ProjectSegmentRegex().IsMatch(s)) &&
               (connection.Kind == Tickets.TicketRef.GitHubSource ? segments.Length == 2 : segments.Length >= 2);
    }

    /// <summary>
    ///     Returns the secure-store key for a connection's token.
    /// </summary>
    /// <param name="id">The connection id.</param>
    /// <returns>The key.</returns>
    private static string SecretKey(string id)
    {
        return $"githost:{id.ToLowerInvariant()}";
    }

    /// <summary>
    ///     Normalizes a URL for prefix comparison: no scheme, no <c>www.</c>, a trailing slash.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The normalized URL.</returns>
    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        var rest = scheme >= 0 ? trimmed[(scheme + 3)..] : trimmed;
        return (rest.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? rest[4..] : rest).TrimEnd('/') + "/";
    }

    /// <summary>
    ///     Matches a valid connection id.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex IdRegex();

    /// <summary>
    ///     Matches one segment of a project path.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"^[\w.-]+$")]
    private static partial Regex ProjectSegmentRegex();

    /// <summary>
    ///     Matches characters that can't appear in an environment variable name.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex("[^A-Z0-9_]")]
    private static partial Regex NonIdentifierRegex();
}
