using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Steps;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the generic pieces the feature flow is built from: common skills and how agents get them, work with no
///     ticket, the scratch directory, <c>outputTemplate</c>, <c>approve-final</c>, and the <c>lines</c> (with a pattern
///     and separator) and <c>items</c> child sources.
/// </summary>
public class BuildingBlockTests : AbstractAgentPoolTest
{
    private static SkillDefinition Skill(string id, string content) => new()
    {
        Id = id, Name = id, Description = $"The {id} skill.", Content = content,
    };

    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    [Fact]
    public void Skills_RepoOverridesGlobal_WhichOverridesTheBuiltIn()
    {
        Assert.Equal(WorkflowScope.BuiltIn, new SkillLibrary(this.Paths, this.RepoPath).List().Single(l => l.Skill.Id == "caveman").Scope);

        new SkillLibrary(this.Paths).Save(Skill("caveman", "global"), WorkflowScope.Global);
        Assert.Equal("global", new SkillLibrary(this.Paths, this.RepoPath).GetRequired("caveman").Content);

        new SkillLibrary(this.Paths, this.RepoPath).Save(Skill("caveman", "repo"), WorkflowScope.Repo);
        Assert.Equal("repo", new SkillLibrary(this.Paths, this.RepoPath).GetRequired("caveman").Content);
        Assert.Equal("global", new SkillLibrary(this.Paths).GetRequired("caveman").Content);
    }

