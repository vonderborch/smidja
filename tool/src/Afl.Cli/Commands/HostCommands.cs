using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.GitHosts;
using Afl.Core.Repos;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Shared behaviour for the <c>host</c> and <c>repo host</c> commands: the connection store and the repo link view.
/// </summary>
public abstract class AbstractHostCommand : AbstractCommand
{
    /// <summary>
    ///     Gets the git host connection store.
    /// </summary>
    protected GitHostStore Hosts => new(this.Paths);

    /// <summary>
    ///     Builds the link view for a repo.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <returns>The view.</returns>
    protected RepoHostLinkView LinkView(RepoRecord repo)
    {
        return new RepoHostLinkView(repo.Id, repo.Host is null ? null : this.Hosts.Find(repo.Host.ConnectionId), repo.Host);
    }
}

/// <summary>
///     <c>afl host add</c>: adds or updates a GitHub or GitLab connection, optionally storing a token.
/// </summary>
[Verb("host-add", HelpText = "Add or update a GitHub or GitLab connection (github and gitlab are built in). The token is read from a hidden prompt (or the first line of stdin); leave it empty to use gh's or glab's sign-in.")]
public class HostAddCommand : AbstractHostCommand
{
    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "A short id for the connection, e.g. work-gitlab (or github / gitlab to set the built-in ones' token).")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the kind.
    /// </summary>
    [Option('k', "kind", HelpText = "github or gitlab (required for a new connection).")]
    public string? Kind { get; set; }

    /// <summary>
    ///     Gets or sets the base URL.
    /// </summary>
    [Option('u', "url", HelpText = "The site's web address, e.g. https://git.acme.com (required for a new connection).")]
    public string? Url { get; set; }

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    [Option("name", HelpText = "A display name (defaults to the id).")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets whether to skip the sign-in check.
    /// </summary>
    [Option("skip-verify", HelpText = "Save without checking the token against the host.")]
    public bool SkipVerify { get; set; }

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Hosts;
        var existing = store.Find(this.Id);
        var connection = new GitHostConnection
        {
            Id = this.Id,
            Name = this.Name ?? existing?.Name ?? this.Id,
            Kind = this.Kind ?? existing?.Kind ?? throw new AflException(AflErrorKind.InvalidInput, "Give the kind with --kind github|gitlab."),
            BaseUrl = this.Url ?? existing?.BaseUrl ?? throw new AflException(AflErrorKind.InvalidInput, "Give the site's address with --url <https://…>."),
            IsBuiltIn = existing?.IsBuiltIn ?? false,
        };
        GitHostStore.Validate(connection);

        var token = this.Output.ReadSecret($"{GitHostClientRegistry.Instance.DisplayNameOf(connection.Kind)} token for {connection.BaseUrl} (empty to use the CLI's sign-in):");
        if (token is not null && !this.SkipVerify)
        {
            var user = await store.CreateClient(connection, token).GetCurrentUserAsync(ct);
            this.Output.Info($"Signed in to {connection.BaseUrl} as {user.Login}.");
        }

        var saved = store.Save(connection, token);
        this.Output.Result(await GitHostConnectionView.FromAsync(store, saved, ct), TextRendering.GitHostConnection);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl host list</c>.
/// </summary>
[Verb("host-list", HelpText = "List GitHub and GitLab connections and where each one's sign-in comes from.")]
public class HostListCommand : AbstractHostCommand
{
    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Hosts;
        var views = new List<GitHostConnectionView>();
        foreach (var connection in store.All())
        {
            views.Add(await GitHostConnectionView.FromAsync(store, connection, ct));
        }

        this.Output.Result(views, TextRendering.GitHostConnectionList);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl host test</c>: signs in and shows who as.
/// </summary>
[Verb("host-test", HelpText = "Sign in to a connection and show the account.")]
public class HostTestCommand : AbstractHostCommand
{
    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The connection id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Hosts;
        var connection = store.GetRequired(this.Id);
        var client = await store.CreateClientAsync(connection, null, ct);
        var user = await client.GetCurrentUserAsync(ct);
        this.Output.Result(new GitHostTestView(connection.Id, user, await store.TokenStatusAsync(connection, ct)), TextRendering.GitHostTest);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl host forget</c>: clears a connection's stored token.
/// </summary>
[Verb("host-forget", HelpText = "Clear a connection's stored token (afl falls back to the gh or glab sign-in).")]
public class HostForgetCommand : AbstractHostCommand
{
    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The connection id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Hosts;
        var connection = store.GetRequired(this.Id);
        store.SetToken(connection.Id, null);
        this.Output.Result(await GitHostConnectionView.FromAsync(store, connection, ct), TextRendering.GitHostConnection);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl host remove</c>.
/// </summary>
[Verb("host-remove", HelpText = "Remove a connection and its stored token (the built-in github and gitlab can't be removed).")]
public class HostRemoveCommand : AbstractHostCommand
{
    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The connection id.")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether to unlink repos that use it.
    /// </summary>
    [Option("force", HelpText = "Also unlink the repos that use it.")]
    public bool Force { get; set; }

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Hosts;
        var connection = store.GetRequired(this.Id);
        var linked = this.Repos.All()
            .Where(r => string.Equals(r.Host?.ConnectionId, connection.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (linked.Count > 0 && !this.Force && !connection.IsBuiltIn)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"'{connection.Id}' is linked to: {string.Join(", ", linked.Select(r => r.Name))}. Pass --force to unlink them too.");
        }

        store.Remove(connection.Id);
        foreach (var repo in linked)
        {
            this.Repos.UnlinkHost(repo.Id);
        }

        this.Output.Result(connection, (c, w) => w.WriteLine($"Removed {c.Id}."));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl repo host</c>: shows the repo's GitHub or GitLab link.
/// </summary>
[Verb("repo-host", HelpText = "Show the GitHub repository or GitLab project the repo's issues come from (see also repo host link|unlink|detect).")]
public class RepoHostCommand : AbstractHostCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.LinkView(this.RequireRepo()), TextRendering.RepoHostLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl repo host link</c>.
/// </summary>
[Verb("repo-host-link", HelpText = "Link the repo to a GitHub repository or GitLab project: afl repo host link <connection> <owner/repo | group/project | URL>.")]
public class RepoHostLinkCommand : AbstractHostCommand
{
    /// <summary>
    ///     Gets or sets the connection id.
    /// </summary>
    [Value(0, MetaName = "connection", Required = true, HelpText = "The connection id (see afl host list), e.g. github.")]
    public string Connection { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the project.
    /// </summary>
    [Value(1, MetaName = "project", Required = true, HelpText = "The project path, e.g. owner/repo or group/sub/project, or its web address.")]
    public string Project { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.Repos.LinkHost(this.RequireRepo().Id, this.Connection, this.Project);
        this.Output.Result(this.LinkView(repo), TextRendering.RepoHostLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl repo host unlink</c>.
/// </summary>
[Verb("repo-host-unlink", HelpText = "Remove the repo's GitHub or GitLab link.")]
public class RepoHostUnlinkCommand : AbstractHostCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.LinkView(this.Repos.UnlinkHost(this.RequireRepo().Id)), TextRendering.RepoHostLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl repo host detect</c>.
/// </summary>
[Verb("repo-host-detect", HelpText = "Link the repo to the project its git remotes point at (origin first), matched against the connections.")]
public class RepoHostDetectCommand : AbstractHostCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.LinkView(this.Repos.DetectHost(this.RequireRepo().Id)), TextRendering.RepoHostLink);
        return Task.FromResult(ExitSuccess);
    }
}
