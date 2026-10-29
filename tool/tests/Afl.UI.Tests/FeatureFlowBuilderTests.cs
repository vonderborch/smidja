using System.Text.Json.Nodes;
using Afl.Core.Constants;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using Afl.UI.DataContexts;
using Avalonia.Headless.XUnit;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for the parts of the app behind the <c>feature-flow</c> family: the skills library in the builder, skill
///     pickers and the 3.4 step fields, work without a ticket, single-threaded runs and the scratch directory setting.
/// </summary>
public class FeatureFlowBuilderTests : AbstractUiTest
{
    [AvaloniaFact]
    public void SkillEditor_CreatesSavesAndReopensASkillWithFiles()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new SkillLibraryEditor());

        Assert.True(dc.HasContent);
        Assert.False(dc.HasParameters);
        Assert.False(dc.ShowsStepArea);
        Assert.Contains(dc.Listings, l => l.Id == "caveman" && l.Scope == WorkflowScope.BuiltIn);

        dc.New("terse");
        Assert.Empty(dc.Steps);
        dc.DraftName = "Terse";
        dc.DraftDescription = "Keep replies short. Use for every step.";
        dc.DraftContent = "Answer in as few words as you can.";
        dc.AddFile();
        dc.Files[0].Path = "examples.md";
        dc.Files[0].Text = "Q: status? A: done.";
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        var file = dc.Save();

        Assert.Equal(Path.Combine(AflPaths.RepoSkillsDirectory(this.RepoPath), "terse.json"), file);
        var json = JsonNode.Parse(File.ReadAllText(file!))!;
        Assert.Equal("Answer in as few words as you can.", (string?)json["content"]);
        Assert.Equal("Q: status? A: done.", (string?)json["files"]!["examples.md"]);
        Assert.Null(json["steps"]);

        // Reopening restores it; workflows can then pick it.
        dc.SelectedListing = dc.Listings.Single(l => l.Id == "caveman");
        dc.SelectedListing = dc.Listings.Single(l => l.Id == "terse");
        Assert.Equal("Answer in as few words as you can.", dc.DraftContent);
        Assert.Equal("examples.md", Assert.Single(dc.Files).Path);
        Assert.False(dc.IsDirty);
        using var workflows = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        workflows.New("short");
        Assert.Contains("terse", workflows.Skills.Available);
        Assert.Contains("terse", workflows.SelectedStep!.AgentPart().Skills.Available);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_RoundTripsSkillsAndTheNewStepFields()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("skilled");
        dc.Skills.ToAdd = "caveman";
        Assert.True(dc.Skills.Add());
        Assert.DoesNotContain("caveman", dc.Skills.Available);
        Assert.True(dc.IsDirty);
        var step = dc.SelectedStep!;
        step.AgentPart().Prompt = "Do it.";
        Assert.True(step.AgentPart().Skills.Add("caveman"));
        Assert.False(step.AgentPart().Skills.Add("caveman"));
        Assert.True(step.AgentPart().Skills.Add("{{params.style}}"));
        Assert.True(step.AgentPart().Skills.Remove("{{params.style}}"));
        step.OutputTemplate = "# Done\n\n{{output}}";
        dc.AddStep();
        var fan = dc.SelectedStep!;
        fan.StepType = StepConstants.TypeWorkflow;
        fan.WorkflowPart().ChildWorkflow = "feature";
        fan.WorkflowPart().ForEachKind = StepConstants.ChildSourceItems;
        Assert.True(fan.WorkflowPart().UsesForEachItems);
        Assert.False(fan.ShowsMergeBack);
        fan.WorkflowPart().ForEachItems = "a, b";
        fan.Workspace = StepConstants.WorkspaceWorktree;
        Assert.True(fan.ShowsMergeBack);
        fan.MergeBack = false;
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        var json = JsonNode.Parse(File.ReadAllText(dc.Save()!))!;
        Assert.Equal("caveman", (string?)json["skills"]![0]);
        var steps = json["steps"]!.AsArray();
        Assert.Equal("caveman", (string?)steps[0]!["skills"]![0]);
        Assert.Equal("# Done\n\n{{output}}", (string?)steps[0]!["outputTemplate"]);
        Assert.Equal("a, b", (string?)steps[1]!["forEach"]!["items"]);
        Assert.Equal(StepConstants.WorkspaceWorktree, (string?)steps[1]!["workspace"]);
        Assert.False((bool?)steps[1]!["mergeBack"]);
        Assert.Null(steps[1]!["skills"]);

        dc.SelectedListing = dc.Listings.Single(l => l.Id == "skilled");
        Assert.Equal(["caveman"], dc.Skills.Picked);
        Assert.Equal(["caveman"], dc.Steps[0].AgentPart().Skills.Picked);
        Assert.DoesNotContain("caveman", dc.Steps[0].AgentPart().Skills.Available);
        Assert.Equal(StepConstants.WorkspaceWorktree, dc.Steps[1].Workspace);
        Assert.False(dc.Steps[1].MergeBack);
        Assert.False(dc.IsDirty);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_SavesABuiltInFlowWithoutLosingAnything()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        var library = shell.Workflows(shell.SelectedRepo);

        foreach (var id in new[] { "feature-flow", "debug-flow", "research", "architecture", "ticket-spike", "ticket-prototype", "ticket-wave", "ticket-implement", "reviewer", "hotfix", "epic" })
        {
            dc.SelectedListing = dc.Listings.Single(l => l.Id == id && l.Scope == WorkflowScope.BuiltIn);
            Assert.True(dc.IsValid, $"{id}: {string.Join("; ", dc.Errors)}");
            var original = WorkflowComposer.ToNode(library.GetRequired(id))!;
            var saved = JsonNode.Parse(File.ReadAllText(dc.Save()!))!;

            foreach (var part in new[] { "skills", "parameters", "steps" })
            {
                Assert.True(JsonNode.DeepEquals(original[part], saved[part]), $"{id}.{part}:\n{original[part]}\n---\n{saved[part]}");
            }
        }
    }

    [AvaloniaFact]
    public async Task IssuesPage_StartsNamedWorkSingleThreaded()
    {
        using var shell = this.CreateShell();
        using var issues = new IssuesPageDataContext(shell);

        Assert.True(await issues.NewWorkAsync("Dark mode"));
        var ticket = issues.SelectedTicket!;
        Assert.Equal(TicketRef.LocalSource, ticket.Source);
        Assert.Equal("dark-mode", ticket.Key);
        Assert.Equal("Dark mode", ticket.Title);

        // Only a workflow that starts child runs offers a choice of threading.
        var flow = shell.Workflows(shell.SelectedRepo).GetRequired("feature-flow");
        Assert.Equal([IssuesPageDataContext.ParallelThreading, IssuesPageDataContext.SingleThreading],
            issues.ThreadingChoices(flow, new Dictionary<string, string>()).Select(c => c.Value));
        var plain = new WorkflowDefinition
        {
            Id = "plain", Name = "Plain", Steps = [new AiStepDefinition { Id = "work", Name = "Work", Prompt = "Work on {{ticket.title}}." }],
        }.Chain();
        Assert.Empty(issues.ThreadingChoices(plain, new Dictionary<string, string>()));

        var run = issues.Start(plain, maxParallel: 1);
        Assert.NotNull(run);
        Assert.True(run.Sequential);
        Assert.Equal("local:dark-mode", $"{run.Ticket.Source}:{run.Ticket.Key}");
        await shell.Driver.Find(run.Id)!.Completion;
        Assert.Equal(RunStatus.Done, new RunStore(shell.Paths).GetRequired(run.Id).Status);
    }

    [AvaloniaFact]
    public void Settings_SavesTheScratchDirectory()
    {
        using var shell = this.CreateShell();
        using var settings = new SettingsPageDataContext(shell);
        Assert.Equal(".scratch", settings.ScratchDirectory);

        settings.ScratchDirectory = "work\\afl/";
        Assert.True(settings.SaveExecution());
        Assert.Equal("work/afl", shell.Settings.Load().ScratchDirectory);

        settings.ScratchDirectory = "../outside";
        Assert.False(settings.SaveExecution());
        Assert.Contains("relative folder inside the repo", settings.ErrorMessage);
        Assert.Equal("work/afl", shell.Settings.Load().ScratchDirectory);
    }
}
