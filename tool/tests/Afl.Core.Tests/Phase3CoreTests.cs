using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Jira;
using Afl.Core.Repos;
using Afl.Core.Terminals;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the core pieces the desktop UI builds on: terminal launch scripts, the repo's Jira link helpers and
///     writing workflows to each scope.
/// </summary>
public class Phase3CoreTests : AbstractAflTest
{
    /// <summary>
    ///     A launch whose prompt contains quotes, spaces and a newline.
    /// </summary>
    private static readonly InteractiveLaunch Launch = new()
    {
        Executable = "claude",
        Arguments = ["--session-id", "abc", "Fix Bob's \"bug\"\nthen stop"],
        WorkingDirectory = "/tmp/my repo",
    };

    [Fact]
    public void UnixLauncher_QuotesEveryArgumentForSh()
    {
        Assert.Equal("'it'\\''s'", AbstractUnixTerminalLauncher.Quote("it's"));

        var script = new MacTerminalLauncher().BuildScript(Launch);

        Assert.StartsWith("#!/bin/sh\ncd '/tmp/my repo' || exit 1\n", script);
        Assert.Contains("'claude' '--session-id' 'abc' 'Fix Bob'\\''s \"bug\"\nthen stop'", script);
    }

    [Fact]
    public void WindowsLauncher_QuotesEveryArgumentForPowerShell()
    {
        Assert.Equal("'it''s'", WindowsTerminalLauncher.Quote("it's"));

        var script = new WindowsTerminalLauncher().BuildScript(Launch);

        Assert.StartsWith("Set-Location -LiteralPath '/tmp/my repo'\n& 'claude' ", script);
        Assert.Contains("'Fix Bob''s \"bug\"\nthen stop'", script);
    }

    [Fact]
    public void TerminalRegistry_FindsALauncherForThisPlatform()
    {
        Assert.Equal(3, TerminalLauncherRegistry.Instance.Keys.Count());
        Assert.True(TerminalLauncherRegistry.Instance.ForCurrentPlatform().IsSupported);
    }

    [Fact]
    public void RepoRegistry_ManagesJiraFilters()
    {
        var repo = this.RegisterRepo();
        var registry = new RepoRegistry(this.Paths);

        var unlinked = Assert.Throws<AflException>(() => registry.SaveJiraFilter(repo.Id, new JiraFilter { Name = "mine" }, false));
        Assert.Equal(AflErrorKind.InvalidState, unlinked.Kind);

        registry.LinkJira(repo.Id, "work");
        registry.SaveJiraFilter(repo.Id, new JiraFilter { Name = " mine ", Assignee = "me" }, false);
        registry.SaveJiraFilter(repo.Id, new JiraFilter { Name = "team", Project = "APP" }, false);
        registry.SaveJiraFilter(repo.Id, new JiraFilter { Name = "MINE", Assignee = "someone" }, false);

        var link = new RepoRegistry(this.Paths).GetRequired(repo.Id).Jira!;
        Assert.Equal("mine", link.DefaultFilter);
        Assert.Equal(2, link.Filters.Count);
        Assert.Equal("someone", link.FindFilter("mine")!.Assignee);

        registry.SetDefaultJiraFilter(repo.Id, "TEAM");
        Assert.Equal("team", registry.GetRequired(repo.Id).Jira!.DefaultFilter);

        registry.RemoveJiraFilter(repo.Id, "team");
        Assert.Null(registry.GetRequired(repo.Id).Jira!.DefaultFilter);
        Assert.Throws<AflException>(() => registry.RemoveJiraFilter(repo.Id, "team"));

        registry.LinkJira(repo.Id, "other");
        var relinked = registry.GetRequired(repo.Id).Jira!;
        Assert.Equal("other", relinked.InstanceId);
        Assert.Single(relinked.Filters);
    }

    [Fact]
    public void WorkflowLibrary_SavesJsonToEachScopeAndDeletes()
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);
        var workflow = Workflow(new UserStepDefinition { Id = "do", Name = "Do it", Instructions = "Go" });

        var globalPath = library.Save(workflow, WorkflowScope.Global);
        workflow.Name = "Repo copy";
        var repoPath = library.Save(workflow, WorkflowScope.Repo);

        Assert.Equal(Path.Combine(this.Paths.WorkflowsDirectory, "test.json"), globalPath);
        Assert.Equal(Path.Combine(this.RepoPath, ".afl", "workflows", "test.json"), repoPath);
        Assert.Contains("\"instructions\": \"Go\"", File.ReadAllText(repoPath));
        Assert.Equal("Repo copy", library.GetRequired("test").Name);

        Assert.True(library.Delete("test", WorkflowScope.Repo));
        Assert.False(library.Delete("test", WorkflowScope.Repo));
        Assert.Equal("Test", library.GetRequired("test").Name);
        Assert.Throws<AflException>(() => library.Delete("feature", WorkflowScope.BuiltIn));
        Assert.Throws<AflException>(() => new WorkflowLibrary(this.Paths).PathFor("x", WorkflowScope.Repo));
    }
}
