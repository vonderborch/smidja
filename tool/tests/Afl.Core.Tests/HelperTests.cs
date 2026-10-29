using Afl.Core.Helpers;
using Afl.Core.Tickets;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for templates, ticket references and git URL parsing.
/// </summary>
public class HelperTests
{
    [Fact]
    public void PromptTemplate_RendersKnownAndBlanksUnknown()
    {
        var result = PromptTemplate.Render("Fix {{ ticket.key }}: {{ticket.title}} {{nope}}!",
            new Dictionary<string, string> { ["ticket.key"] = "#7", ["ticket.title"] = "Crash" });

        Assert.Equal("Fix #7: Crash !", result);
    }

    [Fact]
    public void PromptTemplate_ListsReferences()
    {
        Assert.Equal(["a.b", "steps.plan.output"], PromptTemplate.References("{{a.b}} and {{ steps.plan.output }} and {{a.b}}"));
    }

    [Theory]
    [InlineData("42", "github", null, 42, null)]
    [InlineData("#42", "github", null, 42, null)]
    [InlineData("acme/app#42", "github", "acme/app", 42, null)]
    [InlineData("https://github.com/acme/app/issues/42", "github", "acme/app", 42, null)]
    [InlineData("https://github.com/acme/app/pull/42", "github", "acme/app", 42, null)]
    [InlineData("acme/platform/app#31", "gitlab", "acme/platform/app", 31, null)]
    [InlineData("https://gitlab.com/acme/platform/app/-/issues/31", "gitlab", "acme/platform/app", 31, null)]
    [InlineData("https://git.acme.test/team/app/-/merge_requests/5", "gitlab", "team/app", 5, null)]
    [InlineData("proj-45", "jira", null, null, "PROJ-45")]
    [InlineData("https://acme.atlassian.net/browse/PROJ-45", "jira", null, null, "PROJ-45")]
    public void TicketRef_Parses(string text, string source, string? repository, int? number, string? key)
    {
        var reference = TicketRef.Parse(text);

        Assert.Equal(source, reference.Source);
        Assert.Equal(repository, reference.Repository);
        Assert.Equal(number, reference.Number);
        Assert.Equal(key, reference.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a ticket")]
    [InlineData("https://example.com/whatever")]
    public void TicketRef_RejectsNonsense(string text)
    {
        Assert.False(TicketRef.TryParse(text, out _));
    }

    [Theory]
    [InlineData("git@github.com:acme/app.git", "github.com", "acme/app")]
    [InlineData("https://github.com/acme/app", "github.com", "acme/app")]
    [InlineData("https://github.com/acme/my.app.git", "github.com", "acme/my.app")]
    [InlineData("https://GitLab.com/acme/app.git", "gitlab.com", "acme/app")]
    [InlineData("ssh://git@git.acme.com:2222/platform/tools/afl.git", "git.acme.com", "platform/tools/afl")]
    [InlineData("git@git.acme.com:platform/tools/afl", "git.acme.com", "platform/tools/afl")]
    [InlineData("https://user@acme.com/gitlab/team/app.git/", "acme.com", "gitlab/team/app")]
    public void GitHelpers_ParsesRemotes(string url, string host, string path)
    {
        Assert.Equal((host, path), GitHelpers.ParseRemote(url));
    }

    [Theory]
    [InlineData("/Users/me/src/app")]
    [InlineData("../app.git")]
    [InlineData("file:///Users/me/src/app")]
    [InlineData("")]
    public void GitHelpers_IgnoresLocalRemotes(string url)
    {
        Assert.Null(GitHelpers.ParseRemote(url));
    }
}
