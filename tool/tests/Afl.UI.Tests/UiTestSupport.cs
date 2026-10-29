using Afl.Core.GitHosts;
using Afl.Core.Helpers;
using Afl.Core.Jira;
using Afl.Core.Mcp;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Tests;
using Afl.UI;
using Afl.UI.DataContexts;
using Afl.UI.Tests;
using Avalonia;
using Avalonia.Headless;
using Niddy.Avalonia.Hosting;
using Niddy.Core.Security;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Afl.UI.Tests;

/// <summary>
///     Starts the afl app (its styles and resources) on the headless platform, rendering with Skia so frames can be
///     captured as screenshots.
/// </summary>
public static class TestAppBuilder
{
    /// <summary>
    ///     Builds the headless application.
    /// </summary>
    /// <returns>The builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
    }
}

/// <summary>
///     The afl <see cref="App" /> with its own shell over a scratch afl home (tests build their own shells for the pages
///     they show), and without the window-state memory and crash dialog, which would write outside the scratch folders.
/// </summary>
public class TestApp : App
{
    /// <inheritdoc />
    protected override void Configure(NiddyAppOptions options)
    {
        base.Configure(options);
        options.RememberWindowState = false;
        options.HandleUnhandledExceptions = false;
    }

    /// <inheritdoc />
    protected override ShellDataContext CreateShell()
    {
        var paths = new AflPaths(Path.Combine(Path.GetTempPath(), "afl-tests", "app-" + Guid.NewGuid().ToString("N")));
        var secrets = new EncryptedFileSecureStorage(paths.Root, "afl-tests");
        return new ShellDataContext(paths, jiraInstances: new JiraInstanceStore(paths, secrets), servers: new McpServerStore(paths, secrets),
            gitHosts: new GitHostStore(paths, secrets, borrowSignIns: false));
    }
}

/// <summary>
///     Base for UI tests: the scratch afl home and repo from the core tests, plus a shell over them whose AI work is
///     done by a <see cref="FakeStepExecutor" /> and whose Jira tokens go to an encrypted file in the scratch home, so
///     nothing touches the real keychain or runs Claude.
/// </summary>
public abstract class AbstractUiTest : AbstractAflTest
{
    /// <summary>
    ///     Gets the executor that does the AI work.
    /// </summary>
    protected FakeStepExecutor Executor { get; } = new();

    /// <summary>
    ///     A search result from the fake Jira server: two issues, on one page.
    /// </summary>
    private const string JiraSearchResult =
        """
        {"isLast": true, "issues": [
          {"key": "APP-12", "fields": {"summary": "Rate-limit the export endpoint", "status": {"name": "To Do"},
            "labels": ["backend"], "description": {"type": "doc", "version": 1, "content": [{"type": "paragraph",
              "content": [{"type": "text", "text": "Exports can hammer the database. Add a per-user limit."}]}]}}},
          {"key": "APP-9", "fields": {"summary": "Show the export progress", "status": {"name": "In Progress"},
            "assignee": {"displayName": "Ada"}, "labels": ["ui"], "description": null}}
        ]}
        """;

    /// <summary>
    ///     The fake Jira server's projects (Cloud's paged project search).
    /// </summary>
    private const string JiraProjects =
        """
        {"isLast": true, "values": [
          {"key": "APP", "name": "App"}, {"key": "OPS", "name": "Operations"}, {"key": "WEB", "name": "Website"}
        ]}
        """;

    /// <summary>
    ///     The fake Jira server's agile boards.
    /// </summary>
    private const string JiraBoards =
        """
        {"isLast": true, "values": [
          {"id": 42, "name": "App scrum", "type": "scrum", "location": {"projectKey": "APP"}},
          {"id": 7, "name": "Web kanban", "type": "kanban", "location": {"projectKey": "WEB"}}
        ]}
        """;

    /// <summary>
    ///     The fake GitLab server's issues.
    /// </summary>
    private const string GitLabIssues =
        """
        [
          {"iid": 31, "title": "Cache the pipeline status badge", "state": "opened", "description": "It's fetched on every page view.",
            "web_url": "https://gitlab.com/acme/platform/app/-/issues/31", "labels": ["performance"], "assignees": []},
          {"iid": 27, "title": "Dark mode for the dashboard", "state": "opened", "description": null,
            "web_url": "https://gitlab.com/acme/platform/app/-/issues/27", "labels": ["ui", "good first issue"],
            "assignees": [{"username": "ada"}]}
        ]
        """;

    /// <summary>
    ///     The fake GitHub server's issues, one of them a pull request (which isn't listed).
    /// </summary>
    private const string GitHubIssues =
        """
        [
          {"number": 14, "title": "Retry flaky uploads", "state": "open", "body": "Uploads fail on bad wifi.",
            "html_url": "https://github.com/acme/app/issues/14", "labels": [{"name": "bug"}], "assignees": []},
          {"number": 15, "title": "Bump the SDK", "state": "open", "html_url": "https://github.com/acme/app/pull/15",
            "pull_request": {}, "labels": [], "assignees": []}
        ]
        """;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractUiTest" /> class.
    /// </summary>
    protected AbstractUiTest()
    {
        this.Jira = new FakeHttpHandler(this.RespondAsJira);
        this.GitHost = new FakeHttpHandler(RespondAsGitHost);
    }

