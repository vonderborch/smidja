using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for permission policies, rules, suggestions and pausing a step to ask.
/// </summary>
public class PermissionTests : AbstractAflTest
{
    private readonly FakeStepExecutor _executor = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor);

    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    private static ExecutionResult Refused(string output, params PermissionDenial[] denials) =>
        new(ExecutionStatus.Completed, output, "s1", null, denials);

    private static PermissionDenial Bash(string command) =>
        ToolRuleSuggesterRegistry.Instance.Describe("Bash", "t1", JsonSerializer.SerializeToElement(new { command }), "/repo");

    [Fact]
    public void Bash_SuggestsExactThenPrefixes()
    {
        var denial = Bash("dotnet test tool/Afl.slnx");

        Assert.Equal(["Bash(dotnet test tool/Afl.slnx)", "Bash(dotnet test:*)", "Bash(dotnet:*)"], denial.SuggestedRules);
        Assert.Equal("Bash: dotnet test tool/Afl.slnx", denial.Display);
    }

    [Fact]
    public void Bash_CompoundCommandGetsOnlyTheExactRule()
    {
        Assert.Equal(["Bash(make && make install)"], Bash("make && make install").SuggestedRules);
    }

    [Fact]
    public void Bash_CommandWithCommaGetsOnlyAPrefix()
    {
        Assert.Equal(["Bash(echo:*)"], Bash("echo a,b").SuggestedRules);
    }

    [Fact]
    public void WebFetch_SuggestsTheDomain()
    {
        var denial = ToolRuleSuggesterRegistry.Instance.Describe("WebFetch", null,
            JsonSerializer.SerializeToElement(new { url = "https://github.com/a/b" }), "/repo");

        Assert.Equal(["WebFetch(domain:github.com)", "WebFetch"], denial.SuggestedRules);
    }

    [Fact]
    public void Write_SuggestsAnEditRuleForTheFileThenItsFolder()
    {
        var denial = ToolRuleSuggesterRegistry.Instance.Describe("Write", null,
            JsonSerializer.SerializeToElement(new { file_path = "/repo/src/App/Main.cs" }), "/repo");

        Assert.Equal(["Edit(src/App/Main.cs)", "Edit(src/App/**)"], denial.SuggestedRules);
    }

    [Fact]
    public void McpTools_FallBackToTheToolName()
    {
        var denial = ToolRuleSuggesterRegistry.Instance.Describe("mcp__jira__search", null, default, "/repo");

        Assert.Equal(["mcp__jira__search"], denial.SuggestedRules);
    }

    [Theory]
    [InlineData("Bash(dotnet test:*)", true)]
    [InlineData("mcp__jira__search", true)]
    [InlineData("Bash(a,b)", false)]
    [InlineData("Bash(a\nb)", false)]
    [InlineData("not a rule", false)]
    public void Rules_AreChecked(string rule, bool valid)
    {
        Assert.Equal(valid, PermissionRule.IsValid(rule));
    }

    [Fact]
    public async Task Rules_FromEveryScopeAndTheGuardsReachTheAgent()
    {
        var repo = this.RegisterRepo();
        var store = new PermissionRuleStore(this.Paths);
        store.Add(PermissionScope.Global, null, "WebFetch(domain:example.com)", PermissionRuleKind.Allow);
        store.Add(PermissionScope.Repo, repo.Id, "Bash(dotnet test:*)", PermissionRuleKind.Allow);
        store.Add(PermissionScope.Repo, repo.Id, "Bash(rm:*)", PermissionRuleKind.Deny);
        var run = this.Engine.Create(repo, Workflow(Ai("fix", "Fix it")), SampleTicket());

        await this.Engine.DriveAsync(run.Id);

        var request = Assert.Single(this._executor.Requests);
        Assert.Contains("Bash(dotnet test:*)", request.AllowedTools);
        Assert.Contains("WebFetch(domain:example.com)", request.AllowedTools);
        Assert.Equal([.. PermissionConstants.DefaultGuardRules, "Bash(rm:*)"], request.DisallowedTools);
    }

    [Fact]
    public async Task Auto_BypassesPermissionsButKeepsTheGuards()
    {
        var review = Ai("review", "Review it");
        review.PermissionMode = StepConstants.ReadOnlyPermissionMode;
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it"), review), SampleTicket(),
            permissions: PermissionConstants.PolicyAuto);

        await this.Engine.DriveAsync(run.Id);

        Assert.Equal(PermissionConstants.BypassPermissionsMode, this._executor.Requests[0].PermissionMode);
        Assert.Equal(PermissionConstants.DefaultGuardRules, this._executor.Requests[0].DisallowedTools);
        Assert.Equal(StepConstants.ReadOnlyPermissionMode, this._executor.Requests[1].PermissionMode);
    }

    [Fact]
    public async Task Auto_ARefusal_PointsAtTheGuardRails()
    {
        this._executor.Respond = _ => Refused("done", Bash("git push"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket(),
            permissions: PermissionConstants.PolicyAuto);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var note = Assert.Single(run.Steps[0].Notes, n => n.Contains("denied permission"));
        Assert.Contains("afl permission guards --remove", note);
        Assert.DoesNotContain("ask policy", note);
    }

    [Fact]
    public void Auto_CantBeTheDefault()
    {
        var settings = new SettingsStore(this.Paths);

        var changed = settings.Load();
        changed.DefaultPermissionPolicy = PermissionConstants.PolicyAuto;

        Assert.Throws<AflException>(() => settings.Save(changed));
    }

    [Fact]
    public async Task Ask_PausesWithTheRefusedCalls()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());

        run = await this.Engine.DriveAsync(run.Id);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.AwaitingPermission, step.Status);
        Assert.True(step.WaitsForUser);
        Assert.Equal("draft", step.Output);
        var request = Assert.Single(step.OpenPermissionRequests);
        Assert.Equal(1, request.Index);
        Assert.Equal("Bash(uname -a)", request.Denial.SuggestedRules[0]);
    }

    [Fact]
    public async Task PermitOnce_ResumesWithTheRuleForOneTurnOnly()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it"), Ai("next", "Next")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);
        this._executor.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "done", "s1");

        run = await this.Engine.PermitAsync(run.Id, [new PermissionChoice(1, PermissionDecision.Once)]);

        var resumed = this._executor.Requests[1];
        Assert.True(resumed.Resume);
        Assert.Contains("Bash(uname -a)", resumed.AllowedTools);
        Assert.Contains("Bash(uname -a)", resumed.Prompt);
        Assert.DoesNotContain("Bash(uname -a)", this._executor.Requests[2].AllowedTools);
        Assert.Equal("done", run.Steps[0].Output);
        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Empty(run.PermissionRules);
    }

    [Fact]
    public async Task PermitAlways_SavesTheRuleToTheScope()
    {
        var repo = this.RegisterRepo();
        this._executor.Respond = _ => Refused("draft", Bash("dotnet test x"));
        var run = this.Engine.Create(repo, Workflow(Ai("fix", "Fix it")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);
        this._executor.Respond = _ => new ExecutionResult(ExecutionStatus.Completed, "done", "s1");

        await this.Engine.PermitAsync(run.Id,
            [new PermissionChoice(1, PermissionDecision.Always, "Bash(dotnet test:*)", PermissionScope.Repo)]);

        var saved = Assert.Single(new RepoRegistry(this.Paths).GetRequired(repo.Id).PermissionRules);
        Assert.Equal("Bash(dotnet test:*)", saved.Rule);
        Assert.Equal("Bash: dotnet test x", saved.Note);
    }

    [Fact]
    public async Task PermitAlwaysForTheRun_KeepsTheRuleOnTheRun()
    {
        this._executor.Respond = _ => Refused("draft", Bash("make"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it"), Ai("next", "Next")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);
        this._executor.Respond = _ => new ExecutionResult(ExecutionStatus.Completed, "done", "s1");

        run = await this.Engine.PermitAsync(run.Id,
            [new PermissionChoice(1, PermissionDecision.Always, null, PermissionScope.Run)]);

        Assert.Equal("Bash(make)", Assert.Single(run.PermissionRules).Rule);
        Assert.Contains("Bash(make)", this._executor.Requests[2].AllowedTools);
    }

    [Fact]
    public async Task PermitDeny_ResumesWithoutTheRule()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);
        this._executor.Respond = _ => new ExecutionResult(ExecutionStatus.Completed, "done", "s1");

        await this.Engine.PermitAsync(run.Id, [new PermissionChoice(1, PermissionDecision.Deny)]);

        var resumed = this._executor.Requests[1];
        Assert.DoesNotContain("Bash(uname -a)", resumed.AllowedTools);
        Assert.Contains("Still refused", resumed.Prompt);
    }

    [Fact]
    public async Task Permit_WaitsUntilEveryRequestIsDecided()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"), Bash("whoami"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.PermitAsync(run.Id, [new PermissionChoice(1, PermissionDecision.Once)]);

        Assert.Equal(StepStatus.AwaitingPermission, run.Steps[0].Status);
        Assert.Single(this._executor.Requests);
        Assert.Equal(PermissionDecision.Once, run.Steps[0].PermissionRequests[0].Decision);
    }

    [Fact]
    public async Task AcceptOutput_FinishesTheStepWithANote()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.PermitAsync(run.Id, [], acceptOutput: true);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("draft", run.Steps[0].Output);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("Bash: uname -a"));
        Assert.Single(this._executor.Requests);
    }

    [Fact]
    public async Task Ask_StopsAskingAfterTheCap()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());
        run = await this.Engine.DriveAsync(run.Id);

        for (var i = 1; i < PermissionConstants.MaxPermissionRounds; i++)
        {
            run = await this.Engine.PermitAsync(run.Id, [new PermissionChoice(i, PermissionDecision.Deny)]);
            Assert.Equal(StepStatus.AwaitingPermission, run.Steps[0].Status);
        }

        run = await this.Engine.PermitAsync(run.Id,
            [new PermissionChoice(PermissionConstants.MaxPermissionRounds, PermissionDecision.Deny)]);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("denied permission"));
    }

    [Fact]
    public async Task Permit_RejectsABadRule()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);

        await Assert.ThrowsAsync<AflException>(() =>
            this.Engine.PermitAsync(run.Id, [new PermissionChoice(1, PermissionDecision.Once, "Bash(a,b)")]));
    }

    [Fact]
    public void ChildRuns_InheritThePolicyAndRules()
    {
        var parent = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket(),
            permissions: PermissionConstants.PolicyStrict);
        parent.PermissionRules.Add(new PermissionRule { Rule = "Bash(make)" });

        var child = this.Engine.CreateChild(parent, "fix", Workflow(Ai("a", "A")), SampleTicket(), parent.Executor, null);

        Assert.Equal(PermissionConstants.PolicyStrict, child.PermissionPolicy);
        Assert.Equal("Bash(make)", Assert.Single(child.PermissionRules).Rule);
    }

    [Fact]
    public void GlobalRules_RoundTrip()
    {
        var store = new PermissionRuleStore(this.Paths);

        Assert.True(store.Add(PermissionScope.Global, null, "Bash(make)", PermissionRuleKind.Allow));
        Assert.False(store.Add(PermissionScope.Global, null, "Bash(make)", PermissionRuleKind.Allow));
        Assert.Equal("Bash(make)", Assert.Single(new PermissionRuleStore(this.Paths).Global()).Rule);
        Assert.True(store.Remove(PermissionScope.Global, null, "Bash(make)"));
        Assert.Empty(store.Global());
    }

    private static PermissionDenial Call(string tool, object input, params string[] denyRules) =>
        ToolRuleSuggesterRegistry.Instance.Describe(tool, null, JsonSerializer.SerializeToElement(input), "/repo", denyRules);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void GuardRails_MarkTheCallsTheyBlock()
    {
        var guards = PermissionConstants.DefaultGuardRules.ToArray();

        Assert.Equal("Read(~/.afl/**)", Call("Glob", new { pattern = "*", path = Path.Combine(Home, ".afl", "worktrees") }, guards).BlockedBy);
        Assert.Equal("Read(~/.afl/**)", Call("Glob", new { pattern = Home + "/.afl/runs/**/*.json" }, guards).BlockedBy);
        Assert.Equal("Read(~/.afl/**)", Call("Grep", new { pattern = "token", path = "~/.afl" }, guards).BlockedBy);
        Assert.Equal("Read(~/.ssh/**)", Call("Read", new { file_path = Home + "/.ssh/id_ed25519" }, guards).BlockedBy);
        Assert.Equal("Edit(~/.afl/**)", Call("Write", new { file_path = Home + "/.afl/settings.json" }, guards).BlockedBy);
        Assert.Equal("Bash(git push:*)", Call("Bash", new { command = "git push origin main" }, guards).BlockedBy);
        Assert.Equal("Bash(git push:*)", Call("Bash", new { command = "git push" }, guards).BlockedBy);

        Assert.Null(Call("Bash", new { command = "git pushx" }, guards).BlockedBy);
        Assert.Null(Call("Glob", new { pattern = "**/*.cs" }, guards).BlockedBy);
        Assert.Null(Call("Read", new { file_path = "/repo/.afl-notes.md" }, guards).BlockedBy);
        Assert.Null(Call("Read", new { file_path = Home + "/.aflx/a" }, guards).BlockedBy);
        Assert.Null(Call("WebFetch", new { url = "https://github.com" }, guards).BlockedBy);
    }

    [Fact]
    public void DenyRules_MatchToolNamesAndPathsTheWayClaudeReadsThem()
    {
        Assert.Equal("WebFetch", Call("WebFetch", new { url = "https://x.test" }, "WebFetch").BlockedBy);
        Assert.Equal("Read", Call("Glob", new { pattern = "*" }, "Read").BlockedBy);
        Assert.Equal("Read(secrets/**)", Call("Read", new { file_path = "/repo/secrets/a/b.txt" }, "Read(secrets/**)").BlockedBy);
        Assert.Equal("Read(.env*)", Call("Read", new { file_path = ".env.local" }, "Read(.env*)").BlockedBy);
        Assert.Equal("Read(//etc/**)", Call("Read", new { file_path = "/etc/hosts" }, "Read(//etc/**)").BlockedBy);
        Assert.Null(Call("Read", new { file_path = "/repo/src/secrets.cs" }, "Read(secrets/**)").BlockedBy);
        Assert.Null(Call("Edit", new { file_path = "/repo/a.cs" }, "Read(**)").BlockedBy);
    }

    [Fact]
    public void Glob_SuggestsReadingItsFolder()
    {
        Assert.Equal(["Read(src/**)", "Glob"], Call("Glob", new { pattern = "*.cs", path = "/repo/src" }).SuggestedRules);
        Assert.Equal(["Glob"], Call("Glob", new { pattern = "**/*.cs" }).SuggestedRules);
    }

    [Fact]
    public async Task Ask_DoesNotAskAboutCallsAGuardRailBlocks()
    {
        var blocked = Call("Glob", new { pattern = "*", path = Home + "/.afl/worktrees" }, [.. PermissionConstants.DefaultGuardRules]);
        this._executor.Respond = _ => Refused("draft", blocked);
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Empty(run.Steps[0].PermissionRequests);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("blocked by Read(~/.afl/**)") && n.Contains("afl permission guards"));
    }

    [Fact]
    public async Task Ask_AsksOnlyAboutTheCallsAllowingWouldFix()
    {
        var blocked = Call("Bash", new { command = "git push" }, [.. PermissionConstants.DefaultGuardRules]);
        this._executor.Respond = _ => Refused("draft", blocked, Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(StepStatus.AwaitingPermission, run.Steps[0].Status);
        Assert.Equal("Bash: uname -a", Assert.Single(run.Steps[0].OpenPermissionRequests).Denial.Display);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("Bash: git push (blocked by Bash(git push:*))"));
    }

    [Fact]
    public async Task Ask_StopsAskingAboutACallRefusedAgainAfterBeingAllowed()
    {
        this._executor.Respond = _ => Refused("draft", Bash("uname -a"));
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Ai("fix", "Fix it")), SampleTicket());
        await this.Engine.DriveAsync(run.Id);

        run = await this.Engine.PermitAsync(run.Id, [new PermissionChoice(1, PermissionDecision.Always, Scope: PermissionScope.Repo)]);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Single(run.Steps[0].PermissionRequests);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains(PermissionConstants.RefusedAfterAllowed));
    }
}
