using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for scanning a repo, recommending skills for it, and the skills picked for a run.
/// </summary>
public class SkillRecommendTests : AbstractAgentPoolTest
{
    private void Write(string relative, string content = "")
    {
        var path = Path.Combine(this.RepoPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private SkillRecommender BuiltIns() => new(new WorkflowLibrary(this.Paths, this.RepoPath).Skills.List().Select(l => l.Skill));

    [Fact]
    public async Task Scan_ListsGitFiles_LeavingOutIgnoredOnes()
    {
        this.Write(".gitignore", "secret/\n");
        this.Write("src/App.csproj");
        this.Write("secret/keys.txt");

        var scan = await RepoScan.OfAsync(this.RepoPath);

        Assert.Contains("src/App.csproj", scan.Files);
        Assert.DoesNotContain("secret/keys.txt", scan.Files);
        Assert.False(scan.IsEmpty);
    }

    [Fact]
    public async Task Scan_OutsideGit_SkipsBuildAndDependencyFolders()
    {
        var plain = Path.Combine(this.Root, "plain");
        Directory.CreateDirectory(Path.Combine(plain, "node_modules", "x"));
        Directory.CreateDirectory(Path.Combine(plain, "bin"));
        Directory.CreateDirectory(Path.Combine(plain, "app"));
        File.WriteAllText(Path.Combine(plain, "node_modules", "x", "index.js"), "");
        File.WriteAllText(Path.Combine(plain, "bin", "App.dll"), "");
        File.WriteAllText(Path.Combine(plain, "app", "main.py"), "");

        var scan = await RepoScan.OfAsync(plain);

        Assert.Equal(["app/main.py"], scan.Files);
    }

    [Fact]
    public async Task Scan_OfOnlyDocsAndDotfiles_IsEmpty()
    {
        this.Write("README.md", "# Hello");
        this.Write("LICENSE");
        this.Write(".editorconfig");
        this.Write("docs/notes.txt");

        Assert.True((await RepoScan.OfAsync(this.RepoPath)).IsEmpty);
        Assert.True(new RepoScan(this.RepoPath, []).IsEmpty);
    }

    [Fact]
    public async Task Recommender_ACSharpRepo_GetsTheCSharpSkillsWithWhatMatched()
    {
        this.Write("Afl.slnx");
        this.Write("src/Afl.Core/Afl.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var scan = await RepoScan.OfAsync(this.RepoPath);
        var recommender = this.BuiltIns();

        var recommended = recommender.Recommend(scan, recommender.StackFor(scan, null));

        var style = Assert.Single(recommended, r => r.Skill.Id == "csharp-style");
        Assert.Equal("C# project: found Afl.Core.csproj, Afl.slnx", style.Reason);
        Assert.DoesNotContain(recommended, r => r.Skill.Id is "benchmarkdotnet" or "python-env" or "caveman");
        Assert.Equal(["csharp"], recommender.StackFor(scan, null)!.Languages);
    }

    [Fact]
    public async Task Recommender_Contains_MatchesFastApiOnlyWhenItIsADependency()
    {
        this.Write("pyproject.toml", "[project]\nname = \"svc\"\ndependencies = [\"httpx\"]\n");
        var recommender = this.BuiltIns();
        var scan = await RepoScan.OfAsync(this.RepoPath);
        var without = recommender.Recommend(scan, recommender.StackFor(scan, null));
        Assert.Contains(without, r => r.Skill.Id == "python-env");
        Assert.DoesNotContain(without, r => r.Skill.Id == "fastapi-service");

        this.Write("pyproject.toml", "[project]\nname = \"svc\"\ndependencies = [\"FastAPI>=0.110\"]\n");
        scan = await RepoScan.OfAsync(this.RepoPath);
        var with = recommender.Recommend(scan, recommender.StackFor(scan, null));
        Assert.Contains(with, r => r.Skill.Id == "fastapi-service" && r.Reason.StartsWith("FastAPI service: found pyproject.toml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Advice_ForAnEmptyRepo_AsksForTheStack_ThenUsesTheSavedOne()
    {
        var repo = this.RegisterRepo();
        this.Write("README.md", "# New thing");
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);

        var first = await SkillRecommender.AdviseAsync(library, repo, library.GetRequired("feature-flow"));
        Assert.True(first.NeedsStack);
        Assert.Empty(first.Recommended);
        Assert.Equal(["caveman"], first.Defaults);
        Assert.Contains("python", first.Choices.Languages);
        Assert.Contains("fastapi", first.Choices.Frameworks);

        var registry = new RepoRegistry(this.Paths);
        repo = registry.SetStack(repo.Id, RepoStack.Of([" Python "], ["FastAPI"]));
        Assert.Equal(["python"], repo.Stack!.Languages);

        var second = await SkillRecommender.AdviseAsync(library, registry.GetRequired(repo.Id), null);
        Assert.False(second.NeedsStack);
        Assert.Contains(second.Recommended, r => r.Skill.Id == "python-env" && r.Reason == "Python project: the project uses python");
        Assert.Contains(second.Recommended, r => r.Skill.Id == "fastapi-service");
        Assert.DoesNotContain(second.Recommended, r => r.Skill.Id == "python-raw-sql");
    }

    [Fact]
    public void Validate_RejectsABadRecommendBlock()
    {
        var skill = new SkillDefinition
        {
            Id = "x", Name = "X", Description = "d", Content = "c",
            Recommend = new SkillRecommendRule { Files = [new SkillFileRule { Glob = "../*.cs" }], Languages = ["C Sharp"] },
        };

        var errors = SkillLibrary.Validate(skill);

        Assert.Contains(errors, e => e.Contains("relative to the repo root", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'C Sharp'", StringComparison.Ordinal));
        Assert.Contains(SkillLibrary.Validate(new SkillDefinition
        {
            Id = "x", Name = "X", Description = "d", Content = "c", Recommend = new SkillRecommendRule(),
        }), e => e.Contains("leave recommend out", StringComparison.Ordinal));
    }

    [Fact]
    public void Pick_StartsFromTheDefaultsOrTheReplacement_ThenAddsAndDrops()
    {
        var workflow = new WorkflowDefinition { Id = "w", Name = "w", Skills = ["caveman", " test-first ", ""] };

        Assert.Null(SkillRecommender.Pick(workflow, null, [], []));
        Assert.Equal(["test-first", "csharp-style"], SkillRecommender.Pick(workflow, null, ["csharp-style", "test-first"], ["caveman"]));
        Assert.Equal(["a", "b"], SkillRecommender.Pick(workflow, ["a", " ", "b"], [], []));
        Assert.Empty(SkillRecommender.Pick(workflow, "".Split(','), [], [])!);
    }

    [Fact]
    public async Task RunSkills_ReplaceTheWorkflowsOnes_ApplySkipSkills_AndReachChildRuns()
    {
        var library = new WorkflowLibrary(this.Paths);
        library.Skills.Save(new SkillDefinition { Id = "terse", Name = "Terse", Description = "Be terse.", Content = "Be terse." }, WorkflowScope.Global);
        library.Save(new WorkflowDefinition
        {
            Id = "child", Name = "child", Skills = ["caveman"], Steps = [new AiStepDefinition { Id = "work", Name = "work", Prompt = "Work" }],
        }.Chain(), WorkflowScope.Global);
        var write = new AiStepDefinition { Id = "write", Name = "write", Prompt = "Write", SkipSkills = ["terse"] };
        var workflow = new WorkflowDefinition
        {
            Id = "parent", Name = "parent", Skills = ["caveman"],
            Steps = [new AiStepDefinition { Id = "plan", Name = "plan", Prompt = "Plan" }, write,
                new WorkflowStepDefinition { Id = "build", Name = "build", Workflow = "child" }],
        }.Chain();

        var run = this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main", skills: ["terse", "test-first"]);
        Assert.Equal(["terse", "test-first"], run.Skills);
        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var requests = this.Fake("main").Requests;
        Assert.Equal(["terse", "test-first"], requests[0].Skills.Select(s => s.Id));
        Assert.Equal(["test-first"], requests[1].Skills.Select(s => s.Id));
        Assert.Equal(["terse", "test-first"], requests[2].Skills.Select(s => s.Id));
        var child = new RunStore(this.Paths).GetRequired(run.Steps[2].ChildRuns.Single().RunId);
        Assert.Equal(["terse", "test-first"], child.Skills);

        var plain = this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main");
        Assert.Null(plain.Skills);
        Assert.Equal(["caveman"], Assert.IsType<AiStepDefinition>(plain.Workflow.Steps[0]).Skills);
        Assert.Throws<AflException>(() => this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main", skills: ["nope"]));
    }
}
