using Afl.Core.Jira;

namespace Afl.Cli.Output;

/// <summary>
///     A Jira instance as the CLI shows it, with whether a token is available.
/// </summary>
/// <param name="Instance">The instance.</param>
/// <param name="HasToken">Whether a token is stored or set in the environment.</param>
/// <param name="TokenVariable">The environment variable that overrides the stored token.</param>
public record JiraInstanceView(JiraInstance Instance, bool HasToken, string TokenVariable)
{
    /// <summary>
    ///     Creates the view for an instance.
    /// </summary>
    /// <param name="store">The instance store.</param>
    /// <param name="instance">The instance.</param>
    /// <returns>The view.</returns>
    public static JiraInstanceView From(JiraInstanceStore store, JiraInstance instance)
    {
        return new JiraInstanceView(instance, store.HasToken(instance.Id), JiraInstanceStore.TokenVariable(instance.Id));
    }
}

/// <summary>
///     A repo's Jira link as the CLI shows it.
/// </summary>
/// <param name="Repo">The repo id.</param>
/// <param name="Instance">The linked instance, or null if it no longer exists.</param>
/// <param name="Link">The link, or null if the repo isn't linked.</param>
public record JiraLinkView(string Repo, JiraInstance? Instance, RepoJiraLink? Link);
