using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Repos;
using Afl.Core.Workflows;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Base for the commands that save or remove a common skill in the global library or the repo.
/// </summary>
public abstract class AbstractSkillScopeCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets whether to work in the repo rather than globally.
    /// </summary>
    [Option("in-repo", HelpText = "Use <repo>/.afl/skills (shared through git) instead of ~/.afl/skills.")]
    public bool InRepo { get; set; }

    /// <summary>
    ///     Gets the scope chosen with <see cref="InRepo" />, checking there's a repo when it's needed.
    /// </summary>
    protected WorkflowScope Scope
    {
        get
        {
            if (this.InRepo)
            {
                this.RequireRepo();
                return WorkflowScope.Repo;
            }

            return WorkflowScope.Global;
        }
    }

    /// <summary>
    ///     Saves a skill in <see cref="Scope" /> and reports where.
    /// </summary>
    /// <param name="skill">The skill.</param>
    /// <param name="replace">Whether a skill with the same id in that scope may be replaced.</param>
    /// <returns>The exit code.</returns>
    protected int Save(SkillDefinition skill, bool replace)
    {
        var library = this.Library.Skills;
        var scope = this.Scope;
        if (!replace && library.ListAllScopes().Any(l => l.Scope == scope && l.Skill.Id == skill.Id))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"A {scope} skill '{skill.Id}' already exists.");
        }

        var path = library.Save(skill, scope);
        this.Output.Result(new { id = skill.Id, scope, path },
            (r, w) => w.WriteLine($"Saved {r.path}. Use it in a step or workflow: \"skills\": [\"{r.id}\"]"));
        return ExitSuccess;
    }
}

/// <summary>
///     Lists common skills.
/// </summary>
[Verb("skill-list", HelpText = "List the common skills steps can give their agents (built-in, global and repo), optionally by tag or search.")]
public class SkillListCommand : AbstractLibraryListCommand<SkillDefinition, SkillListing>
{
    /// <inheritdoc />
    protected override AbstractLayeredLibrary<SkillDefinition, SkillListing> LayerOf(WorkflowLibrary library)
    {
        return library.Skills;
    }

    /// <inheritdoc />
    protected override void Render(IReadOnlyList<SkillListing> listings, TextWriter writer, IReadOnlyDictionary<string, LibraryMatch>? matches)
    {
        TextRendering.SkillList(listings, writer, matches);
    }
}

/// <summary>
///     Shows a common skill.
/// </summary>
[Verb("skill-show", HelpText = "Show a common skill's instructions.")]
public class SkillShowCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the skill id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The skill id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.Library.Skills.GetRequired(this.Id), TextRendering.Skill);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Validates a common skill file.
/// </summary>
[Verb("skill-validate", HelpText = "Check a common skill file for problems.")]
public class SkillValidateCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the file.
    /// </summary>
    [Value(0, MetaName = "file", Required = true, HelpText = "The skill JSON file.")]
    public string File { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        return Task.FromResult(this.ReportProblems(this.File, SkillLibrary.Validate(this.Library.Skills.ReadExternal(this.File))));
    }
}

/// <summary>
///     Creates a common skill from a starting template.
/// </summary>
[Verb("skill-new", HelpText = "Create a common skill to fill in, in the global library or this repo.")]
public class SkillNewCommand : AbstractSkillScopeCommand
{
    /// <summary>
    ///     Gets or sets the new skill id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The new skill id (lowercase, digits and dashes).")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    [Option("name", HelpText = "The display name (defaults to the id).")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets the description.
    /// </summary>
    [Option("description", HelpText = "What the skill does and when to use it.")]
    public string? Description { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var skill = new SkillDefinition
        {
            Id = this.Id,
            Name = this.Name ?? this.Id,
            Description = this.Description ?? $"TODO: say what {this.Id} does and when an agent should use it.",
            Content = $"# {this.Name ?? this.Id}\n\nTODO: the instructions the agent follows.\n",
        };
        return Task.FromResult(this.Save(skill, false));
    }
}

/// <summary>
///     Deletes a common skill.
/// </summary>
[Verb("skill-delete", HelpText = "Delete a common skill from the global library or this repo (built-ins can't be deleted).")]
public class SkillDeleteCommand : AbstractSkillScopeCommand
{
    /// <summary>
    ///     Gets or sets the skill id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The skill id.")]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var scope = this.Scope;
        if (!this.Library.Skills.Delete(this.Id, scope))
        {
            throw new AflException(AflErrorKind.NotFound, $"There's no {scope} skill '{this.Id}'.");
        }

