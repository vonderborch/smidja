using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Workflows;
using Afl.UI.DataContexts;
using Avalonia.Headless.XUnit;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for the builder's composition editors (common steps, includes, child-run steps, parameters and requires),
///     and for child runs on the Runs page.
/// </summary>
public class CompositionBuilderTests : AbstractUiTest
{
    [AvaloniaFact]
    public void WorkflowBuilder_RoundTripsUseIncludeParametersAndRequires()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("composed");

        // A step based on a common step takes its type, shows its values as watermarks and fills its parameters.
        var review = dc.SelectedStep!;
        review.Id = "review";
        review.Basis = StepDraftDataContext.UseChoice;
        review.Use = "review-standards";
        Assert.Contains("review-standards", review.CommonStepChoices);
        Assert.Equal(StepConstants.TypeAi, review.StepType);
        Assert.Equal("Standards review", review.NamePlaceholder);
        Assert.False(string.IsNullOrEmpty(review.AgentPart().PromptPlaceholder));
        Assert.Equal(["scope", "context", "extra"], review.With.Values.Select(v => v.Id));
        review.With.Values.Single(v => v.Id == "extra").Value = "{{params.area}}";

        // The draft's own parameter, used above.
        dc.AddParameter();
        dc.Parameters[0].Id = "area";
        dc.Parameters[0].Default = "the API";

        // A step that includes another workflow's steps keeps only its id, name and values.
        dc.AddStep();
        var include = dc.SelectedStep!;
        include.Id = "fix";
        include.Basis = StepDraftDataContext.IncludeChoice;
        include.Include = "bugfix";
        Assert.False(include.ShowsStepFields);
        Assert.Contains("includes bugfix", include.Title);
        var includeDefinition = Assert.IsAssignableFrom<AbstractAgentStepDefinition>(include.ToDefinition());
        Assert.Equal("bugfix", includeDefinition.Include);
        Assert.Null(includeDefinition.Prompt);

        dc.AddRequirement();
        dc.Requires[0].Workflow = "feature";

        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        var file = dc.Save();
        Assert.NotNull(file);
        var json = JsonNode.Parse(File.ReadAllText(file!))!;
        var steps = json["steps"]!.AsArray();
        Assert.Equal("review-standards", (string?)steps[0]!["use"]);
        Assert.Equal("{{params.area}}", (string?)steps[0]!["with"]!["extra"]);
        Assert.Null(steps[0]!["prompt"]);
        Assert.True(string.IsNullOrEmpty((string?)steps[0]!["name"]));
        Assert.Null(steps[0]!["permissionMode"]);
        Assert.Equal("bugfix", (string?)steps[1]!["include"]);
        Assert.Equal("area", (string?)json["parameters"]![0]!["id"]);
        Assert.Equal("the API", (string?)json["parameters"]![0]!["default"]);
        Assert.Equal("feature", (string?)json["requires"]![0]!["workflow"]);

        // Opening it again restores every part.
        dc.SelectedListing = dc.Listings.Single(l => l.Id == "bugfix" && l.Scope == WorkflowScope.BuiltIn);
        dc.SelectedListing = dc.Listings.Single(l => l.Id == "composed");
        Assert.True(dc.Steps[0].IsUse);
        Assert.Equal("review-standards", dc.Steps[0].Use);
        Assert.Equal("{{params.area}}", dc.Steps[0].With.Values.Single(v => v.Id == "extra").Value);
        Assert.True(dc.Steps[1].IsInclude);
        Assert.Equal("bugfix", dc.Steps[1].Include);
        Assert.Equal("area", Assert.Single(dc.Parameters).Id);
        Assert.Equal("feature", Assert.Single(dc.Requires).Workflow);
        Assert.False(dc.IsDirty);