    /// <summary>
    ///     Gets the fake GitHub and GitLab API every git host connection talks to: two GitLab issues, one GitHub issue
    ///     (plus a pull request) and a signed-in user.
    /// </summary>
    protected FakeHttpHandler GitHost { get; }

    /// <summary>
    ///     Gets the fake Jira server the shell's Jira sources and the settings pages talk to: three projects, two
    ///     boards, and every search finds the same two issues.
    /// </summary>
    protected FakeHttpHandler Jira { get; }

    /// <summary>
    ///     Gets or sets whether the fake Jira server is down, answering every request with 503.
    /// </summary>
    protected bool JiraDown { get; set; }

    /// <summary>
    ///     Answers a request to the fake Jira server.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    private HttpResponseMessage RespondAsJira(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        return this.JiraDown ? FakeHttpHandler.Json("""{"errorMessages": ["Jira is down"]}""", System.Net.HttpStatusCode.ServiceUnavailable)
            : path.EndsWith("/project/search", StringComparison.Ordinal) ? FakeHttpHandler.Json(JiraProjects)
            : path.EndsWith("/agile/1.0/board", StringComparison.Ordinal) ? FakeHttpHandler.Json(JiraBoards)
            : FakeHttpHandler.Json(JiraSearchResult);
    }

    /// <summary>
    ///     Answers a request to the fake git host API.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    private static HttpResponseMessage RespondAsGitHost(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path.EndsWith("/user", StringComparison.Ordinal) ? FakeHttpHandler.Json("""{"login": "ada", "username": "ada", "name": "Ada"}""")
            : path.StartsWith("/projects/", StringComparison.Ordinal) ? FakeHttpHandler.Json(GitLabIssues)
            : FakeHttpHandler.Json(GitHubIssues);
    }

    /// <summary>
    ///     Creates a git host store over the scratch home that never borrows the CLIs' or the environment's sign-ins.
    /// </summary>
    /// <returns>The store.</returns>
    protected GitHostStore CreateGitHostStore()
    {
        return new GitHostStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"), borrowSignIns: false);
    }

    /// <summary>
    ///     Creates a Jira instance store over the scratch home.
    /// </summary>
    /// <returns>The store.</returns>
    protected JiraInstanceStore CreateJiraStore()
    {
        return new JiraInstanceStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests"));
    }

    /// <summary>
    ///     Creates a shell over the scratch home, with the scratch repo registered and selected.
    /// </summary>
    /// <returns>The shell.</returns>
    protected ShellDataContext CreateShell()
    {
        var repo = this.RegisterRepo();
        var jira = this.CreateJiraStore();
        var shell = new ShellDataContext(this.Paths, new RunEngine(new RunStore(this.Paths), this.Executor), jira,
            new TicketSourceRegistry
            {
                JiraInstances = jira,
                JiraHttp = new HttpClient(this.Jira),
                GitHosts = this.CreateGitHostStore(),
                GitHostHttp = new HttpClient(this.GitHost) { BaseAddress = new Uri("https://git.test/") },
            },
            new McpServerStore(this.Paths, new EncryptedFileSecureStorage(this.Paths.Root, "afl-tests")));
        shell.SelectedRepoItem = shell.RepoItems.Single(r => r.Id == repo.Id);
        return shell;
    }
}

/// <summary>
///     Reaches the per-type part of a step draft in tests, failing the test when the step is of another type.
/// </summary>
public static class StepDraftParts
{
    /// <summary>
    ///     The agent part of an ai or interactive step.
    /// </summary>
    /// <param name="step">The step draft.</param>
    /// <returns>Its agent part.</returns>
    public static AgentStepDetailsDataContext AgentPart(this StepDraftDataContext step)
    {
        return Xunit.Assert.IsType<AgentStepDetailsDataContext>(step.Details);
    }

    /// <summary>
    ///     The part of a step the user does.
    /// </summary>
    /// <param name="step">The step draft.</param>
    /// <returns>Its user part.</returns>
    public static UserStepDetailsDataContext UserPart(this StepDraftDataContext step)
    {
        return Xunit.Assert.IsType<UserStepDetailsDataContext>(step.Details);
    }

    /// <summary>
    ///     The part of a step that starts runs of another workflow.
    /// </summary>
    /// <param name="step">The step draft.</param>
    /// <returns>Its workflow part.</returns>
    public static WorkflowStepDetailsDataContext WorkflowPart(this StepDraftDataContext step)
    {
        return Xunit.Assert.IsType<WorkflowStepDetailsDataContext>(step.Details);
    }
}
