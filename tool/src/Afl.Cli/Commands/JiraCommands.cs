using Afl.Cli.Output;
using Afl.Core.Exceptions;
using Afl.Core.Jira;
using Afl.Core.Repos;
using CommandLine;

namespace Afl.Cli.Commands;

/// <summary>
///     Shared behaviour for the <c>jira</c> commands: the instance store, and reading and changing the repo's link.
/// </summary>
public abstract class AbstractJiraCommand : AbstractCommand
{
    /// <summary>
    ///     Gets the Jira instance store.
    /// </summary>
    protected JiraInstanceStore Instances => new(this.Paths);

    /// <summary>
    ///     Picks the instance to talk to: the one named, else the repo's linked one, else the only one configured.
    /// </summary>
    /// <param name="instanceId">The instance id given on the command line, if any.</param>
    /// <returns>The instance.</returns>
    protected JiraInstance ResolveInstance(string? instanceId)
    {
        var store = this.Instances;
        if (instanceId is not null)
        {
            return store.GetRequired(instanceId);
        }

        var repo = this.RepoOption is not null ? this.Repos.GetRequired(this.RepoOption) : this.CurrentRepo;
        if (repo is not null)
        {
            return JiraTicketSource.ResolveInstance(store, repo, null);
        }

        var all = store.All();
        return all.Count == 1
            ? all[0]
            : throw new AflException(AflErrorKind.InvalidInput, "Name the Jira instance with --instance <id>.");
    }

    /// <summary>
    ///     Builds the link view for a repo.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <returns>The view.</returns>
    protected JiraLinkView LinkView(RepoRecord repo)
    {
        return new JiraLinkView(repo.Id, repo.Jira is null ? null : this.Instances.Find(repo.Jira.InstanceId), repo.Jira);
    }
}

/// <summary>
///     Commands that take Jira search criteria: saved-filter definitions and ad-hoc issue searches.
/// </summary>
public abstract class AbstractJiraCriteriaCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the project key.
    /// </summary>
    [Option("project", HelpText = "Jira: only this project key.")]
    public string? Project { get; set; }

    /// <summary>
    ///     Gets or sets the board id.
    /// </summary>
    [Option("board", HelpText = "Jira: read issues from this agile board id (see afl jira boards).")]
    public int? Board { get; set; }

    /// <summary>
    ///     Gets or sets the team.
    /// </summary>
    [Option("team", HelpText = "Jira: Cloud team id, or a raw JQL clause on Data Center.")]
    public string? Team { get; set; }

    /// <summary>
    ///     Gets or sets the assignee.
    /// </summary>
    [Option("assignee", HelpText = "Only tickets assigned to this user: me (or @me) for you, none for unassigned.")]
    public string? Assignee { get; set; }

    /// <summary>
    ///     Gets or sets the statuses.
    /// </summary>
    [Option("status", Separator = ',', HelpText = "Jira: only these statuses (comma-separated); replaces --state.")]
    public IEnumerable<string> Statuses { get; set; } = [];

    /// <summary>
    ///     Gets or sets the labels.
    /// </summary>
    [Option('l', "label", Separator = ',', HelpText = "Only tickets with all these labels (comma-separated).")]
    public IEnumerable<string> Labels { get; set; } = [];

    /// <summary>
    ///     Gets or sets extra JQL.
    /// </summary>
    [Option("jql", HelpText = "Jira: extra JQL, ANDed with the rest (an ORDER BY replaces the default order).")]
    public string? Jql { get; set; }

    /// <summary>
    ///     Gets whether any Jira-only criterion was given.
    /// </summary>
    protected bool HasJiraOnlyCriteria => this.Project is not null || this.Board is not null || this.Team is not null ||
                                          this.Statuses.Any() || this.Jql is not null;

    /// <summary>
    ///     Builds the criteria from the options.
    /// </summary>
    /// <param name="name">The filter name.</param>
    /// <returns>The criteria.</returns>
    protected JiraFilter ToCriteria(string name = "")
    {
        return new JiraFilter
        {
            Name = name,
            Project = this.Project,
            BoardId = this.Board,
            Team = this.Team,
            Assignee = this.Assignee,
            Statuses = this.Statuses.ToList(),
            Labels = this.Labels.ToList(),
            Jql = this.Jql,
        };
    }
}

