using Afl.Core.Exceptions;
using Afl.Core.Repos;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the repo registry, workflow library and ticket source base.
/// </summary>
public class StoreTests : AbstractAflTest
{
    [Fact]
    public void RepoRegistry_RegistersOnceFromAnySubdirectory()
    {
        var registry = new RepoRegistry(this.Paths);
        var sub = Directory.CreateDirectory(Path.Combine(this.RepoPath, "src", "deep")).FullName;

        var first = registry.EnsureRegistered(this.RepoPath);
        var second = registry.EnsureRegistered(sub);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(registry.All());
        Assert.Equal(RepoRegistry.InitialDefaultWorkflowId, first.DefaultWorkflowId);
        Assert.StartsWith("sample-repo-", first.Id);
    }

    [Fact]
    public void RepoRegistry_FindsByIdNameOrPathAndPersistsChanges()
    {
        var repo = this.RegisterRepo();
        var registry = new RepoRegistry(this.Paths);

        Assert.Equal(repo.Id, registry.GetRequired(repo.Id).Id);
        Assert.Equal(repo.Id, registry.GetRequired("sample-repo").Id);
        Assert.Equal(repo.Id, registry.GetRequired(this.RepoPath).Id);

        registry.Modify(repo.Id, r => r.DefaultWorkflowId = "bugfix");
        Assert.Equal("bugfix", new RepoRegistry(this.Paths).GetRequired(repo.Id).DefaultWorkflowId);

        Assert.True(registry.Remove(repo.Id));
        Assert.Throws<AflException>(() => registry.GetRequired(repo.Id));
    }

    [Fact]
    public void RepoRegistry_RejectsNonRepoDirectory()
    {
        var outside = Directory.CreateDirectory(Path.Combine(this.Root, "not-a-repo")).FullName;

        var ex = Assert.Throws<AflException>(() => new RepoRegistry(this.Paths).EnsureRegistered(outside));
        Assert.Equal(AflErrorKind.InvalidInput, ex.Kind);
    }

    [Fact]
    public void WorkflowLibrary_BuiltInsAreValid()
    {
        var library = new WorkflowLibrary(this.Paths);
        var listings = library.List();

        Assert.Contains(listings, l => l.Workflow.Id == "feature");
        Assert.Contains(listings, l => l.Workflow.Id == "bugfix");
        Assert.All(listings, l => Assert.Empty(new WorkflowComposer(library).Validate(l.Workflow)));
    }

    [Fact]
    public void WorkflowLibrary_RepoOverridesGlobalOverridesBuiltIn()
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);
        var global = library.GetRequired("feature");
        global.Name = "Global feature";
        library.Save(global, WorkflowScope.Global);
        Assert.Equal("Global feature", new WorkflowLibrary(this.Paths).GetRequired("feature").Name);

        global.Name = "Repo feature";
        library.Save(global, WorkflowScope.Repo);

        Assert.Equal("Repo feature", new WorkflowLibrary(this.Paths, this.RepoPath).GetRequired("feature").Name);
        Assert.Equal("Global feature", new WorkflowLibrary(this.Paths).GetRequired("feature").Name);
        Assert.Equal(WorkflowScope.Repo, library.List().Single(l => l.Workflow.Id == "feature").Scope);
    }

    [Fact]
    public void WorkflowLibrary_ValidationReportsEachProblem()
    {
        var workflow = Workflow(
            new AiStepDefinition { Id = "a", Name = "A", },
            new UserStepDefinition { Id = "a", Name = "Dup", Instructions = "x" },
            new AiStepDefinition { Id = "c", Name = "C", Prompt = "{{steps.d.output}} {{ticket.key}} {{steps.a.output}}" },
            new AiStepDefinition { Id = "d", Name = "D", Advance = "approve", Prompt = "ok", PermissionMode = "plan" },
            new InteractiveStepDefinition { Id = "e", Name = "E", Prompt = "ok", PermissionMode = "plan" });

        var errors = WorkflowLibrary.Validate(workflow);

        Assert.Contains(errors, e => e.StartsWith("Step 'a': an AI step needs a 'prompt'"));
        Assert.Contains(errors, e => e.Contains("used by an earlier step"));
        Assert.Contains(errors, e => e.Contains("'{{steps.d.output}}' is not available"));
        Assert.Contains(errors, e => e.StartsWith("Step 'd': permissionMode 'plan' only works interactively"));
        // The two steps named 'a' make the arrow between them a loop, which also hides 'a' from 'c'.
        Assert.Contains(errors, e => e.StartsWith("Step 'a': going back to 'a' makes a loop with no way out"));
        Assert.Contains(errors, e => e.Contains("'{{steps.a.output}}' is not available"));
        Assert.Equal(6, errors.Count);
        Assert.Throws<AflException>(() => WorkflowLibrary.ThrowIfInvalid(workflow));
    }

    [Fact]
    public void WorkflowLibrary_RefusesNewerSchema()
    {
        var path = Path.Combine(this.Root, "future.json");
        File.WriteAllText(path, """{ "schemaVersion": "9.0", "id": "f", "name": "F", "steps": [] }""");

        Assert.Throws<AflException>(() => new WorkflowLibrary(this.Paths).ReadExternal(path));
    }

    [Fact]
    public async Task TicketSource_WidensBaseFilter()
    {
        var source = new FakeTicketSource();
        source.Tickets.AddRange([
            new Ticket { Key = "1", Title = "Dark mode" }, new Ticket { Key = "2", Title = "Crash on start" },
        ]);

        var result = await source.ListAsync(this.RegisterRepo(), new TicketFilter { Search = "crash" });

        Assert.Equal("2", Assert.Single(result).Key);
        Assert.IsType<TicketFilter>(source.CreateFilter());
    }
}
