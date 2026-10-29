using System.Net;
using System.Text;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Jira;
using Afl.Core.Repos;
using Afl.Core.Tickets;
using Niddy.Core.Security;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for JQL building.
/// </summary>
public class JqlBuilderTests
{
    /// <summary>
    ///     Data Center's team clause: raw JQL in brackets.
    /// </summary>
    private static readonly Func<string, string> RawTeam = team => $"({team})";

    [Fact]
    public void EmptyCriteriaGiveOpenTicketsNewestFirst()
    {
        Assert.Equal("statusCategory != Done ORDER BY updated DESC", JqlBuilder.Build(new JiraFilter(), new TicketFilter(), RawTeam));
    }

    [Fact]
    public void AllStatesAndNoCriteriaGiveOnlyTheOrdering()
    {
        Assert.Equal(JqlBuilder.DefaultOrderBy, JqlBuilder.Build(new JiraFilter(), new TicketFilter { State = "all" }, RawTeam));
    }

    [Fact]
    public void ClosedStateMeansDone()
    {
        Assert.StartsWith("statusCategory = Done", JqlBuilder.Build(new JiraFilter(), new TicketFilter { State = "closed" }, RawTeam));
    }

    [Fact]
    public void CriteriaAreAndedInOrder()
    {
        var criteria = new JiraFilter
        {
            Project = "PROJ", Team = "cf[10001] = 42", Assignee = "me", Labels = ["backend", "p1"],
        };
        var jql = JqlBuilder.Build(criteria, new TicketFilter { Search = "dark mode" }, RawTeam);
        Assert.Equal(
            "project = \"PROJ\" AND (cf[10001] = 42) AND assignee = currentUser() AND statusCategory != Done AND " +
            "labels = \"backend\" AND labels = \"p1\" AND text ~ \"dark mode\" ORDER BY updated DESC", jql);
    }

    [Theory]
    [InlineData("@me", "assignee = currentUser()")]
    [InlineData("unassigned", "assignee is EMPTY")]
    [InlineData("none", "assignee is EMPTY")]
    [InlineData("jdoe", "assignee = \"jdoe\"")]
    public void AssigneeShortcuts(string assignee, string clause)
    {
        var jql = JqlBuilder.Build(new JiraFilter { Assignee = assignee }, new TicketFilter { State = "all" }, RawTeam);
        Assert.Equal($"{clause} {JqlBuilder.DefaultOrderBy}", jql);
    }

    [Fact]
    public void StatusesReplaceTheStateClause()
    {
        var jql = JqlBuilder.Build(new JiraFilter { Statuses = ["In Progress", "Review"] }, new TicketFilter(), RawTeam);
        Assert.Equal("status in (\"In Progress\", \"Review\") ORDER BY updated DESC", jql);
    }

    [Fact]
    public void RawJqlIsBracketedAndItsOrderingWins()
    {
        var jql = JqlBuilder.Build(new JiraFilter { Project = "P", Jql = "priority = High OR sprint in openSprints() order by rank" },
            new TicketFilter { State = "all" }, RawTeam);
        Assert.Equal("project = \"P\" AND (priority = High OR sprint in openSprints()) order by rank", jql);
    }

    [Fact]
    public void QuotesAreEscaped()
    {
        Assert.Equal("\"say \\\"hi\\\" \\\\ bye\"", JqlBuilder.Quote("say \"hi\" \\ bye"));
    }

    [Fact]
    public void CloudTeamUsesTheTeamField()
    {
        var client = new JiraCloudClient(new JiraInstance { Id = "c", BaseUrl = "https://x", Email = "a@b" }, "t", new HttpClient());
        Assert.Equal("\"Team[Team]\" = \"abc-123\"", client.TeamClause("abc-123"));
    }

    [Fact]
    public void ProjectScopeLimitsSearchesThatPickNoProject()
    {
        var all = new TicketFilter { State = "all" };
        Assert.Equal("project = \"APP\" ORDER BY updated DESC",
            JqlBuilder.Build(new JiraFilter { ProjectScope = ["APP"] }, all, RawTeam));
        Assert.Equal("project in (\"APP\", \"WEB\") ORDER BY updated DESC",
            JqlBuilder.Build(new JiraFilter { ProjectScope = ["APP", "WEB"] }, all, RawTeam));
    }