/// <summary>
///     <c>afl jira</c>: shows the repo's Jira link.
/// </summary>
[Verb("jira", HelpText = "Show the repo's Jira link (see also jira instance|link|unlink|project|projects|boards|filter).")]
public class JiraCommand : AbstractJiraCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        this.Output.Result(this.LinkView(this.RequireRepo()), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira instance add</c>: adds or updates an instance and stores its token.
/// </summary>
[Verb("jira-instance-add", HelpText = "Add or update a Jira instance. The token is read from a hidden prompt (or the first line of stdin).")]
public class JiraInstanceAddCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "A short id for the instance, e.g. work.")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the kind.
    /// </summary>
    [Option('k', "kind", Required = true, HelpText = "cloud or datacenter.")]
    public JiraInstanceKind Kind { get; set; }

    /// <summary>
    ///     Gets or sets the base URL.
    /// </summary>
    [Option('u', "url", Required = true, HelpText = "The site URL, e.g. https://acme.atlassian.net.")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the email.
    /// </summary>
    [Option('e', "email", HelpText = "Your account email (required for Cloud).")]
    public string? Email { get; set; }

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    [Option("name", HelpText = "A display name (defaults to the id).")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets whether to skip the connection check.
    /// </summary>
    [Option("skip-verify", HelpText = "Save without checking the token against Jira.")]
    public bool SkipVerify { get; set; }

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Instances;
        var instance = new JiraInstance
        {
            Id = this.Id, Name = this.Name ?? this.Id, Kind = this.Kind, BaseUrl = this.Url.Trim().TrimEnd('/'), Email = this.Email,
        };
        JiraInstanceStore.Validate(instance);

        var what = this.Kind == JiraInstanceKind.Cloud ? "API token" : "personal access token";
        var token = this.Output.ReadSecret($"Jira {what} for {instance.BaseUrl}:");
        if (token is null && !store.HasToken(instance.Id))
        {
            throw new AflException(AflErrorKind.InvalidInput, $"A {what} is needed (or set {JiraInstanceStore.TokenVariable(instance.Id)}).");
        }

        if (!this.SkipVerify)
        {
            var user = await store.CreateClient(instance, token ?? store.ResolveToken(instance.Id)).GetMyselfAsync(ct);
            this.Output.Info($"Connected to {instance.BaseUrl} as {user.DisplayName}.");
        }

        var saved = store.Save(instance, token);
        this.Output.Result(JiraInstanceView.From(store, saved), TextRendering.JiraInstance);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl jira instance list</c>.
/// </summary>
[Verb("jira-instance-list", HelpText = "List Jira instances.")]
public class JiraInstanceListCommand : AbstractJiraCommand
{
    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Instances;
        this.Output.Result(store.All().Select(i => JiraInstanceView.From(store, i)).ToList(), TextRendering.JiraInstanceList);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira instance remove</c>.
/// </summary>
[Verb("jira-instance-remove", HelpText = "Remove a Jira instance and its stored token.")]
public class JiraInstanceRemoveCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the id.
    /// </summary>
    [Value(0, MetaName = "id", Required = true, HelpText = "The instance id.")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether to unlink repos that use it.
    /// </summary>
    [Option("force", HelpText = "Also unlink the repos that use it (their saved filters are lost).")]
    public bool Force { get; set; }

    /// <inheritdoc />
    protected override bool RegistersCurrentRepo => false;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Instances;
        var instance = store.GetRequired(this.Id);
        var linked = this.Repos.All()
            .Where(r => string.Equals(r.Jira?.InstanceId, instance.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (linked.Count > 0 && !this.Force)
        {
            throw new AflException(AflErrorKind.InvalidState,
                $"Jira '{instance.Id}' is linked to: {string.Join(", ", linked.Select(r => r.Name))}. Pass --force to unlink them too.");
        }

        foreach (var repo in linked)
        {
            this.Repos.Modify(repo.Id, r => r.Jira = null);
        }

        store.Remove(instance.Id);
        this.Output.Result(instance, (i, w) => w.WriteLine($"Removed {i.Id}."));
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira link</c>: links the repo to an instance, keeping its saved filters.
/// </summary>
[Verb("jira-link", HelpText = "Link the repo to a Jira instance.")]
public class JiraLinkCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the instance id.
    /// </summary>
    [Option('i', "instance", HelpText = "The instance id (optional when only one is set up).")]
    public string? Instance { get; set; }

    /// <summary>
    ///     Gets or sets the Jira projects to associate with the repo.
    /// </summary>
    [Option('p', "project", Separator = ',', HelpText = "Jira project keys whose tickets belong in this repo (comma-separated; replaces the current ones).")]
    public IEnumerable<string> Projects { get; set; } = [];

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var store = this.Instances;
        var all = store.All();
        var instance = this.Instance is not null
            ? store.GetRequired(this.Instance)
            : all.Count == 1
                ? all[0]
                : throw new AflException(AflErrorKind.InvalidInput, all.Count == 0
                    ? "No Jira instances are set up. Add one with: afl jira instance add <id> --kind cloud|datacenter --url <url>"
                    : $"Pick one with --instance: {string.Join(", ", all.Select(i => i.Id))}.");

        var projects = this.Projects.ToList();
        var repo = this.Repos.LinkJira(this.RequireRepo().Id, instance.Id, projects.Count > 0 ? projects : null);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira unlink</c>.
/// </summary>
[Verb("jira-unlink", HelpText = "Remove the repo's Jira link and its saved filters.")]
public class JiraUnlinkCommand : AbstractJiraCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.Repos.Modify(this.RequireRepo().Id, r => r.Jira = null);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira project add</c>: associates Jira projects with the repo.
/// </summary>
[Verb("jira-project-add", HelpText = "Associate Jira projects with the repo: listings default to them, and their ticket keys find this repo.")]
public class JiraProjectAddCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the project keys.
    /// </summary>
    [Value(0, MetaName = "keys", Min = 1, HelpText = "Project keys, e.g. APP WEB.")]
    public IEnumerable<string> Keys { get; set; } = [];

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.Repos.AddJiraProjects(this.RequireRepo().Id, this.Keys);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira project remove</c>: stops associating Jira projects with the repo.
/// </summary>
[Verb("jira-project-remove", HelpText = "Stop associating Jira projects with the repo.")]
public class JiraProjectRemoveCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the project keys.
    /// </summary>
    [Value(0, MetaName = "keys", Min = 1, HelpText = "Project keys.")]
    public IEnumerable<string> Keys { get; set; } = [];

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.Repos.RemoveJiraProjects(this.RequireRepo().Id, this.Keys);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira projects</c>: lists projects, for building filters.
/// </summary>
[Verb("jira-projects", HelpText = "List the Jira projects you can see.")]
public class JiraProjectsCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the instance id.
    /// </summary>
    [Option('i', "instance", HelpText = "The instance id (defaults to the repo's).")]
    public string? Instance { get; set; }

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var client = this.Instances.CreateClient(this.ResolveInstance(this.Instance));
        this.Output.Result(await client.ListProjectsAsync(ct), TextRendering.JiraProjects);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl jira boards</c>: lists agile boards, for building filters.
/// </summary>
[Verb("jira-boards", HelpText = "List Jira agile boards.")]
public class JiraBoardsCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the instance id.
    /// </summary>
    [Option('i', "instance", HelpText = "The instance id (defaults to the repo's).")]
    public string? Instance { get; set; }

    /// <summary>
    ///     Gets or sets the project key.
    /// </summary>
    [Option("project", HelpText = "Only boards of this project.")]
    public string? Project { get; set; }

    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var client = this.Instances.CreateClient(this.ResolveInstance(this.Instance));
        this.Output.Result(await client.ListBoardsAsync(this.Project, ct), TextRendering.JiraBoards);
        return ExitSuccess;
    }
}

/// <summary>
///     <c>afl jira filter add</c>: saves (or replaces) a named filter on the repo's link.
/// </summary>
[Verb("jira-filter-add", HelpText = "Save a named Jira filter for this repo (replaces one with the same name).")]
public class JiraFilterAddCommand : AbstractJiraCriteriaCommand
{
    /// <summary>
    ///     Gets or sets the filter name.
    /// </summary>
    [Value(0, MetaName = "name", Required = true, HelpText = "The filter name.")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets whether to make it the default.
    /// </summary>
    [Option("default", HelpText = "Make it the default filter for afl issues.")]
    public bool MakeDefault { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.Repos.SaveJiraFilter(this.RequireRepo().Id, this.ToCriteria(this.Name), this.MakeDefault);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira filter list</c>.
/// </summary>
[Verb("jira-filter-list", HelpText = "List the repo's saved Jira filters.")]
public class JiraFilterListCommand : AbstractJiraCommand
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.RequireRepo();
        RepoRegistry.RequireJiraLink(repo);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira filter remove</c>.
/// </summary>
[Verb("jira-filter-remove", HelpText = "Remove a saved Jira filter.")]
public class JiraFilterRemoveCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the filter name.
    /// </summary>
    [Value(0, MetaName = "name", Required = true, HelpText = "The filter name.")]
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var repo = this.Repos.RemoveJiraFilter(this.RequireRepo().Id, this.Name);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}

/// <summary>
///     <c>afl jira filter default</c>: sets or clears the default filter.
/// </summary>
[Verb("jira-filter-default", HelpText = "Set the filter afl issues uses when none is named (--clear for none).")]
public class JiraFilterDefaultCommand : AbstractJiraCommand
{
    /// <summary>
    ///     Gets or sets the filter name.
    /// </summary>
    [Value(0, MetaName = "name", HelpText = "The filter name.")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets or sets whether to clear the default.
    /// </summary>
    [Option("clear", HelpText = "Use no default filter.")]
    public bool Clear { get; set; }

    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        if ((this.Name is null) != this.Clear)
        {
            throw new AflException(AflErrorKind.InvalidInput, "Give a filter name, or --clear.");
        }

        var repo = this.Repos.SetDefaultJiraFilter(this.RequireRepo().Id, this.Clear ? null : this.Name);
        this.Output.Result(this.LinkView(repo), TextRendering.JiraLink);
        return Task.FromResult(ExitSuccess);
    }
}
