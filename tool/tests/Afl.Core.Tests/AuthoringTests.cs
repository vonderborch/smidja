using System.Text.Json;
using Afl.Core.Authoring;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the AI authoring helper: drafting skills, common steps and workflows in hidden runs, checking and fixing
///     drafts, and reviewing them into a library.
/// </summary>
public class AuthoringTests : AbstractAflTest
{
    private readonly FakeStepExecutor _executor = new();

    private RunEngine Engine => new(new RunStore(this.Paths), this._executor);

    private AuthoringService Service => new(this.Engine);

    private static string Skill(string id = "commit-messages", string name = "Commit messages") =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = "1.1", id, name, description = "Write conventional commit messages.", tags = new[] { "git" },
            content = "# Commit messages\n\nUse `type(scope): subject`.\n",
        });

    /// <summary>
    ///     Answers each request in turn: writes the draft given (when not null) into the agent's folder, then replies.
    /// </summary>
    private void Answer(params (string? Draft, string Reply)[] turns)
    {
        var turn = 0;
        this._executor.Respond = r =>
        {
            var (draft, reply) = turns[Math.Min(turn++, turns.Length - 1)];
            if (draft is not null)
            {
                File.WriteAllText(Path.Combine(r.WorkingDirectory, AuthoringConstants.DraftFileName), draft);
            }

            return new ExecutionResult(ExecutionStatus.Completed, reply, r.SessionId);
        };
    }

    [Fact]
    public async Task NewSkill_RunsHiddenInItsOwnFolder_AndWaitsForReview()
    {
        this.Answer((Skill(), "Wrote a commit message skill."));

        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill for commit messages");

        Assert.Equal(AuthoringStatus.Review, view.Status);
        Assert.True(view.Check.IsValid);
        Assert.Equal("commit-messages", view.Check.Id);
        Assert.Equal("Wrote a commit message skill.", view.Step!.Output);

        var store = new RunStore(this.Paths);
        Assert.DoesNotContain(store.List(), r => r.Id == view.Run!.Id);
        Assert.Contains(store.List(includeAuthoring: true), r => r.Id == view.Run!.Id);
        Assert.Equal(RunKind.Authoring, view.Run!.Kind);

        var request = Assert.Single(this._executor.Requests);
        var folder = this.Service.Sessions.FolderFor(view.Session.Id);
        Assert.Equal(folder, request.WorkingDirectory);
        Assert.False(folder.StartsWith(this.Paths.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Contains("a skill for commit messages", request.Prompt);
        Assert.Contains("reference/skill.schema.json", request.Prompt);
        Assert.Contains("WebSearch", request.AllowedTools);
        Assert.DoesNotContain(request.AllowedTools, t => t.StartsWith("WebFetch", StringComparison.Ordinal));
        Assert.Equal(StepConstants.ReadOnlyPermissionMode, request.PermissionMode);

        var reference = Path.Combine(folder, AuthoringConstants.ReferenceFolderName);
        Assert.True(File.Exists(Path.Combine(reference, "skill.schema.json")));
        Assert.Contains("test-first", File.ReadAllText(Path.Combine(reference, AbstractAuthoringKind.ExampleFileName)));
        Assert.Contains("`test-first` (builtin)", File.ReadAllText(Path.Combine(reference, AbstractAuthoringKind.CatalogFileName)));
    }

    [Fact]
    public async Task InvalidDraft_IsSentBackToTheSameConversationToFix()
    {
        this.Answer(("{ \"id\": \"Bad Id\" }", "First go."), (Skill(), "Fixed it."));

        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        Assert.Equal(AuthoringStatus.Review, view.Status);
        Assert.True(view.Check.IsValid);
        Assert.Equal(2, this._executor.Requests.Count);
        var fix = this._executor.Requests[1];
        Assert.True(fix.Resume);
        Assert.Contains("can't be saved yet", fix.Prompt);
        Assert.Equal(1, view.Step!.CheckRounds);
        Assert.Contains(view.Step.Notes, n => n.Contains("round 1 of 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DraftThatStaysInvalid_StopsAtTheCap_AndCantBeApproved()
    {
        this.Answer((null, "I didn't write anything."));

        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        Assert.Equal(AuthoringStatus.Review, view.Status);
        Assert.False(view.Check.IsValid);
        Assert.Equal(1 + AuthoringConstants.DefaultMaxFixRounds, this._executor.Requests.Count);
        Assert.Contains(view.Step!.Notes, n => n.Contains("still has 1 problem", StringComparison.Ordinal));
        await Assert.ThrowsAsync<AflException>(() => this.Service.ApproveAsync(view.Session.Id, WorkflowScope.Global));
    }

    [Fact]
    public async Task AgentQuestion_PausesForTheUser_ThenCarriesOn()
    {
        this.Answer(
            (null, "Before I start:\n\n```afl-ask\n{\"questions\": [{\"label\": \"Which commit style?\"}]}\n```"),
            (Skill(), "Done, using your style."));

        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        Assert.Equal(AuthoringStatus.NeedsYou, view.Status);
        Assert.True(view.NeedsUser);
        Assert.Equal(StepStatus.AwaitingInput, view.Step!.Status);

        view = await this.Service.AnswerAsync(view.Session.Id, new Dictionary<string, string> { ["ask-1"] = "Conventional Commits" });

        Assert.Equal(AuthoringStatus.Review, view.Status);
        Assert.Contains("Conventional Commits", this._executor.Requests[1].Prompt);
    }

    [Fact]
    public async Task RefusedWebFetch_AsksPermissionForThatSite()
    {
        var denial = ToolRuleSuggesterRegistry.Instance.Describe("WebFetch", "t1",
            JsonSerializer.SerializeToElement(new { url = "https://www.conventionalcommits.org/en/v1.0.0/" }), "/repo");
        this._executor.Respond = r => new ExecutionResult(ExecutionStatus.Completed, "Couldn't fetch the spec.", "s1", null, [denial]);

        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        Assert.Equal(AuthoringStatus.NeedsYou, view.Status);
        var request = Assert.Single(view.Step!.OpenPermissionRequests);
        Assert.Equal("WebFetch(domain:www.conventionalcommits.org)", request.Denial.SuggestedRules[0]);
    }

    [Fact]
    public async Task Approve_SavesToTheLibrary_AndFinishesTheRun()
    {
        this.Answer((Skill(), "Done."));
        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        view = await this.Service.ApproveAsync(view.Session.Id, WorkflowScope.Global);

        Assert.Equal(AuthoringStatus.Saved, view.Status);
        Assert.Equal(RunStatus.Done, view.Run!.Status);
        Assert.Equal("commit-messages", view.Session.SavedId);
        var saved = new SkillLibrary(this.Paths, null).List().Single(l => l.Document.Id == "commit-messages");
        Assert.Equal(WorkflowScope.Global, saved.Scope);
        Assert.Equal(view.Session.SavedPath, saved.Path);
    }

    [Fact]
    public async Task Approve_ToTheRepo_SavesInTheRepo()
    {
        this.Answer((Skill(), "Done."));
        var repo = this.RegisterRepo();
        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill", repo);

        Assert.Contains(repo.Path, this._executor.Requests[0].AdditionalDirectories);
        view = await this.Service.ApproveAsync(view.Session.Id, WorkflowScope.Repo);

        Assert.StartsWith(repo.Path, view.Session.SavedPath);
        Assert.Equal(WorkflowScope.Repo, new SkillLibrary(this.Paths, repo.Path).List().Single(l => l.Document.Id == "commit-messages").Scope);
    }

    [Fact]
    public async Task Approve_WontReplaceADocumentWithoutOverwrite()
    {
        this.Answer((Skill(), "Done."));
        var library = new SkillLibrary(this.Paths, null);
        library.Save(library.ReadExternal(this.WriteFile(Skill(name: "Mine"))), WorkflowScope.Global);
        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        var ex = await Assert.ThrowsAsync<AflException>(() => this.Service.ApproveAsync(view.Session.Id, WorkflowScope.Global));
        Assert.Contains("overwrite", ex.Message);
        Assert.Equal("Mine", library.List().Single(l => l.Document.Id == "commit-messages").Document.Name);

        view = await this.Service.ApproveAsync(view.Session.Id, WorkflowScope.Global, true);

        Assert.Equal(AuthoringStatus.Saved, view.Status);
        Assert.Equal("Commit messages", library.List().Single(l => l.Document.Id == "commit-messages").Document.Name);
    }

    [Fact]
    public async Task ImprovingABuiltIn_StartsFromIt_AndSavesACopy()
    {
        var library = new SkillLibrary(this.Paths, null);
        var builtIn = library.List().Single(l => l.Document.Id == "test-first");
        string? before = null;
        this._executor.Respond = r =>
        {
            var path = Path.Combine(r.WorkingDirectory, AuthoringConstants.DraftFileName);
            before = File.ReadAllText(path);
            File.WriteAllText(path, before.Replace("\"Test first\"", "\"Test first, shorter\"", StringComparison.Ordinal));
            return new ExecutionResult(ExecutionStatus.Completed, "Shortened it.", r.SessionId);
        };

        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "make it shorter", baseId: "test-first");

        Assert.Equal("test-first", view.Session.BaseId);
        Assert.Equal(WorkflowScope.BuiltIn, view.Session.BaseScope);
        Assert.Contains("\"Test first\"", before);
        Assert.Contains("Improve the skill `test-first`", this._executor.Requests[0].Prompt);

        view = await this.Service.ApproveAsync(view.Session.Id, WorkflowScope.Global);

        var copy = library.List().Single(l => l.Document.Id == "test-first");
        Assert.Equal(WorkflowScope.Global, copy.Scope);
        Assert.Equal("Test first, shorter", copy.Document.Name);
        Assert.Equal("Test first", builtIn.Document.Name);
    }

    [Fact]
    public async Task RequestChanges_SendsTheFeedbackBack()
    {
        this.Answer((Skill(), "Done."), (Skill(name: "Commits"), "Renamed."));
        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        view = await this.Service.RequestChangesAsync(view.Session.Id, "Call it Commits.");

        Assert.Equal(AuthoringStatus.Review, view.Status);
        Assert.Contains("Call it Commits.", this._executor.Requests[1].Prompt);
        Assert.Equal("Commits", view.Check.Name);
    }

    [Fact]
    public async Task UpdateDraft_ReplacesAndChecksIt()
    {
        this.Answer((Skill(), "Done."));
        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");

        var check = this.Service.UpdateDraft(view.Session.Id, Skill(name: "Edited"));

        Assert.True(check.IsValid);
        Assert.Equal("Edited", check.Name);
        Assert.Contains("Edited", this.Service.ReadDraft(view.Session.Id));
    }

    [Fact]
    public async Task Discard_RemovesTheRunTheFolderAndTheRecord()
    {
        this.Answer((Skill(), "Done."));
        var view = await this.Service.StartAsync(AuthoringConstants.KindSkill, "a skill");
        var folder = this.Service.Sessions.FolderFor(view.Session.Id);

        this.Service.Discard(view.Session.Id);

        Assert.False(Directory.Exists(folder));
        Assert.Null(this.Service.Sessions.Get(view.Session.Id));
        Assert.Null(new RunStore(this.Paths).Get(view.Run!.Id));
        Assert.Empty(this.Service.List());
    }

    [Fact]
    public void WorkflowKind_ChecksReferencesToCommonSteps()
    {
        var kind = AuthoringKindRegistry.Instance.Create(AuthoringConstants.KindWorkflow);
        var draft = this.WriteFile("""
            {"schemaVersion": "5.0", "id": "mine", "name": "Mine", "steps": [{"id": "a", "name": "A", "use": "no-such-step"}]}
            """);

        var check = kind.Check(this.Paths, null, draft);

        Assert.False(check.IsValid);
        Assert.Contains(check.Problems, p => p.Contains("no-such-step", StringComparison.Ordinal));
    }

    [Fact]
    public void CommonStepKind_DescribesAValidDraft_AndCatalogsTheLibrary()
    {
        var kind = AuthoringKindRegistry.Instance.Create(AuthoringConstants.KindStep);
        var draft = this.WriteFile("""
            {"schemaVersion": "2.0", "id": "lint", "name": "Lint", "description": "Runs the linter.",
             "step": {"id": "lint", "name": "Lint", "type": "ai", "prompt": "Run the linter."}}
            """);

        var check = kind.Check(this.Paths, null, draft);

        Assert.True(check.IsValid, string.Join("; ", check.Problems));
        Assert.Equal("ai step, 0 parameters", check.Detail);
        Assert.Contains("`code-review` (builtin)", kind.Catalog(this.Paths, null));
    }

    [Fact]
    public void AuthorDraftSteps_AreInternal()
    {
        var step = new AuthorDraftStepDefinition { Id = "d", Name = "D", Kind = AuthoringConstants.KindSkill, Prompt = "x" };
        var workflow = Workflow(step);

        Assert.DoesNotContain(StepConstants.TypeAuthorDraft, StepHandlerRegistry.Instance.PublicKeys);
        Assert.True(StepHandlerRegistry.Instance.IsInternal(StepConstants.TypeAuthorDraft));
        Assert.NotEmpty(new WorkflowLibrary(this.Paths, null).ValidateForSave(workflow));
        var ex = Assert.Throws<AflException>(() => this.Engine.Create(this.RegisterRepo(), workflow, SampleTicket()));
        Assert.Equal(AflErrorKind.InvalidWorkflow, ex.Kind);
    }

    [Fact]
    public void Create_RejectsAnUnknownKindOrBase()
    {
        Assert.Throws<AflException>(() => this.Service.Create("poem", "a poem"));
        Assert.Throws<AflException>(() => this.Service.Create(AuthoringConstants.KindSkill, "x", baseId: "no-such-skill"));
        Assert.Empty(this.Service.List());
    }

    /// <summary>
    ///     Writes text to a new file in the scratch root.
    /// </summary>
    private string WriteFile(string text)
    {
        var path = Path.Combine(this.Root, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, text);
        return path;
    }
}