    [Fact]
    public void Skills_WorkflowLevelOnesGoToAgentSteps_AheadOfTheirOwn_AndUnknownOnesFailValidation()
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);
        library.Skills.Save(Skill("terse", "Be terse."), WorkflowScope.Global);
        var own = Ai("b", "B");
        own.Skills = ["terse", " "];
        var workflow = Workflow(Ai("a", "A"), own, new UserStepDefinition { Id = "c", Name = "c", });
        workflow.Skills = ["caveman", ""];

        var expanded = new WorkflowComposer(library).Expand(workflow);

        Assert.Equal(["caveman"], Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[0]).Skills);
        Assert.Equal(["caveman", "terse"], Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[1]).Skills);
        Assert.IsType<UserStepDefinition>(expanded.Steps[2]);
        Assert.Null(expanded.Skills);

        own.Skills = ["nope"];
        Assert.Contains(new WorkflowComposer(library).Validate(workflow), p => p.Contains("no skill 'nope'", StringComparison.Ordinal));
    }

    [Fact]
    public void Skills_AStepCanSkipTheWorkflowsOnes_KeepingItsOwn()
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);
        library.Skills.Save(Skill("terse", "Be terse."), WorkflowScope.Global);
        var plain = Ai("b", "B");
        plain.Skills = ["terse"];
        plain.SkipSkills = ["{{params.style}}", " ", "terse"];
        var workflow = Workflow(Ai("a", "A"), plain);
        workflow.Parameters = [new ParameterDefinition { Id = "style", Label = "Style", Default = "caveman" }];
        workflow.Skills = ["{{params.style}}"];

        var expanded = new WorkflowComposer(library).Expand(workflow);

        Assert.Equal(["caveman"], Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[0]).Skills);
        var skipping = Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[1]);
        Assert.Equal(["terse"], skipping.Skills);
        Assert.Null(skipping.SkipSkills);
        Assert.Empty(new WorkflowComposer(library).Validate(workflow));
    }

    [Fact]
    public async Task Skills_ByDefault_GoIntoTheNewConversationsPrompt()
    {
        var step = Ai("a", "Do it");
        step.Skills = ["caveman"];

        var run = await this.Engine.DriveAsync(this.Start("main", step).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var request = Assert.Single(this.Fake("main").Requests);
        Assert.StartsWith("Do it\n\n## Skill: ", request.Prompt);
        Assert.Contains(new SkillLibrary(this.Paths).GetRequired("caveman").Content.Trim(), request.Prompt);
    }

    [Fact]
    public async Task Skills_ForClaude_AreASessionPlugin_ItIsToldToInvoke()
    {
        var capture = Path.Combine(this.Root, "capture");
        Directory.CreateDirectory(capture);
        var stub = Path.Combine(this.Root, "claude-stub");
        File.WriteAllText(stub, $$"""
            #!/bin/sh
            : > '{{capture}}/args'
            prev=""
            for a in "$@"; do
              printf '%s\n' "$a" >> '{{capture}}/args'
              if [ "$prev" = "--plugin-dir" ]; then
                cp "$a/.claude-plugin/plugin.json" '{{capture}}/plugin.json'
                cp "$a/skills/terse/SKILL.md" '{{capture}}/SKILL.md'
                cp "$a/skills/terse/notes/more.md" '{{capture}}/more.md'
              fi
              prev="$a"
            done
            echo '{"type":"result","result":"done","session_id":"s1"}'
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var executor = AgentStore.CreateExecutor(new AgentDefinition { Id = "c", Kind = ClaudeCliExecutor.ExecutorName, Command = stub });
        var skill = Skill("terse", "Be terse.");
        skill.Files = new Dictionary<string, string> { ["notes/more.md"] = "More." };
        var request = new ExecutionRequest
        {
            Prompt = "Do it", WorkingDirectory = this.RepoPath, SessionId = "s1", PermissionMode = StepConstants.DefaultPermissionMode,
            LogPath = Path.Combine(this.Root, "run", "a.log"), Skills = [skill],
        };

        executor.PrepareSkills(request);
        var result = await executor.ExecuteAsync(request, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal("Do it\n\nInvoke the `afl-skills:terse` skill before you start.", request.Prompt);
        Assert.Contains("--plugin-dir", File.ReadAllLines(Path.Combine(capture, "args")));
        Assert.Contains("\"name\": \"afl-skills\"", File.ReadAllText(Path.Combine(capture, "plugin.json")));
        Assert.Equal("---\nname: terse\ndescription: \"The terse skill.\"\n---\n\nBe terse.\n", File.ReadAllText(Path.Combine(capture, "SKILL.md")));
        Assert.Equal("More.", File.ReadAllText(Path.Combine(capture, "more.md")));
    }

    [Fact]
    public void Skills_ImportAndExport_RoundTripTheFrontmatterAndFiles()
    {
        var skill = Skill("terse", "Be terse.\n\nVery.");
        skill.Description = "Says: \"less\".";
        skill.Files = new Dictionary<string, string> { ["notes/more.md"] = "More." };
        var folder = Path.Combine(this.Root, "export", "terse");

        skill.WriteFolder(folder);
        var back = SkillLibrary.Import(folder);

        Assert.Equal((skill.Id, skill.Name, skill.Description, skill.Content), (back.Id, back.Name, back.Description, back.Content));
        Assert.Equal(skill.Files, back.Files);
        Assert.Empty(SkillLibrary.Validate(back));
    }

    [Theory]
    [InlineData("description: >\n  Folded over\n  two lines.\nlicense: MIT\n", "Folded over two lines.")]
    [InlineData("description: >-\n  One.\n\n  Two.\n", "One.\nTwo.")]
    [InlineData("description: |\n  Line one\n    indented\n", "Line one\n  indented")]
    [InlineData("description: Plain value\n  carried on.\n", "Plain value carried on.")]
    [InlineData("description: 'It''s quoted'\n", "It's quoted")]
    public void Skills_Import_ReadsYamlDescriptionForms(string frontmatter, string expected)
    {
        var skill = SkillDefinition.FromSkillMarkdown($"---\nname: terse\n{frontmatter}---\n\nBe terse.\n", "fallback");

        Assert.Equal(("terse", expected, "Be terse."), (skill.Id, skill.Description, skill.Content));
    }

    [Fact]
    public async Task LocalWork_IsANamedTicketKeyedByItsSlug()
    {
        var reference = TicketRef.Parse("local:Dark mode, v2");

        var ticket = await new LocalTicketSource().GetAsync(this.RegisterRepo(), reference);

        Assert.Equal(TicketRef.LocalSource, reference.Source);
        Assert.Equal("local:Dark mode, v2", reference.ToString());
        Assert.Equal((TicketRef.LocalSource, "dark-mode-v2", "Dark mode, v2"), (ticket.Source, ticket.Key, ticket.Title));
    }

    [Fact]
    public async Task Scratch_IsMadeOnFirstUse_IgnoredByGit_AndNamedByTheTemplateVariables()
    {
        var step = Ai("a", "root={{scratch.root}} dir={{scratch.dir}} path={{scratch.path}}");
        step.Artifact = "{{scratch.dir}}/NOTES.md";
        var run = this.Start("main", step);
        var scratch = Path.Combine(this.RepoPath, ".scratch", "acme-app-7");
        Assert.True(Directory.Exists(scratch));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal($"root=.scratch dir=.scratch/acme-app-7 path={scratch}", this.Fake("main").Requests.Single().Prompt);
        Assert.True(File.Exists(Path.Combine(scratch, "NOTES.md")));
        Assert.True(ProcessRunner.Run("git", ["check-ignore", "-q", Path.Combine(scratch, "NOTES.md")], this.RepoPath).Success);
        Assert.Empty(ProcessRunner.Run("git", ["status", "--porcelain"], this.RepoPath).StandardOutput.Trim());
    }

    [Fact]
    public void Scratch_UsesTheSavedSetting()
    {
        var settings = new SettingsStore(this.Paths);
        settings.Save(new AflSettings { ScratchDirectory = "work/afl" });
        Assert.Equal("work/afl", settings.Load().ScratchDirectory);

        var run = this.Start("main", Ai("a", "{{scratch.dir}}"));

        Assert.Equal("work/afl/acme-app-7", run.ScratchDirectory);
        Assert.True(Directory.Exists(Path.Combine(this.RepoPath, "work", "afl", "acme-app-7")));
    }

    [Fact]
    public void Scratch_IsNotMadeForWorkflowsThatDontUseIt()
    {
        this.Start("main", Ai("a", "Do it"));

        Assert.False(Directory.Exists(Path.Combine(this.RepoPath, ".scratch")));
    }

    [Fact]
    public async Task OutputTemplate_TurnsAFormsAnswersIntoTheStepsOutput()
    {
        var form = new UserStepDefinition
        {
            Id = "form", Name = "form",             Inputs = [new StepInput { Id = "goal", Label = "Goal", Type = StepConstants.InputMultiline }],
            OutputTemplate = "# {{ticket.title}}\n\n## Goal\n\n{{inputs.goal}}\n\n{{output}}",
        };
        var run = await this.Engine.DriveAsync(this.Start("main", form).Id);

        run = await this.Engine.CompleteAsync(run.Id, "noted", new Dictionary<string, string> { ["goal"] = "Dark." });

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("# Add dark mode\n\n## Goal\n\nDark.\n\nnoted", run.Steps[0].Output);
    }

    [Fact]
    public async Task ApproveFinal_LoopsByItself_AndAsksOnlyOnTheLastPass()
    {
        var replies = new Queue<string>(["draft", "better", "done"]);
        this.Fake("main").Respond = r => new ExecutionResult(ExecutionStatus.Completed, replies.Dequeue(), r.SessionId);
        var step = Ai("a", "Pass {{loop.iteration}}");
        step.Advance = StepConstants.AdvanceApproveFinal;
        step.Arrows =
        [
            new ArrowDefinition { To = "a", Until = new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "done" }, Max = 5 },
        ];

        var run = await this.Engine.DriveAsync(this.Start("main", step).Id);

        Assert.Equal(StepStatus.AwaitingApproval, run.Steps[0].Status);
        Assert.Equal(["Pass 1", "Pass 2", "Pass 3"], this.Fake("main").Requests.Select(r => r.Prompt.Split("\n\n")[0]));
        run = await this.Engine.ApproveAsync(run.Id);
        Assert.Equal(RunStatus.Done, run.Status);
    }

    [Fact]
    public void ApproveFinal_NeedsARepeat()
    {
        var step = Ai("a", "x");
        step.Advance = StepConstants.AdvanceApproveFinal;

        var problems = StepHandlerRegistryProblems(step);

        Assert.Contains(problems, p => p.Contains(StepConstants.AdvanceApproveFinal, StringComparison.Ordinal));
    }

    [Fact]
    public void ApproveFinal_NeedsItsOwnArrowBack_NotJustAnArrowOut()
    {
        var step = Ai("a", "x");
        step.Advance = StepConstants.AdvanceApproveFinal;
        step.Arrows = [new ArrowDefinition { To = "b" }];
        var workflow = new WorkflowDefinition { Id = "t", Name = "T", Steps = [step, Ai("b", "y")] };

        var problems = new FlowMachine(workflow).Validate();

        Assert.Contains(problems, p => p.Contains("none of its arrows goes back on success", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StepConstants.ChildSourceLines, "^Wave \\d+: (.+)$", ",", "a|b|c")]
    [InlineData(StepConstants.ChildSourceLines, "^Wave 2: (.+)$", null, "c")]
    [InlineData(StepConstants.ChildSourceItems, null, null, "x|y|z")]
    public async Task ChildSources_LinesWithAPatternAndSeparator_AndItems(string kind, string? pattern, string? separator, string expected)
    {
        new WorkflowLibrary(this.Paths).Save(new WorkflowDefinition
        {
            Id = "child", Name = "child", Steps = [Ai("work", "Work")],
        }.Chain(), WorkflowScope.Global);
        var order = new AiStepDefinition { Id = "order", Name = "order", Prompt = "Order" };
        this.Fake("main").Respond = r => new ExecutionResult(ExecutionStatus.Completed,
            r.Prompt == "Order" ? "Plan:\nWave 1: a, b\nWave 2: c\nDone." : "ok", r.SessionId);
        var build = new WorkflowStepDefinition
        {
            Id = "build", Name = "build", Workflow = "child",
            ForEach = kind == StepConstants.ChildSourceItems
                ? new ChildSourceDefinition { Kind = kind, Items = "x, y\nz" }
                : new ChildSourceDefinition { Kind = kind, Step = "order", Pattern = pattern, Separator = separator },
        };

        var run = await this.Engine.DriveAsync(this.Start("main", order, build).Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(expected, string.Join("|", run.Steps[1].ChildRuns.Select(c => c.Item)));
    }

    /// <summary>
    ///     Returns the problems the step's handler finds with it.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The problems.</returns>
    private static List<string> StepHandlerRegistryProblems(AbstractStepDefinition step)
    {
        return StepHandlerRegistry.Instance.Create(step.Type).ValidateAll(step).ToList();
    }
}
