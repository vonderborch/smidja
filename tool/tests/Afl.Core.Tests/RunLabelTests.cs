using Afl.Core.Exceptions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for what runs are labelled with: their own names, tags and the folders they're filed in.
/// </summary>
public class RunLabelTests : AbstractAflTest
{
    private RunStore Store => new(this.Paths);

    private RunEngine Engine => new(this.Store, new FakeStepExecutor());

    [Fact]
    public void NormalizeTags_TrimsDropsHashesAndCaseDuplicates()
    {
        Assert.Equal(["billing", "Urgent"], RunLabels.NormalizeTags([" #billing ", "", "Urgent", "BILLING", "#urgent"]));
        Assert.Equal(AflErrorKind.InvalidInput, Assert.Throws<AflException>(() => RunLabels.NormalizeTags(["a,b"])).Kind);
    }

    [Theory]
    [InlineData(" billing / q4 / ", "billing/q4")]
    [InlineData("billing\\q4", "billing/q4")]
    [InlineData(" / ", null)]
    [InlineData(null, null)]
    public void NormalizeFolder_TidiesPaths(string? folder, string? expected)
    {
        Assert.Equal(expected, RunLabels.NormalizeFolder(folder));
    }

    [Fact]
    public void FolderPaths_AreRebasedAndListedWithTheirParents()
    {
        Assert.Throws<AflException>(() => RunLabels.NormalizeFolder("billing/../x"));
        Assert.True(RunLabels.IsWithin("Billing/Q4", "billing"));
        Assert.False(RunLabels.IsWithin("billing2", "billing"));
        Assert.Equal("archive/q4/eu", RunLabels.Rebase("billing/q4/eu", "billing", "archive"));
        Assert.Equal("q4", RunLabels.Rebase("billing/q4", "billing", null));
        Assert.Equal("other", RunLabels.Rebase("other", "billing", "archive"));
        Assert.Equal(["a", "a/b", "a/b/c", "d"], RunLabels.WithParents(["a/b/c", null, "D", "d", "A/b"]).Select(f => f.ToLowerInvariant()));
    }

    [Fact]
    public void RenameAndTag_AreSavedAndShownInsteadOfTheTicketsTitle()
    {
        var run = this.CreateRun();

        run = this.Engine.Rename(run.Id, "  Dark mode, take two ");
        Assert.Equal("Dark mode, take two", run.DisplayTitle);
        Assert.Equal("Add dark mode", run.Ticket.Title);

        this.Engine.Tag(run.Id, ["ui", "#urgent"]);
        run = this.Engine.Tag(run.Id, ["UI", "q4"], ["URGENT"]);
        Assert.Equal(["ui", "q4"], this.Store.GetRequired(run.Id).Tags);

        run = this.Engine.Rename(run.Id, " ");
        Assert.Null(run.Title);
        Assert.Equal("Add dark mode", run.DisplayTitle);
        Assert.Empty(this.Engine.SetTags(run.Id, []).Tags);
    }

    [Fact]
    public void Folders_AreCreatedFiledInRenamedAndRemovedWithoutLosingRuns()
    {
        var repo = this.RegisterRepo();
        var first = this.CreateRun(repo);
        var second = this.CreateRun(repo);

        Assert.Equal("Billing", this.Engine.CreateFolder(repo.Id, "Billing"));
        Assert.Equal("Billing", this.Engine.CreateFolder(repo.Id, "billing"));
        Assert.Equal("Billing/q4", this.Engine.MoveToFolder(first.Id, "billing/q4").Folder);
        this.Engine.MoveToFolder(second.Id, "Billing");
        Assert.Equal(["Billing", "Billing/q4"], this.Engine.ListFolders(repo.Id));

        var moved = this.Engine.RenameFolder(repo.Id, "billing", "archive/2026");
        Assert.Equal(new[] { first.Id, second.Id }.Order(), moved.Order());
        Assert.Equal("archive/2026/q4", this.Store.GetRequired(first.Id).Folder);
        Assert.Equal(["archive", "archive/2026", "archive/2026/q4"], this.Engine.ListFolders(repo.Id));
        Assert.Equal(AflErrorKind.InvalidInput, Assert.Throws<AflException>(() =>
            this.Engine.RenameFolder(repo.Id, "archive", "archive/old")).Kind);

        this.Engine.RemoveFolder(repo.Id, "archive/2026");
        Assert.Equal("archive/q4", this.Store.GetRequired(first.Id).Folder);
        Assert.Equal("archive", this.Store.GetRequired(second.Id).Folder);
        Assert.Equal(["archive", "archive/q4"], this.Engine.ListFolders(repo.Id));
        Assert.Equal(AflErrorKind.NotFound, Assert.Throws<AflException>(() => this.Engine.RemoveFolder(repo.Id, "nope")).Kind);

        // An empty folder is kept until it's removed.
        this.Engine.MoveToFolder(first.Id, null);
        this.Engine.MoveToFolder(second.Id, "");
        Assert.Equal(["archive", "archive/q4"], this.Engine.ListFolders(repo.Id));
        Assert.Equal(["archive", "archive/q4"], new RepoRegistry(this.Paths).Find(repo.Id)!.RunFolders.Order());
    }

    [Fact]
    public void MoveToFolder_RefusesARunStartedByAnother()
    {
        var parent = this.CreateRun();
        var child = this.CreateRun();
        child.ParentRunId = parent.Id;
        this.Store.Save(child);

        var ex = Assert.Throws<AflException>(() => this.Engine.MoveToFolder(child.Id, "x"));

        Assert.Equal(AflErrorKind.InvalidState, ex.Kind);
        Assert.Contains("move that run instead", ex.Message);
    }

    /// <summary>
    ///     Creates a run of a one-step workflow.
    /// </summary>
    /// <param name="repo">The repo, or null to register the scratch one.</param>
    /// <returns>The run.</returns>
    private Run CreateRun(RepoRecord? repo = null)
    {
        var step = (UserStepDefinition)StepHandlerRegistry.Instance.CreateDefinition("user");
        step.Id = "u";
        step.Name = "u";
        step.Advance = "approve";
        step.Instructions = "You do u";
        return this.Engine.Create(repo ?? this.RegisterRepo(), Workflow(step), SampleTicket());
    }
}
