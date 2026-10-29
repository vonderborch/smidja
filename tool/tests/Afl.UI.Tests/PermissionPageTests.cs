using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Workflows;
using Afl.UI.DataContexts;
using Avalonia.Headless.XUnit;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for asking about refused commands on the Runs page, choosing a permission policy at start, and editing
///     rules in Settings and repo settings.
/// </summary>
public class PermissionPageTests : AbstractUiTest
{
    /// <summary>
    ///     A one-step workflow whose agent is refused <c>uname -a</c> and <c>whoami</c>.
    /// </summary>
    /// <param name="shell">The shell.</param>
    /// <returns>The run, paused on the refusals.</returns>
    private async Task<Run> PausedRunAsync(ShellDataContext shell)
    {
        this.Executor.Respond = _ => new ExecutionResult(ExecutionStatus.Completed, "draft", "s1", null, [Bash("uname -a"), Bash("whoami")]);
        var workflow = new WorkflowDefinition
        {
            Id = "one", Name = "One", Steps = [new AiStepDefinition { Id = "fix", Name = "Fix", Prompt = "Fix it" }],
        }.Chain();
        var run = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);
        this.Executor.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "done", r.SessionId);
        return run;
    }

    /// <summary>
    ///     Describes a refused Bash call.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <returns>The denial.</returns>
    private static PermissionDenial Bash(string command)
    {
        return ToolRuleSuggesterRegistry.Instance.Describe("Bash", null, JsonSerializer.SerializeToElement(new { command }), "/repo");
    }

    [AvaloniaFact]
    public async Task RunsPage_AllowsOnceAlwaysOrDeniesEachRefusedCall()
    {
        using var shell = this.CreateShell();
        var run = await this.PausedRunAsync(shell);

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.True(dc.CanPermit);
        Assert.Equal("The agent was refused 2 commands or tools", dc.PermissionsHeading);
        Assert.Equal(["1. Bash: uname -a", "2. Bash: whoami"], dc.PermissionRequests.Select(r => r.Label));
        Assert.False(dc.CanContinuePermit);

        var first = dc.PermissionRequests[0];
        Assert.Equal("Bash(uname -a)", first.Rule);
        first.IsAlways = true;
        Assert.True(first.IsReady);
        first.Rule = "Bash(a,b)";
        Assert.NotNull(first.RuleProblem);
        Assert.False(first.IsReady);
        first.Rule = "Bash(uname:*)";
        first.IsAlways = false;
        Assert.Null(first.Decision);
        first.IsAlways = true;
        dc.PermissionRequests[1].IsDenied = true;
        Assert.Null(dc.PermissionRequests[1].RuleProblem);
        Assert.True(dc.CanContinuePermit);

        dc.Permit();
        await shell.Driver.Find(run.Id)!.Completion;

        var done = new RunStore(this.Paths).GetRequired(run.Id);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal("done", done.Steps[0].Output);
        Assert.Contains(this.Executor.Requests[^1].AllowedTools, t => t == "Bash(uname:*)");
        Assert.Contains(shell.Permissions.In(PermissionScope.Repo, shell.SelectedRepo!.Id), r => r.Rule == "Bash(uname:*)");
    }

    [AvaloniaFact]
    public async Task RunsPage_AcceptsTheOutputWithoutAllowingAnything()
    {
        using var shell = this.CreateShell();
        var run = await this.PausedRunAsync(shell);

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        dc.AcceptWithoutPermissions();
        await shell.Driver.Find(run.Id)!.Completion;

        var done = new RunStore(this.Paths).GetRequired(run.Id);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal("draft", done.Steps[0].Output);
        Assert.Single(this.Executor.Requests);
    }

    [AvaloniaFact]
    public async Task IssuesPage_StartsWithTheChosenPolicy()
    {
        using var shell = this.CreateShell();
        using var issues = new IssuesPageDataContext(shell);
        issues.SelectedTicket = SampleTicket();
        Assert.Equal(PermissionConstants.DefaultPolicy, issues.PermissionChoices()[0].Name);
        Assert.Contains(issues.PermissionChoices(), p => p.Name == PermissionConstants.PolicyAuto && p.Warning is not null);
        Assert.Contains("Bash(git push:*)", issues.GuardRules());

        var run = issues.Start(issues.WorkflowChoices()[0].Workflow, permissions: PermissionConstants.PolicyAuto);
        Assert.NotNull(run);
        Assert.Equal(PermissionConstants.PolicyAuto, run.PermissionPolicy);
        await shell.Driver.Find(run.Id)!.Completion;
    }

    [AvaloniaFact]
    public void Settings_SavesTheDefaultPolicyAndEditsGlobalRulesAndGuardRails()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        Assert.DoesNotContain(dc.DefaultPolicies, p => p.Value == PermissionConstants.PolicyAuto);
        Assert.Contains("Full auto", dc.PerRunPolicies);

        dc.DefaultPolicy = dc.DefaultPolicies.Single(p => p.Value == PermissionConstants.PolicyStrict);
        Assert.Equal(PermissionConstants.PolicyStrict, shell.Settings.Load().DefaultPermissionPolicy);

        var global = dc.GlobalRules;
        Assert.True(global.IsEmpty);
        global.NewRule = "Bash(dotnet test:*)";
        Assert.True(global.Add());
        Assert.Equal("Added Bash(dotnet test:*).", global.Note);
        Assert.Null(global.NewRule);
        global.Selected = global.Rules.Single();
        Assert.Equal("Change to deny", global.ToggleText);
        Assert.True(global.Toggle());
        Assert.Equal(PermissionRuleKind.Deny, shell.Permissions.Global().Single().Kind);
        global.NewRule = "Bash(a,b)";
        Assert.False(global.Add());
        Assert.NotNull(dc.ErrorMessage);
        global.Selected = global.Rules.Single();
        Assert.True(global.Remove());
        Assert.Empty(shell.Permissions.Global());

        var guards = dc.GuardRules;
        Assert.False(guards.CanAllow);
        Assert.Equal(PermissionConstants.DefaultGuardRules, guards.Rules.Select(r => r.Rule));
        guards.Selected = guards.Rules.First();
        Assert.True(guards.Remove());
        Assert.DoesNotContain(PermissionConstants.DefaultGuardRules[0], shell.Permissions.Guards());
        guards.NewRule = "Bash(docker push:*)";
        Assert.True(guards.Add());
        Assert.Contains("Bash(docker push:*)", shell.Permissions.Guards());
        Assert.True(guards.Reset());
        Assert.Equal(PermissionConstants.DefaultGuardRules, shell.Permissions.Guards());
    }

    [AvaloniaFact]
    public void RepoSettings_EditsTheRepoRules()
    {
        using var shell = this.CreateShell();
        var repoId = shell.SelectedRepo!.Id;
        using var dc = new RepoSettingsPageDataContext(shell);
        var rules = Assert.IsType<ScopedPermissionRuleListDataContext>(dc.PermissionRules);
        Assert.Equal(repoId, rules.RepoId);

        rules.NewRule = "WebFetch(domain:github.com)";
        rules.NewRuleDenies = true;
        Assert.True(rules.Add());

        var saved = Assert.Single(new RepoRegistry(this.Paths).GetRequired(repoId).PermissionRules);
        Assert.Equal("WebFetch(domain:github.com)", saved.Rule);
        Assert.Equal(PermissionRuleKind.Deny, saved.Kind);
        Assert.Empty(shell.Permissions.Global());
    }
}
