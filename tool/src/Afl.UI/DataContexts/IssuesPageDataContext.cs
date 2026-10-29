using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.Execution;
using Afl.Core.Jira;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using ReactiveUI;
using System.Collections.ObjectModel;

namespace Afl.UI.DataContexts;

/// <summary>
///     The selected repo's tickets from each source it's set up for (GitHub or GitLab issues, Jira), a box to link any ticket
///     directly, and starting a run on a ticket.
/// </summary>
public class IssuesPageDataContext : AbstractPageDataContext
{
    /// <summary>
    ///     The threading choice that runs child runs as the workflow says.
    /// </summary>
    public const string ParallelThreading = "parallel";

    /// <summary>
    ///     The threading choice that runs every child run one at a time.
    /// </summary>
    public const string SingleThreading = "single";

    /// <summary>
    ///     The Jira filter choice that lists without any saved filter.
    /// </summary>
    public const string NoJiraFilter = "(no filter)";

    /// <summary>
    ///     Counts listings, so a slow one that finishes after a newer one started is dropped.
    /// </summary>
    private int _listingVersion;

    /// <summary>
    ///     The repo whose tickets are shown, so settings-only changes to it don't refetch.
    /// </summary>
    private string? _loadedRepoId;

    /// <summary>
    ///     Initializes a new instance of the <see cref="IssuesPageDataContext" /> class.
    /// </summary>
    /// <param name="shell">The shell that owns the stores.</param>
    public IssuesPageDataContext(ShellDataContext shell) : base(shell)
    {
        this.Reload();
        _ = this.RefreshAsync();
    }

    /// <summary>
    ///     Gets the ticket sources set up for the repo, e.g. "github" and "jira".
    /// </summary>
    public ObservableCollection<string> Sources { get; } = [];

