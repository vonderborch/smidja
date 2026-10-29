using System.Collections.ObjectModel;
using Afl.Core.Jira;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     The settings of the repo picked in the sidebar: its name and default workflow, its Jira link, the Jira projects
///     associated with it and its saved Jira filters. Projects and boards are offered from the linked Jira instance once
///     <see cref="LoadFromJiraAsync" /> has listed them; until then, or when Jira can't be reached, they can still be
///     typed.
/// </summary>
public class RepoSettingsPageDataContext : AbstractPageDataContext
{
    /// <summary>
    ///     The Jira link choice that unlinks the repo.
    /// </summary>
    public const string NotLinked = "(not linked)";

    /// <summary>
    ///     Separates a board's id from its name in <see cref="BoardChoices" />.
    /// </summary>
    private const string BoardSeparator = " · ";

    /// <summary>
    ///     The projects listed from Jira, or empty until <see cref="LoadFromJiraAsync" /> has run.
    /// </summary>
    private IReadOnlyList<JiraProject> _jiraProjects = [];

    /// <summary>
    ///     The boards listed from Jira, or empty until <see cref="LoadFromJiraAsync" /> has run.
    /// </summary>
    private IReadOnlyList<JiraBoard> _jiraBoards = [];

    /// <summary>
    ///     The instance <see cref="_jiraProjects" /> and <see cref="_jiraBoards" /> came from.
    /// </summary>
    private string? _jiraListedFrom;

    /// <summary>
    ///     Whether <see cref="Load" /> is filling the page, so the setters that save don't fire.
    /// </summary>
    private bool _loading;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RepoSettingsPageDataContext" /> class.
    /// </summary>
    /// <param name="shell">The shell that owns the stores.</param>
    public RepoSettingsPageDataContext(ShellDataContext shell) : base(shell)
    {
        this.ReloadOnChange(h => shell.JiraInstances.Changed += h, h => shell.JiraInstances.Changed -= h);
        this.ReloadOnChange(h => shell.GitHosts.Changed += h, h => shell.GitHosts.Changed -= h);
        this.Reload();
    }

    /// <summary>
    ///     Gets or sets the repo's display name, as typed. <see cref="SaveName" /> saves it.
    /// </summary>
    public string RepoName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets the git host connections the repo can be linked to.
    /// </summary>
    public ObservableCollection<string> HostChoices { get; } = [];

