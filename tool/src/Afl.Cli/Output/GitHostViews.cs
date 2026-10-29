using Afl.Core.GitHosts;

namespace Afl.Cli.Output;

/// <summary>
///     A git host connection as the CLI shows it: never the token, only where one would come from.
/// </summary>
/// <param name="Connection">The connection.</param>
/// <param name="KindName">The kind's display name, e.g. <c>GitHub</c>.</param>
/// <param name="TokenStatus">Where its token comes from, e.g. "Token set" or "Using gh's sign-in".</param>
/// <param name="TokenVariable">The environment variable that overrides its token.</param>
public record GitHostConnectionView(GitHostConnection Connection, string KindName, string TokenStatus, string TokenVariable)
{
    /// <summary>
    ///     Creates the view for a connection, checking where its token would come from.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="connection">The connection.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The view.</returns>
    public static async Task<GitHostConnectionView> FromAsync(GitHostStore store, GitHostConnection connection, CancellationToken ct)
    {
        return new GitHostConnectionView(connection, GitHostClientRegistry.Instance.DisplayNameOf(connection.Kind),
            await store.TokenStatusAsync(connection, ct), GitHostStore.TokenVariable(connection.Id));
    }
}

/// <summary>
///     A repo's GitHub or GitLab link as the CLI shows it.
/// </summary>
/// <param name="Repo">The repo id.</param>
/// <param name="Connection">The linked connection, or null if it no longer exists.</param>
/// <param name="Link">The link, or null if the repo isn't linked.</param>
public record RepoHostLinkView(string Repo, GitHostConnection? Connection, RepoHostLink? Link);

/// <summary>
///     The result of signing in to a git host.
/// </summary>
/// <param name="Connection">The connection id.</param>
/// <param name="User">The account signed in as.</param>
/// <param name="TokenStatus">Where the token came from.</param>
public record GitHostTestView(string Connection, GitHostUser User, string TokenStatus);