    [Theory]
    [InlineData("OPS", null, null)]
    [InlineData(null, 12, null)]
    [InlineData(null, null, "project = OPS ORDER BY rank")]
    [InlineData(null, null, "Project in (OPS, INFRA)")]
    public void ProjectScopeYieldsToAnExplicitProjectBoardOrJql(string? project, int? board, string? jql)
    {
        var built = JqlBuilder.Build(new JiraFilter { Project = project, BoardId = board, Jql = jql, ProjectScope = ["APP", "WEB"] },
            new TicketFilter { State = "all" }, RawTeam);
        Assert.DoesNotContain("APP", built);
    }

    [Fact]
    public void ProjectScopeStaysWhenJqlOnlyMentionsProjectInText()
    {
        var built = JqlBuilder.Build(new JiraFilter { Jql = "summary ~ \"project plan\"", ProjectScope = ["APP"] },
            new TicketFilter { State = "all" }, RawTeam);
        Assert.StartsWith("project = \"APP\" AND", built);
    }
}

/// <summary>
///     Tests for the Cloud and Data Center clients against a fake server.
/// </summary>
public class JiraClientTests
{
    [Fact]
    public async Task CloudSearchUsesBasicAuthAndPagesByToken()
    {
        var handler = new FakeHttpHandler(request => FakeHttpHandler.Query(request, "nextPageToken") is null
            ? FakeHttpHandler.Json($"{{\"issues\":[{Issue("P-1", Adf("Hello"))}],\"nextPageToken\":\"p2\",\"isLast\":false}}")
            : FakeHttpHandler.Json($"{{\"issues\":[{Issue("P-2", "null")}],\"isLast\":true}}"));
        var client = Cloud(handler);

        var tickets = await client.ListAsync(new JiraFilter { Project = "P" }, new TicketFilter());

        Assert.Equal(["P-1", "P-2"], tickets.Select(t => t.Key));
        Assert.Equal(2, handler.Requests.Count);
        var first = handler.Requests[0];
        Assert.Equal("/rest/api/3/search/jql", first.RequestUri!.AbsolutePath);
        Assert.Equal("Basic", first.Headers.Authorization!.Scheme);
        Assert.Equal("me@acme.com:secret", Encoding.UTF8.GetString(Convert.FromBase64String(first.Headers.Authorization.Parameter!)));
        Assert.Equal("project = \"P\" AND statusCategory != Done ORDER BY updated DESC", FakeHttpHandler.Query(first, "jql"));
        Assert.Equal("p2", FakeHttpHandler.Query(handler.Requests[1], "nextPageToken"));

        var ticket = tickets[0];
        Assert.Equal(TicketRef.JiraSource, ticket.Source);
        Assert.Equal("Summary of P-1", ticket.Title);
        Assert.Equal("Hello", ticket.Body?.Trim());
        Assert.Equal("In Progress", ticket.Status);
        Assert.Equal("Ada", ticket.Assignee);
        Assert.Equal(["ui"], ticket.Labels);
        Assert.Equal("https://acme.atlassian.net/browse/P-1", ticket.Url);
    }

