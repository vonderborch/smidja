using System.Net;
using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.GitHosts;
using Afl.Core.Helpers;
using Afl.Core.Jira;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using Niddy.Core.Security;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the AI filing tickets part-way through a step (<c>afl-ticket</c> blocks).
/// </summary>
public class TicketBlockTests : AbstractAgentPoolTest
{
    /// <summary>
    ///     The request bodies the fake servers received, read before the clients dispose them.
    /// </summary>
    private readonly List<(string Path, JsonObject Body)> _posted = [];

    /// <summary>
    ///     Initializes a new instance of the <see cref="TicketBlockTests" /> class, giving the scratch repo a GitHub origin.
    /// </summary>
    public TicketBlockTests()
    {
        ProcessRunner.Run("git", ["remote", "add", "origin", "https://github.com/acme/app.git"], this.RepoPath);
    }

    private static AiStepDefinition Harden(bool createTickets = true) => new()
    {
        Id = "harden", Name = "Harden", Prompt = "Harden it", CreateTickets = createTickets,
    };

    private static string Block(string tickets) => $"Looked.\n\n```afl-ticket\n{{\"tickets\": [{tickets}]}}\n```";

    /// <summary>
    ///     Makes the main agent answer each prompt from a script, in order, then echo.
    /// </summary>
    private void Script(params string[] replies)
    {
        var queue = new Queue<string>(replies);
        this.Fake("main").Respond = r =>
            new ExecutionResult(ExecutionStatus.Completed, queue.Count > 0 ? queue.Dequeue() : $"main did: {r.Prompt}", r.SessionId ?? "main-session");
    }

