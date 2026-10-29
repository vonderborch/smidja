using Afl.Core.Constants;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for library tags and <see cref="LibraryQuery" />: normalizing and checking tags, and finding workflows,
///     common steps and skills by tag, scope, name or contents.
/// </summary>
public class LibraryQueryTests : AbstractAflTest
{
    private WorkflowLibrary Library => new(this.Paths, this.RepoPath);

    [Fact]
    public void NormalizeTags_TrimsLowercasesHyphenatesAndSorts()
    {
        Assert.Equal(["big-change", "review"], AbstractLibraryDocument.NormalizeTags([" Review ", "big  change", "review", " "]));
    }

    [Fact]
    public void ValidateTags_ReportsBadAndRepeatedTags()
    {
        var step = Step("tagged", "Tagged", "Do it.");
        step.Tags = ["Bad!", "ok", "ok", new string('a', AbstractLibraryDocument.MaxTagLength + 1)];

        var problems = this.Library.Steps.ValidateForSave(step);

        Assert.Contains(problems, p => p.Contains("tag 'Bad!' must be lowercase", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("tag 'ok' is listed more than once", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("at most 40 characters", StringComparison.Ordinal));
        step.Tags = [.. Enumerable.Range(0, AbstractLibraryDocument.MaxTags + 1).Select(i => $"t{i}")];
        Assert.Contains(this.Library.Steps.ValidateForSave(step), p => p.Contains("the most is 20", StringComparison.Ordinal));
    }

    [Fact]
    public void Workflow_WithABadTag_FailsValidation()
    {
        var workflow = this.Library.GetRequired("bugfix");
        workflow.Tags = ["Not A Tag"];

        Assert.Contains(new WorkflowComposer(this.Library).Validate(workflow), p => p.StartsWith("Workflow tag 'Not A Tag'", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltIns_AreTagged()
    {
        Assert.Contains(("review", 7), this.Library.Steps.AllTags());
        Assert.Contains(this.Library.AllTags(), t => t.Tag == "planning");
        Assert.Equal(["style", "tokens"], this.Library.Skills.GetRequired("caveman").Tags);
    }

    [Fact]
    public void Search_ByTags_NeedsEveryTag()
    {
        var review = this.Library.Steps.Search(new LibraryQuery { Tags = ["review"] }).Select(h => h.Listing.Step.Id);
        var security = this.Library.Steps.Search(new LibraryQuery { Tags = ["review", "security"] }).Select(h => h.Listing.Step.Id);

        Assert.Contains("code-review", review);
        Assert.Equal(["review-security"], security);
    }

    [Fact]
    public void Search_FindsByNameFirstThenByContents()
    {
        var library = this.Library;
        library.Steps.Save(Step("alpha", "Alpha", "Check the zebra crossing.\nThen stop."), WorkflowScope.Global);
        library.Steps.Save(Step("zebra", "Zebra crossing", "Look both ways."), WorkflowScope.Global);

        var hits = library.Steps.Search(new LibraryQuery { Text = "ZEBRA crossing" });

        Assert.Equal(["zebra", "alpha"], hits.Select(h => h.Listing.Step.Id));
        Assert.True(hits[0].Match.InName);
        Assert.Null(hits[0].Match.Snippet);
        Assert.False(hits[1].Match.InName);
        Assert.Equal("Check the zebra crossing.", hits[1].Match.Snippet);

        var namesOnly = library.Steps.Search(new LibraryQuery { Text = "zebra", SearchContents = false });
        Assert.Equal(["zebra"], namesOnly.Select(h => h.Listing.Step.Id));
    }

    [Fact]
    public void Search_ByScope_AndInSkillContent()
    {
        var library = this.Library;
        library.Skills.Save(new SkillDefinition
        {
            Id = "tidy", Name = "Tidy", Description = "Keeps things neat.", Content = "# Tidy\n\nAlways sort the imports quokkawise.",
            Tags = ["style"],
        }, WorkflowScope.Repo);

        var repo = library.Skills.Search(new LibraryQuery { Scope = WorkflowScope.Repo });
        var found = library.Skills.Search(new LibraryQuery { Text = "quokkawise", Tags = ["style"] });

        Assert.Equal(["tidy"], repo.Select(h => h.Listing.Skill.Id));
        Assert.Equal(["tidy"], found.Select(h => h.Listing.Skill.Id));
        Assert.Equal("Always sort the imports quokkawise.", found[0].Match.Snippet);
        Assert.Empty(library.Skills.Search(new LibraryQuery { Text = "quokkawise", Scope = WorkflowScope.BuiltIn }));
    }

    [Fact]
    public void Snippet_IsShortenedAroundTheTerm()
    {
        var line = new string('x', 120) + " needle " + new string('y', 120);
        var match = new LibraryQuery { Text = "needle" }.Match(Step("long", "Long", line), WorkflowScope.Global)!;

        Assert.Contains("needle", match.Snippet);
        Assert.StartsWith("…", match.Snippet);
        Assert.EndsWith("…", match.Snippet);
        Assert.True(match.Snippet!.Length <= 92);
    }

    private static CommonStepDefinition Step(string id, string name, string prompt)
    {
        return new CommonStepDefinition
        {
            Id = id, Name = name, Step = new AiStepDefinition { Id = id, Name = name, Prompt = prompt },
        };
    }
}
