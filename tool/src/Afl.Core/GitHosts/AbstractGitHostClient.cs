using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Tickets;

namespace Afl.Core.GitHosts;

/// <summary>
///     Registers an <see cref="AbstractGitHostClient" /> subclass for a kind of git host.
/// </summary>
/// <param name="kind">The kind, as stored on connections and repo links, e.g. <c>github</c>.</param>
/// <param name="displayName">How the kind is shown, e.g. <c>GitHub</c>.</param>
public class GitHostClientRegistrationAttribute(string kind, string displayName) : AbstractRegistrationAttribute(kind)
{
    /// <summary>
    ///     Gets how the kind is shown, e.g. <c>GitHub</c>.
    /// </summary>
    public string DisplayName { get; } = displayName;
}

/// <summary>
///     Finds the client class for each kind of git host.
/// </summary>
public class GitHostClientRegistry() : TypeRegistry<AbstractGitHostClient, GitHostClientRegistrationAttribute>("git host kind")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static GitHostClientRegistry Instance { get; } = new();

    /// <summary>
    ///     Creates the client for a connection.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="token">The token, or null to make anonymous requests (enough to read public projects).</param>
    /// <param name="http">The HTTP client to send requests with.</param>
    /// <returns>The client.</returns>
    public AbstractGitHostClient Create(GitHostConnection connection, string? token, HttpClient http)
    {
        return this.Create(connection.Kind, connection, token, http);
    }

    /// <summary>
    ///     Returns how a kind is shown, e.g. <c>GitHub</c> for <c>github</c>.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The display name, or the kind itself if it isn't registered.</returns>
    public string DisplayNameOf(string kind)
    {
        return this.Registrations.FirstOrDefault(r => string.Equals(r.Key, kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? kind;
    }
}

/// <summary>
///     Talks to one git host connection over REST, and knows what's particular to its kind: where its API lives, which CLI
///     can lend a sign-in, the token variables its cloud site uses, and the shape of its issues. The base holds the
///     sign-in plumbing and error messages; subclasses supply the issue calls.
/// </summary>
/// <param name="connection">The connection.</param>
/// <param name="token">The token, or null to make anonymous requests.</param>
/// <param name="http">The HTTP client; a base address on it replaces the API's (for tests).</param>
public abstract class AbstractGitHostClient(GitHostConnection connection, string? token, HttpClient http) : AbstractRestClient(http)
{
    /// <summary>
    ///     The most issues asked for in one page.
    /// </summary>
    protected const int MaxPageSize = 100;

    /// <summary>
    ///     Gets the connection.
    /// </summary>
    public GitHostConnection Connection { get; } = connection;

    /// <summary>
    ///     Gets whether requests are signed in.
    /// </summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(this.Token);

    /// <summary>
    ///     Gets or sets a directory to cache responses in, for kinds that can, or null to cache nothing.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    ///     Gets the API's address: the HTTP client's base address if it has one, otherwise the kind's for the connection.
    /// </summary>
    public string ApiBase => this.Http.BaseAddress?.ToString() ?? this.DefaultApiBase;

    /// <summary>
    ///     Gets how the kind is shown, e.g. <c>GitHub</c>.
    /// </summary>
    public abstract string DisplayName { get; }

    /// <summary>
    ///     Gets the name of the CLI that can lend a sign-in, e.g. <c>gh</c>.
    /// </summary>
    public abstract string CliName { get; }

    /// <summary>
    ///     Gets the environment variable that overrides the CLI's executable.
    /// </summary>
    public abstract string CliVariable { get; }

    /// <summary>
    ///     Gets the host of the kind's cloud site, e.g. <c>github.com</c>.
    /// </summary>
    public abstract string CloudHost { get; }

    /// <summary>
    ///     Gets the standard token variables, tried for the cloud site after the connection's own.
    /// </summary>
    public abstract IReadOnlyList<string> EnvironmentTokens { get; }

    /// <summary>
    ///     Gets the command that signs the CLI in, for messages, e.g. <c>gh auth login --hostname github.com</c>.
    /// </summary>
    public abstract string CliSignInCommand { get; }

    /// <summary>
    ///     Gets the page where a personal access token can be created.
    /// </summary>
    public abstract string CreateTokenUrl { get; }

    /// <summary>
    ///     Gets the API's base URL when the HTTP client doesn't set one.
    /// </summary>
    protected abstract string DefaultApiBase { get; }

    /// <summary>
    ///     Gets the token, or null when requests are anonymous.
    /// </summary>
    protected string? Token { get; } = token;

    /// <summary>
    ///     Gets the base URL of the connection's web site, without a trailing slash.
    /// </summary>
    protected string WebBase => this.Connection.BaseUrl.TrimEnd('/');

    /// <inheritdoc />
    protected override string ServiceName => this.Connection.DisplayName;

    /// <summary>
    ///     Gets the arguments that make the CLI print its token for this connection's host.
    /// </summary>
    protected abstract IReadOnlyList<string> CliTokenArguments { get; }

    /// <summary>
    ///     Gets whether this connection is the kind's cloud site, whose standard token variables apply.
    /// </summary>
    public bool IsCloud => string.Equals(this.Connection.Host, this.CloudHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns the account the token signs in as.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The account.</returns>
    /// <exception cref="AflException">There's no token, or the host rejected it.</exception>
    public abstract Task<GitHostUser> GetCurrentUserAsync(CancellationToken ct = default);

    /// <summary>
    ///     Lists a project's issues (not pull or merge requests).
    /// </summary>
    /// <param name="project">The project, e.g. <c>owner/repo</c> or <c>group/sub/project</c>.</param>
    /// <param name="filter">What to list.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The issues.</returns>
    public abstract Task<IReadOnlyList<Ticket>> ListIssuesAsync(string project, GitHostTicketFilter filter, CancellationToken ct = default);

    /// <summary>
    ///     Gets one issue.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="number">The issue's number in the project.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The issue.</returns>
    public abstract Task<Ticket> GetIssueAsync(string project, int number, CancellationToken ct = default);

    /// <summary>
    ///     Files an issue. Needs a token.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="ticket">The issue to file.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The filed issue.</returns>
    /// <exception cref="AflException">There's no token, or the host refused.</exception>
    public abstract Task<Ticket> CreateIssueAsync(string project, NewTicket ticket, CancellationToken ct = default);

    /// <summary>
    ///     Returns the web page that files an issue with the given fields filled in, for filing by hand.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="ticket">The issue to file.</param>
    /// <returns>The URL.</returns>
    public abstract string NewIssueUrl(string project, NewTicket ticket);

    /// <summary>
    ///     Asks the kind's CLI for its token for this connection's host. The token is only held in memory.
    /// </summary>
    /// <param name="ct">Cancels the CLI.</param>
    /// <returns>The token, or null when the CLI is missing or not signed in to the host.</returns>
    public virtual async Task<string?> CliTokenAsync(CancellationToken ct = default)
    {
        try
        {
            var executable = Environment.GetEnvironmentVariable(this.CliVariable) ?? this.CliName;
            var result = await ProcessRunner.RunAsync(executable, this.CliTokenArguments, ct: ct);
            var value = result.StandardOutput.Trim();
            return result.Success && value.Length > 0 && !value.Contains('\n') ? value : null;
        }
        catch (Exception ex) when (ex is AflException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Explains how to sign in to this connection, for messages.
    /// </summary>
    /// <returns>The explanation, e.g. "add a token in Settings → Git hosts, run 'gh auth login …', or set …".</returns>
    public string SignInHint()
    {
        var variables = new List<string> { GitHostStore.TokenVariable(this.Connection.Id) };
        if (this.IsCloud)
        {
            variables.AddRange(this.EnvironmentTokens);
        }

        return $"add a token in Settings → Git hosts (or 'afl host add {this.Connection.Id} …'), run '{this.CliSignInCommand}', " +
               $"or set {string.Join(" or ", variables)}";
    }

    /// <inheritdoc />
    protected override string RequestUrl(string path)
    {
        return $"{this.ApiBase.TrimEnd('/')}/{path}";
    }

    /// <inheritdoc />
    protected override void Authorize(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("afl", "2"));
        if (this.HasToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.Token);
        }
    }

    /// <inheritdoc />
    protected override AflException MapError(HttpStatusCode status, string body)
    {
        var detail = this.ReadErrorDetail(body);
        var suffix = detail is null ? string.Empty : $" ({detail})";
        return status switch
        {
            HttpStatusCode.Unauthorized when this.HasToken => new AflException(AflErrorKind.InvalidState,
                $"{this.ServiceName} rejected the token{suffix}. Sign in again: {this.SignInHint()}."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when !this.HasToken => new AflException(AflErrorKind.InvalidState,
                $"{this.ServiceName} needs you to sign in{suffix}: {this.SignInHint()}."),
            HttpStatusCode.NotFound when !this.HasToken => new AflException(AflErrorKind.NotFound,
                $"{this.ServiceName} found nothing there{suffix}. If the project is private, sign in: {this.SignInHint()}."),
            _ => base.MapError(status, body),
        };
    }

    /// <inheritdoc />
    protected override string? ReadErrorDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // GitHub and GitLab say what went wrong in "message" (GitLab sometimes as an object of field errors) or "error".
            if (root.TryGetProperty("message", out var message))
            {
                return message.ValueKind == JsonValueKind.String ? message.GetString() : message.GetRawText();
            }

            return GetString(root, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Fails when there's no token, for calls that can't be anonymous.
    /// </summary>
    /// <exception cref="AflException">There's no token.</exception>
    protected void RequireToken()
    {
        if (!this.HasToken)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"There's no {this.DisplayName} token for {this.Connection.DisplayName}: {this.SignInHint()}.");
        }
    }

    /// <summary>
    ///     Reads a string array property, or the given property of each object in an array.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The array property.</param>
    /// <param name="field">The property to read from each object, or null for an array of strings.</param>
    /// <returns>The values.</returns>
    protected static List<string> GetStrings(JsonElement element, string name, string? field = null)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(item => field is null ? item.ValueKind == JsonValueKind.String ? item.GetString() : null : GetString(item, field))
            .OfType<string>()
            .ToList();
    }

    /// <summary>
    ///     Returns how many issues to ask for in one page.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <returns>The page size.</returns>
    protected static string PageSizeFor(TicketFilter filter)
    {
        return Math.Clamp(filter.Limit, 1, MaxPageSize).ToString();
    }

    /// <summary>
    ///     Builds a query string from fields.
    /// </summary>
    /// <param name="fields">The fields; null values are skipped.</param>
    /// <returns>The query, without the <c>?</c>.</returns>
    protected static string QueryString(params (string Name, string? Value)[] fields)
    {
        return WithQuery(string.Empty, fields).TrimStart('?');
    }
}