    /// <summary>
    ///     Creates a fake server that records each POST's body and answers from a function.
    /// </summary>
    private FakeHttpHandler Server(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        return new FakeHttpHandler(request =>
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            this._posted.Add((request.RequestUri!.AbsolutePath, body));
            return respond(request, this._posted.Count);
        });
    }

    /// <summary>
    ///     Creates an engine whose tickets go to the given sources.
    /// </summary>
    private RunEngine EngineWith(TicketSourceRegistry tickets)
    {
        return new RunEngine(new RunStore(this.Paths), this.ExecutorFor) { Agents = this.Agents, Tickets = tickets };
    }

    /// <summary>
    ///     A GitHub fake that numbers issues from 100.
    /// </summary>
    private TicketSourceRegistry GitHub(string? token = "test-token")
    {
        var handler = this.Server((_, n) => FakeHttpHandler.Json(
            $$"""{"number": {{99 + n}}, "html_url": "https://github.com/acme/app/issues/{{99 + n}}"}""", HttpStatusCode.Created));
        var hosts = new GitHostStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"), borrowSignIns: false);
        hosts.SetToken(GitHostStore.GitHubId, token);
        return new TicketSourceRegistry
        {
            GitHosts = hosts,
            GitHostHttp = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test/") },
        };
    }

    [Fact]
    public async Task Tickets_AreFiledOnGitHubAndTheAiCarriesOnWithTheirKeys()
    {
        this.Script(Block("""
            {"title": "Rate-limit the login endpoint", "body": "It has none.", "labels": ["security", " security "], "type": "bug"},
            {"title": "Add audit logging"}
            """), "Hardened; filed acme/app#100 and #101.");
        var run = this.Start("main", Harden());

        run = await this.EngineWith(this.GitHub()).DriveAsync(run.Id);

        var main = this.Fake("main").Requests;
        Assert.Contains("```afl-ticket", main[0].Prompt);
        Assert.Contains("GitHub issues in acme/app", main[0].Prompt);
        Assert.Contains($"File at most {StepConstants.MaxTicketsPerStep} more", main[0].Prompt);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal("Hardened; filed acme/app#100 and #101.", step.Output);
        Assert.Equal(["acme/app#100", "acme/app#101"], step.CreatedTickets.Select(t => t.Key));
        Assert.All(step.CreatedTickets, t => Assert.Equal(1, t.Round));
        Assert.Equal("https://github.com/acme/app/issues/100", step.CreatedTickets[0].Url);

        Assert.Equal("/repos/acme/app/issues", this._posted[0].Path);
        Assert.Equal("Rate-limit the login endpoint", this._posted[0].Body["title"]!.GetValue<string>());
        Assert.Equal(["security"], this._posted[0].Body["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Null(this._posted[0].Body["type"]);

        Assert.True(main[1].Resume);
        Assert.StartsWith(StepConstants.TicketResultsHeading, main[1].Prompt);
        Assert.Contains("- acme/app#100: Rate-limit the login endpoint (https://github.com/acme/app/issues/100)", main[1].Prompt);
    }

    [Fact]
    public async Task NoToken_RecordsTheErrorAndWhereToFileItByHand()
    {
        this.Script(Block("""{"title": "Add audit logging", "body": "Who did what."}"""), "Noted.");
        var run = this.Start("main", Harden());

        run = await this.EngineWith(this.GitHub(token: null)).DriveAsync(run.Id);

        var ticket = Assert.Single(run.Steps[0].CreatedTickets);
        Assert.Null(ticket.Key);
        Assert.Contains("no GitHub token", ticket.Error);
        Assert.StartsWith("https://github.com/acme/app/issues/new?", ticket.Url);
        Assert.Contains("issues/new", ticket.Url);
        Assert.Empty(this._posted);
        Assert.Contains("- not created (", this.Fake("main").Requests[1].Prompt);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
    }

    [Fact]
    public async Task TooManyTickets_FinishesTheStepWithANote()
    {
        var tickets = string.Join(", ", Enumerable.Range(1, StepConstants.MaxTicketsPerStep + 1).Select(i => $$"""{"title": "T{{i}}"}"""));
        this.Script(Block(tickets));
        var run = this.Start("main", Harden());

        run = await this.EngineWith(this.GitHub()).DriveAsync(run.Id);

        var step = run.Steps[0];
        Assert.Equal("Looked.", step.Output);
        Assert.Empty(step.CreatedTickets);
        Assert.Contains(step.Notes, n => n.Contains("tried to file tickets") && n.Contains($"only {StepConstants.MaxTicketsPerStep} more"));
    }

    [Theory]
    [InlineData("", "has no tickets")]
    [InlineData("""{"body": "x"}""", "ticket 1 has no title")]
    public async Task UnusableBlock_FinishesTheStepWithANote(string tickets, string why)
    {
        this.Script(Block(tickets));
        var run = this.Start("main", Harden());

        run = await this.EngineWith(this.GitHub()).DriveAsync(run.Id);

        Assert.Contains(run.Steps[0].Notes, n => n.Contains(why));
        Assert.Empty(this._posted);
    }

    [Fact]
    public async Task StepWithoutCreateTickets_IgnoresTheBlock()
    {
        this.Script(Block("""{"title": "X"}"""));
        var run = this.Start("main", Harden(createTickets: false));

        run = await this.EngineWith(this.GitHub()).DriveAsync(run.Id);

        Assert.DoesNotContain("afl-ticket", this.Fake("main").Requests[0].Prompt);
        Assert.Empty(run.Steps[0].CreatedTickets);
        Assert.Contains("```afl-ticket", run.Steps[0].Output);
    }

    /// <summary>
    ///     Starts a run on a given ticket.
    /// </summary>
    private Run StartOn(Ticket ticket, params AbstractStepDefinition[] steps)
    {
        return this.Engine.Create(this.RegisterRepo(), Workflow(steps), ticket, "main");
    }

    [Theory]
    [InlineData(JiraInstanceKind.Cloud, "/rest/api/3/issue")]
    [InlineData(JiraInstanceKind.Datacenter, "/rest/api/2/issue")]
    public async Task JiraRun_FilesInTheTicketsProjectWithTheMappedIssueType(JiraInstanceKind kind, string path)
    {
        var store = new JiraInstanceStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"));
        store.Save(new JiraInstance { Id = "work", Kind = kind, BaseUrl = "https://jira.acme.test", Email = "me@acme.test" }, "tok");
        var repo = this.RegisterRepo();
        new RepoRegistry(this.Paths).LinkJira(repo.Id, "work", ["WEB"]);
        var handler = this.Server((_, _) => FakeHttpHandler.Json("""{"id": "1", "key": "APP-9"}""", HttpStatusCode.Created));
        this.Script(Block("""{"title": "Fix the race", "body": "First line\nsecond line\n\nNext paragraph", "labels": ["tech debt"], "type": "feature"}"""), "Done.");
        var run = this.StartOn(new Ticket { Source = TicketRef.JiraSource, Key = "APP-3", Title = "Login" }, Harden());

        run = await this.EngineWith(new TicketSourceRegistry { JiraInstances = store, JiraHttp = new HttpClient(handler) }).DriveAsync(run.Id);

        var ticket = Assert.Single(run.Steps[0].CreatedTickets);
        Assert.Equal(("APP-9", "https://jira.acme.test/browse/APP-9"), (ticket.Key, ticket.Url));
        Assert.Contains("Jira project APP", this.Fake("main").Requests[0].Prompt);
        var (posted, body) = Assert.Single(this._posted);
        Assert.Equal(path, posted);
        var fields = body["fields"]!;
        Assert.Equal("APP", fields["project"]!["key"]!.GetValue<string>());
        Assert.Equal("Story", fields["issuetype"]!["name"]!.GetValue<string>());
        Assert.Equal(["tech-debt"], fields["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
        if (kind == JiraInstanceKind.Cloud)
        {
            Assert.Equal("doc", fields["description"]!["type"]!.GetValue<string>());
            Assert.Equal(2, fields["description"]!["content"]!.AsArray().Count);
            Assert.Equal("hardBreak", fields["description"]!["content"]![0]!["content"]![1]!["type"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal("First line\nsecond line\n\nNext paragraph", fields["description"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task LinkedJira_IsPreferredOverGitHub_AndAMissingIssueTypeFallsBackToTask()
    {
        var store = new JiraInstanceStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"));
        store.Save(new JiraInstance { Id = "work", Kind = JiraInstanceKind.Datacenter, BaseUrl = "https://jira.acme.test" }, "tok");
        new RepoRegistry(this.Paths).LinkJira(this.RegisterRepo().Id, "work", ["APP"]);
        var handler = this.Server((_, n) => n == 1
            ? FakeHttpHandler.Json("""{"errors": {"issuetype": "Specify a valid issue type"}}""", HttpStatusCode.BadRequest)
            : FakeHttpHandler.Json("""{"key": "APP-10"}""", HttpStatusCode.Created));
        this.Script(Block("""{"title": "Crash on start", "type": "bug"}"""), "Done.");
        var run = this.StartOn(new Ticket { Key = "adhoc", Title = "Tidy up" }, Harden());

        run = await this.EngineWith(new TicketSourceRegistry { JiraInstances = store, JiraHttp = new HttpClient(handler) }).DriveAsync(run.Id);

        Assert.Equal("APP-10", Assert.Single(run.Steps[0].CreatedTickets).Key);
        Assert.Equal(["Bug", "Task"], this._posted.Select(p => p.Body["fields"]!["issuetype"]!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task GitLabLinkedRepo_FilesOnItsGitLabProject()
    {
        var hosts = new GitHostStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"), borrowSignIns: false);
        hosts.SetToken(GitHostStore.GitLabId, "gl-token");
        new RepoRegistry(this.Paths) { GitHosts = hosts }.LinkHost(this.RegisterRepo().Id, GitHostStore.GitLabId, "acme/platform/app");
        var handler = this.Server((_, n) => FakeHttpHandler.Json(
            $$"""{"iid": {{40 + n}}, "web_url": "https://gitlab.com/acme/platform/app/-/issues/{{40 + n}}", "state": "opened"}""",
            HttpStatusCode.Created));
        this.Script(Block("""{"title": "Cache the build", "labels": ["ci", "perf"]}"""), "Filed.");
        var run = this.StartOn(new Ticket { Key = "adhoc", Title = "Speed up CI" }, Harden());

        run = await this.EngineWith(new TicketSourceRegistry
        {
            GitHosts = hosts, GitHostHttp = new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.test/api/v4/") },
        }).DriveAsync(run.Id);

        Assert.Contains("GitLab issues in acme/platform/app", this.Fake("main").Requests[0].Prompt);
        var ticket = Assert.Single(run.Steps[0].CreatedTickets);
        Assert.Equal("acme/platform/app#41", ticket.Key);
        Assert.Equal("https://gitlab.com/acme/platform/app/-/issues/41", ticket.Url);
        Assert.Equal("/api/v4/projects/acme%2Fplatform%2Fapp/issues", this._posted[0].Path);
        Assert.Equal("ci,perf", this._posted[0].Body["labels"]!.GetValue<string>());
    }

    [Fact]
    public void CreateTickets_OnAStepWithoutAnAgent_IsInvalid()
    {
        var workflow = Workflow(ReadStep(
            """{ "id": "pr", "name": "PR", "type": "user", "instructions": "Open a PR", "createTickets": true }"""));

        var problems = WorkflowLibrary.Validate(workflow);

        Assert.Contains(problems, p => p.Contains("doesn't take 'createTickets'"));
    }
}