    /// <summary>
    ///     Gets or sets the connection picked in the git host card. <see cref="SaveHostLink" /> saves it.
    /// </summary>
    public string? HostConnectionId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the project typed in the git host card, e.g. <c>owner/repo</c>, <c>group/sub/project</c> or its
    ///     web address.
    /// </summary>
    public string HostProject
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets what the repo is built with (its saved languages and frameworks, which skills are recommended from while
    ///     its files don't say); null until a repo is loaded.
    /// </summary>
    public StackDataContext? Stack
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the permission rules for every run in the repo; null until a repo is loaded.
    /// </summary>
    public AbstractPermissionRuleListDataContext? PermissionRules
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the repo is linked to a GitHub repository or GitLab project.
    /// </summary>
    public bool IsHostLinked => this.Repo?.Host is not null;

    /// <summary>
    ///     Gets where the repo's issues come from, or a note that they don't.
    /// </summary>
    public string HostLinkText => this.Repo?.Host is { } link && this.Shell.GitHosts.Find(link.ConnectionId) is var connection
        ? $"Issues come from {link.Project} on {connection?.DisplayName ?? link.ConnectionId}."
        : "Not linked: no remote is on a known GitHub or GitLab site. Pick the site and type the project, or add the site in Settings → Git hosts.";

    /// <summary>
    ///     Gets whether the linked connection has a sign-in, e.g. "Token set", or how to add one.
    /// </summary>
    public string? HostStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the ids of the workflows the repo can use.
    /// </summary>
    public ObservableCollection<string> WorkflowIds { get; } = [];

    /// <summary>
    ///     Gets or sets the repo's default workflow. Changing it saves it.
    /// </summary>
    public string? DefaultWorkflowId
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref field, value);
            if (!this._loading && value is not null)
            {
                this.ChangeRepo(id => this.Shell.Repos.Modify(id, r => r.DefaultWorkflowId = value));
            }
        }
    }

    /// <summary>
    ///     Gets the Jira link choices: <see cref="NotLinked" /> then every instance id.
    /// </summary>
    public ObservableCollection<string> LinkChoices { get; } = [];

    /// <summary>
    ///     Gets or sets the instance the repo is linked to. Changing it links or unlinks the repo.
    /// </summary>
    public string? LinkedInstance
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsLinked));
            if (!this._loading && value is not null)
            {
                this.ApplyLink(value);
            }
        }
    }

    /// <summary>
    ///     Gets whether the repo is linked to Jira, so it can have projects and filters.
    /// </summary>
    public bool IsLinked => this.LinkedInstance is { } id && id != NotLinked;

    /// <summary>
    ///     Gets whether projects and boards are being listed from Jira.
    /// </summary>
    public bool IsLoadingJira
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets what listing projects and boards from Jira found, or why it couldn't.
    /// </summary>
    public string? JiraStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the projects the repo can be associated with: those listed from Jira, plus any saved or typed key Jira
    ///     didn't list. The selected ones are saved by <see cref="SaveProjects" />.
    /// </summary>
    public ObservableCollection<SelectableItem<string>> ProjectChoices { get; } = [];

    /// <summary>
    ///     Gets or sets a project key typed to add to <see cref="ProjectChoices" />.
    /// </summary>
    public string? NewProjectKey
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the project keys offered for a filter's project: those listed from Jira, or else the repo's own.
    /// </summary>
    public ObservableCollection<string> ProjectKeys { get; } = [];

    /// <summary>
    ///     Gets the boards offered for a filter, as "id · name", limited to the filter's project when it has one.
    /// </summary>
    public ObservableCollection<string> BoardChoices { get; } = [];

    /// <summary>
    ///     Gets the assignees offered for a filter; any user name can be typed too.
    /// </summary>
    public IReadOnlyList<string> AssigneeChoices { get; } = ["me", "unassigned"];

    /// <summary>
    ///     Gets the repo's saved Jira filters.
    /// </summary>
    public ObservableCollection<JiraFilter> Filters { get; } = [];

    /// <summary>
    ///     Gets or sets the filter being edited. Selecting a different one fills the form.
    /// </summary>
    public JiraFilter? SelectedFilter
    {
        get;
        set
        {
            var changed = value?.Name != field?.Name;
            this.RaiseAndSetIfChanged(ref field, value);
            if (changed && !this._loading)
            {
                this.FillFilterForm(value);
            }
        }
    }

    /// <summary>
    ///     Gets the name of the repo's default filter.
    /// </summary>
    public string? DefaultFilter
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the filter form's name.
    /// </summary>
    public string FilterName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the filter form's project key. Changing it narrows <see cref="BoardChoices" />.
    /// </summary>
    public string? FilterProject
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RefreshBoardChoices();
        }
    }

    /// <summary>
    ///     Gets or sets the filter form's board: an id, or a <see cref="BoardChoices" /> entry.
    /// </summary>
    public string? FilterBoard
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the filter form's team (Cloud).
    /// </summary>
    public string? FilterTeam
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the filter form's assignee ("me" for the token's user, "unassigned" for none).
    /// </summary>
    public string? FilterAssignee
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the filter form's statuses, comma-separated.
    /// </summary>
    public string? FilterStatuses
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the filter form's labels, comma-separated.
    /// </summary>
    public string? FilterLabels
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the filter form's extra JQL.
    /// </summary>
    public string? FilterJql
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether saving the filter also makes it the default.
    /// </summary>
    public bool FilterMakeDefault
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Saves <see cref="RepoName" /> as the repo's display name.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public bool SaveName()
    {
        var name = this.RepoName.Trim();
        return this.ChangeRepo(id => this.Shell.Repos.Modify(id, r =>
            r.Name = name.Length > 0 ? name : throw new InvalidOperationException("Enter a name for the repo.")));
    }

    /// <summary>
    ///     Saves <see cref="Stack" /> on the repo; nothing picked forgets it.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public bool SaveStack()
    {
        return this.Stack is { } stack && this.ChangeRepo(id => this.Shell.Repos.SetStack(id, stack.ToStack()));
    }

    /// <summary>
    ///     Lists the linked instance's projects and boards to pick from. A failure is reported in
    ///     <see cref="JiraStatus" /> rather than as an error, since keys and board ids can still be typed.
    /// </summary>
    /// <returns>A task that completes when the listing has finished.</returns>
    public async Task LoadFromJiraAsync()
    {
        if (!this.IsLinked || this.Shell.JiraInstances.Find(this.LinkedInstance!) is not { } instance)
        {
            return;
        }

        this.IsLoadingJira = true;
        this.JiraStatus = $"Listing projects and boards from {instance.Name}…";
        try
        {
            var client = this.Shell.JiraInstances.CreateClient(instance, this.Shell.TicketSources.JiraHttp);
            var projects = await client.ListProjectsAsync();
            var boards = await client.ListBoardsAsync();
            this._jiraProjects = projects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
            this._jiraBoards = boards.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            this._jiraListedFrom = instance.Id;
            this.JiraStatus = $"{projects.Count} projects and {boards.Count} boards in {instance.Name}.";
        }
        catch (Exception ex)
        {
            this.JiraStatus = $"Couldn't list from {instance.Name}: {ex.Message} Project keys and board ids can still be typed.";
        }
        finally
        {
            this.IsLoadingJira = false;
        }

        this.RefreshJiraChoices(this.ProjectChoices.Where(c => c.IsSelected).Select(c => c.Value));
    }

    /// <summary>
    ///     Adds <see cref="NewProjectKey" /> to <see cref="ProjectChoices" />, selected. Nothing is saved until
    ///     <see cref="SaveProjects" />.
    /// </summary>
    /// <returns>True if a valid key was added or selected.</returns>
    public bool AddProjectKey()
    {
        var added = this.Try(() =>
        {
            foreach (var key in RepoRegistry.NormalizeProjectKeys(SplitList(this.NewProjectKey)))
            {
                if (this.ProjectChoices.FirstOrDefault(c => c.Value == key) is { } choice)
                {
                    choice.IsSelected = true;
                }
                else
                {
                    this.ProjectChoices.Add(new SelectableItem<string>(key, key, true));
                }
            }
        });

        if (added)
        {
            this.NewProjectKey = null;
        }

        return added;
    }

    /// <summary>
    ///     Saves the selected <see cref="ProjectChoices" /> as the repo's associated Jira projects.
    /// </summary>
    /// <returns>True if they were saved.</returns>
    public bool SaveProjects()
    {
        var keys = this.ProjectChoices.Where(c => c.IsSelected).Select(c => c.Value).ToList();
        return this.ChangeRepo(id => this.Shell.Repos.SetJiraProjects(id, keys));
    }

    /// <summary>
    ///     Clears the filter form to add a new filter.
    /// </summary>
    public void NewFilter()
    {
        this.SelectedFilter = null;
        this.FillFilterForm(null);
    }

    /// <summary>
    ///     Saves the filter form to the repo's Jira link, replacing a filter with the same name.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public bool SaveFilter()
    {
        var name = this.FilterName.Trim();
        var saved = this.ChangeRepo(repoId =>
        {
            var filter = new JiraFilter
            {
                Name = name,
                Project = NullIfBlank(this.FilterProject)?.ToUpperInvariant(),
                BoardId = ParseBoard(this.FilterBoard),
                Team = NullIfBlank(this.FilterTeam),
                Assignee = NullIfBlank(this.FilterAssignee),
                Statuses = SplitList(this.FilterStatuses),
                Labels = SplitList(this.FilterLabels),
                Jql = NullIfBlank(this.FilterJql),
            };
            this.Shell.Repos.SaveJiraFilter(repoId, filter, this.FilterMakeDefault);
        });

        if (saved)
        {
            this.SelectedFilter = this.Filters.FirstOrDefault(f => f.Name == name);
        }

        return saved;
    }

    /// <summary>
    ///     Removes the selected filter.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool RemoveFilter()
    {
        if (this.SelectedFilter is not { } filter
            || !this.ChangeRepo(id => this.Shell.Repos.RemoveJiraFilter(id, filter.Name)))
        {
            return false;
        }

        this.NewFilter();
        return true;
    }

    /// <summary>
    ///     Makes the selected filter the repo's default.
    /// </summary>
    public void MakeDefaultFilter()
    {
        if (this.SelectedFilter is { } filter)
        {
            this.ChangeRepo(id => this.Shell.Repos.SetDefaultJiraFilter(id, filter.Name));
        }
    }

    /// <summary>
    ///     Clears the repo's default filter.
    /// </summary>
    public void ClearDefaultFilter()
    {
        this.ChangeRepo(id => this.Shell.Repos.SetDefaultJiraFilter(id, null));
    }

    /// <summary>
    ///     Stops tracking the repo. Its runs and files are kept.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool RemoveRepo()
    {
        return this.Repo is { } repo && this.Try(() => this.Shell.RemoveRepo(repo));
    }

    /// <summary>
    ///     Links the repo to the project typed in the git host card.
    /// </summary>
    /// <returns>True if it was linked.</returns>
    public bool SaveHostLink()
    {
        if (this.HostConnectionId is not { } connectionId || string.IsNullOrWhiteSpace(this.HostProject))
        {
            this.ErrorMessage = "Pick a site and type the project, e.g. owner/repo or group/project.";
            return false;
        }

        return this.ChangeRepo(id => this.Shell.Repos.LinkHost(id, connectionId, this.HostProject));
    }

    /// <summary>
    ///     Removes the repo's GitHub or GitLab link.
    /// </summary>
    /// <returns>True if it was unlinked.</returns>
    public bool UnlinkHost()
    {
        return this.ChangeRepo(id => this.Shell.Repos.UnlinkHost(id));
    }

    /// <summary>
    ///     Links the repo to the project its remotes point at, if any is on a known site.
    /// </summary>
    /// <returns>True if a project was found and linked.</returns>
    public bool DetectHostLink()
    {
        return this.ChangeRepo(id => this.Shell.Repos.DetectHost(id)) && this.IsHostLinked;
    }

    /// <summary>
    ///     Works out whether the linked connection has a sign-in, which can mean asking its CLI.
    /// </summary>
    /// <returns>A task that completes when <see cref="HostStatus" /> is set.</returns>
    public async Task RefreshHostStatusAsync()
    {
        if (this.Repo?.Host is not { } link || this.Shell.GitHosts.Find(link.ConnectionId) is not { } connection)
        {
            this.HostStatus = null;
            return;
        }

        string status;
        try
        {
            status = await this.Shell.GitHosts.TokenStatusAsync(connection);
        }
        catch (Exception ex)
        {
            status = ex.Message;
        }

        // The repo may have changed while the CLI answered.
        if (this.Repo?.Host?.ConnectionId == connection.Id)
        {
            this.HostStatus = status == "Not signed in"
                ? $"Not signed in to {connection.DisplayName}: public issues still list. Add a token in Settings → Git hosts to see private ones and file issues."
                : $"{status} for {connection.DisplayName}.";
        }
    }

    /// <inheritdoc />
    protected override void OnRepoChanged()
    {
        this.RaisePropertyChanged(nameof(this.HostLinkText));
        this.RaisePropertyChanged(nameof(this.IsHostLinked));
        base.OnRepoChanged();
    }

    /// <inheritdoc />
    protected override void Load()
    {
        this._loading = true;
        try
        {
            this.LoadRepo();
        }
        finally
        {
            this._loading = false;
        }
    }

    /// <summary>
    ///     Loads the repo's name, default workflow, Jira link, projects and filters.
    /// </summary>
    private void LoadRepo()
    {
        this.LinkChoices.Clear();
        this.LinkChoices.Add(NotLinked);
        foreach (var instance in this.Shell.JiraInstances.All())
        {
            this.LinkChoices.Add(instance.Id);
        }

        this.HostChoices.Clear();
        foreach (var connection in this.Shell.GitHosts.All())
        {
            this.HostChoices.Add(connection.Id);
        }

        var selectedFilter = this.SelectedFilter?.Name;
        this.Filters.Clear();
        this.WorkflowIds.Clear();
        if (this.Repo is not { } repo)
        {
            this.RepoName = string.Empty;
            this.LinkedInstance = null;
            this.DefaultFilter = null;
            this.DefaultWorkflowId = null;
            this.HostConnectionId = null;
            this.HostProject = string.Empty;
            this.HostStatus = null;
            this.RefreshJiraChoices([]);
            return;
        }

        this.RepoName = repo.Name;
        if ((this.PermissionRules as ScopedPermissionRuleListDataContext)?.RepoId != repo.Id)
        {
            this.PermissionRules = new ScopedPermissionRuleListDataContext(this.Shell.Permissions, this.Try, PermissionScope.Repo, repo.Id);
        }

        this.PermissionRules!.Load();
        this.Try(() => this.Stack = new StackDataContext(
            new SkillRecommender(this.Shell.Workflows(repo).Skills.List().Select(l => l.Skill)).StackChoices(), repo.Stack));
        this.HostConnectionId = repo.Host?.ConnectionId ?? this.HostChoices.FirstOrDefault();
        this.HostProject = repo.Host?.Project ?? string.Empty;
        this.RaisePropertyChanged(nameof(this.HostLinkText));
        this.RaisePropertyChanged(nameof(this.IsHostLinked));
        _ = this.RefreshHostStatusAsync();
        this.LinkedInstance = repo.Jira?.InstanceId ?? NotLinked;
        this.RaisePropertyChanged(nameof(this.IsLinked));
        if (this._jiraListedFrom != repo.Jira?.InstanceId)
        {
            // Projects and boards listed from another instance don't apply to this one.
            this._jiraProjects = [];
            this._jiraBoards = [];
            this._jiraListedFrom = null;
            this.JiraStatus = null;
        }

        foreach (var filter in repo.Jira?.Filters ?? [])
        {
            this.Filters.Add(filter);
        }

        this.DefaultFilter = repo.Jira?.DefaultFilter;
        this.RefreshJiraChoices(repo.Jira?.Projects ?? []);
        this.SelectedFilter = this.Filters.FirstOrDefault(f => f.Name == selectedFilter);

        this.Try(() =>
        {
            foreach (var id in this.Shell.Workflows(repo).List().Select(l => l.Workflow.Id).Order())
            {
                this.WorkflowIds.Add(id);
            }
        });

        this.DefaultWorkflowId = repo.DefaultWorkflowId ?? RepoRegistry.InitialDefaultWorkflowId;
    }

    /// <summary>
    ///     Rebuilds the project and board choices from what Jira listed and the repo's projects.
    /// </summary>
    /// <param name="selectedKeys">The project keys to show as selected.</param>
    private void RefreshJiraChoices(IEnumerable<string> selectedKeys)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        this.ProjectChoices.Clear();
        foreach (var project in this._jiraProjects)
        {
            this.ProjectChoices.Add(new SelectableItem<string>(project.Key, $"{project.Key} — {project.Name}",
                selected.Contains(project.Key)));
        }

        // Saved or typed keys that Jira didn't list (or hasn't been asked about yet) stay, selected.
        foreach (var key in selected.Where(k => this._jiraProjects.All(p => !p.Key.Equals(k, StringComparison.OrdinalIgnoreCase))))
        {
            this.ProjectChoices.Add(new SelectableItem<string>(key, key, true));
        }

        this.ProjectKeys.Clear();
        foreach (var key in this._jiraProjects.Count > 0 ? this._jiraProjects.Select(p => p.Key) : selected.Order())
        {
            this.ProjectKeys.Add(key);
        }

        this.RefreshBoardChoices();
    }

    /// <summary>
    ///     Lists the boards listed from Jira, limited to the filter form's project when it names one.
    /// </summary>
    private void RefreshBoardChoices()
    {
        var project = NullIfBlank(this.FilterProject);
        this.BoardChoices.Clear();
        foreach (var board in this._jiraBoards.Where(b =>
                     project is null || string.Equals(b.ProjectKey, project, StringComparison.OrdinalIgnoreCase)))
        {
            this.BoardChoices.Add(BoardLabel(board));
        }
    }

    /// <summary>
    ///     Links or unlinks the repo.
    /// </summary>
    /// <param name="instanceId">The instance id, or <see cref="NotLinked" />.</param>
    private void ApplyLink(string instanceId)
    {
        var linked = this.ChangeRepo(id =>
        {
            if (instanceId == NotLinked)
            {
                this.Shell.Repos.Modify(id, r => r.Jira = null);
            }
            else
            {
                this.Shell.Repos.LinkJira(id, instanceId);
            }
        });

        if (linked && this.IsLinked)
        {
            _ = this.LoadFromJiraAsync();
        }
    }

    /// <summary>
    ///     Changes the repo's record, then reloads the shell so the sidebar, this page and the others get the new record
    ///     straight away rather than when the file watcher next fires.
    /// </summary>
    /// <param name="change">The change, given the repo's id.</param>
    /// <returns>True if there was a repo and the change succeeded.</returns>
    private bool ChangeRepo(Action<string> change)
    {
        if (this.Repo is not { } repo || !this.Try(() => change(repo.Id)))
        {
            return false;
        }

        this.Shell.Reload();
        return true;
    }

    /// <summary>
    ///     Fills the filter form from a filter, or clears it.
    /// </summary>
    /// <param name="filter">The filter, or null.</param>
    private void FillFilterForm(JiraFilter? filter)
    {
        this.FilterName = filter?.Name ?? string.Empty;
        this.FilterProject = filter?.Project;
        this.FilterBoard = filter?.BoardId is { } boardId
            ? this._jiraBoards.FirstOrDefault(b => b.Id == boardId) is { } board ? BoardLabel(board) : boardId.ToString()
            : null;
        this.FilterTeam = filter?.Team;
        this.FilterAssignee = filter?.Assignee;
        this.FilterStatuses = filter is { Statuses.Count: > 0 } ? string.Join(", ", filter.Statuses) : null;
        this.FilterLabels = filter is { Labels.Count: > 0 } ? string.Join(", ", filter.Labels) : null;
        this.FilterJql = filter?.Jql;
        this.FilterMakeDefault = filter is not null && filter.Name == this.DefaultFilter;
    }

    /// <summary>
    ///     Returns how a board is offered in <see cref="BoardChoices" />.
    /// </summary>
    /// <param name="board">The board.</param>
    /// <returns>"id · name".</returns>
    private static string BoardLabel(JiraBoard board)
    {
        return $"{board.Id}{BoardSeparator}{board.Name}";
    }

    /// <summary>
    ///     Reads a board id from a typed id or a <see cref="BoardChoices" /> entry.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The id, or null for blank text.</returns>
    private static int? ParseBoard(string? value)
    {
        if (NullIfBlank(value) is not { } text)
        {
            return null;
        }

        var id = text.Split(BoardSeparator, 2)[0].Trim();
        return int.TryParse(id, out var board) ? board : throw new FormatException($"Board '{text}' isn't a board id.");
    }

    /// <summary>
    ///     Returns trimmed text, or null for blank text.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text, or null.</returns>
    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    ///     Splits a comma-separated list.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The items.</returns>
    private static List<string> SplitList(string? value)
    {
        return (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
