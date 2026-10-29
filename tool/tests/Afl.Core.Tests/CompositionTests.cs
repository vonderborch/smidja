using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Flow;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for composing workflows when a run is created: common steps (<c>use</c>), included workflows
///     (<c>include</c>), parameters and requirements.
/// </summary>
public class CompositionTests : AbstractAgentPoolTest
{
    private static readonly string SnapshotsDirectory = Path.Combine(AppContext.BaseDirectory, "Snapshots");

    private WorkflowLibrary Library => new(this.Paths, this.RepoPath);

    private WorkflowComposer Composer => new(this.Library);

    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    private static WorkflowDefinition Named(string id, params AbstractStepDefinition[] steps)
    {
        return new WorkflowDefinition { Id = id, Name = id, Steps = steps.ToList() }.Chain();
    }

    private void SaveCommon(string id, AbstractStepDefinition step, WorkflowScope scope = WorkflowScope.Global, params ParameterDefinition[] parameters)
    {
        this.Library.Steps.Save(new CommonStepDefinition { Id = id, Name = $"Common {id}", Step = step, Parameters = parameters.ToList() }, scope);
    }

    private static string StepsJson(WorkflowDefinition workflow)
    {
        return JsonSerializer.Serialize(workflow.Steps, JsonConstants.Options);
    }

