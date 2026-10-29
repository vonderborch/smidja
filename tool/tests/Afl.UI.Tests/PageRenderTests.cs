using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.GitHosts;
using Afl.Core.Jira;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using Afl.UI.Pages;
using Afl.UI.DataContexts;
using Afl.UI.Flow;
using Afl.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Niddy.Avalonia.Hosting;
using Niddy.Avalonia.PageSystem;
using Page = Niddy.Avalonia.PageSystem.Page;

namespace Afl.UI.Tests;

/// <summary>
///     Renders every afl page, inside the app's layout, over sample data, checking each loads without an error. Set
///     <c>AFL_UI_SCREENSHOTS</c> to a folder to also save a PNG of each page there.
/// </summary>
public class PageRenderTests : AbstractUiTest
{
    /// <summary>
    ///     The environment variable naming the folder screenshots are saved to.
    /// </summary>
    private const string ScreenshotVariable = "AFL_UI_SCREENSHOTS";

    [AvaloniaFact]
    public async Task EveryPage_RendersOverSampleData()
    {
        using var shell = this.CreateShell();
        var repo = shell.SelectedRepo!;
        var runId = await this.SeedAsync(shell, repo);

        // The app's own shell composition (layout, toasts, dialogs), over this test's shell rather than the app's.
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<RunsPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();

        // The menu lists pages by their SortKey, the workflow's natural order. The two settings pages are opened from
        // the sidebar's buttons instead, but render here too.
        var menu = PageRegistry.GetPages().Where(p => p.PageType.Assembly == typeof(App).Assembly);
        Assert.Equal(["Issues", "Runs", "Workflows", "Common steps", "Skills"], menu.Select(p => p.DisplayName));
        var registrations = PageRegistry.GetAllPages().Where(p => p.PageType.Assembly == typeof(App).Assembly).ToList();
        Assert.Equal(7, registrations.Count);
        foreach (var page in registrations)
        {
            // The Runs page is handed the seeded run, as the Issues page does after starting one.
            Assert.True(app.Pages.NavigateTo(page.PageId, addToBackStack: false,
                parameter: page.PageType == typeof(RunsPage) ? runId : null));
            Pump();
            var control = window.GetVisualDescendants().OfType<Page>().Single();
            Assert.Equal(page.PageType, control.GetType());
            var dc = Assert.IsAssignableFrom<AbstractPageDataContext>(control.DataContext);
            if (dc is RunsPageDataContext runs)
            {
                Assert.Equal(runId, runs.SelectedRun?.Id);
            }

            await this.ArrangeAsync(dc);
            Pump();

            Assert.True(dc.ErrorMessage is null, $"{page.DisplayName}: {dc.ErrorMessage}");
            Save(window, page.DisplayName);
            if (dc is SettingsPageDataContext settings)
            {
                // The other Settings tabs: git hosts, agents, MCP servers (the seeded one picked) and execution.
                var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Tabs");
                settings.SelectedServer = settings.ServerList.FirstOrDefault();
                Assert.Equal("docs", settings.ServerId);
                foreach (var (index, name) in new[] { (1, "settings-hosts"), (2, "settings-agents"), (3, "settings-mcp"), (4, "settings-execution"), (5, "settings-permissions") })
                {
                    tabs.SelectedIndex = index;
                    Pump();
                    Assert.True(settings.ErrorMessage is null, $"{name}: {settings.ErrorMessage}");
                    Save(window, name);
                }
            }
        }

        // A step several agents did, waiting for a choice, and the builder's settings for one.
        var compare = shell.Workflows(repo).GetRequired("compare");
        var choiceRun = shell.Driver.Engine.Create(repo, compare, SampleTicket());
        await shell.Driver.Engine.DriveAsync(choiceRun.Id);
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: choiceRun.Id));
        Pump();
        var choice = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.True(choice.CanChoose, choice.ErrorMessage);
        Assert.Equal(2, choice.Candidates.Count);
        Pump();
        Save(window, "runs-choice");

        Assert.True(app.Pages.NavigateTo(PageId.Of<WorkflowsPage>(), addToBackStack: false));
        Pump();
        var builder = Assert.IsType<LibraryPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        builder.SelectedListing = builder.Listings.First(l => l.Id == "compare");
        builder.SelectedStep = builder.Steps[0];
        Assert.True(builder.SelectedStep.AgentPart().ShowsSeveralAgents);
        Pump();
        Assert.True(builder.ErrorMessage is null, builder.ErrorMessage);
        Save(window, "workflows-agents");

        // A run that looped and branched past a step, and the builder's flow settings for the loop.
        var loop = shell.Workflows(repo).GetRequired("loop");
        var loopRun = shell.Driver.Engine.Create(repo, loop, SampleTicket());
        await shell.Driver.Engine.DriveAsync(loopRun.Id);
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: loopRun.Id));
        Pump();
        var looped = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.Equal(RunStatus.Done, looped.SelectedRun?.Status);
        Assert.Equal(StepStatus.Pending, looped.SelectedRun?.Steps[2].Status);
        Assert.StartsWith("Path: fix › fix #2 › fix #3 › check", looped.PathText);
        Pump();
        Save(window, "runs-loop");

        // A run whose fork waits on both its branches, and one of the path runs.
        var forkRun = shell.Driver.Engine.Create(repo, shell.Workflows(repo).GetRequired("fork"), SampleTicket());
        await shell.Driver.Engine.DriveAsync(forkRun.Id);
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: forkRun.Id));
        Pump();
        var forkPage = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.Equal(StepStatus.AwaitingPaths, forkPage.CurrentStep?.Status);
        Assert.Contains("lint is awaiting approval", forkPage.PathStepsText);
        Assert.Contains("test is awaiting approval", forkPage.PathStepsText);
        Assert.Equal(2, forkPage.Runs.Count(r => r.ParentRunId == forkRun.Id && r.IsPathRun));
        var forkRow = forkPage.RunRows.Single(r => r.Run.Id == forkRun.Id);
        Assert.True(forkRow.HasChildren);
        Assert.False(forkRow.IsExpanded);
        Assert.Equal("2 paths · 2 need you", forkRow.Summary);
        Assert.DoesNotContain(forkPage.RunRows, r => r.Run.ParentRunId == forkRun.Id);

        // The run opens on its workflow's graph, with the step waiting in a path open in the popup.
        Assert.Equal(0, forkPage.SelectedTab);
        Assert.True(forkPage.IsPopupOpen);
        Assert.Equal("Waiting for you in a path", forkPage.PopupHeading);
        Assert.NotNull(forkPage.PopupPathRunId);
        Assert.False(forkPage.PopupIsCurrent);
        Pump();
        Save(window, "runs-fork");

        // The graph itself: the plan fired both arrows and both paths are running.
        forkPage.ClosePopup();
        Assert.False(forkPage.IsPopupOpen);
        Assert.True(forkPage.CanOpenStep);
        forkPage.Reload();
        Assert.False(forkPage.IsPopupOpen);
        Assert.IsType<RunOverlay>(forkPage.Graph.Overlay);
        Assert.False(forkPage.Graph.CanEdit);
        FlowNodeMark MarkOf(string id) => forkPage.Graph.StepNodes.Single(n => n.Step!.Id == id).Mark;
        Assert.Equal(FlowNodeMark.Active, MarkOf("lint"));
        Assert.Equal(FlowNodeMark.Active, MarkOf("test"));
        Assert.Equal(FlowNodeMark.NotReached, MarkOf("ship"));
        Assert.All(forkPage.Graph.Paths.Where(p => p.Edge.From.Step?.Id == "plan"), p => Assert.Equal(ArrowState.Fired, forkPage.Graph.Overlay!.StateOf(p.Edge)));
        Assert.All(forkPage.Graph.Paths.Where(p => p.Edge.Arrow?.To == "ship"), p => Assert.Equal(ArrowState.Pending, forkPage.Graph.Overlay!.StateOf(p.Edge)));
        Assert.False(forkPage.Graph.StepNodes.First().CanConnect);
        Assert.Contains("'lint'", forkPage.Graph.OverlaySummary);
        Pump();
        Save(window, "runs-graph");
        string? focused = null;
        forkPage.StepFocused += (_, id) => focused = id;
        forkPage.Graph.Select(forkPage.Graph.StepNodes.Single(n => n.Step!.Id == "test"));
        Assert.True(forkPage.IsPopupOpen);
        Assert.Equal("test", forkPage.PopupStepId);
        Assert.Equal(StepStatus.AwaitingApproval, forkPage.PopupStep?.Status);
        Pump();
        Save(window, "runs-popup");
        forkPage.ShowInTimeline();
        Assert.False(forkPage.IsPopupOpen);
        Assert.Equal(1, forkPage.SelectedTab);
        Assert.Equal("test", focused);
        Pump();
        Save(window, "runs-timeline");

        // Expand opens the plan in Niddy's view dialog, filling a window (as Niddy's desktop shell shows dialogs) sized to
        // the one it opens over rather than to Niddy's fixed markdown height.
        var plan = window.GetVisualDescendants().OfType<MarkdownView>().First(v => v.IsEffectivelyVisible && !string.IsNullOrEmpty(v.Markdown));
        var planText = plan.Markdown;
        plan.FindControl<Button>("ExpandButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var dialogWindow = Assert.Single(window.OwnedWindows);
        var viewer = dialogWindow.GetVisualDescendants().OfType<MarkdownView>().Single();
        Assert.Equal(planText, viewer.Markdown);
        Assert.False(viewer.FindControl<Button>("ExpandButton")!.IsVisible);
        Assert.True(viewer.FindControl<Button>("CopyButton")!.IsVisible);
        Assert.True(viewer.Bounds.Height > 420, $"The dialog's markdown is {viewer.Bounds.Height} tall.");
        Save(dialogWindow, "markdown-dialog");
        dialogWindow.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Close")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 20 && window.OwnedWindows.Count > 0; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.Empty(window.OwnedWindows);
        forkPage.SelectedTab = 0;
        var lint = forkPage.CurrentStep!.Paths!.Single(b => b.Branch == "lint");
        Assert.True(forkPage.OpenRun(lint.RunId));
        Assert.True(forkPage.RunRows.Single(r => r.Run.Id == forkRun.Id).IsExpanded);
        Assert.Contains(forkPage.RunRows, r => r.Run.Id == lint.RunId && r.Level == 1);
        Assert.True(forkPage.PopupIsCurrent);
        Assert.Equal("Waiting for you", forkPage.PopupHeading);
        Assert.True(forkPage.CanApprove);
        Assert.StartsWith("Path lint of run", forkPage.ParentText);
        Pump();
        Save(window, "runs-branch");

        // A step whose agent was refused two commands, asking what to allow.
        var permitRespond = this.Executor.Respond;
        this.Executor.Respond = _ => new Afl.Core.Execution.ExecutionResult(Afl.Core.Execution.ExecutionStatus.Completed, "Draft fix.", "s1", null,
        [
            Afl.Core.Permissions.ToolRuleSuggesterRegistry.Instance.Describe("Bash", null,
                System.Text.Json.JsonSerializer.SerializeToElement(new { command = "dotnet test tool/Afl.slnx" }), repo.Path),
            Afl.Core.Permissions.ToolRuleSuggesterRegistry.Instance.Describe("WebFetch", null,
                System.Text.Json.JsonSerializer.SerializeToElement(new { url = "https://learn.microsoft.com/dotnet" }), repo.Path),
        ]);
        var permitRun = shell.Driver.Engine.Create(repo, new WorkflowDefinition
        {
            Id = "permit", Name = "Fix", Steps = [new AiStepDefinition { Id = "fix", Name = "Fix", Prompt = "Fix it." }],
        }.Chain(), SampleTicket());
        await shell.Driver.Engine.DriveAsync(permitRun.Id);
        this.Executor.Respond = permitRespond;
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: permitRun.Id));
        Pump();
        var permitPage = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.True(permitPage.CanPermit, permitPage.ErrorMessage);
        permitPage.PermissionRequests[0].IsAlways = true;
        permitPage.PermissionRequests[0].Rule = permitPage.PermissionRequests[0].Rules[1];
        permitPage.PermissionRequests[1].IsOnce = true;
        Pump();
        Save(window, "run-permission");

        // Starting in Full auto asks for the policy, then warns with the guard rails.
        Assert.True(app.Pages.NavigateTo(PageId.Of<IssuesPage>(), addToBackStack: false));
        Pump();
        var issuesPage = window.GetVisualDescendants().OfType<IssuesPage>().Single();
        var issuesDc = (IssuesPageDataContext)issuesPage.DataContext!;
        var autoIndex = issuesDc.PermissionChoices().ToList().FindIndex(p => p.Name == Afl.Core.Constants.PermissionConstants.PolicyAuto);
        var askingPolicy = issuesPage.AskPermissionsAsync(issuesDc);
        Pump();
        var policyWindow = Assert.Single(window.OwnedWindows);
        var policyList = policyWindow.GetVisualDescendants().OfType<ListBox>().Single();
        Save(policyWindow, "start-permissions");
        policyList.SelectedIndex = autoIndex;
        Pump();
        policyWindow.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Continue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Window? warning = null;
        for (var i = 0; i < 20 && warning is null; i++)
        {
            Pump();
            await Task.Delay(10);
            warning = window.OwnedWindows.SingleOrDefault(w => w != policyWindow);
        }

        Assert.NotNull(warning);
        Pump();
        Save(warning, "start-auto");
        warning.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 20 && !askingPolicy.IsCompleted; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.Null(await askingPolicy);

        Assert.True(app.Pages.NavigateTo(PageId.Of<WorkflowsPage>(), addToBackStack: false));
        Pump();
        builder = Assert.IsType<LibraryPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        builder.SelectedListing = builder.Listings.First(l => l.Id == "loop");
        Pump();
        Assert.False(builder.ShowsStepPanel);
        builder.Flow.ShowCommonSteps();
        Pump();
        Save(window, "workflows-picker");
        builder.Flow.ClosePicker();
        builder.Flow.Select(builder.Flow.NodeFor(builder.Steps[1].Id)!);
        Assert.True(builder.ShowsStepPanel);
        Assert.Equal(2, builder.SelectedStep!.Arrows.Count);
        Assert.True(builder.SelectedStep.FollowsFirst);
        Pump();
        Assert.IsType<AgentStepDetailsDataContext>(builder.SelectedStep.Details);
        Assert.True(builder.Skills.Add("caveman"));
        Assert.True(builder.Skills.Add("{{params.style}}"));
        Assert.True(builder.SelectedStep.AgentPart().Skills.Add("caveman"));
        Pump();
        Save(window, "workflows-step");
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(x => x.Header as string != "Variables"))
        {
            expander.IsExpanded = true;
        }

        Pump();
        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.GetVisualDescendants().OfType<Expander>().Any()))
        {
            scroller.ScrollToEnd();
        }

        Pump();
        Assert.True(builder.ErrorMessage is null, builder.ErrorMessage);
        Save(window, "workflows-flow");

        // A step based on a common step, one that starts a child run per line, and the common steps list.
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>())
        {
            expander.IsExpanded = false;
        }

        builder.New("composed");
        builder.SelectedStep!.Basis = StepDraftDataContext.UseChoice;
        builder.SelectedStep.Use = "code-review";
        builder.AddStep();
        builder.SelectedStep!.StepType = StepConstants.TypeWorkflow;
        builder.SelectedStep.WorkflowPart().ChildWorkflow = "feature";
        builder.SelectedStep.WorkflowPart().ForEachKind = StepConstants.ChildSourceLines;
        builder.SelectedStep.WorkflowPart().ForEachStep = builder.Steps[0].Id;
        builder.Flow.IsPropertiesOpen = true;
        Assert.True(builder.SelectedStep.CanViewReferenced);
        Pump();
        Assert.True(builder.ErrorMessage is null, builder.ErrorMessage);
        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scroller.ScrollToHome();
        }

        Pump();
        Save(window, "workflows-children");
        builder.SelectedStep = builder.Steps[0];
        builder.Flow.IsPropertiesOpen = true;
        Assert.Equal("View common step", builder.SelectedStep!.ViewReferencedLabel);
        Pump();
        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scroller.ScrollToHome();
        }

        Pump();
        Save(window, "workflows-use");
        // Leaving with unsaved edits asks first; with the draft put back, the Common steps page opens.
        Assert.True(builder.IsDirty);
        Assert.False(app.Pages.NavigateTo(PageId.Of<CommonStepsPage>(), addToBackStack: false));
        Pump();
        builder.SelectedListing = builder.Listings.First(l => l.Id == "loop");
        Assert.False(builder.IsDirty);
        Assert.True(app.Pages.NavigateTo(PageId.Of<CommonStepsPage>(), addToBackStack: false));
        Pump();
        builder = Assert.IsType<LibraryPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        builder.SelectedListing = builder.Listings.First(l => l.Id == "code-review");
        Pump();
        Assert.True(builder.ErrorMessage is null, builder.ErrorMessage);
        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scroller.ScrollToHome();
        }

        Pump();
        Save(window, "workflows-common-step");

        // Filtered by a tag and searched by a word only in a step's prompt, which shows the line it was found on.
        builder.Tags.Add("review");
        builder.SearchText = "deserialization";
        Pump();
        Assert.Equal(["review-security"], builder.Listings.Select(l => l.Id));
        builder.SelectedListing = builder.Listings[0];
        Pump();
        Save(window, "workflows-common-step-search");
        builder.ClearFilters();
        Pump();

        // The skills library, and a built-in flow whose AI steps all get a skill, with a worktree fan-out.
        Assert.True(app.Pages.NavigateTo(PageId.Of<SkillsPage>(), addToBackStack: false));
        Pump();
        builder = Assert.IsType<LibraryPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        builder.SelectedListing = builder.Listings.First(l => l.Id == "caveman");
        Pump();
        Assert.True(builder.ErrorMessage is null, builder.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(builder.DraftContent));
        Save(window, "workflows-skill");

        // The skills filtered by their most common tag: it shows as a picked chip, and the counts left are faceted.
        var common = builder.Tags.Suggestions[0];
        builder.Tags.Add(common.Tag);
        Pump();
        Assert.Equal(common.Count, builder.Listings.Count);
        Assert.All(builder.Tags.Choices, c => Assert.True(c.Count <= common.Count));
        Save(window, "skills-tags");
        builder.ClearFilters();
        Pump();
        Assert.True(app.Pages.NavigateTo(PageId.Of<WorkflowsPage>(), addToBackStack: false));
        Pump();
        builder = Assert.IsType<LibraryPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        builder.SelectedListing = builder.Listings.First(l => l.Id == "feature-flow" && l.Scope == WorkflowScope.BuiltIn);
        builder.SelectedStep = builder.Steps.First(st => st.Id == "prototypes");
        Pump();
        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scroller.ScrollToHome();
        }

        Pump();
        Assert.True(builder.ErrorMessage is null, builder.ErrorMessage);
        Assert.True(builder.IsValid, string.Join("; ", builder.Errors));
        Save(window, "workflows-feature-flow");

        // A fork into two branches that join again, with the fork's join and workspace settings open.
        builder.New("fork");
        builder.Steps[0].Name = "Plan";
        builder.AddStep();
        builder.SelectedStep!.Name = "Backend";
        builder.SelectedStep = null;
        builder.AddStep();
        builder.SelectedStep!.Name = "Frontend";
        builder.SelectedStep = null;
        builder.AddStep();
        builder.SelectedStep!.Name = "Review";
        var forked = builder.Flow.StepNodes.ToList();
        Assert.True(builder.Flow.Connect(forked[0], forked[2]));
        Assert.True(builder.Flow.Connect(forked[1], forked[3]));
        Assert.True(builder.Flow.Connect(forked[2], forked[3]));
        builder.Steps[0].Workspace = "worktree";
        builder.Flow.Select(forked[0]);
        Pump();
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(x => x.Header as string != "Variables"))
        {
            expander.IsExpanded = true;
        }

        Pump();
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Arrows out").BringIntoView();
        Pump();
        Assert.Contains("⑂ waits for all", forked[3].Badges);
        Assert.True(builder.IsValid, string.Join("; ", builder.Errors));
        Save(window, "workflows-fork");

        // Clicking one of the fork's arrows opens its own editor, with the dot to drag its end by.
        builder.Flow.IsPropertiesOpen = false;
        builder.Flow.OpenArrow(builder.Flow.Paths.Single(p => p.Edge.From == forked[0] && p.Edge.To == forked[2]).Edge);
        Pump();
        Assert.True(builder.Flow.HasArrowHandle);
        Save(window, "workflows-arrow");

        // Its condition, nested two deep: all of (the plan mentions the API, not any of (docs only, a typo)).
        var arrow = Assert.IsType<ArrowDataContext>(builder.Flow.Arrow).Arrow;
        arrow.ConditionMode = ArrowDraftDataContext.WhenCondition;
        arrow.Condition.Kind = StepConstants.ConditionAll;
        arrow.Condition.Children[0].Contains = "API";
        var not = arrow.Condition.AddChild();
        not.Kind = StepConstants.ConditionNot;
        not.Children[0].Kind = StepConstants.ConditionAny;
        not.Children[0].Children[0].Contains = "docs only";
        not.Children[0].AddChild().Matches = "typo|spelling";
        Pump();
        window.GetVisualDescendants().OfType<TextBox>().Last(t => t.Text == "typo|spelling").BringIntoView();
        Pump();
        Assert.True(builder.IsValid, string.Join("; ", builder.Errors));
        Save(window, "workflows-condition");
        arrow.ConditionMode = ArrowDraftDataContext.NoCondition;
        builder.Flow.CloseArrow();

        // A third path into the review, which waits for both code paths or for the docs, letting the others finish.
        builder.SelectedStep = null;
        builder.AddStep();
        builder.SelectedStep!.Name = "Docs";
        var docs = builder.Flow.NodeFor(builder.SelectedStep.Id)!;
        foreach (var path in builder.Flow.Paths.Where(p => p.Edge.From == docs || p.Edge.To == docs).ToList())
        {
            builder.Flow.Disconnect(path.Edge);
        }

        Assert.True(builder.Flow.Connect(builder.Flow.NodeFor(forked[0].Step!.Id)!, builder.Flow.NodeFor(docs.Step!.Id)!));
        Assert.True(builder.Flow.Connect(builder.Flow.NodeFor(docs.Step.Id)!, builder.Flow.NodeFor(forked[3].Step!.Id)!));
        var review = builder.Steps.Single(st => st.Name == "Review");
        builder.Flow.Select(builder.Flow.NodeFor(review.Id)!);
        review.WaitChoice = StepDraftDataContext.WaitCustom;
        review.WaitGroup!.Kind = ArrowConstants.WaitAny;
        review.WaitGroup.Items.Clear();
        var both = review.WaitGroup.AddGroup();
        both.Kind = ArrowConstants.WaitAll;
        ((WaitStepDraftDataContext)both.Items[0]).Step = forked[1].Step!.Id;
        ((WaitStepDraftDataContext)both.AddStep()).Step = forked[2].Step!.Id;
        ((WaitStepDraftDataContext)review.WaitGroup.AddStep()).Step = docs.Step!.Id;
        review.LetsOthersFinish = true;
        Pump();
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(x => x.Header as string != "Variables"))
        {
            expander.IsExpanded = true;
        }

        Pump();
        var waitsFor = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Waits for");
        var panel = waitsFor.FindAncestorOfType<ScrollViewer>()!;
        panel.Offset = new Vector(0, panel.Offset.Y + (waitsFor.TranslatePoint(default, panel)?.Y ?? 0));
        Pump();
        Assert.Contains("⑂ waits for: custom", builder.Flow.NodeFor(review.Id)!.Badges);
        Assert.Equal($"any(all({forked[1].Step!.Id}, {forked[2].Step!.Id}), {docs.Step.Id})", review.WaitGroup.Summary);
        Assert.True(builder.IsValid, string.Join("; ", builder.Errors));
        Save(window, "workflows-wait");

        // Two steps picked by a box, then moved off the layered layout.
        builder.Flow.IsPropertiesOpen = false;
        var moved = builder.Flow.NodeFor(forked[1].Step!.Id)!;
        builder.Flow.PickIn(moved.Bounds.Union(builder.Flow.NodeFor(forked[2].Step!.Id)!.Bounds), false);
        Assert.True(builder.Flow.BeginMove(moved));
        builder.Flow.Move(new Vector(60, 40));
        Assert.True(builder.Flow.EndMove());
        Pump();
        Assert.True(builder.Flow.HasMovedSteps);
        Save(window, "workflows-moved");

        // Simulating the same draft: the frontend fails, so the review starts from the other paths.
        builder.Flow.IsSimulating = true;
        await builder.Flow.CycleOutcomeAsync(builder.Flow.NodeFor(forked[2].Step!.Id)!);
        Pump();
        Assert.IsType<SimulationOverlay>(builder.Flow.Overlay);
        Assert.Equal(FlowNodeMark.Failed, builder.Flow.NodeFor(forked[2].Step!.Id)!.Mark);
        Save(window, "workflows-simulate");
        builder.Flow.IsSimulating = false;
        Assert.True(builder.Save() is not null, builder.ErrorMessage);

        // A run waiting on the child runs one of its steps started, one per line of an earlier step.
        shell.Workflows(repo).Save(new WorkflowDefinition
        {
            Id = "area", Name = "One area",
            Steps = [new AiStepDefinition { Id = "work", Name = "Work on it", Prompt = "Work", Advance = StepConstants.AdvanceApprove }],
        }.Chain(), WorkflowScope.Repo);
        var fanOut = new WorkflowDefinition
        {
            Id = "fan-out", Name = "Each area",
            Steps =
            [
                new AiStepDefinition { Id = "plan", Name = "List the areas", Prompt = "List them" },
                new WorkflowStepDefinition
                {
                    Id = "each", Name = "Work on each area", Workflow = "area",
                    ForEach = new ChildSourceDefinition { Kind = StepConstants.ChildSourceLines, Step = "plan" }, ChildrenTogether = true,
                },
            ],
        }.Chain();
        var respond = this.Executor.Respond;
        this.Executor.Respond = r => r.Prompt.StartsWith("List them", StringComparison.Ordinal)
            ? new(Afl.Core.Execution.ExecutionStatus.Completed, "Sign-in page\nBilling settings\nTheme toggle", r.SessionId)
            : respond(r);
        var parentRun = shell.Driver.Engine.Create(repo, fanOut, SampleTicket());
        await shell.Driver.Engine.DriveAsync(parentRun.Id);
        this.Executor.Respond = respond;
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: parentRun.Id));
        Pump();
        var waiting = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.Equal(StepStatus.AwaitingChildren, waiting.CurrentStep?.Status);
        Assert.True(waiting.CurrentStep!.ChildRuns.Count > 1, waiting.CurrentStep.Error);
        Pump();
        Save(window, "runs-children");
        var parentRow = waiting.RunRows.Single(r => r.Run.Id == parentRun.Id);
        Assert.Equal(waiting.CurrentStep.ChildRuns.Count, waiting.Runs.Count(r => r.ParentRunId == parentRun.Id));
        Assert.DoesNotContain(waiting.RunRows, r => r.Run.ParentRunId == parentRun.Id);
        waiting.ToggleRow(parentRow);
        Assert.Equal(waiting.CurrentStep.ChildRuns.Count, waiting.RunRows.Count(r => r.Run.ParentRunId == parentRun.Id));
        Pump();
        Save(window, "runs-children-open");
        Assert.True(waiting.OpenRun(waiting.CurrentStep.ChildRuns[0].RunId));
        waiting.ToggleRow(waiting.RunRows.Single(r => r.Run.Id == parentRun.Id));
        Assert.Equal(parentRun.Id, waiting.SelectedRun?.Id);
        Assert.True(waiting.OpenRun(waiting.CurrentStep!.ChildRuns[0].RunId));
        Pump();
        Save(window, "runs-child");
        Assert.True(app.Pages.NavigateTo(PageId.Of<WorkflowsPage>(), addToBackStack: false));
        Pump();
        builder = Assert.IsType<LibraryPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);

        // A step the AI paused to ask about a second time, with its first round answered.
        var askFlow = new WorkflowDefinition
        {
            Id = "ask", Name = "Ask as it goes",
            Steps =
            [
                new AiStepDefinition
                {
                    Id = "draft", Name = "Draft the change", Prompt = "Draft it.", Ask = true,
                    Inputs =
                    [
                        new StepInput { Id = "tone", Label = "Which tone should the copy use?", Type = StepConstants.InputChoice,
                            Options = ["Formal", "Casual"], When = StepConstants.WhenAi },
                    ],
                },
            ],
        }.Chain();
        var round = 0;
        this.Executor.Respond = r => new(Afl.Core.Execution.ExecutionStatus.Completed, ++round == 1
            ? "I've read the settings code.\n```afl-ask\n{\"inputs\": [\"tone\"]}\n```"
            : "The toggle is in.\n```afl-ask\n{\"questions\": [{\"label\": \"Should the old theme page stay?\", \"type\": \"confirm\"}]}\n```",
            r.SessionId);
        var askRun = shell.Driver.Engine.Create(repo, askFlow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(askRun.Id);
        await shell.Driver.Engine.AnswerAsync(askRun.Id, new Dictionary<string, string> { ["tone"] = "Formal" });
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: askRun.Id));
        Pump();
        var asking = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.True(asking.CanAnswer, asking.ErrorMessage);
        Assert.Equal("The AI is asking", asking.InputsHeading);
        Pump();
        Save(window, "runs-ask");

        // A step waiting for approval whose output ends with questions for the user.
        var reviewFlow = new WorkflowDefinition
        {
            Id = "review", Name = "Map it",
            Steps = [new AiStepDefinition { Id = "map", Name = "Map the architecture", Prompt = "Map it.", Advance = StepConstants.AdvanceApprove }],
        }.Chain();
        this.Executor.Respond = r => new(Afl.Core.Execution.ExecutionStatus.Completed,
            "## Findings\nThe app is split in three.\n\n## Open questions\n1. **Do the earlier decisions still bind?** *Assumed yes.*\n" +
            "2. **Which version is \"current\"?** *Assumed 1.x*, since it is the released code.\n3. Is app-local the right default root?",
            r.SessionId);
        var reviewRun = shell.Driver.Engine.Create(repo, reviewFlow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(reviewRun.Id);
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: reviewRun.Id));
        Pump();
        var reviewing = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.True(reviewing.ShowQuestions, reviewing.ErrorMessage);
        Assert.Equal("The AI left 3 questions for you", reviewing.QuestionsHeading);
        reviewing.Questions[1].Answer = "v2alt: judge it on its own.";
        Pump();
        Save(window, "runs-questions");

        window.Close();
    }

    [AvaloniaFact]
    public async Task StartingInAnEmptyRepo_AsksItsStack_ThenPicksSkills()
    {
        using var shell = this.CreateShell();
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<IssuesPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();
        var issuesPage = window.GetVisualDescendants().OfType<IssuesPage>().Single();
        var issues = (IssuesPageDataContext)issuesPage.DataContext!;
        var workflow = shell.Workflows(shell.SelectedRepo).GetRequired("feature-flow");

        // The repo has no code yet, so the stack is asked first and saved on the repo.
        var asking = issuesPage.AskSkillsAsync(issues, workflow);
        var stackWindow = await WaitForDialogAsync(window, null);
        var stack = Assert.IsType<StackDataContext>(stackWindow.GetVisualDescendants().OfType<StackView>().Single().DataContext);
        Assert.Contains("python", stack.Languages.Available);
        Assert.True(stack.Languages.Add("python"));
        Assert.True(stack.Frameworks.Add("fastapi"));
        Pump();
        Save(stackWindow, "start-stack");
        Click(stackWindow, "Save");

        // Then the picker: the workflow's skills to start with, and the ones recommended for the stack to tick.
        var pickerWindow = await WaitForDialogAsync(window, stackWindow);
        Assert.Equal(["python"], shell.Repos.GetRequired(shell.SelectedRepo!.Id).Stack!.Languages);
        var picker = Assert.IsType<SkillPickerDataContext>(pickerWindow.GetVisualDescendants().OfType<SkillPickerView>().Single().DataContext);
        Assert.Equal(["caveman"], picker.Skills.Picked);
        var python = picker.Recommended.Single(r => r.Value.Skill.Id == "python-env");
        Assert.False(python.IsSelected);
        python.IsSelected = true;
        Assert.Equal(["caveman", "python-env"], picker.Skills.Picked);
        Assert.True(picker.Skills.Remove("python-env"));
        Assert.False(python.IsSelected);
        picker.TickAll();
        Assert.Contains("fastapi-service", picker.Skills.Picked);
        Assert.True(picker.Skills.Remove("caveman"));
        Assert.True(picker.Skills.Add("test-first"));
        python.IsSelected = false;
        Pump();
        Save(pickerWindow, "start-skills");
        Click(pickerWindow, "Continue");
        for (var i = 0; i < 20 && !asking.IsCompleted; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        var (go, skills) = await asking;
        Assert.True(go);
        Assert.Equal(picker.Picked(), skills);
        Assert.DoesNotContain("python-env", skills!);
        Assert.Contains("test-first", skills!);

        // Repo settings edits the saved stack.
        Assert.True(app.Pages.NavigateTo(PageId.Of<RepoSettingsPage>(), addToBackStack: false));
        Pump();
        var settings = Assert.IsType<RepoSettingsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.Equal(["fastapi"], settings.Stack!.Frameworks.Picked);
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Project" && t.Classes.Contains("h2")).BringIntoView();
        Pump();
        Save(window, "repo-project");
        Assert.True(settings.Stack.Frameworks.Remove("fastapi"));
        Assert.True(settings.Stack.Languages.Remove("python"));
        Assert.True(settings.SaveStack(), settings.ErrorMessage);
        Assert.Null(shell.Repos.GetRequired(shell.SelectedRepo!.Id).Stack);

        window.Close();
    }

    [AvaloniaFact]
    public async Task GitLabLinkedRepo_RendersItsLinkAndIssues()
    {
        using var shell = this.CreateShell();
        shell.GitHosts.SetToken(GitHostStore.GitLabId, "gl-token");
        shell.Repos.LinkHost(shell.SelectedRepo!.Id, GitHostStore.GitLabId, "acme/platform/app");
        shell.Reload();
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<IssuesPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();

        var issues = Assert.IsType<IssuesPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.Equal(TicketRef.GitLabSource, issues.SelectedSource);
        Assert.True(issues.IsGitHost);
        await issues.RefreshAsync();
        Assert.True(issues.ErrorMessage is null, issues.ErrorMessage);
        Assert.Equal(["acme/platform/app#31", "acme/platform/app#27"], issues.Tickets.Select(t => t.Key));
        issues.SelectedTicket = issues.Tickets[1];
        Pump();
        Save(window, "issues-gitlab");

        Assert.True(app.Pages.NavigateTo(PageId.Of<RepoSettingsPage>(), addToBackStack: false));
        Pump();
        var settings = Assert.IsType<RepoSettingsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        await settings.RefreshHostStatusAsync();
        Assert.Equal("Issues come from acme/platform/app on GitLab.", settings.HostLinkText);
        Pump();
        Assert.True(settings.ErrorMessage is null, settings.ErrorMessage);
        Save(window, "repo-hosts");

        window.Close();
    }

    [AvaloniaFact]
    public void RunsInFolders_AreListedUnderTheirFoldersAndFilteredByTag()
    {
        using var shell = this.CreateShell();
        var repo = shell.SelectedRepo!;
        var engine = shell.Driver.Engine;
        var feature = shell.Workflows(repo).GetRequired("feature");
        var billing = engine.Create(repo, feature, SampleTicket());
        var archived = engine.Create(repo, feature, SampleTicket());
        var loose = engine.Create(repo, feature, SampleTicket());
        engine.Rename(billing.Id, "Billing dark mode");
        engine.SetTags(billing.Id, ["billing", "urgent"]);
        engine.SetTags(loose.Id, ["urgent"]);
        engine.MoveToFolder(billing.Id, "billing/q4");
        engine.MoveToFolder(archived.Id, "archive");
        engine.CreateFolder(repo.Id, "later");
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<IssuesPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();

        // Opening a run opens the folders it's in; the other folders stay closed.
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: billing.Id));
        Pump();
        var runs = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.True(runs.ErrorMessage is null, runs.ErrorMessage);
        Assert.Equal(["folder archive 0", "folder billing 0", "folder billing/q4 1", $"run {billing.Id} 2", "folder later 0", $"run {loose.Id} 0"],
            runs.Rows.Select(Describe));
        Assert.Equal("Billing dark mode", runs.RunRows[0].Title);
        Assert.Equal("#billing  #urgent", runs.RunRows[0].TagsText);
        Assert.Equal("1 run", runs.Rows.OfType<FolderRowDataContext>().Single(f => f.Folder == "archive").Summary);
        Assert.Equal("Empty", runs.Rows.OfType<FolderRowDataContext>().Single(f => f.Folder == "later").Summary);
        Assert.Equal(["billing (1)", "urgent (2)"], runs.TagFilter.Choices.Select(c => c.Label).Order());
        Pump();
        Save(window, "runs-folders");

        // Filtering by a tag leaves out the folders with nothing listed in them.
        runs.TagFilter.Add("urgent");
        Assert.Equal(["folder billing 0", "folder billing/q4 1", $"run {billing.Id} 2", $"run {loose.Id} 0"], runs.Rows.Select(Describe));
        runs.TagFilter.Clear();

        // A selected folder stays selected, and new folders are created inside it.
        runs.SelectedRow = runs.Rows.OfType<FolderRowDataContext>().Single(f => f.Folder == "later");
        Assert.Equal(billing.Id, runs.SelectedRun?.Id);
        Assert.True(runs.CanEditFolder);
        Assert.True(runs.CreateFolder("soon"), runs.ErrorMessage);
        Assert.Equal("later", runs.SelectedFolder?.Folder);
        Assert.Contains(runs.Rows, r => r is FolderRowDataContext { Folder: "later/soon", Level: 1 });

        // Renaming a folder moves its runs with it; removing one moves them up.
        runs.SelectedRow = runs.Rows.OfType<FolderRowDataContext>().Single(f => f.Folder == "billing");
        Assert.True(runs.RenameFolder("finance"), runs.ErrorMessage);
        Assert.Equal("finance/q4", shell.Driver.Engine.Store.GetRequired(billing.Id).Folder);
        runs.SelectedRow = runs.Rows.OfType<FolderRowDataContext>().Single(f => f.Folder == "finance/q4");
        Assert.Contains("moves up into folder finance", runs.RemoveFolderText);
        Assert.True(runs.RemoveFolder(), runs.ErrorMessage);
        Assert.Equal("finance", shell.Driver.Engine.Store.GetRequired(billing.Id).Folder);

        // Moving the selected run to the top level, and renaming and tagging it.
        runs.SelectedRun = runs.Runs.Single(r => r.Id == billing.Id);
        Assert.True(runs.MoveToFolder(null), runs.ErrorMessage);
        Assert.True(runs.Rename(""), runs.ErrorMessage);
        Assert.True(runs.SetTags("ui, #ui, later"), runs.ErrorMessage);
        var row = runs.RunRows.Single(r => r.Run.Id == billing.Id);
        Assert.Equal((0, "Add dark mode", "#ui  #later"), (row.Level, row.Title, row.TagsText));
        Assert.Equal("ui, later", runs.TagsInput);

        window.Close();

        static string Describe(AbstractRunListRowDataContext row)
        {
            return row switch
            {
                FolderRowDataContext folder => $"folder {folder.Folder} {folder.Level}",
                RunRowDataContext run => $"run {run.Run.Id} {run.Level}",
                _ => row.ToString()!,
            };
        }
    }

    [AvaloniaFact]
    public void Runs_ShowTheirStateAndAreSearchedAndFilteredByIt()
    {
        using var shell = this.CreateShell();
        var repo = shell.SelectedRepo!;
        var engine = shell.Driver.Engine;
        var feature = shell.Workflows(repo).GetRequired("feature");
        Run Create(string title, RunStatus status, StepStatus step, string? folder = null)
        {
            var run = engine.Create(repo, feature, SampleTicket());
            engine.Rename(run.Id, title);
            if (folder is not null)
            {
                engine.MoveToFolder(run.Id, folder);
            }

            run = engine.Store.GetRequired(run.Id);
            run.Status = status;
            run.CurrentStepId = run.Steps[0].StepId;
            run.Steps[0].Status = step;
            engine.Store.Save(run);
            return run;
        }

        var review = Create("Review the billing plan", RunStatus.Waiting, StepStatus.AwaitingApproval, "billing");
        var failed = Create("Fix the export", RunStatus.Failed, StepStatus.Failed, "billing");
        var running = Create("Night theme", RunStatus.Running, StepStatus.Running);
        Create("Old cleanup", RunStatus.Done, StepStatus.Done);
        Create("Dropped idea", RunStatus.Cancelled, StepStatus.Pending);
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<IssuesPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();

        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: review.Id));
        Pump();
        var runs = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        Assert.True(runs.ErrorMessage is null, runs.ErrorMessage);
        Assert.Equal(RunState.NeedsYou, runs.RunRows.Single(r => r.Run.Id == review.Id).State.State);
        Assert.Equal(["● 1", "✖ 1"], runs.Rows.OfType<FolderRowDataContext>().Single().StateCounts.Select(c => c.Text));
        Assert.Equal(["Needs you (1)", "Running (1)", "Waiting (0)", "Failed (1)", "Done (1)", "Cancelled (1)"],
            runs.Filter.States.Select(s => s.Text));
        Assert.False(runs.IsFiltered);
        Pump();
        Save(window, "runs-states");

        // A search keeps the runs with every word in them, and the folders they're in.
        runs.Filter.SearchText = "billing export";
        Assert.Equal([failed.Id], runs.RunRows.Select(r => r.Run.Id));
        Assert.Equal(["billing"], runs.Rows.OfType<FolderRowDataContext>().Select(f => f.Folder));
        Assert.True(runs.IsFiltered);

        // Picking states shows only runs in them; the counts follow the search.
        runs.Filter.SearchText = null;
        runs.Filter.Toggle(RunState.Failed);
        runs.Filter.Toggle(RunState.Running);
        Assert.Equal(new[] { failed.Id, running.Id }.Order(), runs.RunRows.Select(r => r.Run.Id).Order());
        runs.Filter.SearchText = "night";
        Assert.Equal([running.Id], runs.RunRows.Select(r => r.Run.Id));
        Assert.Equal(1, runs.Filter.States.Single(s => s.State == RunState.Running).Count);
        Assert.Equal(0, runs.Filter.States.Single(s => s.State == RunState.Failed).Count);
        Pump();
        Save(window, "runs-filtered");

        runs.Filter.SearchText = "nothing like this";
        Assert.Empty(runs.Rows);
        Assert.Equal("No runs match the filter.", runs.EmptyMessage);
        runs.ClearFilters();
        Assert.Equal(5, runs.RunRows.Count);
        Assert.Null(runs.EmptyMessage);
        Assert.False(runs.IsFiltered);

        window.Close();
    }

    [AvaloniaFact]
    public void RightClickingARun_SelectsItAndOpensItsMenu()
    {
        using var shell = this.CreateShell();
        var repo = shell.SelectedRepo!;
        var engine = shell.Driver.Engine;
        var feature = shell.Workflows(repo).GetRequired("feature");
        var first = engine.Create(repo, feature, SampleTicket());
        var second = engine.Create(repo, feature, SampleTicket());
        engine.Rename(second.Id, "Second run");
        engine.SetTags(first.Id, ["billing"]);
        engine.CreateFolder(repo.Id, "archive");
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<IssuesPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();
        Assert.True(app.Pages.NavigateTo(PageId.Of<RunsPage>(), addToBackStack: false, parameter: first.Id));
        Pump();
        var runs = Assert.IsType<RunsPageDataContext>(window.GetVisualDescendants().OfType<Page>().Single().DataContext);
        var list = ListWithMenus(window);
        Assert.Equal(first.Id, runs.SelectedRun?.Id);

        // Right-clicking the other run selects it, and opens the run menu with what that run allows.
        RightClick(window, RowOf(list, r => r is RunRowDataContext row && row.Run.Id == second.Id));
        Assert.Equal(second.Id, runs.SelectedRun?.Id);
        var menu = OpenMenu(list);
        Assert.Equal(["Open step", "Resume", "Add note…", "Rename…", "Tags", "Move to", "Copy run id", "Cancel run…", "Delete run…"], VisibleItems(menu));
        Assert.Same(runs, menu.DataContext);
        Assert.Equal(["billing", "Edit tags…"], runs.RunTagChoices.Select(c => c.Header));
        Assert.False(runs.RunTagChoices[0].IsChecked);
        Assert.Equal(["(top level)", "archive", "New folder…"], runs.RunFolderChoices.Select(c => c.Header));
        Assert.True(runs.RunFolderChoices[0].IsChecked);
        Save(window, "runs-run-menu");

        // A run changing while the menu is open doesn't rebuild the rows under it; they catch up once it closes.
        engine.Rename(first.Id, "First, renamed");
        runs.Reload();
        Pump();
        Assert.True(menu.IsOpen);
        menu.Close();
        Pump();
        Assert.Contains(runs.RunRows, r => r.Run.DisplayTitle == "First, renamed");
        RightClick(window, RowOf(list, r => r is RunRowDataContext row && row.Run.Id == second.Id));
        menu = OpenMenu(list);

        // Picking a tag in the Tags submenu puts it on the run; a folder in Move to files the run there.
        Pick(window, menu, "Tags", "billing", "runs-tags-menu");
        Assert.Equal(["billing"], engine.Store.GetRequired(second.Id).Tags);
        Pump();
        RightClick(window, RowOf(list, r => r is RunRowDataContext row && row.Run.Id == second.Id));
        Pick(window, OpenMenu(list), "Move to", "archive", "runs-move-menu");
        Assert.Equal("archive", engine.Store.GetRequired(second.Id).Folder);
        Pump();

        // A folder's row has the folder menu, and selects the folder.
        RightClick(window, RowOf(list, r => r is FolderRowDataContext { Folder: "archive" }));
        Assert.Equal("archive", runs.SelectedFolder?.Folder);
        Assert.Equal(["New folder inside…", "Rename folder…", "Remove folder…"], VisibleItems(OpenMenu(list)));
        OpenMenu(list).Close();
        Pump();

        // No menu opens over the list's empty space.
        var empty = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 4), window)!.Value;
        window.MouseDown(empty, MouseButton.Right);
        window.MouseUp(empty, MouseButton.Right);
        Pump();
        Assert.DoesNotContain(Menus(list), m => m.IsOpen);

        window.Close();
    }

    [AvaloniaFact]
    public async Task RightClickingARow_SelectsItAndOpensItsListsMenuOnEveryPage()
    {
        using var shell = this.CreateShell();
        var repo = shell.SelectedRepo!;
        await this.SeedAsync(shell, repo);

        // The seed writes the repo's Jira link straight to repos.json; pick it up now rather than from the watcher.
        shell.Reload();
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<IssuesPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();

        // Issues: the ticket table's rows.
        var issues = Assert.IsType<IssuesPageDataContext>(CurrentPage(window).DataContext);
        await issues.RefreshAsync();
        Pump();
        Assert.True(issues.Tickets.Count > 0, $"{issues.SelectedSource} {issues.ErrorMessage} {issues.Tickets.Count}");
        issues.SelectedTicket = issues.Tickets[0];
        Pump();
        var table = ListWithMenus(window);
        RightClick(window, RowOf(table, t => t is Ticket { Key: "APP-9" }));
        Assert.Equal("APP-9", issues.SelectedTicket?.Key);
        Assert.Equal(["Start workflow…", "Open in browser", "Copy key", "Copy link"], VisibleItems(OpenMenu(table)));
        Save(window, "issues-ticket-menu");
        OpenMenu(table).Close();
        Pump();

        // Workflows: a built-in listing can be duplicated but not deleted.
        Assert.True(app.Pages.NavigateTo(PageId.Of<WorkflowsPage>(), addToBackStack: false));
        Pump();
        var workflows = Assert.IsType<LibraryPageDataContext>(CurrentPage(window).DataContext);
        var listings = ListWithMenus(window);
        ((ListBox)listings).ScrollIntoView(workflows.Listings.First(l => l.Id == "feature"));
        Pump();
        RightClick(window, RowOf(listings, l => l is LibraryEntry { Id: "feature" }));
        Assert.Equal("feature", workflows.SelectedListing?.Id);
        var menu = OpenMenu(listings);
        Assert.Equal(["Duplicate…", "Copy id", "Delete…"], VisibleItems(menu));
        Assert.False(menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Delete…").IsEnabled);
        Save(window, "workflows-listing-menu");
        menu.Close();
        Pump();

        // Settings: the Jira instance list.
        Assert.True(app.Pages.NavigateTo(PageId.Of<SettingsPage>(), addToBackStack: false));
        Pump();
        var settings = Assert.IsType<SettingsPageDataContext>(CurrentPage(window).DataContext);
        settings.SelectedInstance = null;
        Pump();
        var instances = window.GetVisualDescendants().OfType<ListBox>().First(l => RowMenus.GetIsEnabled(l) && l.IsEffectivelyVisible);
        RightClick(window, RowOf(instances, i => i is JiraInstance { Id: "work" }));
        Assert.Equal("work", settings.SelectedInstance?.Id);
        Assert.Equal(["Test", "Remove…"], VisibleItems(OpenMenu(instances)));
        OpenMenu(instances).Close();
        Pump();

        // Repo settings: the saved filters, where "No default" needs a default.
        Assert.True(app.Pages.NavigateTo(PageId.Of<RepoSettingsPage>(), addToBackStack: false));
        Pump();
        var repoSettings = Assert.IsType<RepoSettingsPageDataContext>(CurrentPage(window).DataContext);
        var filters = ListWithMenus(window);
        var filter = repoSettings.Filters.First();
        RightClick(window, RowOf(filters, f => ReferenceEquals(f, filter)));
        Assert.Same(filter, repoSettings.SelectedFilter);
        Assert.Equal(["Make default", "No default", "Remove…"], VisibleItems(OpenMenu(filters)));
        OpenMenu(filters).Close();
        Pump();

        window.Close();
    }

    /// <summary>
    ///     On macOS Control+click is a right-click: it selects the row and opens its menu, without clicking it. Where it's
    ///     off (other platforms) it's an ordinary click.
    /// </summary>
    [AvaloniaFact]
    public void ControlClickingARow_OpensItsMenuOnMac()
    {
        using var shell = this.CreateShell();
        var app = new AppShell(new NiddyAppOptions
        {
            StartingPage = PageId.Of<WorkflowsPage>(),
            AppDataContext = shell,
            Layout = pages => new ShellLayout(pages) { DataContext = shell },
        });
        var window = new Window { Width = 1440, Height = 900, Content = app };
        window.Show();
        Pump();
        var workflows = Assert.IsType<LibraryPageDataContext>(CurrentPage(window).DataContext);
        var listings = ListWithMenus(window);
        ((ListBox)listings).ScrollIntoView(workflows.Listings.First(l => l.Id == "feature"));
        Pump();
        var was = ContextClicks.IsEnabled;
        try
        {
            ContextClicks.IsEnabled = true;
            ControlClick(window, RowOf(listings, l => l is LibraryEntry { Id: "feature" }));
            Assert.Equal("feature", workflows.SelectedListing?.Id);
            Assert.Equal(["Duplicate…", "Copy id", "Delete…"], VisibleItems(OpenMenu(listings)));
            Save(window, "workflows-control-click-menu");
            OpenMenu(listings).Close();
            Pump();

            ContextClicks.IsEnabled = false;
            var other = workflows.Listings.First(l => l.Id != "feature");
            ControlClick(window, RowOf(listings, l => ReferenceEquals(l, other)));
            Assert.DoesNotContain(Menus(listings), m => m.IsOpen);
        }
        finally
        {
            ContextClicks.IsEnabled = was;
        }

        window.Close();
    }

    /// <summary>
    ///     Gets the page shown.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <returns>The page.</returns>
    private static Page CurrentPage(Window window)
    {
        return window.GetVisualDescendants().OfType<Page>().Single();
    }

    /// <summary>
    ///     Gets the one list shown whose rows have context menus.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <returns>The list.</returns>
    private static Control ListWithMenus(Window window)
    {
        return window.GetVisualDescendants().OfType<Control>().Single(c => RowMenus.GetIsEnabled(c) && c.IsEffectivelyVisible);
    }

    /// <summary>
    ///     Finds the row of a list showing an item.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="match">Picks the item.</param>
    /// <returns>The row's control.</returns>
    private static Control RowOf(Control list, Func<object?, bool> match)
    {
        return list.GetVisualDescendants().OfType<Control>().First(c => match(c.DataContext) && RowMenus.FindRow(list, c) is not null);
    }

    /// <summary>
    ///     Right-clicks a row, a third of the way along.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="row">The row.</param>
    private static void RightClick(Window window, Control row)
    {
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 3, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Pump();
    }

    /// <summary>
    ///     Control+clicks a row, a third of the way along.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="row">The row.</param>
    private static void ControlClick(Window window, Control row)
    {
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 3, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        Pump();
    }

    /// <summary>
    ///     Gets the context menus of a list and of the controls in it.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <returns>The menus.</returns>
    private static IEnumerable<ContextMenu> Menus(Control list)
    {
        return new[] { list }.Concat(list.GetVisualDescendants().OfType<Control>()).Select(c => c.ContextMenu).OfType<ContextMenu>().Distinct();
    }

    /// <summary>
    ///     Gets the menu open over a list.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <returns>The menu.</returns>
    private static ContextMenu OpenMenu(Control list)
    {
        return Menus(list).Single(m => m.IsOpen);
    }

    /// <summary>
    ///     Gets the headers of a menu's visible items.
    /// </summary>
    /// <param name="menu">The menu.</param>
    /// <returns>The headers.</returns>
    private static List<string> VisibleItems(ContextMenu menu)
    {
        return [.. menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => (string)i.Header!)];
    }

    /// <summary>
    ///     Picks a choice in one of a menu's submenus, saving a screenshot with the submenu open.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="menu">The menu.</param>
    /// <param name="submenu">The submenu's header.</param>
    /// <param name="choice">The choice's header.</param>
    /// <param name="screenshot">The screenshot's name.</param>
    private static void Pick(Window window, ContextMenu menu, string submenu, string choice, string screenshot)
    {
        var parent = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == submenu);
        parent.IsSubMenuOpen = true;
        Pump();
        Save(window, screenshot);
        var item = parent.GetLogicalChildren().OfType<MenuItem>().Single(i => i.DataContext is MenuChoice { Header: var h } && h == choice);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Pump();
    }

    /// <summary>
    ///     Runs the dispatcher's queued work and renders a frame.
    /// </summary>
    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    ///     Waits for a dialog to open over a window.
    /// </summary>
    /// <param name="window">The window it opens over.</param>
    /// <param name="previous">A dialog that was open before, which may still be closing; null for none.</param>
    /// <returns>The dialog's window.</returns>
    private static async Task<Window> WaitForDialogAsync(Window window, Window? previous)
    {
        Window? dialog = null;
        for (var i = 0; i < 50 && dialog is null; i++)
        {
            Pump();
            await Task.Delay(10);
            dialog = window.OwnedWindows.SingleOrDefault(w => w != previous);
        }

        Assert.NotNull(dialog);
        Pump();
        return dialog;
    }

    /// <summary>
    ///     Clicks a dialog's button.
    /// </summary>
    /// <param name="dialog">The dialog's window.</param>
    /// <param name="label">The button's text.</param>
    private static void Click(Window dialog, string label)
    {
        dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    /// <summary>
    ///     Saves a screenshot of the window, if a folder was given.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="name">The screenshot's name, e.g. the page shown.</param>
    private static void Save(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable(ScreenshotVariable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, $"{name.ToLowerInvariant().Replace(' ', '-')}.png"), new PngBitmapEncoderOptions());
    }

    /// <summary>
    ///     Puts a page into an interesting state for its screenshot.
    /// </summary>
    /// <param name="dc">The page's data context.</param>
    /// <returns>A task that completes when the page is ready.</returns>
    private async Task ArrangeAsync(AbstractPageDataContext dc)
    {
        switch (dc)
        {
            case IssuesPageDataContext issues:
                // The scratch repo has no GitHub remote, so the page lists the linked Jira's tickets (from the fake
                // Jira server) through the default filter.
                await issues.RefreshAsync();
                Assert.Equal(["APP-12", "APP-9"], issues.Tickets.Select(t => t.Key));
                Assert.Contains(this.Jira.Requests, r => r.Headers.Authorization?.Scheme == "Basic");
                issues.SelectedTicket = issues.Tickets[0];
                break;
            case LibraryPageDataContext { Editor: CommonStepLibraryEditor } steps:
                steps.SelectedListing = steps.Listings.First(l => l.Id == "code-review");
                break;
            case LibraryPageDataContext { Editor: SkillLibraryEditor } skills:
                skills.SelectedListing = skills.Listings.First(l => l.Id == "caveman");
                break;
            case LibraryPageDataContext workflows:
                workflows.SelectedListing = workflows.Listings.First(l => l.Id == "review");
                workflows.SelectedStep = workflows.Steps[0];
                Assert.Equal("codex", workflows.SelectedStep.Agent);
                break;
            case SettingsPageDataContext settings:
                settings.SelectedInstance = settings.Instances.FirstOrDefault();
                settings.SelectedAgent = settings.AgentList.FirstOrDefault(a => a.Id == "codex");
                break;
            case RepoSettingsPageDataContext repoSettings:
                // Navigating there lists the linked instance's projects and boards from the fake Jira server.
                await repoSettings.LoadFromJiraAsync();
                Assert.Equal(["APP", "OPS", "WEB"], repoSettings.ProjectChoices.Select(c => c.Value));
                Assert.Equal(["APP", "WEB"], repoSettings.ProjectChoices.Where(c => c.IsSelected).Select(c => c.Value));
                repoSettings.SelectedFilter = repoSettings.Filters.FirstOrDefault();
                break;
        }
    }

    /// <summary>
    ///     Adds a Jira instance, links the repo with a filter, saves a repo workflow and drives a run to an approval
    ///     gate.
    /// </summary>
    /// <param name="shell">The shell.</param>
    /// <param name="repo">The repo.</param>
    /// <returns>The run's id, once it is waiting.</returns>
    private async Task<string> SeedAsync(ShellDataContext shell, RepoRecord repo)
    {
        shell.JiraInstances.Save(new JiraInstance
        {
            Id = "work", Name = "Work Jira", Kind = JiraInstanceKind.Cloud, BaseUrl = "https://example.atlassian.net",
            Email = "me@example.com",
        }, "test-token");
        shell.Agents.Save(Afl.Core.Agents.AgentStore.GetPreset("codex"));
        shell.Servers.Save(new Afl.Core.Mcp.McpServerDefinition
        {
            Id = "docs", Name = "Docs search", Description = "Searches the team's docs.", Transport = AgentConstants.TransportStdio,
            Command = "npx", Args = ["-y", "docs-mcp"], Env = new() { ["DOCS_TOKEN"] = "{{secret.DOCS_TOKEN}}" },
            Secrets = ["DOCS_TOKEN"],
        }, new Dictionary<string, string?> { ["DOCS_TOKEN"] = "test-secret" });
        var registry = new RepoRegistry(this.Paths);
        registry.LinkJira(repo.Id, "work", ["APP", "WEB"]);
        registry.SaveJiraFilter(repo.Id, new JiraFilter { Name = "mine", Project = "APP", Assignee = "me", Statuses = ["To Do"] }, true);

        var library = shell.Workflows(repo);
        var review = library.GetRequired("feature");
        review.Id = "review";
        review.Name = "Review only";
        review.Steps[0].Agent = "codex";
        library.Save(review, WorkflowScope.Repo);
        library.Save(new WorkflowDefinition
        {
            Id = "compare", Name = "Compare plans",
            Steps =
            [
                new AiStepDefinition
                {
                    Id = "plan", Name = "Plan", Prompt = "Plan {{ticket.title}}.",
                    PermissionMode = StepConstants.ReadOnlyPermissionMode, Agents = ["claude", "codex"],
                    Combine = StepConstants.CombineChoose, MergeAgent = "claude",
                },
                new AiStepDefinition { Id = "build", Name = "Build", Prompt = "Do: {{steps.plan.output}}" },
            ],
        }.Chain(), WorkflowScope.Repo);

        library.Save(new WorkflowDefinition
        {
            Id = "loop", Name = "Fix until green",
            Steps =
            [
                new AiStepDefinition
                {
                    Id = "fix", Name = "Fix", Prompt = "Fix it (try {{loop.iteration}}).",
                    Arrows =
                    [
                        new ArrowDefinition
                        {
                            To = "fix", Until = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "all green" },
                            Max = 3, OnLimit = StepConstants.OnLimitContinue,
                        },
                        new ArrowDefinition { To = "check" },
                    ],
                },
                new AiStepDefinition
                {
                    Id = "check", Name = "Check", Prompt = "Check: {{steps.fix.output}}",
                    Follow = ArrowConstants.FollowFirst,
                    Arrows =
                    [
                        new ArrowDefinition
                        {
                            To = "ship",
                            When = new ConditionDefinition
                            {
                                Kind = StepConstants.ConditionAny,
                                Conditions =
                                [
                                    new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "plan" },
                                    new ConditionDefinition { Kind = StepConstants.ConditionOutput, Matches = "^ok" },
                                ],
                            },
                        },
                        new ArrowDefinition { To = "escalate" },
                    ],
                },
                new AiStepDefinition { Id = "escalate", Name = "Escalate", Prompt = "Ask for help." },
                new AiStepDefinition { Id = "ship", Name = "Ship", Prompt = "Ship it." },
            ],
        }.Chain(), WorkflowScope.Repo);

        library.Save(new WorkflowDefinition
        {
            Id = "fork", Name = "Check in parallel",
            Steps =
            [
                new AiStepDefinition { Id = "plan", Name = "Plan", Prompt = "Plan it.", Arrows = [new() { To = "lint" }, new() { To = "test" }] },
                new AiStepDefinition { Id = "lint", Name = "Lint", Prompt = "Lint it.", Advance = StepConstants.AdvanceApprove, Arrows = [new() { To = "ship" }] },
                new AiStepDefinition { Id = "test", Name = "Test", Prompt = "Test it.", Advance = StepConstants.AdvanceApprove, Arrows = [new() { To = "ship" }] },
                new AiStepDefinition { Id = "ship", Name = "Ship", Prompt = "Ship it." },
            ],
        }, WorkflowScope.Repo);

        this.Executor.Respond = r => new(Afl.Core.Execution.ExecutionStatus.Completed,
            $"Plan for {SampleTicket().Title}:\n1. Add a theme toggle\n2. Persist the choice\n3. Test both themes", r.SessionId);
        // The plan gate asks questions, so the Runs page shows its input form.
        var feature = library.GetRequired("feature");
        feature.Steps[1].Inputs =
        [
            new StepInput { Id = "scope", Label = "How much should it cover?", Type = StepConstants.InputChoice,
                Options = ["Just the toggle", "Toggle and settings page"], Default = "Just the toggle" },
            new StepInput { Id = "areas", Label = "Areas to test", Type = StepConstants.InputMultiChoice,
                Options = ["macOS", "Windows", "Linux"], Help = "Pick every platform the change must be checked on." },
            new StepInput { Id = "notes", Label = "Notes for the implementer", Type = StepConstants.InputMultiline, Required = false },
            new StepInput { Id = "tests", Label = "Write UI tests too", Type = StepConstants.InputConfirm, Default = "yes" },
        ];
        var run = shell.Driver.Engine.Create(repo, feature, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);

        // Replaying the first step with feedback gives it (and the gate after it) an earlier attempt to show.
        await shell.Driver.Engine.ReplayAsync(run.Id, feature.Steps[0].Id, "Also look at the settings page.");
        return run.Id;
    }
}
