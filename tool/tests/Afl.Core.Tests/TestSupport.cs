using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Repos;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using Niddy.Core.IO;

namespace Afl.Core.Tests;

/// <summary>
///     Base for tests: a scratch afl home and a scratch git repo, deleted afterwards.
/// </summary>
public abstract class AbstractAflTest : IDisposable
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractAflTest" /> class.
    /// </summary>
    protected AbstractAflTest()
    {
        this.Root = Path.Combine(Path.GetTempPath(), "afl-tests", Guid.NewGuid().ToString("N"));
        this.Paths = new AflPaths(Path.Combine(this.Root, "home"));
        this.RepoPath = Path.Combine(this.Root, "sample-repo");
        Directory.CreateDirectory(this.RepoPath);
        ProcessRunner.Run("git", ["init", "-q"], this.RepoPath);
        this.RepoPath = GitHelpers.FindGitRoot(this.RepoPath)!;
    }

    /// <summary>
    ///     Gets the scratch root.
    /// </summary>
    protected string Root { get; }

    /// <summary>
    ///     Gets the scratch afl paths.
    /// </summary>
    protected AflPaths Paths { get; }

    /// <summary>
    ///     Gets the scratch git repo.
    /// </summary>
    protected string RepoPath { get; }

    /// <summary>
    ///     Reads a step from JSON, as a workflow file would give it.
    /// </summary>
    /// <param name="json">The step's JSON.</param>
    /// <returns>The step.</returns>
    protected static AbstractStepDefinition ReadStep(string json)
    {
        return JsonSerializer.Deserialize<AbstractStepDefinition>(json, JsonConstants.Options)!;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.DeleteWithRetry(this.Root, true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Registers the scratch repo.
    /// </summary>
    /// <returns>The repo.</returns>
    protected RepoRecord RegisterRepo()
    {
        return new RepoRegistry(this.Paths).EnsureRegistered(this.RepoPath);
    }

    /// <summary>
    ///     Creates a ticket.
    /// </summary>
    /// <returns>The ticket.</returns>
    protected static Ticket SampleTicket()
    {
        return new Ticket
        {
            Source = TicketRef.GitHubSource, Key = "acme/app#7", Title = "Add dark mode", Body = "Users want it.",
            Url = "https://github.com/acme/app/issues/7",
        };
    }

    /// <summary>
    ///     Creates a workflow from steps.
    /// </summary>
    /// <param name="steps">The steps.</param>
    /// <returns>The workflow.</returns>
    protected static WorkflowDefinition Workflow(params AbstractStepDefinition[] steps)
    {
        return new WorkflowDefinition { Id = "test", Name = "Test", Steps = steps.ToList() }.Chain();
    }
}

/// <summary>
///     An executor that answers from a script and records every request.
/// </summary>
public class FakeStepExecutor() : AbstractStepExecutor("fake")
{
    /// <summary>
    ///     How many requests are being answered now.
    /// </summary>
    private int _atOnce;

    /// <summary>
    ///     Gets the requests received.
    /// </summary>
    public List<ExecutionRequest> Requests { get; } = [];

    /// <summary>
    ///     Gets or sets how to answer a request. Defaults to echoing the prompt.
    /// </summary>
    public Func<ExecutionRequest, ExecutionResult> Respond { get; set; } =
        r => new ExecutionResult(ExecutionStatus.Completed, $"output for: {r.Prompt}", r.SessionId);

    /// <summary>
    ///     Gets or sets whether the executor can open interactive sessions.
    /// </summary>
    public bool SupportsInteractive { get; set; }

    /// <summary>
    ///     Gets or sets whether the executor can continue a step's conversation.
    /// </summary>
    public bool Resumes { get; set; } = true;

    /// <inheritdoc />
    public override bool SupportsPermissions => true;

    /// <inheritdoc />
    public override bool SupportsResume => this.Resumes;

    /// <summary>
    ///     Gets or sets whether the executor can be given MCP servers.
    /// </summary>
    public bool TakesMcpServers { get; set; }

    /// <inheritdoc />
    public override bool CanRunInteractive => true;

    /// <inheritdoc />
    public override bool SupportsMcpServers => this.TakesMcpServers;

    /// <summary>
    ///     Gets or sets how long each request takes, so tests can see requests overlap.
    /// </summary>
    public TimeSpan Delay { get; set; }

    /// <summary>
    ///     Gets or sets how many requests to wait for at once before answering, so tests see them overlap however
    ///     slowly they start; each waits at most ten seconds, and none waits once that many have overlapped.
    /// </summary>
    public int Together { get; set; }

    /// <summary>
    ///     Gets the most requests that were being answered at once.
    /// </summary>
    public int MostAtOnce { get; private set; }

    /// <inheritdoc />
    public override async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, IProgress<ExecutorEvent>? progress, CancellationToken ct)
    {
        lock (this.Requests)
        {
            this.Requests.Add(request);
            this.MostAtOnce = Math.Max(this.MostAtOnce, ++this._atOnce);
        }

        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (this.MostAtOnce < this.Together && DateTime.UtcNow < until)
            {
                await Task.Delay(10, ct);
            }

            if (this.Delay > TimeSpan.Zero)
            {
                await Task.Delay(this.Delay, ct);
            }

            return this.Respond(request);
        }
        finally
        {
            lock (this.Requests)
            {
                this._atOnce--;
            }
        }
    }

    /// <inheritdoc />
    public override InteractiveLaunch? GetInteractiveLaunch(ExecutionRequest request)
    {
        return this.SupportsInteractive
            ? new InteractiveLaunch { Executable = "claude", Arguments = ["--session-id", request.SessionId, request.Prompt] }
            : null;
    }
}

/// <summary>
///     A ticket source over an in-memory list, to exercise the generic filter handling.
/// </summary>
public class FakeTicketSource() : AbstractTicketSource<TicketFilter>("fake")
{
    /// <summary>
    ///     Gets the tickets.
    /// </summary>
    public List<Ticket> Tickets { get; } = [];

    /// <inheritdoc />
    public override bool IsAvailableFor(RepoRecord repo)
    {
        return true;
    }

    /// <inheritdoc />
    public override Task<Ticket> GetAsync(RepoRecord repo, TicketRef reference, CancellationToken ct = default)
    {
        return Task.FromResult(this.Tickets.Single(t => t.Key == reference.Key));
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<Ticket>> ListTypedAsync(RepoRecord repo, TicketFilter filter, CancellationToken ct)
    {
        IReadOnlyList<Ticket> result = this.Tickets
            .Where(t => filter.Search is null || t.Title.Contains(filter.Search, StringComparison.OrdinalIgnoreCase))
            .Take(filter.Limit)
            .ToList();
        return Task.FromResult(result);
    }
}

/// <summary>
///     An HTTP handler that answers from a function and records every request, for testing HTTP clients.
/// </summary>
/// <param name="responder">Produces the response for a request.</param>
public class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    /// <summary>
    ///     Gets the requests received, in order.
    /// </summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    ///     Creates a JSON response.
    /// </summary>
    /// <param name="json">The body.</param>
    /// <param name="status">The status.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Json(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }

    /// <summary>
    ///     Returns a query parameter of a request, unescaped.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The value, or null.</returns>
    public static string? Query(HttpRequestMessage request, string name)
    {
        foreach (var pair in request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == name)
            {
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            }
        }

        return null;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Pages list on background threads, so two requests can arrive at once.
        lock (this.Requests)
        {
            this.Requests.Add(request);
        }

        return Task.FromResult(responder(request));
    }
}
