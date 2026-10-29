using System.Text.Json.Serialization;
using Afl.Core.Storage;

namespace Afl.Core.GitHosts;

/// <summary>
///     A GitHub or GitLab site afl can read issues from: github.com, gitlab.com, a GitHub Enterprise Server or a
///     self-managed GitLab. The token is not stored here; it lives in the OS secure store.
/// </summary>
public class GitHostConnection
{
    /// <summary>
    ///     Gets or sets the short id used on the command line and in repo links.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the kind of host, a registered <see cref="AbstractGitHostClient" /> key (<c>github</c> or
    ///     <c>gitlab</c>).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the site's web address, e.g. <c>https://github.com</c> or <c>https://git.acme.com</c>.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets when the connection was added.
    /// </summary>
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>
    ///     Gets or sets whether afl provides this connection itself (github.com and gitlab.com), so it can't be removed.
    /// </summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    /// <summary>
    ///     Gets the site's host name, lower case, e.g. <c>github.com</c>; empty if the address isn't a URL.
    /// </summary>
    [JsonIgnore]
    public string Host => Uri.TryCreate(this.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;

    /// <summary>
    ///     Gets the path the site is served under, e.g. <c>/gitlab</c> for <c>https://acme.com/gitlab</c>; empty at the root.
    /// </summary>
    [JsonIgnore]
    public string PathPrefix => Uri.TryCreate(this.BaseUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath.TrimEnd('/') : string.Empty;

    /// <summary>
    ///     Gets the name shown for the connection: its name, or its id when it has none.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(this.Name) ? this.Id : this.Name;
}

/// <summary>
///     The <c>githosts.json</c> document. The built-in connections aren't kept in it.
/// </summary>
[DocumentVersion(1, 0)]
public class GitHostDocument : AbstractVersionedDocument
{
    /// <summary>
    ///     Gets or sets the connections added on top of the built-in ones.
    /// </summary>
    public List<GitHostConnection> Connections { get; set; } = [];
}

/// <summary>
///     Where a git host token came from.
/// </summary>
public enum GitHostTokenSource
{
    /// <summary>
    ///     An environment variable: the connection's <c>AFL_GITHOST_TOKEN_&lt;ID&gt;</c>, or a standard one such as
    ///     <c>GH_TOKEN</c> for the cloud site.
    /// </summary>
    Environment,

    /// <summary>
    ///     The token saved for the connection in the OS secure store.
    /// </summary>
    Keychain,

    /// <summary>
    ///     Borrowed from the host's own CLI (<c>gh</c> or <c>glab</c>) sign-in, and only held in memory.
    /// </summary>
    Cli,
}

/// <summary>
///     A token for a git host and where it came from. Its <see cref="ToString" /> never includes the value, so it can't
///     end up in a log or message by accident.
/// </summary>
/// <param name="value">The token.</param>
/// <param name="source">Where it came from.</param>
/// <param name="origin">What it came from, for status text, e.g. <c>GH_TOKEN</c> or <c>gh</c>.</param>
public sealed class GitHostToken(string value, GitHostTokenSource source, string origin)
{
    /// <summary>
    ///     Gets the token.
    /// </summary>
    public string Value { get; } = value;

    /// <summary>
    ///     Gets where the token came from.
    /// </summary>
    public GitHostTokenSource Source { get; } = source;

    /// <summary>
    ///     Gets what the token came from, e.g. <c>GH_TOKEN</c> or <c>gh</c>.
    /// </summary>
    public string Origin { get; } = origin;

    /// <summary>
    ///     Gets a description of where the token came from, e.g. "Token set" or "Using gh's sign-in".
    /// </summary>
    public string Description => this.Source switch
    {
        GitHostTokenSource.Keychain => "Token set",
        GitHostTokenSource.Cli => $"Using {this.Origin}'s sign-in",
        _ => $"Token from {this.Origin}",
    };

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Description;
    }
}

/// <summary>
///     The account a git host token signs in as.
/// </summary>
/// <param name="Login">The user name.</param>
/// <param name="Name">The display name, if set.</param>
public record GitHostUser(string Login, string? Name);

/// <summary>
///     The GitHub repository or GitLab project a repo's issues come from.
/// </summary>
public class RepoHostLink
{
    /// <summary>
    ///     Gets or sets the id of the <see cref="GitHostConnection" /> the project is on.
    /// </summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the kind of host (<c>github</c> or <c>gitlab</c>), kept so the right ticket source is picked
    ///     without loading the connection.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the project's path on the host, e.g. <c>owner/repo</c> or <c>group/sub/project</c>.
    /// </summary>
    public string Project { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{this.Project} on {this.ConnectionId}";
    }
}