    /// <summary>
    ///     Gets or sets the source listed.
    /// </summary>
    public string? SelectedSource
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.IsGitHost = value is not null && this.Shell.TicketSources.Create(value) is AbstractGitHostTicketSource;
            this.RaisePropertyChanged(nameof(this.IsJira));
        }
    }

    /// <summary>
    ///     Gets whether Jira is the source listed.
    /// </summary>
    public bool IsJira => this.SelectedSource == TicketRef.JiraSource;

    /// <summary>
    ///     Gets whether a GitHub or GitLab project is the source listed, so its assignee and label filters apply.
    /// </summary>
    public bool IsGitHost
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets whether a GitHub or GitLab listing shows only issues assigned to the signed-in user.
    /// </summary>
    public bool OnlyMine
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the labels a GitHub or GitLab listing must have, separated by commas.
    /// </summary>
    public string? LabelsText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets whether the repo has no ticket source, so the page offers to link one in Repo settings.
    /// </summary>
    public bool HasNoSources => this.HasRepo && this.Sources.Count == 0;

    /// <summary>
    ///     Gets the repo's saved Jira filters, led by <see cref="NoJiraFilter" />.
    /// </summary>
    public ObservableCollection<string> JiraFilters { get; } = [];

    /// <summary>
    ///     Gets or sets the saved Jira filter to list with.
    /// </summary>
    public string? SelectedJiraFilter
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the ticket states that can be listed.
    /// </summary>
    public IReadOnlyList<string> States { get; } = ["open", "closed", "all"];

    /// <summary>
    ///     Gets or sets the state listed.
    /// </summary>
    public string State
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "open";

    /// <summary>
    ///     Gets or sets the search text.
    /// </summary>
    public string? Search
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets the ticket reference to link directly: a number, key or URL.
    /// </summary>
    public string? LinkText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the listed tickets.
    /// </summary>
    public ObservableCollection<Ticket> Tickets { get; } = [];

    /// <summary>
    ///     Gets or sets the selected ticket.
    /// </summary>
    public Ticket? SelectedTicket
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets a line describing the listing, or why there's nothing to list.
    /// </summary>
    public string? Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Lists the tickets from the selected source with the current options.
    /// </summary>
    /// <returns>A task that completes when the listing has loaded or failed.</returns>
    public async Task RefreshAsync()
    {
        if (this.Repo is not { } repo || this.SelectedSource is not { } sourceName)
        {
            return;
        }

        var version = ++this._listingVersion;
        var source = this.Shell.TicketSources.Create(sourceName);
        var filter = this.CreateFilter(source);
        this.Status = $"Loading {sourceName} tickets…";
        IReadOnlyList<Ticket> tickets = [];
        var loaded = await this.TryAsync(async () => tickets = await Task.Run(() => source.ListAsync(repo, filter)));
        if (version != this._listingVersion)
        {
            return;
        }

        this.Tickets.Clear();
        foreach (var ticket in tickets)
        {
            this.Tickets.Add(ticket);
        }

        this.SelectedTicket = this.Tickets.FirstOrDefault();
        this.Status = loaded ? $"{tickets.Count} {filter.State} {sourceName} ticket(s)." : null;
    }

    /// <summary>
    ///     Fetches the ticket in <see cref="LinkText" />, from whichever source it belongs to, and selects it.
    /// </summary>
    /// <returns>A task that completes when the ticket has loaded or failed.</returns>
    public async Task LinkAsync()
    {
        if (this.Repo is not { } repo || string.IsNullOrWhiteSpace(this.LinkText))
        {
            return;
        }

        var text = this.LinkText.Trim();
        Ticket? ticket = null;
        if (await this.TryAsync(async () => ticket = await Task.Run(() => this.Shell.TicketSources.FetchAsync(repo, text))))
        {
            var existing = this.Tickets.FirstOrDefault(t => t.Source == ticket!.Source && t.Key == ticket.Key);
            if (existing is not null)
            {
                this.Tickets.Remove(existing);
            }

            this.Tickets.Insert(0, ticket!);
            this.SelectedTicket = ticket;
            this.LinkText = null;
            this.Status = this.Shell.Repos.ResolveForTicket(TicketRef.Parse(text), repo).Note ?? this.Status;
        }
    }

    /// <summary>
    ///     Starts a piece of work that has no ticket: a <c>local:&lt;name&gt;</c> ticket, added to the list and selected,
    ///     whose key (and scratch directory) is the name's slug.
    /// </summary>
    /// <param name="name">The work's name, e.g. "Dark mode".</param>
    /// <returns>True if it was added and selected.</returns>
    public async Task<bool> NewWorkAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        this.LinkText = TicketRef.LocalPrefix + name.Trim();
        await this.LinkAsync();
        return this.LinkText is null && this.SelectedTicket?.Source == TicketRef.LocalSource;
    }

    /// <summary>
    ///     Returns the ways a run can be threaded, when the workflow starts child runs that could run at the same time;
    ///     otherwise none, as there is nothing to choose.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="parameters">The values for its parameters.</param>
    /// <returns>The choices, as the workflow says first; empty when there's no choice to make.</returns>
    public IReadOnlyList<SettingChoice> ThreadingChoices(WorkflowDefinition workflow, IReadOnlyDictionary<string, string> parameters)
    {
        var startsChildren = false;
        this.Try(() => startsChildren = new WorkflowComposer(this.Shell.Workflows(this.Repo)).Expand(workflow, parameters).Steps
            .Any(s => s.Type == StepConstants.TypeWorkflow));
        return startsChildren
            ? [
                new SettingChoice(ParallelThreading, "As the workflow says", "Child runs marked all at once run at the same time"),
                new SettingChoice(SingleThreading, "Single-threaded", "Every path and child run, at any depth, runs one at a time"),
            ]
            : [];
    }

    /// <summary>
    ///     Returns the optional steps of a workflow as it expands with the given parameters, for whoever starts a run to
    ///     tick the ones to include; each starts ticked as the workflow's default says.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="parameters">The parameter values.</param>
    /// <returns>The optional steps, by id; none when the workflow has none or couldn't be expanded.</returns>
    public IReadOnlyList<SelectableItem<string>> OptionalStepChoices(WorkflowDefinition workflow,
        IReadOnlyDictionary<string, string> parameters)
    {
        IReadOnlyList<AbstractStepDefinition> optional = [];
        this.Try(() => optional = new WorkflowComposer(this.Shell.Workflows(this.Repo)).Expand(workflow, parameters).OptionalSteps);
        return optional
            .Select(s => new SelectableItem<string>(s.Id,
                string.IsNullOrWhiteSpace(s.Optional!.Help) ? s.Name : $"{s.Name} — {s.Optional.Help.Trim()}",
                s.Optional.Default))
            .ToList();
    }

    /// <summary>
    ///     Returns the workflows a run can use, the repo's default first.
    /// </summary>
    /// <returns>The workflows, or none if they couldn't be read.</returns>
    public IReadOnlyList<WorkflowListing> WorkflowChoices()
    {
        IReadOnlyList<WorkflowListing> listings = [];
        if (!this.Try(() => listings = this.Shell.Workflows(this.Repo).List()))
        {
            return [];
        }

        var defaultId = this.Repo?.DefaultWorkflowId ?? RepoRegistry.InitialDefaultWorkflowId;
        return listings.OrderByDescending(l => l.Workflow.Id == defaultId).ThenBy(l => l.Workflow.Name).ToList();
    }

    /// <summary>
    ///     Returns the agents a run started here can use by default: Claude Code first, then the user's agents. The
    ///     calling-session agent is left out, since there's no Claude session behind the app.
    /// </summary>
    /// <returns>The agents.</returns>
    public IReadOnlyList<AgentDefinition> AgentChoices()
    {
        IReadOnlyList<AgentDefinition> agents = [];
        this.Try(() => agents = this.Shell.Agents.All());
        var preferred = this.Shell.Settings.DefaultAgent();
        return agents.Where(a => a.Id != SessionExecutor.ExecutorName).OrderByDescending(a => a.Id == preferred).ToList();
    }

    /// <summary>
    ///     Returns how a run's agents can be run, the default (from Settings) first.
    /// </summary>
    /// <returns>The run modes.</returns>
    public IReadOnlyList<SettingChoice> RunModeChoices()
    {
        var preferred = RunModeRegistry.Instance.Resolve(null).Name;
        this.Try(() => preferred = this.Shell.Settings.Load().DefaultRunMode);
        return RunModeRegistry.Instance.CreateAll().Select(m => new SettingChoice(m.Name, m.DisplayName, m.Description))
            .OrderByDescending(c => c.Value == preferred).ThenBy(c => c.Value).ToList();
    }

    /// <summary>
    ///     Returns the permission policies a run can start with, the default (from Settings) first.
    /// </summary>
    /// <returns>The policies.</returns>
    public IReadOnlyList<AbstractPermissionPolicy> PermissionChoices()
    {
        var preferred = PermissionPolicyRegistry.Instance.Resolve(null).Name;
        this.Try(() => preferred = PermissionPolicyRegistry.Instance.Resolve(this.Shell.Settings.Load().DefaultPermissionPolicy).Name);
        return PermissionPolicyRegistry.Instance.All.OrderByDescending(p => p.Name == preferred).ToList();
    }

    /// <summary>
    ///     Returns the guard rails every run gets, whatever its policy.
    /// </summary>
    /// <returns>The deny rules.</returns>
    public IReadOnlyList<string> GuardRules()
    {
        IReadOnlyList<string> rules = [];
        this.Try(() => rules = this.Shell.Permissions.Guards());
        return rules;
    }

    /// <summary>
    ///     Works out which skills to offer when starting a workflow in the repo: the workflow's own and those recommended
    ///     from a quick look at the repo's files.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The advice, or null if the repo couldn't be read.</returns>
    public async Task<SkillAdvice?> SkillAdviceAsync(WorkflowDefinition workflow)
    {
        if (this.Repo is not { } repo)
        {
            return null;
        }

        SkillAdvice? advice = null;
        await this.TryAsync(async () => advice = await SkillRecommender.AdviseAsync(this.Shell.Workflows(repo), repo, workflow));
        return advice;
    }

    /// <summary>
    ///     Returns every skill a run in the repo can be given.
    /// </summary>
    /// <returns>The skills.</returns>
    public IReadOnlyList<SkillDefinition> SkillChoices()
    {
        IReadOnlyList<SkillDefinition> skills = [];
        this.Try(() => skills = this.Shell.Workflows(this.Repo).Skills.List().Select(l => l.Skill).ToList());
        return skills;
    }

    /// <summary>
    ///     Saves what the repo is built with, so skills can be recommended for it while it's still empty.
    /// </summary>
    /// <param name="stack">The languages and frameworks; null to forget them.</param>
    /// <returns>True if it was saved.</returns>
    public bool SaveStack(RepoStack? stack)
    {
        if (this.Repo is not { } repo || !this.Try(() => this.Shell.Repos.SetStack(repo.Id, stack)))
        {
            return false;
        }

        this.Shell.Reload();
        return true;
    }

    /// <summary>
    ///     Starts a run of a workflow on the selected ticket and drives it in the background.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="agent">The id of the agent for steps that don't pick their own.</param>
    /// <param name="mode">How agents' commands run (see <see cref="RunModeChoices" />); null for the default.</param>
    /// <param name="parameters">The values for the workflow's parameters, by id.</param>
    /// <param name="maxParallel">The most steps or child runs to run at once, or null for no limit (see <see cref="ThreadingChoices" />).</param>
    /// <param name="optionalSteps">
    ///     Whether to include each optional step, by id (see <see cref="OptionalStepChoices" />); a step not listed
    ///     follows its default.
    /// </param>
    /// <param name="permissions">The permission policy (see <see cref="PermissionChoices" />); null for the default.</param>
    /// <param name="skills">The run's skills in place of the workflow's (see <see cref="SkillAdviceAsync" />); null for the workflow's.</param>
    /// <returns>The run, or null if it couldn't start.</returns>
    public Run? Start(
        WorkflowDefinition workflow,
        string agent = ExecutorRegistry.DefaultExecutor,
        string? mode = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        int? maxParallel = null,
        IReadOnlyDictionary<string, bool>? optionalSteps = null,
        string? permissions = null,
        IReadOnlyList<string>? skills = null)
    {
        if (this.Repo is not { } repo || this.SelectedTicket is not { } ticket)
        {
            return null;
        }

        Run? run = null;
        if (!this.Try(() => run = this.Shell.Driver.Engine.Create(repo, workflow, ticket, agent, mode, parameters, maxParallel, optionalSteps, permissions, skills)))
        {
            return null;
        }

        this.Try(() => this.Shell.Driver.Start(run!.Id, "Start", (engine, progress, ct) => engine.DriveAsync(run.Id, progress, ct)));
        return run;
    }

    /// <inheritdoc />
    protected override void OnRepoChanged()
    {
        // The shell raises a repo change whenever repos.json changes; refetch only when it's a different repo.
        var repoChanged = this.Repo?.Id != this._loadedRepoId;
        base.OnRepoChanged();
        if (!repoChanged)
        {
            return;
        }

        this.Tickets.Clear();
        _ = this.RefreshAsync();
    }

    /// <inheritdoc />
    protected override void Load()
    {
        var repoChanged = this.Repo?.Id != this._loadedRepoId;
        this._loadedRepoId = this.Repo?.Id;
        if (this.Repo is not { } repo)
        {
            this.Sources.Clear();
            this.JiraFilters.Clear();
            this.Tickets.Clear();
            this.Status = null;
            this.RaisePropertyChanged(nameof(this.HasNoSources));
            return;
        }

        var previousSource = this.SelectedSource;
        this.Sources.Clear();
        foreach (var source in this.Shell.TicketSources.CreateAll().Where(s => s.IsAvailableFor(repo)))
        {
            this.Sources.Add(source.Name);
        }

        this.SelectedSource = this.Sources.Contains(previousSource ?? string.Empty) ? previousSource : this.Sources.FirstOrDefault();

        var previousFilter = repoChanged ? null : this.SelectedJiraFilter;
        this.JiraFilters.Clear();
        this.JiraFilters.Add(NoJiraFilter);
        foreach (var filter in repo.Jira?.Filters ?? [])
        {
            this.JiraFilters.Add(filter.Name);
        }

        this.SelectedJiraFilter = this.JiraFilters.FirstOrDefault(f => f == previousFilter)
                                  ?? this.JiraFilters.FirstOrDefault(f => f == repo.Jira?.DefaultFilter)
                                  ?? NoJiraFilter;

        if (this.Sources.Count == 0)
        {
            this.Status = "This repo isn't linked to GitHub, GitLab or Jira. Link it in Repo settings; you can still link a ticket by URL.";
        }

        this.RaisePropertyChanged(nameof(this.HasNoSources));
    }

    /// <summary>
    ///     Builds a source's listing options from the page's state, search and Jira filter.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>The options.</returns>
    private TicketFilter CreateFilter(AbstractTicketSource source)
    {
        var filter = source.CreateFilter();
        filter.State = this.State;
        filter.Search = string.IsNullOrWhiteSpace(this.Search) ? null : this.Search.Trim();
        if (filter is JiraTicketFilter jira)
        {
            jira.SavedFilter = this.SelectedJiraFilter is { } name && name != NoJiraFilter ? name : null;
            jira.SkipDefaultFilter = jira.SavedFilter is null;
        }
        else if (filter is GitHostTicketFilter gitHost)
        {
            gitHost.Assignee = this.OnlyMine ? "@me" : null;
            gitHost.Labels = (this.LabelsText ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return filter;
    }
}
