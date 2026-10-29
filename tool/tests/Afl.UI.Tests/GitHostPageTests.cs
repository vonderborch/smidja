using Afl.Core.GitHosts;
using Afl.Core.Helpers;
using Afl.Core.Tests;
using Afl.Core.Tickets;
using Afl.UI.DataContexts;
using Avalonia.Headless.XUnit;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for connecting to GitHub and GitLab from the UI: the Settings page's Git hosts tab, the repo settings
///     card that links a repo to a project, and listing a linked project's issues.
/// </summary>
public class GitHostPageTests : AbstractUiTest
{
    [AvaloniaFact]
    public async Task GitHostsTab_TestsAndSavesATokenWithoutKeepingItInTheJson()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        Assert.Equal([GitHostStore.GitHubId, GitHostStore.GitLabId], dc.GitHostConnections.Select(c => c.Id));

        dc.NewGitHost();
        dc.GitHostId = "acme-gl";
        dc.GitHostName = "Acme GitLab";
        dc.GitHostKind = TicketRef.GitLabSource;
        dc.GitHostBaseUrl = "https://git.acme.test/";
        dc.GitHostToken = "gl-s3cret-value";
        Assert.Equal("https://git.acme.test/-/user_settings/personal_access_tokens", dc.GitHostTokenUrl!.ToString());

        Assert.True(await dc.SaveGitHostAsync(), dc.ErrorMessage);

        Assert.Null(dc.GitHostToken);
        Assert.Equal("Saved acme-gl; signed in as ada.", dc.GitHostStatus);
        Assert.Equal("acme-gl", dc.SelectedGitHost?.Id);
        Assert.StartsWith("Token set", dc.GitHostTokenStatus);
        Assert.True(dc.CanRemoveGitHost);
        Assert.True(shell.GitHosts.HasToken("acme-gl"));
        Assert.Contains(this.GitHost.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(this.Paths.Root, "*.json", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("gl-s3cret-value", File.ReadAllText(file));
        }

        // A built-in site keeps its address; only its token can change, and it can't be removed.
        dc.SelectedGitHost = dc.GitHostConnections.Single(c => c.Id == GitHostStore.GitHubId);
        Assert.Equal("https://github.com", dc.GitHostBaseUrl);
        Assert.True(dc.IsGitHostFormLocked);
        Assert.False(dc.CanRemoveGitHost);
        Assert.False(dc.RemoveGitHost());
        Assert.StartsWith("Not signed in", dc.GitHostTokenStatus);

        dc.SelectedGitHost = dc.GitHostConnections.Single(c => c.Id == "acme-gl");
        Assert.True(dc.ClearGitHostToken());
        Assert.False(shell.GitHosts.HasToken("acme-gl"));
        Assert.True(dc.RemoveGitHost());
        Assert.Null(shell.GitHosts.Find("acme-gl"));
        Assert.DoesNotContain(dc.GitHostConnections, c => c.Id == "acme-gl");

        // A bad address is reported, not saved.
        dc.NewGitHost();
        dc.GitHostId = "bad";
        dc.GitHostBaseUrl = "not a url";
        Assert.False(await dc.SaveGitHostAsync());
        Assert.Contains("isn't an http(s) URL", dc.ErrorMessage);
    }

    [AvaloniaFact]
    public void RepoSettings_DetectsLinksAndUnlinksTheProject()
    {
        using var shell = this.CreateShell();
        using var dc = new RepoSettingsPageDataContext(shell);
        Assert.False(dc.IsHostLinked);
        Assert.False(dc.DetectHostLink());

        ProcessRunner.Run("git", ["remote", "add", "origin", "git@gitlab.com:acme/platform/app.git"], this.RepoPath);
        Assert.True(dc.DetectHostLink(), dc.ErrorMessage);
        Assert.Equal((GitHostStore.GitLabId, "acme/platform/app"), (dc.HostConnectionId, dc.HostProject));
        Assert.Equal("Issues come from acme/platform/app on GitLab.", dc.HostLinkText);

        Assert.True(dc.UnlinkHost());
        Assert.False(dc.IsHostLinked);
        Assert.Null(shell.SelectedRepo!.Host);

        dc.HostConnectionId = GitHostStore.GitHubId;
        dc.HostProject = "acme/platform/app";
        Assert.False(dc.SaveHostLink());
        Assert.Contains("isn't a GitHub repository", dc.ErrorMessage);

        dc.HostProject = "https://github.com/acme/app";
        Assert.True(dc.SaveHostLink(), dc.ErrorMessage);
        Assert.Equal("acme/app", shell.SelectedRepo!.Host!.Project);
        Assert.Equal(TicketRef.GitHubSource, shell.SelectedRepo.Host.Kind);
    }

    [AvaloniaFact]
    public async Task IssuesPage_ListsTheLinkedGitLabProjectsIssues()
    {
        using var shell = this.CreateShell();
        using (var unlinked = new IssuesPageDataContext(shell))
        {
            Assert.True(unlinked.HasNoSources);
        }

        shell.Repos.LinkHost(shell.SelectedRepo!.Id, GitHostStore.GitLabId, "acme/platform/app");
        shell.Reload();
        using var dc = new IssuesPageDataContext(shell);
        Assert.Equal([TicketRef.GitLabSource], dc.Sources);
        Assert.True(dc.IsGitHost);

        dc.OnlyMine = true;
        dc.LabelsText = "ui, good first issue";
        await dc.RefreshAsync();

        Assert.True(dc.ErrorMessage is null, dc.ErrorMessage);
        Assert.Equal(2, dc.Tickets.Count);
        // The page also loads once when it opens, unfiltered; find the filtered listing.
        var request = Assert.Single(this.GitHost.Requests, r => FakeHttpHandler.Query(r, "labels") is not null);
        Assert.Equal("/projects/acme%2Fplatform%2Fapp/issues", request.RequestUri!.AbsolutePath);
        Assert.Equal("assigned_to_me", FakeHttpHandler.Query(request, "scope"));
        Assert.Equal("ui,good first issue", FakeHttpHandler.Query(request, "labels"));
    }
}
