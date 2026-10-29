using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for saving a step's output to a file in the repo (<c>artifact</c>) and for skipping a step before it
///     starts (<c>skipWhen</c>).
/// </summary>
public class ArtifactAndSkipTests : AbstractAgentPoolTest
{
    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    [Fact]
    public async Task Artifact_IsSavedInTheRepoAndLaterStepsReadItsPath()
    {
        var design = Ai("design", "Design it");
        design.Artifact = "docs/afl/{{ticket.slug}}/DESIGN.md";
        var plan = Ai("plan", "Plan from {{steps.design.artifact}}");

        var run = await this.Engine.DriveAsync(this.Start("main", design, plan).Id);

        Assert.Equal("docs/afl/acme-app-7/DESIGN.md", run.Steps[0].ArtifactPath);
        Assert.Equal("main did: Design it" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(this.RepoPath, "docs", "afl", "acme-app-7", "DESIGN.md")));
        Assert.Equal("Plan from docs/afl/acme-app-7/DESIGN.md", this.Fake("main").Requests[1].Prompt);
        Assert.Null(run.Steps[1].ArtifactPath);
    }

    [Fact]
    public async Task Artifact_WhoseRenderedPathLeavesTheRepo_IsNotSaved()
    {
        var design = Ai("design", "Design it");
        design.Artifact = "docs/{{ticket.body}}";
        var ticket = new Ticket { Source = TicketRef.GitHubSource, Key = "acme/app#8", Title = "T", Body = "../../outside.md" };

        var run = await this.Engine.DriveAsync(this.Engine.Create(this.RegisterRepo(), Workflow(design), ticket, "main").Id);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Null(run.Steps[0].ArtifactPath);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("must stay inside the repo"));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(this.RepoPath, "..", "outside.md"))));
    }

    [Fact]
    public async Task Directories_AreRenderedOnePerLineAndGivenToTheAgent_SkippingMissingOnes()
    {
        var other = Directory.CreateDirectory(Path.Combine(this.RepoPath, "..", "other-project")).FullName;
        var look = Ai("look", "Look around");
        look.Directories = ["{{ticket.body}}", "~"];
        var ticket = new Ticket { Source = TicketRef.GitHubSource, Key = "acme/app#9", Title = "T", Body = "../other-project\n\n /no/such/folder \n" + other };

        var run = await this.Engine.DriveAsync(this.Engine.Create(this.RegisterRepo(), Workflow(look), ticket, "main").Id);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal([Path.TrimEndingDirectorySeparator(other), Path.GetFullPath(home)],
            this.Fake("main").Requests[0].AdditionalDirectories.Select(Path.TrimEndingDirectorySeparator));
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("'/no/such/folder'"));
    }

    [Theory]
    [InlineData("/etc/x.md", "must be relative")]
    [InlineData("docs/../../x.md", "must stay inside the repo")]
    [InlineData(".git/hooks/x", "must stay inside the repo")]
    [InlineData("docs/", "must name a file")]
    [InlineData("docs/{{steps.later.output}}.md", "is not available here")]
    public void Validate_ChecksArtifactPaths(string artifact, string problem)
    {
        var step = Ai("design", "x");
        step.Artifact = artifact;

        Assert.Contains(WorkflowLibrary.Validate(Workflow(step, Ai("later", "y"))), p => p.Contains(problem));
    }

    [Theory]
    [InlineData("ACME/app#7", "acme-app-7")]
    [InlineData("WEB-12", "web-12")]
    [InlineData("  --Weird__Key!! ", "weird-key")]
    [InlineData("###", "ticket")]
    public void Slug_IsLowercaseLettersDigitsAndSingleDashes(string key, string slug)
    {
        Assert.Equal(slug, TemplateVariables.Slug(key));
    }

    /// <summary>
    ///     A triage step, then a security step that runs only when triage finds something security-related: the arrow
    ///     into it has <c>when: not X</c> and a bypass arrow to ship has <c>when: X</c>.
    /// </summary>
    private static AbstractStepDefinition[] TriageThenSecurity()
    {
        var noImpact = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Step = "triage", Contains = "no security impact" };
        var triage = Ai("triage", "Triage");
        triage.Arrows =
        [
            new ArrowDefinition { To = "security", When = new ConditionDefinition { Kind = StepConstants.ConditionNot, Conditions = [noImpact] } },
            new ArrowDefinition { To = "ship", When = noImpact },
        ];
        return [triage, Ai("security", "Check security"), Ai("ship", "Ship")];
    }

    [Theory]
    [InlineData("Small UI change; no security impact.", true)]
    [InlineData("Touches the login flow.", false)]
    public async Task ConditionalArrows_GoRoundAStep_AndTheStepAfterStillStarts(string triage, bool bypassed)
    {
        this.Fake("main").Respond = r =>
            new ExecutionResult(ExecutionStatus.Completed, r.Prompt == "Triage" ? triage : $"main did: {r.Prompt}", r.SessionId);

        var run = await this.Engine.DriveAsync(this.Start("main", TriageThenSecurity()).Id);

        var security = run.Steps[1];
        Assert.Equal(bypassed ? StepStatus.Pending : StepStatus.Done, security.Status);
        Assert.Equal(bypassed, run.NotReached(security));
        Assert.Equal(bypassed, this.Fake("main").Requests.All(r => r.Prompt != "Check security"));
        Assert.Equal(StepStatus.Done, run.Steps[2].Status);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public void Validate_AnArrowsConditionMustLookAtThisStepOrAnEarlierOne()
    {
        var triage = Ai("triage", "x");
        triage.Arrows = [new ArrowDefinition { To = "ship", When = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Step = "ship", Contains = "x" } }];

        Assert.Contains(WorkflowLibrary.Validate(Workflow(triage, Ai("ship", "y"))),
            p => p.Contains("condition step 'ship' must be this step or one that leads to it"));
        Assert.Empty(WorkflowLibrary.Validate(Workflow(TriageThenSecurity())));
    }
}
