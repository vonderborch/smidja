using System.Net;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.GitHosts;
using Afl.Core.Helpers;
using Afl.Core.Repos;
using Afl.Core.Tickets;
using Niddy.Core.Security;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for GitHub and GitLab connections: the store and its tokens, linking repos from their remotes, and the REST
///     clients against a fake server.
/// </summary>
public class GitHostTests : AbstractAflTest
{
    /// <summary>
    ///     The GitHub issues list the fake server returns: one issue and one pull request.
    /// </summary>
    private const string GitHubIssues = """
        [
          {"number": 14, "title": "Crash on start", "state": "open", "html_url": "https://github.com/acme/app/issues/14",
           "assignees": [{"login": "ada"}], "labels": [{"name": "bug"}]},
          {"number": 15, "title": "Fix crash", "state": "open", "html_url": "https://github.com/acme/app/pull/15",
           "pull_request": {"url": "x"}}
        ]
        """;

    /// <summary>
    ///     A GitLab issue as the fake server returns it.
    /// </summary>
    private const string GitLabIssue = """
        {"iid": 31, "title": "Slow builds", "description": "Ten minutes.", "state": "opened",
         "web_url": "https://git.acme.test/platform/team/app/-/issues/31", "assignees": [{"username": "ada"}], "labels": ["ci"]}
        """;

    /// <summary>
    ///     Creates a store over an encrypted-file secret store in the scratch home, so nothing touches the keychain.
    /// </summary>
    /// <param name="borrowSignIns">Whether the store may borrow sign-ins from the environment and the CLIs.</param>
    /// <returns>The store.</returns>
    private GitHostStore CreateStore(bool borrowSignIns = false)
    {
        return new GitHostStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"), borrowSignIns);
    }

    /// <summary>
    ///     Creates a store with a GitHub Enterprise Server and a self-managed GitLab added.
    /// </summary>
    /// <returns>The store.</returns>
    private GitHostStore CreateStoreWithSites()
    {
        var store = this.CreateStore();
        store.Save(new GitHostConnection { Id = "ghe", Name = "Acme GitHub", Kind = "github", BaseUrl = "https://ghe.acme.test/" }, null);
        store.Save(new GitHostConnection { Id = "acme-gl", Kind = "gitlab", BaseUrl = "https://git.acme.test" }, null);
        return store;
    }

    /// <summary>
    ///     Creates a client for a connection over a fake server.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="id">The connection id.</param>
    /// <param name="handler">The fake server.</param>
    /// <param name="token">The token, or null for anonymous requests.</param>
    /// <returns>The client.</returns>
    private static AbstractGitHostClient Client(GitHostStore store, string id, FakeHttpHandler handler, string? token = "tok")
    {
        return store.CreateClient(store.GetRequired(id), token, new HttpClient(handler));
    }

    [Fact]
    public void BuiltInSites_AreAlwaysThere_AndCantBeRemoved()
    {
        var store = this.CreateStoreWithSites();

        Assert.Equal(["github", "gitlab", "acme-gl", "ghe"], store.All().Select(c => c.Id));
        Assert.All(store.All().Take(2), c => Assert.True(c.IsBuiltIn));
        Assert.Throws<AflException>(() => store.Remove(GitHostStore.GitHubId));
        Assert.Equal("https://ghe.acme.test", store.GetRequired("ghe").BaseUrl);
    }

    [Fact]
    public async Task SavedToken_GoesToTheSecureStore_NotTheJson()
    {
        var store = this.CreateStore();

        store.Save(new GitHostConnection { Id = "ghe", Kind = "github", BaseUrl = "https://ghe.acme.test" }, "secret-ghe-token");
        store.Save(store.GetRequired(GitHostStore.GitHubId), "secret-cloud-token");

        var json = await File.ReadAllTextAsync(this.Paths.GitHostsFile);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("\"github\"", json.Replace("\"kind\": \"github\"", string.Empty));
        Assert.True(store.HasToken("ghe"));
        var token = await store.ResolveTokenAsync(store.GetRequired("ghe"));
        Assert.Equal("secret-ghe-token", token!.Value);
        Assert.Equal(GitHostTokenSource.Keychain, token.Source);
        Assert.DoesNotContain("secret", token.ToString());
        Assert.Equal("Token set", await store.TokenStatusAsync(store.GetRequired(GitHostStore.GitHubId)));
    }