        this.Output.Result(new { id = this.Id, scope, deleted = true }, (r, w) => w.WriteLine($"Deleted the {r.scope} skill '{r.id}'."));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Imports a Claude Code style skill (a <c>SKILL.md</c>, or a folder holding one) as a common skill.
/// </summary>
[Verb("skill-import", HelpText = "Import a SKILL.md (or a folder with one and its supporting files) as a common skill.")]
public class SkillImportCommand : AbstractSkillScopeCommand
{
    /// <summary>
    ///     Gets or sets the file or folder.
    /// </summary>
    [Value(0, MetaName = "path", Required = true, HelpText = "The SKILL.md file, or the skill's folder.")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the id to save it as.
    /// </summary>
    [Option("id", HelpText = "The id to save it as (defaults to the frontmatter name or the folder name).")]
    public string? Id { get; set; }

    /// <summary>
    ///     Gets or sets whether an existing skill with the same id may be replaced.
    /// </summary>
    [Option("replace", HelpText = "Replace a skill with the same id in that scope.")]
    public bool Replace { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var skill = SkillLibrary.Import(this.Path);
        if (this.Id is not null)
        {
            skill.Id = this.Id;
        }

        var errors = SkillLibrary.Validate(skill);
        if (errors.Count > 0)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"Can't import {this.Path}:\n  - {string.Join("\n  - ", errors)}");
        }

        return Task.FromResult(this.Save(skill, this.Replace));
    }
}

/// <summary>
///     Writes a common skill out as a Claude Code style skill folder.
/// </summary>
[Verb("skill-export", HelpText = "Write a common skill out as a folder with a SKILL.md and its supporting files.")]
public class SkillExportCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the skill id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The skill id.")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the folder to write.
    /// </summary>
    [Value(1, MetaName = "dir", Required = true, HelpText = "The folder to write (e.g. .claude/skills/<id>).")]
    public string Directory { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var skill = this.Library.Skills.GetRequired(this.Id);
        var directory = System.IO.Path.GetFullPath(this.Directory);
        skill.WriteFolder(directory);
        this.Output.Result(new { id = skill.Id, path = directory }, (r, w) => w.WriteLine($"Wrote {r.path}."));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     Recommends skills for a run in the repo, from a quick look at its files (or, for an empty repo, the languages
///     and frameworks it will use).
/// </summary>
[Verb("skill-recommend",
    HelpText = "Recommend skills for a workflow in this repo, from its files; for an empty repo, give --language/--framework (saved on the repo).")]
public class SkillRecommendCommand : AbstractWorkflowCommand
{
    /// <summary>
    ///     Gets or sets the workflow id.
    /// </summary>
    [Option('w', "workflow", HelpText = "The workflow whose default skills to list (defaults to the repo's default workflow).")]
    public string? Workflow { get; set; }

    /// <summary>
    ///     Gets or sets the repo's languages to save.
    /// </summary>
    [Option("language", Separator = ',', HelpText = "Save what the repo is written in (e.g. csharp,python), replacing what was saved.")]
    public IEnumerable<string> Languages { get; set; } = [];

    /// <summary>
    ///     Gets or sets the repo's frameworks to save.
    /// </summary>
    [Option("framework", Separator = ',', HelpText = "Save the frameworks the repo uses (e.g. fastapi,avalonia), replacing what was saved.")]
    public IEnumerable<string> Frameworks { get; set; } = [];

    /// <summary>
    ///     Gets or sets whether to forget the saved languages and frameworks.
    /// </summary>
    [Option("clear-stack", HelpText = "Forget the languages and frameworks saved on the repo.")]
    public bool ClearStack { get; set; }

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.RequireRepo();
        var languages = this.Languages.ToList();
        var frameworks = this.Frameworks.ToList();
        if (this.ClearStack && (languages.Count > 0 || frameworks.Count > 0))
        {
            throw new AflException(AflErrorKind.InvalidInput, "Give --clear-stack or --language/--framework, not both.");
        }

        if (this.ClearStack || languages.Count > 0 || frameworks.Count > 0)
        {
            repo = this.Repos.SetStack(repo.Id, this.ClearStack ? null : RepoStack.Of(languages, frameworks));
        }

        var library = new WorkflowLibrary(this.Paths, repo.Path);
        var workflow = library.GetRequired(this.Workflow ?? repo.DefaultWorkflowId ?? RepoRegistry.InitialDefaultWorkflowId);
        var advice = await SkillRecommender.AdviseAsync(library, repo, workflow, ct);
        var result = new
        {
            repo = repo.Id,
            workflow = workflow.Id,
            defaults = advice.Defaults,
            recommended = advice.Recommended.Select(r => new { id = r.Skill.Id, name = r.Skill.Name, reason = r.Reason, matched = r.Matched }),
            stack = advice.Stack,
            savedStack = repo.Stack,
            needsStack = advice.NeedsStack,
            stackChoices = advice.Choices,
        };
        this.Output.Result(result, (r, w) => TextRendering.SkillAdvice(advice, workflow, w));
        return ExitSuccess;
    }
}
