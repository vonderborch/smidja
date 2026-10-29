using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Flow;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;
using Afl.Core.Workspaces;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for child runs' workspaces: git worktrees per child, merging back, conflicts, cancelling, inheriting the
///     working copy, and single-thread runs.
/// </summary>
public class WorkspaceTests : AbstractAgentPoolTest
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkspaceTests" /> class: the repo gets a first commit, and the
    ///     <c>main</c> agent writes <c>&lt;item&gt;.txt</c> (or the file a prompt names) in its working directory.
    /// </summary>
    public WorkspaceTests()
    {
        File.WriteAllText(Path.Combine(this.RepoPath, "README.md"), "hello\n");
        this.Git(this.RepoPath, "add", "-A");
        this.Git(this.RepoPath, "commit", "-q", "-m", "first");
        this.Fake("main").Respond = r =>
        {
            lock (this.Seen)
            {
                // Only the step's own prompt counts: gated steps also get the open-questions instructions after it.
                this.Seen.Add((r.Prompt.Split("\n\n")[0], r.WorkingDirectory, Directory.EnumerateFiles(r.WorkingDirectory, "*.txt").Select(f => Path.GetFileName(f)).ToList()));
            }

            if (r.Prompt.StartsWith("Write ", StringComparison.Ordinal))
            {
                var parts = r.Prompt.Split("\n\n")[0]["Write ".Length..].Split(' ', 2);
                File.WriteAllText(Path.Combine(r.WorkingDirectory, parts[0]), parts.Length > 1 ? parts[1] : parts[0]);
            }

            return new ExecutionResult(ExecutionStatus.Completed, $"did: {r.Prompt}", r.SessionId);
        };
    }

    /// <summary>
    ///     Gets each request the <c>main</c> agent saw: its prompt, working directory and the text files there.
    /// </summary>
    private List<(string Prompt, string Directory, List<string> Files)> Seen { get; } = [];

    private static AiStepDefinition Ai(string id, string prompt) => new() { Id = id, Name = id, Prompt = prompt };

    private static AbstractStepDefinition Build(string items, string? workspace = StepConstants.WorkspaceWorktree, bool parallel = true)
    {
        return new WorkflowStepDefinition
        {
            Id = "build", Name = "build", Workflow = "child", Workspace = workspace,
            ChildrenTogether = parallel, ForEach = new ChildSourceDefinition { Kind = StepConstants.ChildSourceItems, Items = items },
            With = new Dictionary<string, string> { ["item"] = "{{item}}" },
        };
    }

    private void SaveChild(string prompt = "Write {{params.item}}.txt", string? advance = null, params AbstractStepDefinition[] more)
    {
        var work = Ai("work", prompt);
        work.Advance = advance;
        new WorkflowLibrary(this.Paths).Save(new WorkflowDefinition
        {
            Id = "child", Name = "child", Steps = [work, .. more], Parameters = [new ParameterDefinition { Id = "item", Default = "" }],
        }.Chain(), WorkflowScope.Global);
    }

    private ProcessResult Git(string directory, params string[] arguments)
    {
        return ProcessRunner.Run("git", ["-c", "user.name=t", "-c", "user.email=t@t", .. arguments], directory);
    }

    private IReadOnlyList<string> Branches() =>
        this.Git(this.RepoPath, "branch", "--format=%(refname:short)").StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private Run Get(string id) => new RunStore(this.Paths).GetRequired(id);

    [Fact]
    public async Task Worktree_GivesEachChildItsOwnCheckout_AndMergesItsWorkBack()
    {
        this.SaveChild();
        var run = this.Start("main", Build("a, b"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(2, this.Seen.Count);
        Assert.All(this.Seen, s => Assert.StartsWith(this.Paths.WorktreesDirectory, s.Directory));
        Assert.NotEqual(this.Seen[0].Directory, this.Seen[1].Directory);
        // Parallel children start from the same commit, so neither sees the other's work.
        Assert.All(this.Seen, s => Assert.Empty(s.Files));
        Assert.True(File.Exists(Path.Combine(this.RepoPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(this.RepoPath, "b.txt")));
        Assert.Contains("Merge a:", this.Git(this.RepoPath, "log", "--format=%s").StandardOutput);
        Assert.DoesNotContain(this.Branches(), b => b.StartsWith(WorktreeWorkspace.BranchPrefix, StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(this.Paths.WorktreesDirectory));
        Assert.All(run.Steps[0].ChildRuns, c => Assert.True(c.WorkspaceSettled));
        var child = this.Get(run.Steps[0].ChildRuns[0].RunId);
        Assert.Equal(StepConstants.WorkspaceWorktree, child.Workspace);
        Assert.Equal("afl/acme-app-7/a", child.WorkspaceBranch);
    }

    [Fact]
    public async Task Worktree_SerialChildren_EachStartFromTheWorkMergedBeforeThem()
    {
        this.SaveChild();
        var run = this.Start("main", Build("a, b", parallel: false));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Empty(this.Seen[0].Files);
        Assert.Equal(["a.txt"], this.Seen[1].Files);
    }

    [Fact]
    public async Task SingleThread_DrivesParallelChildrenOneAtATime_AndChildrenInheritIt()
    {
        this.SaveChild();
        var run = this.Engine.Create(this.RegisterRepo(), Workflow(Build("a, b")), SampleTicket(), "main", maxParallel: 1);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal(["a.txt"], this.Seen[1].Files);
        Assert.All(run.Steps[0].ChildRuns, c => Assert.Equal(1, this.Get(c.RunId).MaxParallel));
    }

    [Fact]
    public async Task Worktree_WithoutMergeBack_KeepsTheBranchUnmerged()
    {
        this.SaveChild();
        var build = Build("spike");
        build.MergeBack = false;
        var run = this.Start("main", build);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.False(File.Exists(Path.Combine(this.RepoPath, "spike.txt")));
        Assert.Contains("afl/acme-app-7/spike", this.Branches());
        Assert.Contains("spike.txt", this.Git(this.RepoPath, "show", "--stat", "afl/acme-app-7/spike").StandardOutput);
        Assert.Contains("unmerged", run.Steps[0].ChildRuns[0].WorkspaceNote);
    }

    [Fact]
    public async Task Worktree_Conflict_FailsTheStep_AndRetryMergesOnceAPersonHasResolvedIt()
    {
        this.SaveChild("Write same.txt {{params.item}}");
        var run = this.Start("main", Build("a, b"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        var error = run.Steps[0].Error!;
        Assert.Contains("afl/acme-app-7/b", error);
        Assert.Contains("git merge afl/acme-app-7/b", error);
        Assert.Equal("a", File.ReadAllText(Path.Combine(this.RepoPath, "same.txt")));
        Assert.Contains("afl/acme-app-7/b", this.Branches());

        // The person resolves it by taking b's side, then retries.
        this.Git(this.RepoPath, "merge", "-q", "-X", "theirs", "-m", "resolved", "afl/acme-app-7/b");
        run = await this.Engine.RetryAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("b", File.ReadAllText(Path.Combine(this.RepoPath, "same.txt")));
        Assert.DoesNotContain("afl/acme-app-7/b", this.Branches());
        Assert.Empty(Directory.EnumerateDirectories(this.Paths.WorktreesDirectory));
    }

    [Fact]
    public async Task Cancel_DiscardsAChildsWorktree_AndKeepsItsBranch()
    {
        this.SaveChild(advance: StepConstants.AdvanceApprove);
        var run = this.Start("main", Build("a"));

        run = await this.Engine.DriveAsync(run.Id);
        var child = this.Get(run.Steps[0].ChildRuns[0].RunId);
        Assert.True(Directory.Exists(child.WorkingPath));

        this.Engine.Cancel(run.Id);

        Assert.False(Directory.Exists(child.WorkingPath));
        Assert.Contains(child.WorkspaceBranch, this.Branches());
    }

    [Fact]
    public async Task CancelAndDelete_DontDeadlockOnAThreadThatIsBlockedOnThem()
    {
        this.SaveChild(advance: StepConstants.AdvanceApprove);
        var run = this.Start("main", Build("a"));
        run = await this.Engine.DriveAsync(run.Id);
        var child = this.Get(run.Steps[0].ChildRuns[0].RunId);
        Exception? failure = null;

        // Like the UI thread: work posted back to it never runs while it waits.
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedSynchronizationContext());
            try
            {
                this.Engine.Cancel(run.Id);
                this.Engine.Delete(run.Id);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Cancel and delete deadlocked.");
        Assert.Null(failure);
        Assert.False(Directory.Exists(child.WorkingPath));
        Assert.Null(new RunStore(this.Paths).Get(child.Id));
    }

    [Fact]
    public async Task GrandchildRuns_InheritTheWorktree_AndAreGivenTheScratchDirectory()
    {
        new WorkflowLibrary(this.Paths).Save(new WorkflowDefinition
        {
            Id = "review", Name = "review", Steps = [Ai("look", "Look at {{scratch.dir}}")],
        }.Chain(), WorkflowScope.Global);
        this.SaveChild(more: new WorkflowStepDefinition { Id = "reviews", Name = "reviews", Workflow = "review" });
        var run = this.Start("main", Build("a"));

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var work = this.Seen.Single(s => s.Prompt.StartsWith("Write", StringComparison.Ordinal));
        var look = this.Seen.Single(s => s.Prompt.StartsWith("Look", StringComparison.Ordinal));
        Assert.Equal(work.Directory, look.Directory);
        var request = this.Fake("main").Requests.Single(r => r.Prompt.StartsWith("Look", StringComparison.Ordinal));
        var scratch = Assert.Single(request.AdditionalDirectories);
        Assert.Equal(Path.GetFullPath(Path.Combine(this.RepoPath, ".scratch", "acme-app-7")), scratch);
    }

    [Theory]
    [InlineData("elsewhere", null, "unknown workspace 'elsewhere'")]
    [InlineData(null, false, "'mergeBack' needs the children to have a workspace of their own")]
    public void Validate_ChecksTheWorkspaceFields(string? workspace, bool? mergeBack, string expected)
    {
        var step = Build("a", workspace);
        step.MergeBack = mergeBack;

        var problems = new WorkflowStepHandler().ValidateAll(step).ToList();

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsWorkspaceFieldsOnOtherSteps()
    {
        var step = Ai("x", "y");
        step.Workspace = StepConstants.WorkspaceWorktree;

        var problems = StepHandlerRegistry.Instance.Create(StepConstants.TypeAi).ValidateAll(step).ToList();

        Assert.Contains(problems, p => p.Contains("remove 'workspace'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("b", "a", false)]
    [InlineData("b", "end", false)]
    [InlineData("b", "c", true)]
    public void Validate_AWorkspaceNeedsArrowsThatSplitTheRun(string first, string second, bool splits)
    {
        var a = Ai("a", "x");
        a.Workspace = StepConstants.WorkspaceWorktree;
        a.Arrows = [new ArrowDefinition { To = first }, new ArrowDefinition { To = second, When = second == "a" ? new ConditionDefinition { Kind = StepConstants.ConditionOutput, Contains = "again" } : null }];
        var workflow = new WorkflowDefinition { Id = "t", Name = "T", Steps = [a, Ai("b", "y"), Ai("c", "z")] };

        var problems = new FlowMachine(workflow).Validate();

        Assert.Equal(splits, !problems.Any(p => p.Contains("don't split the run", StringComparison.Ordinal)));
    }

    [Fact]
    public void Validate_ASuccessAndAFailureArrowDontSplitTheRun()
    {
        var a = Ai("a", "x");
        a.Workspace = StepConstants.WorkspaceWorktree;
        a.Arrows = [new ArrowDefinition { To = "b" }, new ArrowDefinition { To = "c", On = ArrowConstants.OnFailure }];
        var workflow = new WorkflowDefinition { Id = "t", Name = "T", Steps = [a, Ai("b", "y"), Ai("c", "z")] };

        Assert.Contains(new FlowMachine(workflow).Validate(), p => p.Contains("don't split the run", StringComparison.Ordinal));
    }

    private Run StartFork(string a, string b, AbstractWaitRule? waitFor = null, string? advanceB = null)
    {
        var plan = Ai("plan", "Plan");
        plan.Arrows = [new ArrowDefinition { To = "a" }, new ArrowDefinition { To = "b" }];
        plan.Workspace = StepConstants.WorkspaceWorktree;
        var first = Ai("a", a);
        first.Arrows = [new ArrowDefinition { To = "review" }];
        var second = Ai("b", b);
        second.Arrows = [new ArrowDefinition { To = "review" }];
        second.Advance = advanceB;
        var review = Ai("review", "Review");
        review.WaitFor = waitFor;
        var workflow = new WorkflowDefinition { Id = "test", Name = "Test", Steps = [plan, first, second, review] };
        return this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main");
    }

    [Fact]
    public async Task ForkWorktree_GivesEachBranchItsOwnCheckout_AndMergesBothAtTheJoin()
    {
        var run = this.StartFork("Write a.txt", "Write b.txt");

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var a = this.Seen.Single(s => s.Prompt == "Write a.txt");
        var b = this.Seen.Single(s => s.Prompt == "Write b.txt");
        Assert.StartsWith(this.Paths.WorktreesDirectory, a.Directory);
        Assert.NotEqual(a.Directory, b.Directory);
        Assert.Empty(b.Files);
        Assert.Equal(this.RepoPath, this.Seen.Single(s => s.Prompt == "Review").Directory);
        Assert.True(File.Exists(Path.Combine(this.RepoPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(this.RepoPath, "b.txt")));
        Assert.All(run.StepOf("plan")!.Paths!, c => Assert.True(c.WorkspaceSettled));
        Assert.Empty(Directory.EnumerateDirectories(this.Paths.WorktreesDirectory));
    }

    private static AiStepDefinition Ai(string id, string prompt, params string[] to)
    {
        var step = Ai(id, prompt);
        step.Arrows = to.Length == 0 ? null : [.. to.Select(t => new ArrowDefinition { To = t })];
        return step;
    }

    [Fact]
    public async Task ForkWorktree_AJoinInsideALane_StaysInTheLanesCheckout()
    {
        var plan = Ai("plan", "Plan", "a", "b");
        plan.Workspace = StepConstants.WorkspaceWorktree;
        AbstractStepDefinition[] steps =
        [
            plan, Ai("a", "Write a.txt", "a1", "a2"), Ai("a1", "Write a1.txt", "a3"), Ai("a2", "Write a2.txt", "a3"),
            Ai("a3", "Check a", "review"), Ai("b", "Write b.txt", "review"), Ai("review", "Review"),
        ];
        var workflow = new WorkflowDefinition { Id = "test", Name = "Test", Steps = [.. steps] };
        var run = this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main");

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var lane = this.Seen.Single(s => s.Prompt == "Write a.txt").Directory;
        var check = this.Seen.Single(s => s.Prompt == "Check a");
        Assert.Equal(lane, check.Directory);
        Assert.Equal(["a.txt", "a1.txt", "a2.txt"], check.Files.Order());
        Assert.Equal(this.RepoPath, this.Seen.Single(s => s.Prompt == "Review").Directory);
        Assert.All(["a.txt", "a1.txt", "a2.txt", "b.txt"], f => Assert.True(File.Exists(Path.Combine(this.RepoPath, f))));
        Assert.Empty(Directory.EnumerateDirectories(this.Paths.WorktreesDirectory));
    }

    [Fact]
    public async Task ForkWorktree_ALaneSplitInsideALane_BranchesOffItAndMergesBackIntoIt()
    {
        var plan = Ai("plan", "Plan", "a", "b");
        plan.Workspace = StepConstants.WorkspaceWorktree;
        var a = Ai("a", "Write a.txt", "a1", "a2");
        a.Workspace = StepConstants.WorkspaceWorktree;
        AbstractStepDefinition[] steps =
        [
            plan, a, Ai("a1", "Write a1.txt", "a3"), Ai("a2", "Write a2.txt", "a3"),
            Ai("a3", "Check a", "review"), Ai("b", "Write b.txt", "review"), Ai("review", "Review"),
        ];
        var workflow = new WorkflowDefinition { Id = "test", Name = "Test", Steps = [.. steps] };
        var run = this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket(), "main");

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var lane = this.Seen.Single(s => s.Prompt == "Write a.txt").Directory;
        var a1 = this.Seen.Single(s => s.Prompt == "Write a1.txt");
        var a2 = this.Seen.Single(s => s.Prompt == "Write a2.txt");
        Assert.NotEqual(lane, a1.Directory);
        Assert.NotEqual(a1.Directory, a2.Directory);
        Assert.Equal(["a.txt"], a1.Files);
        Assert.Equal(["a.txt"], a2.Files);
        var check = this.Seen.Single(s => s.Prompt == "Check a");
        Assert.Equal(lane, check.Directory);
        Assert.Equal(["a.txt", "a1.txt", "a2.txt"], check.Files.Order());
        Assert.Equal(this.RepoPath, this.Seen.Single(s => s.Prompt == "Review").Directory);
        Assert.All(["a.txt", "a1.txt", "a2.txt", "b.txt"], f => Assert.True(File.Exists(Path.Combine(this.RepoPath, f))));
        Assert.All(run.StepOf("plan")!.Paths!, c => Assert.NotEqual(false, c.WorkspaceSettled));
        Assert.Empty(Directory.EnumerateDirectories(this.Paths.WorktreesDirectory));
    }

    [Fact]
    public async Task ForkWorktree_JoinAny_MergesOnlyTheWinner()
    {
        var run = this.StartFork("Write a.txt", "Write b.txt", new AnyWaitRule(), StepConstants.AdvanceApprove);

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.True(File.Exists(Path.Combine(this.RepoPath, "a.txt")));
        Assert.False(File.Exists(Path.Combine(this.RepoPath, "b.txt")));
        Assert.Empty(Directory.EnumerateDirectories(this.Paths.WorktreesDirectory));
    }

    [Fact]
    public async Task ForkWorktree_ReplayingAPathsStep_RunsItInAWorktreeAgain()
    {
        var run = this.StartFork("Write a.txt", "Write b.txt", advanceB: StepConstants.AdvanceApprove);
        run = await this.Engine.DriveAsync(run.Id);
        Assert.Equal(RunStatus.Waiting, run.Status);

        run = await this.Engine.ReplayAsync(run.Id, "a");
        run = await this.Engine.ApproveAsync(run.Id);

        Assert.Equal(RunStatus.Done, run.Status);
        var runs = this.Seen.Where(s => s.Prompt == "Write a.txt").ToList();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.StartsWith(this.Paths.WorktreesDirectory, r.Directory));
        Assert.True(File.Exists(Path.Combine(this.RepoPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(this.RepoPath, "b.txt")));
        Assert.Empty(Directory.EnumerateDirectories(this.Paths.WorktreesDirectory));
    }

    [Fact]
    public async Task ForkWorktree_Conflict_FailsTheFork()
    {
        var run = this.StartFork("Write same.txt a", "Write same.txt b");

        run = await this.Engine.DriveAsync(run.Id);

        Assert.Equal(RunStatus.Failed, run.Status);
        var fork = run.StepOf("plan")!;
        Assert.Equal(StepStatus.Failed, fork.Status);
        Assert.Contains("Path 'b'", fork.Error);
        Assert.Contains("git merge", fork.Error);
        Assert.Equal("a", File.ReadAllText(Path.Combine(this.RepoPath, "same.txt")));
    }
}

/// <summary>
///     A synchronization context whose posted work never runs, as on a UI thread that is blocked waiting.
/// </summary>
internal sealed class BlockedSynchronizationContext : SynchronizationContext
{
    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
    }
}