    [Fact]
    public async Task CloudSearchStopsAtTheLimit()
    {
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Json($"{{\"issues\":[{Issue("P-1", "null")},{Issue("P-2", "null")}],\"nextPageToken\":\"more\"}}"));
        var tickets = await Cloud(handler).SearchAsync("project = P", 2);
        Assert.Equal(2, tickets.Count);
        Assert.Single(handler.Requests);
        Assert.Equal("2", FakeHttpHandler.Query(handler.Requests[0], "maxResults"));
    }

    [Fact]
    public async Task DataCenterSearchUsesBearerAndPagesByOffset()
    {
        var handler = new FakeHttpHandler(request => FakeHttpHandler.Query(request, "startAt") == "0"
            ? FakeHttpHandler.Json($"{{\"total\":2,\"issues\":[{Issue("D-1", "\"h1. Wiki *text*\"")}]}}")
            : FakeHttpHandler.Json($"{{\"total\":2,\"issues\":[{Issue("D-2", "null")}]}}"));
        var client = DataCenter(handler);

        var tickets = await client.SearchAsync("project = D", 10);

        Assert.Equal(["D-1", "D-2"], tickets.Select(t => t.Key));
        Assert.Equal("/jira/rest/api/2/search", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("secret", handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal("1", FakeHttpHandler.Query(handler.Requests[1], "startAt"));
        Assert.Equal("h1. Wiki *text*", tickets[0].Body);
        Assert.Equal("https://jira.acme.com/jira/browse/D-1", tickets[0].Url);
    }

    [Fact]
    public async Task BoardFiltersGoThroughTheAgileApiWithoutOrdering()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json($"{{\"total\":1,\"issues\":[{Issue("B-1", "null")}]}}"));
        var tickets = await DataCenter(handler).ListAsync(new JiraFilter { BoardId = 7, Assignee = "me" }, new TicketFilter());

        Assert.Single(tickets);
        var request = handler.Requests.Single();
        Assert.Equal("/jira/rest/agile/1.0/board/7/issue", request.RequestUri!.AbsolutePath);
        Assert.Equal("assignee = currentUser() AND statusCategory != Done", FakeHttpHandler.Query(request, "jql"));
    }

    [Fact]
    public async Task BoardsAreListedAcrossPages()
    {
        var handler = new FakeHttpHandler(request => FakeHttpHandler.Query(request, "startAt") == "0"
            ? FakeHttpHandler.Json("{\"values\":[{\"id\":1,\"name\":\"Team A\",\"type\":\"scrum\",\"location\":{\"projectKey\":\"A\"}}],\"isLast\":false}")
            : FakeHttpHandler.Json("{\"values\":[{\"id\":2,\"name\":\"Team B\",\"type\":\"kanban\"}],\"isLast\":true}"));
        var boards = await Cloud(handler).ListBoardsAsync("A");

        Assert.Equal([1, 2], boards.Select(b => b.Id));
        Assert.Equal("A", boards[0].ProjectKey);
        Assert.Equal("A", FakeHttpHandler.Query(handler.Requests[0], "projectKeyOrId"));
    }

    [Fact]
    public async Task UnauthorizedMeansReAddTheToken()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}", HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<AflException>(() => Cloud(handler).GetMyselfAsync());
        Assert.Equal(AflErrorKind.InvalidState, error.Kind);
        Assert.Contains("afl jira instance add", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BadRequestCarriesJiraMessages()
    {
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Json("{\"errorMessages\":[\"Field 'bogus' does not exist.\"]}", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<AflException>(() => DataCenter(handler).SearchAsync("bogus = 1", 5));
        Assert.Equal(AflErrorKind.InvalidInput, error.Kind);
        Assert.Contains("Field 'bogus' does not exist.", error.Message);
    }

    [Fact]
    public async Task TransientFailuresAreRetried()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => ++calls == 1
            ? FakeHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
            : FakeHttpHandler.Json("{\"displayName\":\"Ada\",\"name\":\"ada\"}"));
        var user = await DataCenter(handler).GetMyselfAsync();
        Assert.Equal("Ada", user.DisplayName);
        Assert.Equal("ada", user.AccountName);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnexpectedResponsesAreReportedNotThrownRaw()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{\"values\":[]}"));
        var error = await Assert.ThrowsAsync<AflException>(() => DataCenter(handler).SearchAsync("x = 1", 5));
        Assert.Equal(AflErrorKind.ExternalTool, error.Kind);
        Assert.Contains("rest/api/2/search", error.Message);
    }

    [Fact]
    public void ClientsAreCreatedByKind()
    {
        var http = new HttpClient();
        Assert.IsType<JiraCloudClient>(JiraClientRegistry.Instance.Create(
            new JiraInstance { Id = "c", Kind = JiraInstanceKind.Cloud, BaseUrl = "https://x" }, "t", http));
        Assert.IsType<JiraDataCenterClient>(JiraClientRegistry.Instance.Create(
            new JiraInstance { Id = "d", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://y" }, "t", http));
    }

    /// <summary>
    ///     Creates a Cloud client talking to a fake server.
    /// </summary>
    /// <param name="handler">The fake server.</param>
    /// <returns>The client.</returns>
    internal static JiraCloudClient Cloud(FakeHttpHandler handler)
    {
        return new JiraCloudClient(new JiraInstance
        {
            Id = "cloud", Kind = JiraInstanceKind.Cloud, BaseUrl = "https://acme.atlassian.net", Email = "me@acme.com",
        }, "secret", new HttpClient(handler));
    }

    /// <summary>
    ///     Creates a Data Center client talking to a fake server.
    /// </summary>
    /// <param name="handler">The fake server.</param>
    /// <returns>The client.</returns>
    internal static JiraDataCenterClient DataCenter(FakeHttpHandler handler)
    {
        return new JiraDataCenterClient(new JiraInstance
        {
            Id = "dc", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://jira.acme.com/jira",
        }, "secret", new HttpClient(handler));
    }

    /// <summary>
    ///     Builds an issue's JSON.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="description">The description JSON.</param>
    /// <returns>The JSON.</returns>
    internal static string Issue(string key, string description)
    {
        return $"{{\"key\":\"{key}\",\"fields\":{{\"summary\":\"Summary of {key}\",\"description\":{description}," +
               "\"status\":{\"name\":\"In Progress\"},\"assignee\":{\"displayName\":\"Ada\"},\"labels\":[\"ui\"]}}";
    }

    /// <summary>
    ///     Builds an Atlassian Document Format description holding one paragraph.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The JSON.</returns>
    private static string Adf(string text)
    {
        return $"{{\"type\":\"doc\",\"version\":1,\"content\":[{{\"type\":\"paragraph\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}]}}";
    }
}

/// <summary>
///     Tests for the instance store and the Jira ticket source.
/// </summary>
public class JiraStoreTests : AbstractAflTest
{
    [Fact]
    public void SaveStoresTheTokenAndRemoveDeletesIt()
    {
        var store = this.CreateStore();
        store.Save(new JiraInstance { Id = "work", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://jira.acme.com/ " }, "tok");

        var saved = store.GetRequired("WORK");
        Assert.Equal("https://jira.acme.com", saved.BaseUrl);
        Assert.True(store.HasToken("work"));
        Assert.Equal("tok", store.ResolveToken("work"));

        var addedAt = saved.AddedAt;
        store.Save(new JiraInstance { Id = "work", Name = "Work", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://jira.acme.com" }, null);
        Assert.Equal(addedAt, store.GetRequired("work").AddedAt);
        Assert.Equal("tok", store.ResolveToken("work"));

        Assert.True(store.Remove("work"));
        Assert.False(store.HasToken("work"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void EnvironmentTokenOverridesTheStoredOne()
    {
        var id = "env-" + Guid.NewGuid().ToString("N")[..8];
        var variable = JiraInstanceStore.TokenVariable(id);
        Assert.Matches("^AFL_JIRA_TOKEN_ENV_[A-Z0-9]+$", variable);
        var store = this.CreateStore();
        store.Save(new JiraInstance { Id = id, Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://j" }, "stored");
        try
        {
            Environment.SetEnvironmentVariable(variable, "from-env");
            Assert.Equal("from-env", store.ResolveToken(id));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Assert.Equal("stored", store.ResolveToken(id));
    }

    [Fact]
    public void MissingTokenIsReported()
    {
        var store = this.CreateStore();
        store.Save(new JiraInstance { Id = "notoken", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://j" }, null);
        Assert.Equal(AflErrorKind.InvalidState, Assert.Throws<AflException>(() => store.ResolveToken("notoken")).Kind);
    }

    [Theory]
    [InlineData("bad id", JiraInstanceKind.Datacenter, "https://j", null)]
    [InlineData("ok", JiraInstanceKind.Datacenter, "ftp://j", null)]
    [InlineData("ok", JiraInstanceKind.Cloud, "https://x.atlassian.net", null)]
    public void InvalidInstancesAreRejected(string id, JiraInstanceKind kind, string url, string? email)
    {
        var error = Assert.Throws<AflException>(() =>
            this.CreateStore().Save(new JiraInstance { Id = id, Kind = kind, BaseUrl = url, Email = email }, null));
        Assert.Equal(AflErrorKind.InvalidInput, error.Kind);
    }

    [Fact]
    public void UrlsFindTheMostSpecificInstance()
    {
        var store = this.CreateStore();
        store.Save(new JiraInstance { Id = "root", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://jira.acme.com" }, null);
        store.Save(new JiraInstance { Id = "sub", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://jira.acme.com/eng" }, null);

        Assert.Equal("sub", store.FindByUrl("http://jira.acme.com/eng/browse/X-1")?.Id);
        Assert.Equal("root", store.FindByUrl("https://jira.acme.com/browse/X-1")?.Id);
        Assert.Null(store.FindByUrl("https://jira.acme.company/browse/X-1"));
    }

    [Fact]
    public void DefaultFilterIsUsedAndOverridden()
    {
        var link = new RepoJiraLink
        {
            InstanceId = "work", DefaultFilter = "mine",
            Filters =
            [
                new JiraFilter { Name = "mine", Project = "P", Assignee = "me", Labels = ["x"] },
                new JiraFilter { Name = "team", Team = "t" },
            ],
        };

        var byDefault = JiraTicketSource.ResolveCriteria(link, new JiraTicketFilter());
        Assert.Equal("P", byDefault.Project);

        var merged = JiraTicketSource.ResolveCriteria(link,
            new JiraTicketFilter { Criteria = new JiraFilter { Assignee = "unassigned", Labels = ["y"] } });
        Assert.Equal("P", merged.Project);
        Assert.Equal("unassigned", merged.Assignee);
        Assert.Equal(["y"], merged.Labels);

        Assert.Equal("t", JiraTicketSource.ResolveCriteria(link, new JiraTicketFilter { SavedFilter = "TEAM" }).Team);
        Assert.True(JiraTicketSource.ResolveCriteria(link, new JiraTicketFilter { SkipDefaultFilter = true }).IsEmpty);
        Assert.Equal(AflErrorKind.NotFound,
            Assert.Throws<AflException>(() => JiraTicketSource.ResolveCriteria(link, new JiraTicketFilter { SavedFilter = "nope" })).Kind);
    }

    [Fact]
    public void RepoProjectsScopeListingsUnlessAllProjectsIsAsked()
    {
        var link = new RepoJiraLink { InstanceId = "work", Projects = ["APP", "WEB"] };

        Assert.Equal(["APP", "WEB"], JiraTicketSource.ResolveCriteria(link, new JiraTicketFilter()).ProjectScope);
        Assert.Empty(JiraTicketSource.ResolveCriteria(link, new JiraTicketFilter { AllProjects = true }).ProjectScope);
        Assert.Empty(link.Filters);
    }

    [Fact]
    public void RepoProjectsAreNormalizedAddedAndRemoved()
    {
        var repos = new RepoRegistry(this.Paths);
        var id = this.RegisterRepo().Id;
        Assert.Equal(AflErrorKind.InvalidState, Assert.Throws<AflException>(() => repos.SetJiraProjects(id, ["APP"])).Kind);

        repos.LinkJira(id, "work", ["app", " web ", "APP", ""]);
        Assert.Equal(["APP", "WEB"], repos.GetRequired(id).Jira!.Projects);

        repos.LinkJira(id, "other");
        Assert.Equal(["APP", "WEB"], repos.GetRequired(id).Jira!.Projects);

        repos.AddJiraProjects(id, ["ops", "web"]);
        Assert.Equal(["APP", "WEB", "OPS"], repos.GetRequired(id).Jira!.Projects);

        repos.RemoveJiraProjects(id, ["app"]);
        Assert.Equal(["WEB", "OPS"], repos.GetRequired(id).Jira!.Projects);
        Assert.Equal(AflErrorKind.NotFound, Assert.Throws<AflException>(() => repos.RemoveJiraProjects(id, ["APP"])).Kind);
        Assert.Equal(AflErrorKind.InvalidInput, Assert.Throws<AflException>(() => repos.AddJiraProjects(id, ["not a key"])).Kind);
        Assert.Equal(["WEB", "OPS"], repos.FindByJiraProject("ops").Single().Jira!.Projects);
    }

    [Fact]
    public void TicketKeysFindTheRepoAssociatedWithTheirProject()
    {
        var repos = new RepoRegistry(this.Paths);
        var app = this.RegisterRepo();
        repos.LinkJira(app.Id, "work", ["APP"]);
        var webPath = Path.Combine(this.Root, "web-repo");
        Directory.CreateDirectory(webPath);
        ProcessRunner.Run("git", ["init", "-q"], webPath);
        var web = repos.EnsureRegistered(webPath);
        repos.LinkJira(web.Id, "work", ["WEB"]);

        Assert.Equal("APP", TicketRef.Parse("app-12").ProjectKey);
        Assert.Equal("MY_PROJ", TicketRef.Parse("https://acme.atlassian.net/browse/MY_PROJ-3").ProjectKey);
        Assert.Null(TicketRef.Parse("#4").ProjectKey);

        // Outside any repo: the project picks it.
        var found = repos.ResolveForTicket(TicketRef.Parse("WEB-7"), null);
        Assert.Equal(web.Id, found.Repo.Id);
        Assert.NotNull(found.Note);

        // In the right repo: no note. In the wrong one: kept, with a note naming the right one.
        Assert.Null(repos.ResolveForTicket(TicketRef.Parse("APP-1"), repos.GetRequired(app.Id)).Note);
        var wrong = repos.ResolveForTicket(TicketRef.Parse("WEB-7"), repos.GetRequired(app.Id));
        Assert.Equal(app.Id, wrong.Repo.Id);
        Assert.Contains(web.Name, wrong.Note);

        // A project no repo claims: noted only if the repo has its own projects.
        Assert.Contains("isn't one of", repos.ResolveForTicket(TicketRef.Parse("OPS-1"), repos.GetRequired(app.Id)).Note);
        Assert.Equal(AflErrorKind.InvalidInput,
            Assert.Throws<AflException>(() => repos.ResolveForTicket(TicketRef.Parse("OPS-1"), null)).Kind);

        // Two repos claiming one project: ambiguous without a repo.
        repos.AddJiraProjects(app.Id, ["WEB"]);
        Assert.Contains("--repo", Assert.Throws<AflException>(() => repos.ResolveForTicket(TicketRef.Parse("WEB-7"), null)).Message);
        Assert.Null(repos.ResolveForTicket(TicketRef.Parse("WEB-7"), repos.GetRequired(app.Id)).Note);
    }

    [Fact]
    public async Task SourceListsWithTheRepoDefaultFilter()
    {
        var store = this.CreateStore();
        store.Save(new JiraInstance { Id = "work", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://jira.acme.com" }, "tok");
        var repo = new RepoRegistry(this.Paths).Modify(this.RegisterRepo().Id, r => r.Jira = new RepoJiraLink
        {
            InstanceId = "work", DefaultFilter = "mine", Filters = [new JiraFilter { Name = "mine", Project = "P" }],
        });
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.Contains("/issue/")
            ? FakeHttpHandler.Json(JiraClientTests.Issue("PR-9", "null"))
            : FakeHttpHandler.Json($"{{\"total\":1,\"issues\":[{JiraClientTests.Issue("PR-9", "null")}]}}"));
        var source = new TestJiraTicketSource(store, handler);

        Assert.True(source.IsAvailableFor(repo));
        var tickets = await source.ListAsync(repo, new JiraTicketFilter { Search = "crash" });

        Assert.Equal("PR-9", tickets.Single().Key);
        Assert.Equal("project = \"P\" AND statusCategory != Done AND text ~ \"crash\" ORDER BY updated DESC",
            FakeHttpHandler.Query(handler.Requests.Single(), "jql"));

        var ticket = await source.GetAsync(repo, TicketRef.Parse("https://jira.acme.com/browse/PR-9"));
        Assert.Equal("/rest/api/2/issue/PR-9", handler.Requests.Last().RequestUri!.AbsolutePath);
        Assert.Equal("PR-9", ticket.Key);
    }

    /// <summary>
    ///     Creates an instance store whose tokens live in the scratch home.
    /// </summary>
    /// <returns>The store.</returns>
    private JiraInstanceStore CreateStore()
    {
        return new JiraInstanceStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"));
    }

    /// <summary>
    ///     A Jira source using a given store and a fake server.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="handler">The fake server.</param>
    private sealed class TestJiraTicketSource(JiraInstanceStore store, FakeHttpHandler handler) : JiraTicketSource
    {
        /// <inheritdoc />
        protected override JiraInstanceStore CreateStore()
        {
            return store;
        }

        /// <inheritdoc />
        protected override AbstractJiraClient CreateClient(JiraInstanceStore instances, JiraInstance instance)
        {
            return instances.CreateClient(instance, new HttpClient(handler));
        }
    }
}