        // The composed check runs live: an unknown parameter value is reported.
        dc.Steps[0].With.Values.Single(v => v.Id == "extra").Value = "{{params.nope}}";
        Assert.False(dc.IsValid);
        Assert.Contains(dc.Errors, e => e.Contains("nope"));
    }

    [AvaloniaFact]
    public void WorkflowBuilder_SetsUpAStepThatStartsChildRuns()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("fan-out");
        dc.AddStep();
        var step = dc.SelectedStep!;
        step.Id = "each";
        step.StepType = StepConstants.TypeWorkflow;
        step.WorkflowPart();
        step.WorkflowPart().ChildWorkflow = "feature";
        step.WorkflowPart().ForEachKind = StepConstants.ChildSourceLines;
        Assert.True(step.WorkflowPart().UsesForEachStep);
        Assert.False(step.WorkflowPart().UsesForEachTickets);
        Assert.Contains("step-1", step.WorkflowPart().ForEachStepChoices);
        step.WorkflowPart().ForEachStep = "step-1";
        step.WorkflowPart().Children = StepConstants.ChildrenStart;
        step.WorkflowPart().ChildrenTogether = true;

        var definition = Assert.IsType<WorkflowStepDefinition>(step.ToDefinition());
        Assert.Equal("feature", definition.Workflow);
        Assert.Equal(StepConstants.ChildSourceLines, definition.ForEach!.Kind);
        Assert.Equal("step-1", definition.ForEach.Step);
        Assert.Equal(StepConstants.ChildrenStart, definition.Children);
        Assert.True(definition.ChildrenTogether);
        Assert.Null(definition.Reuse);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        // The default child source is the run's own ticket, written as no forEach at all.
        step.WorkflowPart().ForEachKind = StepConstants.ChildSourceTicket;
        Assert.Null(Assert.IsType<WorkflowStepDefinition>(step.ToDefinition()).ForEach);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_OpensWhatAStepRefersTo_AndCountsOverrides()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("refers");
        var step = dc.SelectedStep!;
        Assert.False(step.ShowsViewReferenced);

        // A step based on a common step opens it, and only what it adds counts as overriding it.
        step.Basis = StepDraftDataContext.UseChoice;
        step.Use = "retro";
        Assert.True(step.ShowsViewReferenced);
        Assert.True(step.RefersToCommonStep);
        Assert.Equal("retro", step.ReferencedId);
        Assert.Equal("View common step", step.ViewReferencedLabel);
        Assert.True(step.ShowsComposedWith);
        Assert.False(step.ShowsOwnWith);
        step.Timeout = "10m";
        Assert.Equal("Override the common step", step.OverridesHeader);
        step.Artifact = "docs/RETRO.md";
        Assert.Equal("Override the common step (1 set)", step.OverridesHeader);

        // An include or a child workflow opens the workflow, once one exists by that id.
        step.Basis = StepDraftDataContext.IncludeChoice;
        step.Include = "feature";
        Assert.Equal("feature", step.ReferencedId);
        Assert.Equal("View workflow", step.ViewReferencedLabel);
        step.Basis = StepDraftDataContext.OwnStepChoice;
        step.StepType = StepConstants.TypeWorkflow;
        step.WorkflowPart().ChildWorkflow = "hotfix";
        Assert.True(step.CanViewReferenced);
        Assert.Equal("hotfix", step.ReferencedId);
        Assert.True(step.ShowsOwnWith);
        Assert.False(step.ShowsComposedWith);

        // Opening one by id picks it even when the search hides it.
        dc.SearchText = "nothing matches this";
        Assert.True(dc.Open("hotfix"));
        Assert.Equal("hotfix", dc.DraftId);
        Assert.False(dc.Open("no-such-workflow"));
        using var common = new LibraryPageDataContext(shell, new CommonStepLibraryEditor());
        Assert.True(common.Open("retro"));
        Assert.Equal("retro", common.DraftId);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_LeavesOutWorkflowsThatWouldMakeACycle()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("inner");
        dc.SelectedStep!.AgentPart().Prompt = "Do it.";
        Assert.NotNull(dc.Save());
        dc.New("outer");
        var start = dc.SelectedStep!;
        start.StepType = StepConstants.TypeWorkflow;
        Assert.DoesNotContain("outer", start.WorkflowChoices);
        start.WorkflowPart().ChildWorkflow = "inner";
        Assert.NotNull(dc.Save());

        // Reopened, inner can't start or include itself, nor outer, which starts it.
        dc.SelectedListing = dc.Listings.Single(l => l.Id == "inner");
        var step = dc.SelectedStep!;
        step.StepType = StepConstants.TypeWorkflow;
        Assert.DoesNotContain("inner", step.WorkflowChoices);
        Assert.DoesNotContain("outer", step.WorkflowChoices);
        Assert.Contains("feature", step.WorkflowChoices);
        dc.AddRequirement();
        Assert.DoesNotContain("outer", dc.Requires[0].WorkflowChoices);
        Assert.DoesNotContain("inner", dc.Requires[0].WorkflowChoices);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_EditsCommonStepsAndSavesStepsAsCommonOnes()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new CommonStepLibraryEditor());

        Assert.False(dc.HasSteps);
        Assert.False(dc.HasRequirements);
        Assert.Contains(dc.Listings, l => l.Id == "code-review" && l.Scope == WorkflowScope.BuiltIn);

        dc.New("lint");
        Assert.Equal("lint", Assert.Single(dc.Steps).Id);
        dc.Steps[0].AgentPart().Prompt = "Lint {{params.path}}";
        Assert.False(dc.IsValid);
        dc.AddParameter();
        dc.Parameters[0].Id = "path";
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        var file = dc.Save();
        Assert.Equal(Path.Combine(AflPaths.RepoStepsDirectory(this.RepoPath), "lint.json"), file);
        var json = JsonNode.Parse(File.ReadAllText(file!))!;
        Assert.Equal("Lint {{params.path}}", (string?)json["step"]!["prompt"]);

        // Workflows can now use it.
        using var workflows = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        workflows.New("uses-lint");
        var step = workflows.SelectedStep!;
        step.Basis = StepDraftDataContext.UseChoice;
        Assert.Contains("lint", step.CommonStepChoices);

        // Saving a step as a common one bases the step on it, and copying it back writes it out again.
        step.Basis = StepDraftDataContext.OwnStepChoice;
        step.Use = null;
        step.StepType = StepConstants.TypeAi;
        Assert.True(string.IsNullOrEmpty(step.AgentPart().Prompt));
        step.Name = "Start";
        step.AgentPart().Prompt = "Start on {{ticket.key}}.";
        Assert.True(step.CanSaveAsCommon);
        var prompt = step.AgentPart().Prompt;
        Assert.True(workflows.SaveStepAsCommon("starter") is not null, workflows.ErrorMessage);
        Assert.True(step.IsUse);
        Assert.Equal("starter", step.Use);
        Assert.True(File.Exists(Path.Combine(AflPaths.RepoStepsDirectory(this.RepoPath), "starter.json")));

        Assert.True(workflows.CopyStepIntoWorkflow());
        var copied = Assert.Single(workflows.Steps);
        Assert.True(copied.IsOwnStep);
        Assert.Equal(prompt, copied.AgentPart().Prompt);
        Assert.True(workflows.IsValid, string.Join("; ", workflows.Errors));
    }

    [AvaloniaFact]
    public void CopyStepIntoWorkflow_KeepsAnIncludeWiredIn()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("wired");
        var include = dc.Steps[0];
        include.Basis = StepDraftDataContext.IncludeChoice;
        include.Include = "bugfix";
        dc.AddStep();
        dc.SelectedStep!.Id = "after";
        dc.SelectedStep = null;
        dc.AddStep();
        dc.SelectedStep!.Id = "before";
        foreach (var arrow in dc.SelectedStep.Arrows.ToList())
        {
            dc.SelectedStep.RemoveArrow(arrow);
        }

        dc.SelectedStep.AddArrow(include.Id.Trim());
        dc.MakeStart(dc.SelectedStep);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        // The include's steps take its place: arrows into it reach its start, and its ends go on to the step after.
        dc.SelectedStep = include;
        Assert.True(dc.CopyStepIntoWorkflow(), dc.ErrorMessage);
        var before = dc.Steps.Single(s => s.Id == "before");
        var start = Assert.Single(before.Targets);
        Assert.StartsWith("step-1-", start);
        Assert.Contains(dc.Steps, s => s.Targets.Contains("after"));
        Assert.Equal("before", dc.StartStepId);
        Assert.Empty(dc.Warnings);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
    }

    [AvaloniaFact]
    public async Task RunsPage_ListsChildRuns_AndLinksBackToTheParent()
    {
        using var shell = this.CreateShell();
        var repo = shell.SelectedRepo!;
        var library = shell.Workflows(repo);
        library.Save(new WorkflowDefinition
        {
            Id = "child", Name = "Child",
            Steps = [new AiStepDefinition { Id = "work", Name = "Work", Prompt = "Work", Advance = StepConstants.AdvanceApprove }],
        }.Chain(), WorkflowScope.Repo);
        var parent = new WorkflowDefinition
        {
            Id = "parent", Name = "Parent",
            Steps =
            [
                new WorkflowStepDefinition { Id = "build", Name = "Build", Workflow = "child" },
                new AiStepDefinition { Id = "after", Name = "After", Prompt = "After" },
            ],
        }.Chain();
        var run = shell.Driver.Engine.Create(repo, parent, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.Equal(StepStatus.AwaitingChildren, dc.CurrentStep?.Status);
        Assert.Contains("child runs", dc.Hint);
        Assert.True(dc.CanResume);
        Assert.Null(dc.ParentText);
        var childId = Assert.Single(dc.CurrentStep!.ChildRuns).RunId;

        // The child follows its parent in the list, folded under it until opened.
        Assert.Equal([run.Id, childId], dc.Runs.Select(r => r.Id));
        Assert.Equal([run.Id], dc.RunRows.Select(r => r.Run.Id));
        Assert.Equal("1 child run · 1 needs you", dc.RunRows[0].Summary);
        Assert.False(dc.IsPopupOpen);

        Assert.True(dc.OpenRun(childId));
        Assert.Equal([run.Id, childId], dc.RunRows.Select(r => r.Run.Id));
        Assert.Equal(1, dc.RunRows[1].Level);
        Assert.True(dc.IsPopupOpen);
        Assert.Equal("Waiting for you", dc.PopupHeading);
        Assert.Equal($"Part of run {run.Id} (Parent), step build", dc.ParentText);
        Assert.True(dc.CanApprove);
        dc.Approve();
        await shell.Driver.Find(childId)!.Completion;

        // Approving the child wakes the parent, which finishes by itself.
        Assert.True(dc.OpenParent());
        Assert.Equal(run.Id, dc.SelectedRun?.Id);
        Assert.Equal(RunStatus.Done, dc.SelectedRun?.Status);
    }

    [AvaloniaFact]
    public async Task IssuesPage_StartsAWorkflowWithParameterValues()
    {
        using var shell = this.CreateShell();
        var workflow = new WorkflowDefinition
        {
            Id = "scoped", Name = "Scoped",
            Parameters = [new ParameterDefinition { Id = "area" }],
            Steps = [new AiStepDefinition { Id = "work", Name = "Work", Prompt = "Work on {{params.area}}." }],
        }.Chain();
        using var issues = new IssuesPageDataContext(shell);
        issues.SelectedTicket = SampleTicket();

        // A required parameter with no value stops the run before it's created.
        Assert.Null(issues.Start(workflow));
        Assert.Contains("area", issues.ErrorMessage);

        var run = issues.Start(workflow, parameters: new Dictionary<string, string> { ["area"] = "billing" });
        Assert.NotNull(run);
        Assert.Equal("Work on billing.", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(run.Workflow.Steps[0]).Prompt);
        await shell.Driver.Find(run.Id)!.Completion;
    }
}