    [Fact]
    public void Use_FillsParametersAndOverlaysTheUsingStepsFields()
    {
        var common = Ai("check", "Check {{ticket.key}} for {{params.focus}}.");
        common.PermissionMode = "default";
        common.AllowedTools = ["Read", "Grep"];
        this.SaveCommon("check", common, WorkflowScope.Global, new ParameterDefinition { Id = "focus", Default = "bugs" });
        var using1 = new AiStepDefinition { Use = "check", With = new() { ["focus"] = "{{steps.build.output}}" }, AllowedTools = ["Read"] };
        var using2 = new AiStepDefinition { Id = "again", Name = "Again", Use = "check" };

        var expanded = this.Composer.Expand(Named("w", Ai("build", "Build"), using1, using2));

        Assert.Equal(["build", "check", "again"], expanded.Steps.Select(s => s.Id));
        Assert.Equal("Check {{ticket.key}} for {{steps.build.output}}.", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[1]).Prompt);
        Assert.Equal("check", expanded.Steps[1].Name);
        Assert.Equal("default", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[1]).PermissionMode);
        Assert.Equal(["Read"], Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[1]).AllowedTools!);
        Assert.Equal("Check {{ticket.key}} for bugs.", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[2]).Prompt);
        Assert.Equal(["Read", "Grep"], Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[2]).AllowedTools!);
        Assert.All(expanded.Steps, s => Assert.False(s.IsComposed));
    }

    [Fact]
    public void Use_KeepsTheCommonStepsTypeAndInheritsEmptyFields()
    {
        this.SaveCommon("ask-user", new UserStepDefinition { Id = "ask-user", Name = "Ask", Instructions = "Do it" });

        var step = Assert.IsType<UserStepDefinition>(this.Composer.Expand(Named("w", ReadStep("""{ "use": "ask-user", "instructions": "", "name": "Mine" }"""))).Steps[0]);

        Assert.Equal("user", step.Type);
        Assert.Equal("Do it", step.Instructions);
        Assert.Equal("Mine", step.Name);
    }

    [Fact]
    public void Validate_ReportsMissingAndUnknownParametersAndUnknownCommonSteps()
    {
        this.SaveCommon("needs", Ai("needs", "Use {{params.what}}"), WorkflowScope.Global, new ParameterDefinition { Id = "what" });

        var missing = this.Composer.Validate(Named("w", new AiStepDefinition { Use = "needs" }));
        var unknown = this.Composer.Validate(Named("w", new AiStepDefinition { Use = "needs", With = new() { ["what"] = "x", ["extra"] = "y" } }));
        var noSuch = this.Composer.Validate(Named("w", new AiStepDefinition { Use = "nope" }));

        Assert.Contains(missing, p => p.Contains("what"));
        Assert.Contains(unknown, p => p.Contains("extra"));
        Assert.Contains(noSuch, p => p.Contains("no common step 'nope'"));
        Assert.Empty(this.Composer.Validate(Named("w", new AiStepDefinition { Use = "needs", With = new() { ["what"] = "x" } })));
    }

    [Fact]
    public void CommonSteps_InTheRepoOverrideGlobalOnesWhichOverrideBuiltIns()
    {
        Assert.Equal(StepConstants.TypeWorkflow, this.Library.Steps.GetRequired("code-review").Step.Type);
        this.SaveCommon("code-review", Ai("review", "Global review"));
        Assert.Equal("Global review", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(this.Library.Steps.GetRequired("code-review").Step).Prompt);
        this.SaveCommon("code-review", Ai("review", "Repo review"), WorkflowScope.Repo);

        Assert.Equal("Repo review", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(this.Library.Steps.GetRequired("code-review").Step).Prompt);
        Assert.Equal("Global review", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(new WorkflowLibrary(this.Paths).Steps.GetRequired("code-review").Step).Prompt);
        Assert.True(File.Exists(Path.Combine(AflPathsRepo(this.RepoPath), "code-review.json")));
    }

    private static string AflPathsRepo(string repo) => Helpers.AflPaths.RepoStepsDirectory(repo);

    private void WriteWorkflow(WorkflowDefinition workflow)
    {
        var path = this.Library.PathFor(workflow.Id, WorkflowScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(workflow, JsonConstants.Options));
    }

    [Fact]
    public void CommonStep_ThatUsesAnother_IsInvalid()
    {
        var errors = CommonStepLibrary.Validate(new CommonStepDefinition { Id = "x", Name = "X", Step = new AiStepDefinition { Use = "y" } });

        Assert.Contains(errors, e => e.Contains("can't itself"));
    }

    [Fact]
    public void Include_SplicesInTheStepsWithPrefixedIdsAndRewrittenReferences()
    {
        var check = Ai("check", "Check {{steps.scan.output}} for {{params.what}}");
        check.Arrows =
        [
            new ArrowDefinition { To = "scan", When = new ConditionDefinition { Kind = "output", Step = "scan", Contains = "again" } },
            new ArrowDefinition { To = ArrowConstants.End },
        ];
        var security = Named("security", Ai("scan", "Scan"), check);
        security.Parameters = [new ParameterDefinition { Id = "what", Default = "secrets" }];
        this.Library.Save(security, WorkflowScope.Global);
        var after = Ai("after", "Fix {{steps.sec.output}}");
        after.Arrows = [new ArrowDefinition { To = "sec", Until = new ConditionDefinition { Kind = "output", Contains = "ok" }, Max = 2 }];

        var expanded = this.Composer.Expand(Named("w", new AiStepDefinition { Id = "sec", Name = "Security", Include = "security", With = new() { ["what"] = "tokens" } }, after));

        Assert.Equal(["sec-scan", "sec-check", "after"], expanded.Steps.Select(s => s.Id));
        Assert.Equal("Security: scan", expanded.Steps[0].Name);
        Assert.Equal("Check {{steps.sec-scan.output}} for tokens", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[1]).Prompt);
        Assert.Equal(["sec-scan", "after"], expanded.Steps[1].Arrows!.Select(a => a.To));
        Assert.Equal("sec-scan", expanded.Steps[1].Arrows![0].When!.Step);
        Assert.Equal("Fix {{steps.sec-check.output}}", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[2]).Prompt);
        Assert.Equal("sec-scan", expanded.Steps[2].Arrows!.Single().To);
        Assert.Empty(WorkflowLibrary.Validate(expanded));
    }

    [Fact]
    public void RenameSteps_RewritesArrowsWaitForConditionsAndTemplates()
    {
        var a = Ai("a", "Start");
        a.Arrows = [new ArrowDefinition { To = "b" }, new ArrowDefinition { To = "c", When = new ConditionDefinition { Kind = "output", Step = "a", Contains = "c" } }];
        var b = Ai("b", "B");
        b.Arrows = [new ArrowDefinition { To = "d" }];
        var c = Ai("c", "C");
        c.Arrows = [new ArrowDefinition { To = "d" }];
        var d = Ai("d", "Use {{steps.b.output}} and {{steps.c.output?}}");
        d.WaitFor = new AnyWaitRule { Items = [new ArrowWaitRule { Step = "b" }, new ArrowWaitRule { Step = "c" }] };

        var renamed = WorkflowComposer.RenameSteps([a, b, c, d], new Dictionary<string, string> { ["a"] = "a2", ["b"] = "b2", ["c"] = "c2", ["d"] = "d2" });

        Assert.Equal(["a2", "b2", "c2", "d2"], renamed.Select(s => s.Id));
        Assert.Equal(["b2", "c2"], renamed[0].Arrows!.Select(x => x.To));
        Assert.Equal("a2", renamed[0].Arrows![1].When!.Step);
        Assert.Equal(["b2", "c2"], renamed[3].WaitFor!.Sources([]));
        Assert.Equal("Use {{steps.b2.output}} and {{steps.c2.output?}}", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(renamed[3]).Prompt);
        Assert.Equal("d", d.Id);
    }

    [Fact]
    public void Include_PrefixesTheStepsInACustomWaitFor()
    {
        var a = Ai("a", "Start");
        a.Arrows = [new ArrowDefinition { To = "b" }, new ArrowDefinition { To = "c" }];
        var b = Ai("b", "B");
        b.Arrows = [new ArrowDefinition { To = "d" }];
        var c = Ai("c", "C");
        c.Arrows = [new ArrowDefinition { To = "d" }];
        var d = Ai("d", "D");
        d.WaitFor = new AnyWaitRule { Items = [new ArrowWaitRule { Step = "b" }, new ArrowWaitRule { Step = "c" }] };
        this.Library.Save(Named("inner", a, b, c, d), WorkflowScope.Global);

        var expanded = this.Composer.Expand(Named("w", new AiStepDefinition { Id = "in", Include = "inner" }));

        Assert.Equal(["in-b", "in-c"], expanded.Steps.Single(s => s.Id == "in-d").WaitFor!.Sources([]));
        Assert.Empty(WorkflowLibrary.Validate(expanded));
    }

    [Fact]
    public void Include_EndGoesToTheStepAfterTheInclude()
    {
        var gate = Ai("gate", "Gate");
        gate.Follow = ArrowConstants.FollowFirst;
        gate.Arrows =
        [
            new ArrowDefinition { To = ArrowConstants.End, When = new ConditionDefinition { Kind = "output", Contains = "stop" } },
            new ArrowDefinition { To = "more" },
        ];
        this.Library.Save(Named("inner", gate, Ai("more", "More")), WorkflowScope.Global);

        var middle = this.Composer.Expand(Named("w", new AiStepDefinition { Id = "in", Include = "inner" }, Ai("last", "Last")));
        var atEnd = this.Composer.Expand(Named("w", Ai("first", "First"), new AiStepDefinition { Id = "in", Include = "inner" }));

        Assert.Equal(["last", "in-more"], middle.Steps[0].Arrows!.Select(a => a.To));
        Assert.NotNull(middle.Steps[0].Arrows![0].When);
        Assert.Equal(ArrowConstants.End, atEnd.Steps[1].Arrows![0].To);
    }

    [Fact]
    public void Include_StartsAtItsStart_AndEveryEndGoesOnToTheIncludesThen()
    {
        var plan = Ai("plan", "Plan");
        plan.Arrows = [new ArrowDefinition { To = "a" }, new ArrowDefinition { To = "b" }];
        var setup = Ai("setup", "Setup");
        setup.Arrows = [new ArrowDefinition { To = "plan" }];
        var inner = new WorkflowDefinition { Id = "inner", Name = "inner", Start = "setup", Steps = [plan, Ai("a", "A"), Ai("b", "B"), setup] };
        this.Library.Save(inner, WorkflowScope.Global);
        var first = Ai("first", "First");
        first.Arrows = [new ArrowDefinition { To = "in" }];

        var expanded = this.Composer.Expand(new WorkflowDefinition
        {
            Id = "w", Name = "w", Steps = [first, new AiStepDefinition { Id = "in", Include = "inner", Arrows = [new ArrowDefinition { To = "last" }] }, Ai("last", "Last")],
        });

        string[] TargetsOf(string id) => expanded.Steps.Single(s => s.Id == id).Arrows?.Select(a => a.To).ToArray() ?? [];
        Assert.Equal(["in-setup"], TargetsOf("first"));
        Assert.Equal(["in-a", "in-b"], TargetsOf("in-plan"));
        Assert.Equal(["last"], TargetsOf("in-a"));
        Assert.Equal(["last"], TargetsOf("in-b"));
        Assert.Empty(TargetsOf("last"));
        Assert.Empty(WorkflowLibrary.Validate(expanded));
    }

    [Theory]
    [InlineData(FlowTrigger.Succeeded)]
    [InlineData(FlowTrigger.Failed)]
    public async Task Simulate_ExpandsIncludes_AndTellsTheRunInTheWorkflowsOwnSteps(FlowTrigger outcome)
    {
        var gate = Ai("gate", "Gate");
        gate.Follow = ArrowConstants.FollowFirst;
        gate.Arrows =
        [
            new ArrowDefinition { To = ArrowConstants.End, When = new ConditionDefinition { Kind = "output", Contains = "stop" } },
            new ArrowDefinition { To = "more" },
        ];
        this.Library.Save(Named("inner", gate, Ai("more", "More")), WorkflowScope.Global);
        var first = Ai("first", "First");
        first.Arrows = [new ArrowDefinition { To = "in" }];
        var workflow = new WorkflowDefinition
        {
            Id = "w", Name = "w", Steps = [first, new AiStepDefinition { Id = "in", Include = "inner", Arrows = [new ArrowDefinition { To = "last" }] }, Ai("last", "Last")],
        };

        var simulator = new FlowSimulator(new Dictionary<string, FlowTrigger> { ["in"] = outcome });
        var result = await simulator.SimulateAsync(workflow, this.Composer);

        Assert.Contains(FlowMachine.KeyOf("first", "in", ArrowConstants.OnSuccess), result.Fired);
        if (outcome == FlowTrigger.Succeeded)
        {
            Assert.Equal(["first", "in", "last"], result.Visits.Select(v => v.StepId));
            Assert.Contains(FlowMachine.KeyOf("in", "last", ArrowConstants.OnSuccess), result.Fired);
            Assert.False(result.Failed);
        }
        else
        {
            Assert.Equal([("first", FlowTrigger.Succeeded), ("in", FlowTrigger.Failed)], result.Visits);
            Assert.True(result.Failed);
            Assert.Equal("in", result.FailedAt);
        }
    }

    [Fact]
    public void Include_Nests()
    {
        this.Library.Save(Named("leaf", Ai("a", "A")), WorkflowScope.Global);
        this.Library.Save(Named("branch", new AiStepDefinition { Id = "l", Include = "leaf" }, Ai("b", "B {{steps.l.output}}")), WorkflowScope.Global);

        var expanded = this.Composer.Expand(Named("w", new AiStepDefinition { Id = "br", Include = "branch" }));

        Assert.Equal(["br-l-a", "br-b"], expanded.Steps.Select(s => s.Id));
        Assert.Equal("B {{steps.br-l-a.output}}", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(expanded.Steps[1]).Prompt);
    }

    [Fact]
    public void Validate_RejectsCyclesAndMissingWorkflows()
    {
        // Written straight to the files, since saving through the library would reject the cycle.
        this.WriteWorkflow(Named("x", new AiStepDefinition { Id = "y", Include = "y" }));
        this.WriteWorkflow(Named("y", new WorkflowStepDefinition { Id = "x", Workflow = "x" }));

        Assert.Contains(this.Composer.Validate(this.Library.GetRequired("x")), p => p.Contains("refers back to itself"));
        Assert.Contains(this.Composer.Validate(Named("w", new AiStepDefinition { Id = "z", Include = "nope" })), p => p.Contains("nope"));
    }

    [Fact]
    public void Include_StepCanOnlyNameItselfAndGiveWith()
    {
        this.Library.Save(Named("leaf", Ai("a", "A")), WorkflowScope.Global);

        Assert.NotEmpty(this.Composer.Validate(Named("w", new AiStepDefinition { Id = "l", Include = "leaf", Prompt = "no" })));
    }

    [Fact]
    public void Requires_AddsALeadingStepThatReusesOrStartsTheRequiredWorkflow()
    {
        this.Library.Save(Named("prep", Ai("p", "Prep")), WorkflowScope.Global);
        var workflow = Named("w", Ai("a", "A"));
        workflow.Requires = [new WorkflowRequirement { Workflow = "prep" }];

        var expanded = this.Composer.Expand(workflow);

        Assert.Equal(StepConstants.RequirementStepPrefix + "prep", expanded.Steps[0].Id);
        Assert.Equal(StepConstants.TypeWorkflow, expanded.Steps[0].Type);
        Assert.Equal("prep", Assert.IsType<WorkflowStepDefinition>(expanded.Steps[0]).Workflow);
        Assert.True(Assert.IsType<WorkflowStepDefinition>(expanded.Steps[0]).Reuse);
        Assert.Null(expanded.Requires);
    }

    [Fact]
    public void Create_FillsTheWorkflowsParametersAndNeedsTheRequiredOnes()
    {
        var workflow = Named("w", Ai("a", "Work on {{params.area}} ({{params.depth}})"));
        workflow.Parameters = [new ParameterDefinition { Id = "area" }, new ParameterDefinition { Id = "depth", Default = "shallow" }];
        var repo = this.RegisterRepo();

        var ex = Assert.Throws<AflException>(() => this.Engine.Create(repo, workflow, SampleTicket(), "main"));
        var run = this.Engine.Create(repo, workflow, SampleTicket(), "main", parameters: new Dictionary<string, string> { ["area"] = "auth" });

        Assert.Contains("--param area=", ex.Message);
        Assert.Equal("w", run.WorkflowId);
        Assert.Equal("Work on auth (shallow)", Assert.IsAssignableFrom<AbstractAgentStepDefinition>(run.Workflow.Steps[0]).Prompt);
        Assert.Null(run.Workflow.Parameters);
    }

    [Theory]
    [InlineData("hotfix")]
    public void BuiltIns_ThatUseCommonSteps_ExpandToTheStepsTheyHadBefore(string id)
    {
        var before = JsonSerializer.Deserialize<WorkflowDefinition>(File.ReadAllText(Path.Combine(SnapshotsDirectory, $"{id}.expanded.json")), JsonConstants.Options)!;

        var after = this.Composer.Expand(new WorkflowLibrary(this.Paths).GetRequired(id));

        Assert.Equal(StepsJson(before.Chain()), StepsJson(after));
    }

    [Fact]
    public void BuiltIns_AreValid()
    {
        var library = new WorkflowLibrary(this.Paths);
        foreach (var listing in library.List())
        {
            Assert.Empty(new WorkflowComposer(library).Validate(listing.Workflow));
        }

        foreach (var listing in library.Steps.List())
        {
            Assert.Empty(CommonStepLibrary.Validate(listing.Step));
        }
    }
}
