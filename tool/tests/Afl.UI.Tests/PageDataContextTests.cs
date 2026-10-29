using System.Text.Json.Nodes;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Jira;
using Afl.Core.Mcp;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using Afl.UI.DataContexts;
using Avalonia.Headless.XUnit;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for the workflow builder and settings data contexts against a scratch afl home.
/// </summary>
public class PageDataContextTests : AbstractUiTest
{
    [AvaloniaFact]
    public void WorkflowBuilder_SavesDraftsAsJsonInTheRepoAndGlobalLibraries()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        Assert.Contains(dc.Listings, l => l.Id == "feature" && l.Scope == WorkflowScope.BuiltIn);

        dc.New("review");
        Assert.True(dc.IsDirty);
        Assert.Equal(WorkflowScope.Repo, dc.SaveScope);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        dc.AddStep();
        var second = dc.SelectedStep!;
        second.Id = "pr";
        second.StepType = StepConstants.TypeUser;
        second.UserPart().Instructions = "Open a PR for {{ticket.url}}";
        Assert.Contains("{{steps.step-1.output}}", dc.Variables);
        Assert.DoesNotContain("{{steps.pr.output}}", dc.Variables);

        // Live validation: a duplicate step id is reported straight away and blocks saving.
        second.Id = "step-1";
        Assert.False(dc.IsValid);
        Assert.NotEmpty(dc.Errors);
        second.Id = "pr";
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));


        var repoFile = dc.Save();
        Assert.Equal(Path.Combine(AflPaths.RepoWorkflowsDirectory(this.RepoPath), "review.json"), repoFile);
        Assert.False(dc.IsDirty);
        var json = JsonNode.Parse(File.ReadAllText(repoFile!))!;
        Assert.Equal("review", (string?)json["id"]);
        var steps = json["steps"]!.AsArray();
        Assert.Equal(2, steps.Count);
        Assert.Equal("user", (string?)steps[1]!["type"]);
        Assert.Null(steps[1]!["prompt"]);
        Assert.Equal(WorkflowScope.Repo, dc.SelectedListing?.Scope);

        // Editing a built-in saves a copy to the global library, which then overrides it.
        dc.SelectedListing = dc.Listings.Single(l => l.Id == "bugfix" && l.Scope == WorkflowScope.BuiltIn);
        Assert.False(dc.CanDelete);
        Assert.Equal(WorkflowScope.Global, dc.SaveScope);
        dc.DraftName = "Bug fix (ours)";
        var globalFile = dc.Save();
        Assert.Equal(Path.Combine(this.Paths.WorkflowsDirectory, "bugfix.json"), globalFile);
        Assert.Equal("Bug fix (ours)", shell.Workflows(shell.SelectedRepo).GetRequired("bugfix").Name);

        dc.SelectedListing = dc.Listings.Single(l => l.Id == "review" && l.Scope == WorkflowScope.Repo);
        Assert.True(dc.Delete());
        Assert.False(File.Exists(repoFile));
        Assert.DoesNotContain(dc.Listings, l => l.Id == "review");
    }

    [AvaloniaFact]
    public void StepDraft_DropsFieldsTheStepTypeDoesNotUse()
    {
        var draft = new StepDraftDataContext(new AiStepDefinition
        {
            Id = "plan", Name = "Plan", Prompt = "Plan it", Model = " opus ",
            AllowedTools = ["Read", "Grep"], PermissionMode = StepConstants.PlanPermissionMode,
        });
        Assert.Equal("Read, Grep", draft.AgentPart().AllowedTools);

        draft.StepType = StepConstants.TypeInteractive;
        var mixed = Assert.IsAssignableFrom<AbstractAgentStepDefinition>(draft.ToDefinition());
        Assert.Equal(StepConstants.TypeInteractive, mixed.Type);
        Assert.Equal("opus", mixed.Model);
        Assert.Equal(["Read", "Grep"], mixed.AllowedTools!);

        draft.StepType = StepConstants.TypeUser;
        draft.UserPart().Instructions = "Do it";
        var user = Assert.IsType<UserStepDefinition>(draft.ToDefinition());
        Assert.Equal("Do it", user.Instructions);
    }

    [AvaloniaFact]
    public void StepDraft_PicksItsEditorPartByType()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("parts");
        var step = dc.SelectedStep!;
        step.AgentPart().Prompt = "Plan it";
        step.Artifact = "PLAN.md";

        step.StepType = StepConstants.TypeInteractive;
        Assert.Equal("Plan it", step.AgentPart().Prompt);
        step.StepType = StepConstants.TypeUser;
        step.UserPart().Instructions = "Check it";
        step.StepType = StepConstants.TypeWorkflow;
        step.WorkflowPart().ChildWorkflow = "feature";
        Assert.Contains("runs feature", step.Title);

        step.StepType = StepConstants.TypeAi;
        Assert.Equal("Plan it", step.AgentPart().Prompt);
        Assert.Equal("PLAN.md", step.Artifact);
        var saved = Assert.IsType<AiStepDefinition>(step.ToDefinition());
        Assert.Equal("Plan it", saved.Prompt);
        Assert.Equal("PLAN.md", saved.Artifact);
    }

    [AvaloniaFact]
    public void StepDraft_EditsInputsForStepsThatAskTheUser()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("ask");
        var first = dc.SelectedStep!;
        Assert.True(first.AgentPart().ShowsAsk);

        first.StepType = StepConstants.TypeUser;
        first.UserPart().Instructions = null;
        first.UserPart();
        var input = first.AddInput();
        Assert.Equal(StepConstants.WhenAfter, input.When);
        input.Id = "size";
        input.Label = "How big?";
        input.Type = StepConstants.InputChoice;
        Assert.True(input.UsesOptions);
        input.Options = "small, large";
        input.Default = "small";
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        Assert.True(dc.IsDirty);

        dc.AddStep();
        Assert.Contains("{{steps.step-1.inputs.size}}", dc.Variables);

        var definition = first.ToDefinition();
        var saved = Assert.Single(definition.Inputs!);
        Assert.Equal(["small", "large"], saved.Options!);
        Assert.Equal("small", saved.Default);

        // Options are dropped for a type that doesn't use them, and an AI step that moves on by itself can't ask
        // anything after its work.
        input.Type = StepConstants.InputText;
        Assert.Null(first.ToDefinition().Inputs![0].Options);
        first.StepType = StepConstants.TypeAi;
        first.AgentPart().Prompt = "Plan";
        Assert.False(dc.IsValid);

        // ...unless it waits for approval, whatever its type.
        first.Advance = StepConstants.AdvanceApprove;
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        Assert.Equal(StepConstants.AdvanceApprove, first.ToDefinition().Advance);
        first.Advance = StepDraftDataContext.DefaultChoice;
        Assert.Null(first.ToDefinition().Advance);
        Assert.False(dc.IsValid);

        // ...but it can ask before its work, or let the AI ask (and ask its own questions too).
        input.When = StepConstants.WhenBefore;
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        Assert.Equal(StepConstants.WhenBefore, first.ToDefinition().Inputs![0].When);
        input.When = StepConstants.WhenAi;
        first.AgentPart().Ask = true;
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        Assert.True(Assert.IsAssignableFrom<AbstractAgentStepDefinition>(first.ToDefinition()).Ask);
        Assert.Equal(StepConstants.WhenAi, first.ToDefinition().Inputs![0].When);
        first.StepType = StepConstants.TypeInteractive;
        Assert.True(first.AgentPart().ShowsAsk);
        Assert.False(dc.IsValid);
        first.StepType = StepConstants.TypeAi;
        first.AgentPart().Ask = false;
        Assert.Null(Assert.IsAssignableFrom<AbstractAgentStepDefinition>(first.ToDefinition()).Ask);
        input.When = StepConstants.WhenAfter;
        Assert.Null(first.ToDefinition().Inputs![0].When);

        first.RemoveInput(input);
        Assert.Equal(StepConstants.WhenBefore, first.AddInput().When);
        first.RemoveInput(first.Inputs[0]);
        Assert.Null(first.ToDefinition().Inputs);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
    }

    [AvaloniaFact]
    public async Task RunsPage_AsksTheStepsInputsBeforeApproving()
    {
        using var shell = this.CreateShell();
        var repo = shell.SelectedRepo!;
        var workflow = new WorkflowDefinition
        {
            Id = "ask", Name = "Ask",
            Steps =
            [
                new AiStepDefinition
                {
                    Id = "plan", Name = "Plan",                     Advance = StepConstants.AdvanceApprove, Prompt = "Plan it",
                    Inputs =
                    [
                        new StepInput { Id = "size", Label = "Size", Type = StepConstants.InputChoice, Options = ["S", "L"] },
                        new StepInput { Id = "areas", Label = "Areas", Type = StepConstants.InputMultiChoice, Options = ["ui", "api"] },
                        new StepInput { Id = "notes", Label = "Notes", Type = StepConstants.InputMultiline, Required = false },
                        new StepInput { Id = "ship", Label = "Ship?", Type = StepConstants.InputConfirm, Default = "yes" },
                    ],
                },
                new UserStepDefinition { Id = "pr", Name = "PR", Instructions = "{{steps.plan.inputs.size}}" },
            ],
        }.Chain();
        var run = shell.Driver.Engine.Create(repo, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.True(dc.CanApprove);
        Assert.True(dc.ShowInputs);
        Assert.Equal([true, false, false, false], dc.Inputs.Select(i => i.IsChoice));
        Assert.True(dc.Inputs[1].IsMultiChoice);
        Assert.True(dc.Inputs[2].IsMultiline);
        Assert.True(dc.Inputs[3].IsToggle);
        Assert.True(dc.Inputs[3].IsChecked);
        Assert.Equal("Notes (optional)", dc.Inputs[2].Label);

        // A missing answer is marked and nothing runs.
        dc.Approve();
        Assert.NotNull(dc.Inputs[0].Error);
        Assert.NotNull(dc.Inputs[1].Error);
        Assert.Null(shell.Driver.Find(run.Id));

        dc.Inputs[0].Choice = "L";
        dc.Inputs[1].Choices[1].IsSelected = true;
        dc.Approve();
        Assert.Null(dc.Inputs[0].Error);
        await shell.Driver.Find(run.Id)!.Completion;

        var saved = new RunStore(this.Paths).GetRequired(run.Id);
        Assert.Equal(new Dictionary<string, string> { ["size"] = "L", ["areas"] = "api", ["ship"] = StepConstants.Yes },
            saved.Steps[0].Answers);
        Assert.Equal(StepStatus.AwaitingUser, saved.CurrentStep!.Status);
        Assert.Equal("L", saved.CurrentStep.PendingPrompt);
    }

    [AvaloniaFact]
    public async Task RunsPage_AnswersBeforeTheStepAndWhenTheAiAsks()
    {
        using var shell = this.CreateShell();
        var asked = false;
        this.Executor.Respond = r =>
        {
            var output = asked ? "done" : "So far.\n\n```afl-ask\n{\"questions\": [{\"label\": \"Which colour?\"}]}\n```";
            asked = true;
            return new(Afl.Core.Execution.ExecutionStatus.Completed, output, r.SessionId);
        };
        var workflow = new WorkflowDefinition
        {
            Id = "ask", Name = "Ask",
            Steps =
            [
                new AiStepDefinition
                {
                    Id = "paint", Name = "Paint", Prompt = "Paint {{inputs.room}}", Ask = true,
                    Inputs = [new StepInput { Id = "room", Label = "Which room?", When = StepConstants.WhenBefore }],
                },
            ],
        }.Chain();
        var run = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.True(dc.CanAnswer);
        Assert.True(dc.ShowInputs);
        Assert.Equal("Before this step starts", dc.InputsHeading);
        Assert.Equal("Which room?", Assert.Single(dc.Inputs).Label);

        dc.Inputs[0].Text = "kitchen";
        dc.Answer();
        await shell.Driver.Find(run.Id)!.Completion;
        dc.Focus(run.Id);

        Assert.Equal("Paint kitchen", this.Executor.Requests[0].Prompt.Split("\n")[0]);
        Assert.True(dc.CanAnswer);
        Assert.Equal("The AI is asking", dc.InputsHeading);
        Assert.Equal("Which colour?", Assert.Single(dc.Inputs).Label);

        dc.Inputs[0].Text = "green";
        dc.Answer();
        await shell.Driver.Find(run.Id)!.Completion;

        var saved = new RunStore(this.Paths).GetRequired(run.Id);
        Assert.Equal(RunStatus.Done, saved.Status);
        Assert.Equal("green", saved.Steps[0].Answers["ask-1"]);
    }

    [AvaloniaFact]
    public async Task RunsPage_ReplaysAStepThatHasRun()
    {
        using var shell = this.CreateShell();
        var workflow = new WorkflowDefinition
        {
            Id = "two", Name = "Two",
            Steps =
            [
                new AiStepDefinition { Id = "draft", Name = "Draft", Prompt = "Draft it" },
                new UserStepDefinition { Id = "check", Name = "Check", Instructions = "Check it" },
            ],
        }.Chain();
        var run = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);
        await shell.Driver.Engine.CompleteAsync(run.Id, "looks fine");

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.Equal(RunStatus.Done, dc.SelectedRun!.Status);
        Assert.True(dc.CanReplay);
        var draft = dc.SelectedRun.Steps[0];
        Assert.Contains("1 step(s) after it", dc.DescribeReplay(draft));

        dc.Replay(draft, "Shorter, please.");
        Assert.False(dc.CanReplay);
        await shell.Driver.Find(run.Id)!.Completion;

        var saved = new RunStore(this.Paths).GetRequired(run.Id);
        Assert.Equal("check", saved.CurrentStep!.StepId);
        Assert.Equal(StepStatus.AwaitingUser, saved.CurrentStep.Status);
        Assert.Equal("Shorter, please.", Assert.Single(saved.Steps[0].History).Reason);
        Assert.Equal("looks fine", Assert.Single(saved.Steps[1].History).Output);
    }

    [AvaloniaFact]
    public async Task RunsPage_CancelsAndDeletesARun()
    {
        using var shell = this.CreateShell();
        var workflow = new WorkflowDefinition
        {
            Id = "one", Name = "One", Steps = [new UserStepDefinition { Id = "check", Name = "Check", Instructions = "Check it" }],
        }.Chain();
        var kept = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        var run = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.True(dc.CanDelete);
        Assert.StartsWith($"Cancel and delete run {run.Id}, with its step logs?", dc.DeleteText);

        Assert.True(dc.Delete());

        Assert.Null(new RunStore(this.Paths).Get(run.Id));
        Assert.Equal(kept.Id, dc.SelectedRun?.Id);
        Assert.DoesNotContain(dc.Runs, r => r.Id == run.Id);
    }

    [AvaloniaFact]
    public async Task RunsPage_AnswersTheAisOpenQuestions()
    {
        using var shell = this.CreateShell();
        var workflow = new WorkflowDefinition
        {
            Id = "one", Name = "One",
            Steps = [new UserStepDefinition { Id = "check", Name = "Check", Instructions = "Check it", Advance = StepConstants.AdvanceApprove }],
        }.Chain();
        var run = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);
        await shell.Driver.Engine.CompleteAsync(run.Id, "Done.\n\n## Questions for you\n- Keep the cache?\n- Drop v1?");

        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.True(dc.ShowQuestions);
        Assert.Equal(["1. Keep the cache?", "2. Drop v1?"], dc.Questions.Select(q => q.Label));

        dc.SendAnswers();
        Assert.Equal("Answer at least one question, or add a note.", dc.ErrorMessage);

        dc.Questions[1].Answer = "Yes, drop it.";
        dc.SendAnswers();
        await shell.Driver.Find(run.Id)!.Completion;

        var state = new RunStore(this.Paths).GetRequired(run.Id).CurrentStep!;
        Assert.Equal(StepStatus.AwaitingUser, state.Status);
        Assert.Contains("1. Keep the cache?\n   Answer: not answered; keep your assumption.", state.PendingPrompt);
        Assert.Contains("2. Drop v1?\n   Answer: Yes, drop it.", state.PendingPrompt);
    }

    [AvaloniaFact]
    public async Task Settings_EditsJiraInstancesAndNeedsAToken()
    {
        using var shell = this.CreateShell();
        shell.JiraInstances.Save(new JiraInstance
        {
            Id = "work", Name = "Work", Kind = JiraInstanceKind.Cloud, BaseUrl = "https://example.atlassian.net",
            Email = "me@example.com",
        }, null);
        using var dc = new SettingsPageDataContext(shell);

        dc.SelectedInstance = dc.Instances.Single();
        Assert.Equal("https://example.atlassian.net", dc.InstanceBaseUrl);
        Assert.StartsWith("No token", dc.TokenStatus);
        Assert.False(await dc.SaveInstanceAsync());
        Assert.NotNull(dc.ErrorMessage);

        // Picking another repo doesn't touch the instance form.
        dc.InstanceName = "Edited";
        shell.SelectedRepoItem = ShellDataContext.AllRepos;
        Assert.Equal("Edited", dc.InstanceName);

        dc.NewInstance();
        Assert.Null(dc.SelectedInstance);
        Assert.Equal(string.Empty, dc.InstanceId);
    }

    [AvaloniaFact]
    public async Task RepoSettings_LinksJiraPicksProjectsAndBoardsSavesFiltersAndDefaultWorkflow()
    {
        using var shell = this.CreateShell();
        shell.JiraInstances.Save(new JiraInstance
        {
            Id = "work", Name = "Work", Kind = JiraInstanceKind.Cloud, BaseUrl = "https://example.atlassian.net",
            Email = "me@example.com",
        }, "test-token");
        using var dc = new RepoSettingsPageDataContext(shell);
        var repoId = shell.SelectedRepo!.Id;
        var registry = new RepoRegistry(this.Paths);

        Assert.Equal(RepoSettingsPageDataContext.NotLinked, dc.LinkedInstance);
        Assert.Contains("work", dc.LinkChoices);
        Assert.Empty(dc.ProjectChoices);

        // Linking lists the instance's projects and boards to pick from.
        dc.LinkedInstance = "work";
        Assert.Equal("work", registry.GetRequired(repoId).Jira?.InstanceId);
        await dc.LoadFromJiraAsync();
        Assert.Equal("3 projects and 2 boards in Work.", dc.JiraStatus);
        Assert.Equal(["APP", "OPS", "WEB"], dc.ProjectChoices.Select(c => c.Value));
        Assert.Equal(["APP", "OPS", "WEB"], dc.ProjectKeys);
        Assert.Equal(["42 · App scrum", "7 · Web kanban"], dc.BoardChoices);

        dc.ProjectChoices.Single(c => c.Value == "APP").IsSelected = true;
        dc.NewProjectKey = "legacy, web";
        Assert.True(dc.AddProjectKey());
        Assert.Null(dc.NewProjectKey);
        dc.NewProjectKey = "not a key!";
        Assert.False(dc.AddProjectKey());
        Assert.NotNull(dc.ErrorMessage);
        Assert.True(dc.SaveProjects());
        Assert.Equal(["APP", "LEGACY", "WEB"], registry.GetRequired(repoId).Jira!.Projects.Order());

        // The reload after saving keeps Jira's list, with a saved key Jira doesn't know still offered.
        Assert.Equal(["APP", "OPS", "WEB", "LEGACY"], dc.ProjectChoices.Select(c => c.Value));
        Assert.Equal(["APP", "LEGACY", "WEB"], dc.ProjectChoices.Where(c => c.IsSelected).Select(c => c.Value).Order());

        dc.NewFilter();
        dc.FilterName = "mine";
        dc.FilterProject = "app";
        Assert.Equal(["42 · App scrum"], dc.BoardChoices);
        dc.FilterAssignee = "me";
        dc.FilterStatuses = "To Do, In Progress";
        dc.FilterBoard = "not a number";
        Assert.False(dc.SaveFilter());
        Assert.Contains("Board", dc.ErrorMessage);

        dc.FilterBoard = dc.BoardChoices.Single();
        dc.FilterMakeDefault = true;
        Assert.True(dc.SaveFilter());
        var link = registry.GetRequired(repoId).Jira!;
        Assert.Equal("mine", link.DefaultFilter);
        var filter = link.FindFilter("mine")!;
        Assert.Equal("APP", filter.Project);
        Assert.Equal(42, filter.BoardId);
        Assert.Equal(["To Do", "In Progress"], filter.Statuses);

        dc.Reload();
        Assert.Equal("mine", dc.SelectedFilter?.Name);
        Assert.Equal("42 · App scrum", dc.FilterBoard);
        dc.ClearDefaultFilter();
        Assert.Null(registry.GetRequired(repoId).Jira!.DefaultFilter);

        Assert.Contains("bugfix", dc.WorkflowIds);
        dc.DefaultWorkflowId = "bugfix";
        Assert.Equal("bugfix", registry.GetRequired(repoId).DefaultWorkflowId);

        dc.RepoName = " ";
        Assert.False(dc.SaveName());
        dc.RepoName = "Renamed";
        Assert.True(dc.SaveName());
        Assert.Equal("Renamed", registry.GetRequired(repoId).Name);
        Assert.Equal("Renamed", shell.SelectedRepo?.Name);

        dc.LinkedInstance = RepoSettingsPageDataContext.NotLinked;
        Assert.Null(registry.GetRequired(repoId).Jira);

        Assert.True(dc.RemoveRepo());
        Assert.Null(registry.Find(repoId));
        Assert.Null(shell.SelectedRepo);
        Assert.False(dc.HasRepo);
    }

    [AvaloniaFact]
    public async Task RepoSettings_StillTakesTypedKeysWhenJiraIsDown()
    {
        using var shell = this.CreateShell();
        shell.JiraInstances.Save(new JiraInstance
        {
            Id = "work", Name = "Work", Kind = JiraInstanceKind.Cloud, BaseUrl = "https://example.atlassian.net",
            Email = "me@example.com",
        }, "test-token");
        new RepoRegistry(this.Paths).LinkJira(shell.SelectedRepo!.Id, "work", ["APP"]);
        shell.Reload();
        this.JiraDown = true;
        using var dc = new RepoSettingsPageDataContext(shell);

        await dc.LoadFromJiraAsync();
        Assert.StartsWith("Couldn't list from Work", dc.JiraStatus);
        Assert.Null(dc.ErrorMessage);
        Assert.Equal(["APP"], dc.ProjectChoices.Select(c => c.Value));
        Assert.Equal(["APP"], dc.ProjectKeys);
        Assert.Empty(dc.BoardChoices);

        dc.NewProjectKey = "WEB";
        Assert.True(dc.AddProjectKey());
        Assert.True(dc.SaveProjects());
        dc.FilterName = "board";
        dc.FilterBoard = "12";
        Assert.True(dc.SaveFilter());
        Assert.Equal(12, new RepoRegistry(this.Paths).GetRequired(shell.SelectedRepo!.Id).Jira!.FindFilter("board")!.BoardId);
    }

    [AvaloniaFact]
    public async Task Settings_AddsEditsTestsAndRemovesAgents()
    {
        using var shell = this.CreateShell();
        using var dc = new SettingsPageDataContext(shell);
        Assert.Equal(["claude", "session"], dc.AgentList.Select(a => a.Id));
        Assert.Equal("claude", dc.SelectedAgent?.Id);
        Assert.False(dc.CanEditAgent);
        dc.IsAgentJson = true;
        Assert.Contains("\"kind\": \"claude\"", dc.AgentForm!.Json);

        // A preset is added under a free id, so adding it twice gives two agents.
        dc.SelectedPreset = dc.AgentPresets.Single(p => p.Agent.Id == "codex");
        Assert.True(dc.AddPresetAgent());
        Assert.True(dc.AddPresetAgent());
        Assert.Equal("codex-2", dc.SelectedAgent?.Id);
        Assert.True(dc.CanEditAgent);
        Assert.Equal(["claude", "session", "codex", "codex-2"], dc.AgentList.Select(a => a.Id));

        // A new agent is edited as JSON, and can be tested before it's saved.
        dc.NewAgent();
        Assert.Null(dc.SelectedAgent);
        dc.IsAgentJson = true;
        dc.AgentForm!.Json = """
                       { "id": "echoer", "name": "Echoer", "kind": "cli", "command": "/bin/sh",
                         "args": ["-c", "echo \"got: $1\"", "sh", "{{prompt}}"] }
                       """;
        await dc.TestAgentAsync();
        Assert.Equal($"echoer answered: got: {AgentStore.TestPrompt}", dc.AgentStatus);
        Assert.True(dc.SaveAgent());
        Assert.Equal("echoer", dc.SelectedAgent?.Id);
        Assert.Equal("/bin/sh", shell.Agents.GetRequired("echoer").Command);

        dc.IsAgentJson = true;
        dc.AgentForm!.Json = "{ not json";
        Assert.False(dc.SaveAgent());
        Assert.Contains("isn't valid", dc.ErrorMessage);
        dc.AgentForm.Json = """{ "id": "Bad Id", "kind": "cli", "command": "x" }""";
        Assert.False(dc.SaveAgent());
        Assert.Contains("isn't a usable agent id", dc.ErrorMessage);

        dc.SelectedAgent = dc.AgentList.Single(a => a.Id == "codex-2");
        Assert.True(dc.RemoveAgent());
        Assert.Null(shell.Agents.Find("codex-2"));
        Assert.Equal("claude", dc.SelectedAgent?.Id);
    }

    [AvaloniaFact]
    public void TagFilter_OffersFacetedChoicesAndCommonSuggestions()
    {
        var filter = new TagFilterDataContext();
        var changes = 0;
        filter.Edited += (_, _) => changes++;
        string[][] items =
        [
            ["python", "testing", "api"], ["python", "testing"], ["python", "db"], ["go", "api"],
            ["docs"], ["ci"], ["ux"], ["security", "python"],
        ];
        filter.Update(items);
        Assert.True(filter.IsVisible);
        Assert.Equal(["api", "ci", "db", "docs", "go", "python", "security", "testing", "ux"], filter.Choices.Select(c => c.Tag));
        Assert.Equal(TagFilterDataContext.SuggestionCount, filter.Suggestions.Count);
        Assert.Equal(new TagChoice("python", 4), filter.Suggestions[0]);
        Assert.Equal("python (4)", filter.Suggestions[0].Label);
        Assert.Equal("Search 9 tags…", filter.SearchPlaceholder);

        // Picking a tag raises Edited; the owner shows what's left and the counts follow it, leaving out dead ends.
        Assert.True(filter.Add("python"));
        Assert.False(filter.Add("python"));
        Assert.Equal(1, changes);
        filter.Update(items.Where(i => filter.Tags.All(i.Contains)));
        Assert.Equal(["api", "db", "security", "testing"], filter.Choices.Select(c => c.Tag));
        Assert.Equal(new TagChoice("testing", 2), filter.Suggestions[0]);

        // Typing picks an exact match (any case, # optional) or the only tag containing the text, never a guess.
        Assert.False(filter.AddTyped("t"));
        Assert.True(filter.AddTyped("#SECUR"));
        Assert.Equal(["python", "security"], filter.Selected);
        Assert.True(filter.Remove("security"));
        Assert.False(filter.Remove("security"));
        Assert.True(filter.AddTyped("Testing"));
        Assert.Equal(["python", "testing"], filter.Tags);

        Assert.True(filter.Clear());
        Assert.False(filter.Clear());
        Assert.Empty(filter.Selected);
        Assert.Equal(5, changes);
    }

    [AvaloniaFact]
    public void LibraryPage_SearchesAndFiltersByTagAndScope()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new CommonStepLibraryEditor());
        var all = dc.Listings.Count;
        Assert.False(dc.IsFiltered);
        Assert.Contains(dc.Tags.Choices, t => t.Tag == "security");

        Assert.True(dc.Tags.Add("review"));
        Assert.True(dc.IsFiltered);
        Assert.All(dc.Listings, l => Assert.Contains("review", l.Tags));
        Assert.Equal($"{dc.Listings.Count} of {all}", dc.FilterSummary);

        dc.SearchText = "secur";
        Assert.Equal(["code-review", "harden", "review-security"], dc.Listings.Select(l => l.Id).Order());

        // A word only in the contents finds the step, with the line it was found on; not when contents are skipped.
        dc.ClearFilters();
        Assert.Equal(all, dc.Listings.Count);
        dc.SearchText = "including in logs";
        var hit = Assert.Single(dc.Listings, l => l.Id == "review-security");
        Assert.True(hit.HasMatchNote);
        Assert.Contains("including in logs", hit.MatchNote, StringComparison.Ordinal);
        dc.SearchContents = false;
        Assert.DoesNotContain(dc.Listings, l => l.Id == "review-security" && l.HasMatchNote);

        dc.ClearFilters();
        dc.ScopeFilter = nameof(WorkflowScope.Repo);
        Assert.Empty(dc.Listings);
        dc.ScopeFilter = LibraryPageDataContext.AllScopes;
        Assert.Equal(all, dc.Listings.Count);
    }

    [AvaloniaFact]
    public void LibraryPage_SavesDraftTags()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new SkillLibraryEditor());
        dc.New("tidy");
        dc.DraftTags = "Style, big change,style";
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        var file = dc.Save();

        var json = JsonNode.Parse(File.ReadAllText(file!))!;
        Assert.Equal(["big-change", "style"], json["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal("big-change, style", dc.DraftTags);
        Assert.Contains(dc.Tags.Choices, t => t.Tag == "big-change");

        dc.DraftTags = "bad!";
        Assert.False(dc.IsValid);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_PicksAnAgentForAStep()
    {
        using var shell = this.CreateShell();
        shell.Agents.Save(AgentStore.GetPreset("codex"));
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("multi");
        var step = dc.SelectedStep!;
        Assert.Equal([StepDraftDataContext.RunAgentChoice, "claude", "session", "codex"], step.AgentChoices);
        Assert.Equal(StepDraftDataContext.RunAgentChoice, step.Agent);
        Assert.Null(step.ToDefinition().Agent);

        step.Agent = "codex";
        Assert.True(dc.IsDirty);
        var file = dc.Save();
        var json = JsonNode.Parse(File.ReadAllText(file!))!;
        Assert.Equal("codex", (string?)json["steps"]![0]!["agent"]);

        // A step naming an agent that isn't defined here keeps it as a choice rather than losing it.
        var draft = new StepDraftDataContext(new AiStepDefinition { Id = "s", Prompt = "p", Agent = "elsewhere" })
        {
            AgentChoices = [StepDraftDataContext.RunAgentChoice, "claude"],
        };
        Assert.Equal("elsewhere", draft.Agent);
        Assert.Contains("elsewhere", draft.AgentChoices);
        Assert.Equal("elsewhere", draft.ToDefinition().Agent);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_ChoosesAgentsByCapabilityDelegatesAndAddsServers()
    {
        using var shell = this.CreateShell();
        shell.Servers.Save(new McpServerDefinition { Id = "docs", Name = "Docs", Transport = AgentConstants.TransportStdio, Command = "docs-mcp" });
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("by-capability");
        var step = dc.SelectedStep!;
        Assert.Contains(step.AgentPart().CapabilityOptions, o => o.Value == "review");
        Assert.Equal(["docs"], step.AgentPart().ServerOptions.Select(o => o.Value));
        Assert.Equal("Agent", step.AgentPart().AgentHeader);
        Assert.False(step.AgentPart().ShowsSelect);

        // Defaults stay out of the JSON.
        var plain = Assert.IsAssignableFrom<AbstractAgentStepDefinition>(step.ToDefinition());
        Assert.Null(plain.Capabilities);
        Assert.Null(plain.Select);
        Assert.Null(plain.Delegate);
        Assert.Null(plain.DelegateCapabilities);
        Assert.Null(plain.McpServers);

        step.AgentPart().CapabilityOptions.First(o => o.Value == "review").IsSelected = true;
        Assert.Equal("Preferred agent", step.AgentPart().AgentHeader);
        Assert.True(step.AgentPart().ShowsSelect);
        step.AgentPart().Select = AgentConstants.SelectorFirst;
        step.AgentPart().Delegate = true;
        Assert.True(step.AgentPart().ShowsDelegateCapabilities);
        step.AgentPart().DelegateCapabilityOptions.First(o => o.Value == "read-only").IsSelected = true;
        step.AgentPart().ServerOptions[0].IsSelected = true;
        Assert.True(dc.IsDirty);
        Assert.True(dc.ErrorMessage is null, dc.ErrorMessage);
        var file = dc.Save();
        var json = JsonNode.Parse(File.ReadAllText(file!))!["steps"]![0]!;
        Assert.Equal("review", (string?)json["capabilities"]![0]);
        Assert.Equal(AgentConstants.SelectorFirst, (string?)json["select"]);
        Assert.True((bool?)json["delegate"]);
        Assert.Equal("read-only", (string?)json["delegateCapabilities"]![0]);
        Assert.Equal("docs", (string?)json["mcpServers"]![0]);

        // Opening the saved step shows the same picks, and a capability defined elsewhere isn't lost.
        var reopened = new StepDraftDataContext(new AiStepDefinition
        {
            Id = "s", Prompt = "p", Capabilities = ["review", "elsewhere"], McpServers = ["gone"],
        });
        reopened.SetCatalog(["review", "plan"], ["docs"]);
        Assert.Equal(["review", "elsewhere"], reopened.AgentPart().CapabilityOptions.Where(o => o.IsSelected).Select(o => o.Value));
        Assert.Equal(["review", "elsewhere"], Assert.IsAssignableFrom<AbstractAgentStepDefinition>(reopened.ToDefinition()).Capabilities);
        Assert.Equal(["gone"], Assert.IsAssignableFrom<AbstractAgentStepDefinition>(reopened.ToDefinition()).McpServers);

        // A step several agents do names them, so it has no capabilities or selector.
        reopened.AgentPart().UsesSeveralAgents = true;
        Assert.Null(Assert.IsAssignableFrom<AbstractAgentStepDefinition>(reopened.ToDefinition()).Capabilities);
        Assert.False(reopened.AgentPart().ShowsDelegate);
    }

    [AvaloniaFact]
    public void StepDraft_RoundTripsArtifactsOptionalServersAndTickets()
    {
        var step = new AiStepDefinition
        {
            Id = "security", Name = "Security", Prompt = "p", Artifact = "docs/afl/{{ticket.slug}}/SECURITY.md",
            OptionalMcpServers = ["docs"], CreateTickets = true,
        };
        var draft = new StepDraftDataContext(step);
        draft.SetCatalog([], ["docs", "gh"]);

        Assert.True(draft.AgentPart().ShowsCreateTickets);
        var definition = Assert.IsAssignableFrom<AbstractAgentStepDefinition>(draft.ToDefinition());
        Assert.Equal(step.Artifact, definition.Artifact);
        Assert.Equal(["docs"], definition.OptionalMcpServers);
        Assert.Null(definition.McpServers);
        Assert.True(definition.CreateTickets);

        // Turning them off leaves them out, and ticket filing only applies to a step one agent does.
        draft.Artifact = "  ";
        draft.AgentPart().UsesSeveralAgents = true;
        definition = Assert.IsAssignableFrom<AbstractAgentStepDefinition>(draft.ToDefinition());
        Assert.Null(definition.Artifact);
        Assert.Null(definition.CreateTickets);
        Assert.False(draft.AgentPart().ShowsCreateTickets);
    }

    [AvaloniaFact]
    public async Task RunsPage_ChoosesOrMergesTheResultsOfSeveralAgents()
    {
        using var shell = this.CreateShell();
        shell.Agents.Save(AgentStore.GetPreset("codex"));
        this.Executor.Respond = r => new(Afl.Core.Execution.ExecutionStatus.Completed,
            r.Prompt.Contains("merge", StringComparison.OrdinalIgnoreCase) ? "merged plan" : $"plan from {Path.GetFileName(r.LogPath)}",
            r.SessionId);
        var workflow = new WorkflowDefinition
        {
            Id = "compare", Name = "Compare",
            Steps =
            [
                new AiStepDefinition { Id = "plan", Name = "Plan", Prompt = "Plan it", Agents = ["claude", "codex"] },
                new UserStepDefinition { Id = "check", Name = "Check", Instructions = "{{steps.plan.output}}" },
            ],
        }.Chain();

        // Choosing: the shown result, as edited, becomes the step's output.
        var run = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);
        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.True(dc.CanChoose);
        Assert.True(dc.CanSendBack);
        Assert.False(dc.CanApprove);
        Assert.Equal(["claude", "codex"], dc.Candidates.Select(c => c.Agent));
        Assert.Equal("plan from plan.codex.log", dc.Candidates[1].Text);
        Assert.Equal(["claude", "session", "codex"], dc.MergeAgents);
        dc.SelectedCandidate = dc.Candidates[1];
        dc.SelectedCandidate.Text += "\nplus tests";
        Assert.True(dc.SelectedCandidate.IsEdited);
        dc.Choose(dc.SelectedCandidate);
        await shell.Driver.Find(run.Id)!.Completion;
        var saved = new RunStore(this.Paths).GetRequired(run.Id);
        Assert.Equal("plan from plan.codex.log\nplus tests", saved.Steps[0].Output);
        Assert.Equal("plan from plan.codex.log\nplus tests", saved.CurrentStep!.PendingPrompt);

        // Merging: the picked agent merges every result.
        var second = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(second.Id);
        dc.Focus(second.Id);
        Assert.True(dc.CanChoose);
        dc.Merge("codex", "Keep the tests.");
        await shell.Driver.Find(second.Id)!.Completion;
        saved = new RunStore(this.Paths).GetRequired(second.Id);
        Assert.Equal("merged plan", saved.Steps[0].Output);
        Assert.Contains("Merged by codex.", saved.Steps[0].Notes);
    }

    [AvaloniaFact]
    public async Task IssuesPage_StartsARunInTheChosenMode()
    {
        using var shell = this.CreateShell();
        var settings = shell.Settings.Load();
        settings.DefaultRunMode = AgentConstants.RunModeTerminal;
        shell.Settings.Save(settings);
        using var issues = new IssuesPageDataContext(shell);
        issues.SelectedTicket = SampleTicket();
        Assert.Equal([AgentConstants.RunModeTerminal, AgentConstants.RunModeBackground], issues.RunModeChoices().Select(c => c.Value));
        Assert.DoesNotContain(issues.AgentChoices(), a => a.Id == "session");

        var run = issues.Start(issues.WorkflowChoices()[0].Workflow, mode: AgentConstants.RunModeBackground);
        Assert.NotNull(run);
        Assert.Equal(AgentConstants.RunModeBackground, run.Mode);
        await shell.Driver.Find(run.Id)!.Completion;
    }

    [AvaloniaFact]
    public async Task RunsPage_PicksTheAgentWhenTheMainAgentLeavesTheChoiceToYou()
    {
        using var shell = this.CreateShell();
        foreach (var id in new[] { "lint-bot", "review-bot" })
        {
            shell.Agents.Save(new AgentDefinition
            {
                Id = id, Name = id, Kind = AgentConstants.KindCli, Command = "sh", Args = ["-c", "echo {{prompt}}"],
                Capabilities = ["review"],
            });
        }

        this.Executor.Respond = r => r.Prompt.Contains("afl-agent", StringComparison.Ordinal)
            ? new(Afl.Core.Execution.ExecutionStatus.AwaitingSession, null, r.SessionId)
            : new(Afl.Core.Execution.ExecutionStatus.Completed, "reviewed", r.SessionId);
        var workflow = new WorkflowDefinition
        {
            Id = "review", Name = "Review",
            Steps =
            [
                new AiStepDefinition
                {
                    Id = "review", Name = "Review", Prompt = "Review it", Capabilities = ["review"],
                    Select = "ai",
                },
                new UserStepDefinition { Id = "check", Name = "Check", Instructions = "{{steps.review.output}}" },
            ],
        }.Chain();

        var run = shell.Driver.Engine.Create(shell.SelectedRepo!, workflow, SampleTicket());
        await shell.Driver.Engine.DriveAsync(run.Id);
        using var dc = new RunsPageDataContext(shell);
        dc.Focus(run.Id);
        Assert.True(dc.CanPickAgent);
        Assert.Contains("Pick the one to use", dc.Hint);
        Assert.Contains("lint-bot", dc.AgentCandidates);
        Assert.Contains("review-bot", dc.AgentCandidates);
        Assert.Equal(dc.AgentCandidates[0], dc.PickedAgent);
        Assert.EndsWith("mode", dc.ModeText);

        dc.PickedAgent = null;
        dc.UseAgent();
        Assert.Equal("Pick an agent first.", dc.ErrorMessage);
        dc.PickedAgent = "review-bot";
        dc.UseAgent();
        await shell.Driver.Find(run.Id)!.Completion;
        var saved = new RunStore(this.Paths).GetRequired(run.Id);
        Assert.Equal("review-bot", saved.Steps[0].AgentChoice!.AgentId);
        Assert.Equal(AgentConstants.ChosenByUser, saved.Steps[0].AgentChoice!.By);
        Assert.False(dc.CanPickAgent);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_SetsUpAStepForSeveralAgents()
    {
        using var shell = this.CreateShell();
        shell.Agents.Save(AgentStore.GetPreset("codex"));
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("compare");
        var step = dc.SelectedStep!;
        Assert.False(step.AgentPart().ShowsSeveralAgents);
        Assert.Equal(["claude", "session", "codex"], step.AgentPart().AgentOptions.Select(o => o.Value));

        step.AgentPart().UsesSeveralAgents = true;
        Assert.True(step.AgentPart().ShowsSeveralAgents);
        Assert.True(step.AgentPart().ShowsMerge);
        step.AgentPart().AgentOptions[0].IsSelected = true;
        Assert.False(dc.IsValid);
        Assert.Contains(dc.Errors, e => e.Contains("two or more", StringComparison.OrdinalIgnoreCase) || e.Contains("at least 2"));
        step.AgentPart().AgentOptions[2].IsSelected = true;
        step.AgentPart().Combine = StepConstants.CombineMerge;
        step.AgentPart().MergeAgent = "codex";
        step.AgentPart().MergePrompt = "Merge these:\n{{candidates}}";
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));

        var file = dc.Save();
        var json = JsonNode.Parse(File.ReadAllText(file!))!["steps"]![0]!;
        Assert.Equal(["claude", "codex"], json["agents"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("merge", (string?)json["combine"]);
        Assert.Equal("codex", (string?)json["mergeAgent"]);
        Assert.Null(json["agent"]);

        // Reopening it keeps the picks, and the merge settings hide when every result is kept.
        var draft = new StepDraftDataContext(step.ToDefinition()) { AgentChoices = [StepDraftDataContext.RunAgentChoice, "claude", "codex"] };
        Assert.True(draft.AgentPart().UsesSeveralAgents);
        Assert.Equal(["claude", "codex"], draft.AgentPart().AgentOptions.Where(o => o.IsSelected).Select(o => o.Value));
        draft.AgentPart().Combine = StepConstants.CombineAll;
        Assert.False(draft.AgentPart().ShowsMerge);
        draft.AgentPart().UsesSeveralAgents = false;
        Assert.Null(Assert.IsAssignableFrom<AbstractAgentStepDefinition>(draft.ToDefinition()).Agents);
    }

    [AvaloniaFact]
    public void WorkflowBuilder_SetsUpArrowsLoopsAndConditions()
    {
        using var shell = this.CreateShell();
        using var dc = new LibraryPageDataContext(shell, new WorkflowLibraryEditor());
        dc.New("flow");
        dc.Steps[0].Id = "fix";
        dc.AddStep();
        var check = dc.SelectedStep!;
        check.Id = "check";
        Assert.Equal("Flow: ends the run", check.FlowSummary);

        // A loop back to the first step until an AI judge is happy, and an arrow that ends the run on an answer.
        var loop = check.AddArrow("fix")!;
        Assert.Equal(["fix", "check", ArrowConstants.End], loop.ToChoices);
        loop.ConditionMode = ArrowDraftDataContext.UntilCondition;
        loop.Condition.Kind = StepConstants.ConditionAi;
        Assert.True(loop.Condition.UsesQuestion);
        Assert.False(loop.Condition.UsesText);
        loop.Condition.Question = "Do the tests pass?";
        loop.Max = 3;
        loop.MoveOnAtLimit = true;
        var done = check.AddArrow(ArrowConstants.End)!;
        done.ConditionMode = ArrowDraftDataContext.WhenCondition;
        done.Condition.Kind = StepConstants.ConditionAny;
        Assert.True(done.Condition.UsesNested);
        var child = Assert.Single(done.Condition.Children);
        Assert.False(child.UsesNested);
        Assert.Contains(StepConstants.ConditionAny, ConditionDraftDataContext.KindChoices);
        child.Contains = "skip";
        done.Condition.AddChild().Matches = "[unclosed";
        Assert.False(dc.IsValid);
        Assert.Contains(dc.Errors, e => e.Contains("the arrow to end"));
        done.Condition.RemoveChild(done.Condition.Children[1]);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        Assert.Equal("Flow: to fix + end", check.FlowSummary);
        check.FollowsFirst = true;
        Assert.Equal("Flow: to the first of fix, end", check.FlowSummary);

        var file = dc.Save();
        var json = JsonNode.Parse(File.ReadAllText(file!))!["steps"]![1]!;
        Assert.Equal(ArrowConstants.FollowFirst, (string?)json["follow"]);
        var arrows = json["arrows"]!.AsArray();
        Assert.Equal("fix", (string?)arrows[0]!["to"]);
        Assert.Equal("ai", (string?)arrows[0]!["until"]!["kind"]);
        Assert.Null(arrows[0]!["until"]!["contains"]);
        Assert.Equal(3, (int?)arrows[0]!["max"]);
        Assert.Equal(StepConstants.OnLimitContinue, (string?)arrows[0]!["onLimit"]);
        Assert.Equal("end", (string?)arrows[1]!["to"]);
        Assert.Equal("skip", (string?)arrows[1]!["when"]!["conditions"]![0]!["contains"]);

        // Renaming a step takes the arrows to it along, and an arrow with no condition drops it.
        dc.Steps[0].Id = "repair";
        Assert.Equal("repair", loop.To);
        Assert.Equal(["check"], dc.Steps[0].Targets);
        Assert.True(dc.IsValid, string.Join("; ", dc.Errors));
        done.ConditionMode = ArrowDraftDataContext.NoCondition;
        check.RemoveArrow(loop);
        var definition = check.ToDefinition();
        var only = Assert.Single(definition.Arrows!);
        Assert.Null(only.When);
        Assert.Null(only.Until);

        // Compound conditions nest to any depth, and each level is edited like the top one.
        var deep = new ConditionDefinition
        {
            Kind = StepConstants.ConditionAll,
            Conditions = [new ConditionDefinition { Kind = StepConstants.ConditionNot, Conditions = [new ConditionDefinition { Kind = "always" }] }],
        };
        var draft = new StepDraftDataContext(new AiStepDefinition
        {
            Id = "x", Name = "X", Prompt = "p",
            Arrows = [new ArrowDefinition { To = "end", When = deep }],
        });
        var not = Assert.Single(draft.Arrows[0].Condition.Children);
        Assert.True(not.UsesNested);
        var inner = Assert.Single(not.Children);
        inner.Kind = StepConstants.ConditionOutput;
        inner.Contains = "skip";
        not.AddChild().Kind = StepConstants.ConditionAny;
        Assert.Single(not.Children[1].Children);
        not.RemoveChild(not.Children[1]);
        var saved = draft.ToDefinition().Arrows![0].When!;
        Assert.Equal(StepConstants.ConditionAll, saved.Kind);
        Assert.Equal(StepConstants.ConditionNot, saved.Conditions![0].Kind);
        Assert.Equal("skip", saved.Conditions[0].Conditions![0].Contains);
    }
}