    [Fact]
    public async Task Remove_DeletesTheToken_AndClearingFallsBackToNotSignedIn()
    {
        var store = this.CreateStore();
        store.Save(new GitHostConnection { Id = "ghe", Kind = "github", BaseUrl = "https://ghe.acme.test" }, "tok");
        store.SetToken(GitHostStore.GitLabId, "gl");

        Assert.True(store.Remove("ghe"));
        store.SetToken(GitHostStore.GitLabId, null);

        Assert.False(store.HasToken("ghe"));
        Assert.Null(store.Find("ghe"));
        Assert.Equal("Not signed in", await store.TokenStatusAsync(store.GetRequired(GitHostStore.GitLabId)));
    }

    [Fact]
    public async Task EnvironmentToken_OverridesTheStoredOne()
    {
        var id = "env-" + Guid.NewGuid().ToString("N")[..8];
        var variable = GitHostStore.TokenVariable(id);
        Assert.Matches("^AFL_GITHOST_TOKEN_ENV_[A-Z0-9]+$", variable);
        var store = this.CreateStore();
        var connection = store.Save(new GitHostConnection { Id = id, Kind = "gitlab", BaseUrl = "https://git.acme.test" }, "stored");
        try
        {
            Environment.SetEnvironmentVariable(variable, "from-env");
            var token = await store.ResolveTokenAsync(connection);
            Assert.Equal("from-env", token!.Value);
            Assert.Equal($"Token from {variable}", token.Description);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Assert.Equal("stored", (await store.ResolveTokenAsync(connection))!.Value);
    }

    [Fact]
    public async Task NoToken_BorrowsTheCliSignIn_WithoutSavingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The fake gh only signs in to this test's host, so other tests that ask gh at the same time get nothing.
        var host = $"ghe-{Guid.NewGuid():N}.acme.test";
        var script = Path.Combine(this.Root, "gh");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\n[ \"$4\" = \"{host}\" ] && echo cli-token || exit 1\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var store = this.CreateStore(borrowSignIns: true);
        var connection = store.Save(new GitHostConnection { Id = "ghe", Kind = "github", BaseUrl = $"https://{host}" }, null);
        GitHostToken? token;
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentConstants.GhExecutable, script);
            token = await store.ResolveTokenAsync(connection);
            Assert.Equal("Using gh's sign-in", await store.TokenStatusAsync(connection));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentConstants.GhExecutable, null);
        }

        Assert.Equal("cli-token", token!.Value);
        Assert.Equal(GitHostTokenSource.Cli, token.Source);
        Assert.False(store.HasToken("ghe"));
        Assert.DoesNotContain("cli-token", await File.ReadAllTextAsync(this.Paths.GitHostsFile));
    }

    [Fact]
    public void Validate_RejectsBadConnections()
    {
        Assert.Throws<AflException>(() => GitHostStore.Validate(new GitHostConnection { Id = "a b", Kind = "github", BaseUrl = "https://x" }));
        Assert.Throws<AflException>(() => GitHostStore.Validate(new GitHostConnection { Id = "x", Kind = "gitea", BaseUrl = "https://x" }));
        Assert.Throws<AflException>(() => GitHostStore.Validate(new GitHostConnection { Id = "x", Kind = "gitlab", BaseUrl = "git.acme.test" }));
    }

    [Theory]
    [InlineData("git@github.com:acme/app.git", "github", "acme/app")]
    [InlineData("https://github.com/acme/app", "github", "acme/app")]
    [InlineData("https://gitlab.com/acme/platform/app.git", "gitlab", "acme/platform/app")]
    [InlineData("ssh://git@git.acme.test:2222/platform/team/app.git", "acme-gl", "platform/team/app")]
    [InlineData("git@ghe.acme.test:acme/app.git", "ghe", "acme/app")]
    [InlineData("https://acme.test/gitlab/team/app.git", "prefixed", "team/app")]
    public void Remote_LinksToItsConnectionAndProject(string remote, string connectionId, string project)
    {
        var store = this.CreateStoreWithSites();
        store.Save(new GitHostConnection { Id = "prefixed", Kind = "gitlab", BaseUrl = "https://acme.test/gitlab" }, null);

        var link = store.LinkForRemote(remote);

        Assert.Equal(connectionId, link!.ConnectionId);
        Assert.Equal(project, link.Project);
        Assert.Equal(store.GetRequired(connectionId).Kind, link.Kind);
    }

    [Theory]
    [InlineData("git@bitbucket.org:acme/app.git")]
    [InlineData("https://github.com/acme/app/extra")]
    [InlineData("/Users/me/src/app")]
    public void UnknownOrMisshapenRemotes_AreNotLinked(string remote)
    {
        Assert.Null(this.CreateStoreWithSites().LinkForRemote(remote));
    }

    [Fact]
    public void Detect_PrefersOrigin_AndRepoLinksCanBeChanged()
    {
        ProcessRunner.Run("git", ["remote", "add", "fork", "https://github.com/someone/app.git"], this.RepoPath);
        ProcessRunner.Run("git", ["remote", "add", "origin", "git@git.acme.test:platform/team/app.git"], this.RepoPath);
        var repos = new RepoRegistry(this.Paths) { GitHosts = this.CreateStoreWithSites() };

        var repo = repos.EnsureRegistered(this.RepoPath);
        Assert.Equal("platform/team/app on acme-gl", repo.Host!.ToString());

        repo = repos.LinkHost(repo.Id, "ghe", "https://ghe.acme.test/acme/app.git");
        Assert.Equal(("ghe", "github", "acme/app"), (repo.Host!.ConnectionId, repo.Host.Kind, repo.Host.Project));
        Assert.Throws<AflException>(() => repos.LinkHost(repo.Id, "github", "acme/platform/app"));

        Assert.Null(repos.UnlinkHost(repo.Id).Host);
        Assert.Equal("acme-gl", repos.DetectHost(repo.Id).Host!.ConnectionId);
    }

    [Fact]
    public async Task GitHub_ListsIssuesWithoutPullRequests()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(GitHubIssues));
        var client = Client(this.CreateStore(), GitHostStore.GitHubId, handler);

        var issues = await client.ListIssuesAsync("acme/app", new GitHostTicketFilter { State = "open", Labels = ["bug", "ui"] });

        var issue = Assert.Single(issues);
        Assert.Equal(("acme/app#14", "Crash on start", "open", "ada"), (issue.Key, issue.Title, issue.Status, issue.Assignee));
        Assert.Equal(["bug"], issue.Labels);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/acme/app/issues", request.RequestUri!.GetLeftPart(UriPartial.Path));
        Assert.Equal("bug,ui", FakeHttpHandler.Query(request, "labels"));
        Assert.Equal("open", FakeHttpHandler.Query(request, "state"));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task GitHub_ListsFromTheCache_WhenGitHubIsDown()
    {
        var down = false;
        var handler = new FakeHttpHandler(_ => down
            ? FakeHttpHandler.Json("""{"message": "Server Error"}""", HttpStatusCode.ServiceUnavailable)
            : FakeHttpHandler.Json(GitHubIssues));
        var store = this.CreateStore();
        var filter = new GitHostTicketFilter { State = "open" };

        await Client(store, GitHostStore.GitHubId, handler).ListIssuesAsync("acme/app", filter);
        down = true;
        var issues = await Client(store, GitHostStore.GitHubId, handler).ListIssuesAsync("acme/app", filter);

        Assert.Equal(["acme/app#14"], issues.Select(i => i.Key));
        Assert.True(Directory.EnumerateFiles(Path.Combine(store.CacheDirectory, TicketRef.GitHubSource), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task GitHub_ReadsOnPastPagesFullOfPullRequests()
    {
        // Page 1 is a full page of pull requests; page 2 has the issues.
        var pullRequests = string.Join(',', Enumerable.Range(200, 100).Select(n => $$$"""{"number": {{{n}}}, "title": "PR", "state": "open", "pull_request": {}}"""));
        var handler = new FakeHttpHandler(request => FakeHttpHandler.Json(FakeHttpHandler.Query(request, "page") == "1"
            ? $"[{pullRequests}]"
            : """[{"number": 7, "title": "Real issue", "state": "open"}, {"number": 6, "title": "Older issue", "state": "open"}]"""));
        var client = Client(this.CreateStore(), GitHostStore.GitHubId, handler);

        var issues = await client.ListIssuesAsync("acme/app", new GitHostTicketFilter { State = "open", Limit = 1 });

        Assert.Equal(["acme/app#7"], issues.Select(i => i.Key));
        Assert.Equal(["1", "2"], handler.Requests.Select(r => FakeHttpHandler.Query(r, "page")));
    }

    [Fact]
    public async Task GitHubEnterprise_UsesItsOwnApi_ForGettingAndFiling()
    {
        var bodies = new List<JsonObject>();
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                bodies.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject());
                return FakeHttpHandler.Json("""{"number": 9, "title": "New", "state": "open"}""", HttpStatusCode.Created);
            }

            return FakeHttpHandler.Json("""{"number": 3, "title": "Old", "state": "closed", "body": "Text"}""");
        });
        var store = this.CreateStoreWithSites();
        Assert.Equal("https://ghe.acme.test/api/v3/", store.CreateClient(store.GetRequired("ghe"), "secret").ApiBase);

        // Niddy's readers take the API address from the HTTP client, so the fake one is given the site's.
        var client = store.CreateClient(store.GetRequired("ghe"), "secret",
            new HttpClient(handler) { BaseAddress = new Uri("https://ghe.acme.test/api/v3/") });

        var got = await client.GetIssueAsync("acme/app", 3);
        var filed = await client.CreateIssueAsync("acme/app", new NewTicket { Title = "New", Body = "B", Labels = ["x"] });

        Assert.Equal(("acme/app#3", "closed", "Text"), (got.Key, got.Status, got.Body));
        Assert.Equal("acme/app#9", filed.Key);
        Assert.Equal(["https://ghe.acme.test/api/v3/repos/acme/app/issues/3", "https://ghe.acme.test/api/v3/repos/acme/app/issues"],
            handler.Requests.Select(r => r.RequestUri!.ToString()));
        Assert.Equal(["x"], bodies[0]["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Equal("https://ghe.acme.test/settings/tokens", client.CreateTokenUrl);
    }

    [Fact]
    public async Task GitLab_EncodesTheProject_AndMapsItsStates()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/issues")
            ? FakeHttpHandler.Json($"[{GitLabIssue}]")
            : FakeHttpHandler.Json(GitLabIssue));
        var client = Client(this.CreateStoreWithSites(), "acme-gl", handler);

        var issues = await client.ListIssuesAsync("platform/team/app",
            new GitHostTicketFilter { State = "open", Assignee = "@me", Search = "slow" });
        var issue = await client.GetIssueAsync("platform/team/app", 31);

        Assert.Equal(("platform/team/app#31", "open", "ada"), (issues[0].Key, issues[0].Status, issues[0].Assignee));
        Assert.Equal(("Ten minutes.", "https://git.acme.test/platform/team/app/-/issues/31"), (issue.Body, issue.Url));
        var list = handler.Requests[0];
        Assert.Equal("https://git.acme.test/api/v4/projects/platform%2Fteam%2Fapp/issues", list.RequestUri!.GetLeftPart(UriPartial.Path));
        Assert.Equal("opened", FakeHttpHandler.Query(list, "state"));
        Assert.Equal("assigned_to_me", FakeHttpHandler.Query(list, "scope"));
        Assert.Equal("slow", FakeHttpHandler.Query(list, "search"));
        Assert.Equal("Bearer tok", list.Headers.Authorization!.ToString());
        Assert.EndsWith("/projects/platform%2Fteam%2Fapp/issues/31", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GitLab_FilesWithLabels_AndBuildsTheManualUrl()
    {
        string? posted = null;
        var handler = new FakeHttpHandler(request =>
        {
            posted = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpHandler.Json("""{"iid": 32, "title": "T", "state": "opened"}""", HttpStatusCode.Created);
        });
        var store = this.CreateStore();
        var client = Client(store, GitHostStore.GitLabId, handler);
        var ticket = new NewTicket { Title = "T", Body = "B", Labels = ["ci", "perf"] };

        var filed = await client.CreateIssueAsync("acme/app", ticket);

        Assert.Equal(("acme/app#32", "open"), (filed.Key, filed.Status));
        Assert.Equal("https://gitlab.com/api/v4/projects/acme%2Fapp/issues", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("ci,perf", JsonNode.Parse(posted!)!["labels"]!.GetValue<string>());
        var manual = Client(store, GitHostStore.GitLabId, handler, null).NewIssueUrl("acme/app", ticket);
        Assert.StartsWith("https://gitlab.com/acme/app/-/issues/new?", manual);
        Assert.Contains("/label", Uri.UnescapeDataString(manual));
    }

    [Theory]
    [InlineData("sekrit-value", "rejected the token")]
    [InlineData(null, "needs you to sign in")]
    public async Task Unauthorized_SaysHowToSignIn(string? token, string message)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"message": "401 Unauthorized"}""", HttpStatusCode.Unauthorized));
        var client = Client(this.CreateStoreWithSites(), "acme-gl", handler, token);

        var error = await Assert.ThrowsAsync<AflException>(() => client.ListIssuesAsync("team/app", new GitHostTicketFilter()));

        Assert.Contains(message, error.Message);
        Assert.Contains("AFL_GITHOST_TOKEN_ACME_GL", error.Message);
        Assert.Contains("glab auth login --hostname git.acme.test", error.Message);
        Assert.DoesNotContain("sekrit", error.Message);
    }

    [Fact]
    public async Task SigningInWithoutAToken_FailsBeforeAnyRequest()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}"));

        await Assert.ThrowsAsync<AflException>(() => Client(this.CreateStore(), GitHostStore.GitHubId, handler, null).GetCurrentUserAsync());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void IssueUrls_AreHandledByTheirConnectionsKind()
    {
        var registry = new TicketSourceRegistry { GitHosts = this.CreateStoreWithSites() };

        // An old-style GitLab URL looks like GitHub's; the site it's on decides.
        Assert.Equal("gitlab", registry.Resolve(TicketRef.Parse("https://git.acme.test/team/app/issues/3")).Name);
        Assert.Equal("github", registry.Resolve(TicketRef.Parse("https://ghe.acme.test/acme/app/issues/3")).Name);
        Assert.Equal("gitlab", registry.Resolve(TicketRef.Parse("https://git.acme.test/platform/team/app/-/issues/31")).Name);
    }

    [Fact]
    public void BareNumbers_AreOnTheReposOwnHost()
    {
        var repo = new RepoRecord { Host = new RepoHostLink { ConnectionId = "acme-gl", Kind = "gitlab", Project = "team/app" } };

        Assert.Equal("gitlab", TicketRef.Parse("#12").ForRepo(repo).Source);
        Assert.Equal("gitlab", TicketRef.Parse("acme/app#12").ForRepo(repo).Source);
        Assert.Equal("github", TicketRef.Parse("https://github.com/acme/app/issues/12").ForRepo(repo).Source);
        Assert.Equal("github", TicketRef.Parse("#12").ForRepo(new RepoRecord()).Source);
    }
}
