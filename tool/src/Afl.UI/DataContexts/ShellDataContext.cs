using System.Collections.ObjectModel;
using Afl.Core.Agents;
using Afl.Core.Authoring;
using Afl.Core.Helpers;
using Afl.Core.GitHosts;
using Afl.Core.Jira;
using Afl.Core.Mcp;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Settings;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     The app-level data context. It owns the shared afl stores, the in-process run driver, the sidebar's registered
///     repos and the selected repo, and is Niddy's app data context, so every page builds its own data context from it.
/// </summary>
public class ShellDataContext : AbstractReloadingDataContext
{
    /// <summary>
    ///     The sidebar entry that stands for every repo.
    /// </summary>
    public static readonly RepoRecord AllRepos = new() { Id = string.Empty, Name = "All repos" };

    /// <summary>
    ///     Whether <see cref="Load" /> is rebuilding <see cref="RepoItems" />.
    /// </summary>
    private bool _syncingRepos;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ShellDataContext" /> class.
    /// </summary>
    /// <param name="paths">The afl home to read from.</param>
    /// <param name="engine">The run engine; defaults to one over <paramref name="paths" />.</param>
    /// <param name="jiraInstances">The Jira instance store; defaults to one over <paramref name="paths" /> using the system keychain.</param>
    /// <param name="ticketSources">The ticket sources; defaults to the registered ones, using <paramref name="jiraInstances" />.</param>
    /// <param name="servers">The MCP server store; defaults to one over <paramref name="paths" /> using the system keychain.</param>
    /// <param name="gitHosts">The git host connection store; defaults to one over <paramref name="paths" /> using the system keychain.</param>
    public ShellDataContext(AflPaths paths, RunEngine? engine = null, JiraInstanceStore? jiraInstances = null,
        TicketSourceRegistry? ticketSources = null, McpServerStore? servers = null, GitHostStore? gitHosts = null)
    {
        this.Paths = paths;
        this.GitHosts = ticketSources?.GitHosts ?? gitHosts ?? new GitHostStore(paths);
        this.Repos = new RepoRegistry(paths) { GitHosts = this.GitHosts };
        this.JiraInstances = jiraInstances ?? new JiraInstanceStore(paths);
        this.Servers = servers ?? new McpServerStore(paths);
        this.Agents = new AgentStore(paths, this.Servers);
        this.Settings = new SettingsStore(paths);
        this.Permissions = new PermissionRuleStore(paths) { Repos = this.Repos, Settings = this.Settings };
        this.TicketSources = ticketSources ?? new TicketSourceRegistry { JiraInstances = this.JiraInstances, GitHosts = this.GitHosts };
        this.Driver = new RunDriver(engine ?? new RunEngine(new RunStore(paths)));
        this.Runs = this.Driver.Engine.Store;
        this.Authoring = new AuthoringService(this.Driver.Engine);
        this.Authoring.Sessions.EnableChangeNotifications();
        this.Repos.EnableChangeNotifications();
        this.Runs.EnableChangeNotifications();
        this.JiraInstances.EnableChangeNotifications();
        this.GitHosts.EnableChangeNotifications();
        this.Agents.EnableChangeNotifications();
        this.Servers.EnableChangeNotifications();
        this.Settings.EnableChangeNotifications();
        this.Permissions.EnableChangeNotifications();
        this.ReloadOnChange(h => this.Repos.Changed += h, h => this.Repos.Changed -= h);
        this.Reload();
    }

    /// <summary>
    ///     Gets the afl home.
    /// </summary>
    public AflPaths Paths { get; }

    /// <summary>
    ///     Gets the repo registry.
    /// </summary>
    public RepoRegistry Repos { get; }

    /// <summary>
    ///     Gets the run store.
    /// </summary>
    public RunStore Runs { get; }

    /// <summary>
    ///     Gets the GitHub and GitLab connection store.
    /// </summary>
    public GitHostStore GitHosts { get; }

    /// <summary>
    ///     Gets the Jira instance store.
    /// </summary>
    public JiraInstanceStore JiraInstances { get; }

    /// <summary>
    ///     Gets the AI agents steps can use.
    /// </summary>
    public AgentStore Agents { get; }

    /// <summary>
    ///     Gets the MCP servers agents can use, as agents or as extra tools.
    /// </summary>
    public McpServerStore Servers { get; }

    /// <summary>
    ///     Gets afl's global settings (default agent, run mode, selector, agent preference and custom capabilities).
    /// </summary>
    public SettingsStore Settings { get; }

    /// <summary>
    ///     Gets the permission rules: global ones, repo ones (through <see cref="Repos" />) and the guard rails
    ///     (through <see cref="Settings" />).
    /// </summary>
    public PermissionRuleStore Permissions { get; }

    /// <summary>
    ///     Gets the ticket sources, set up to use this shell's stores.
    /// </summary>
    public TicketSourceRegistry TicketSources { get; }

    /// <summary>
    ///     Gets the in-process run driver.
    /// </summary>
    public RunDriver Driver { get; }

    /// <summary>
    ///     Gets the AI helper that drafts skills, common steps and workflows on hidden runs the driver drives.
    /// </summary>
    public AuthoringService Authoring { get; }

    /// <summary>
    ///     Gets the sidebar's repo entries, led by <see cref="AllRepos" />.
    /// </summary>
    public ObservableCollection<RepoRecord> RepoItems { get; } = [];

    /// <summary>
    ///     Gets or sets the selected sidebar entry.
    /// </summary>
    public RepoRecord? SelectedRepoItem
    {
        get;
        set
        {
            if (this._syncingRepos)
            {
                // The list is being rebuilt; Load raises one change at the end instead of one per step.
                field = value;
                return;
            }

            if (!ReferenceEquals(field, value))
            {
                this.RaiseAndSetIfChanged(ref field, value);
                this.RaisePropertyChanged(nameof(this.SelectedRepo));
            }
        }
    }

    /// <summary>
    ///     Gets the selected repo, or null when "All repos" is selected.
    /// </summary>
    public RepoRecord? SelectedRepo => this.SelectedRepoItem is { Id.Length: > 0 } repo ? repo : null;

    /// <summary>
    ///     Gets the window title.
    /// </summary>
    public string Title => $"afl — {this.Paths.Root}";

    /// <summary>
    ///     Returns the workflow library as seen from a repo: its own workflows, then the global library, then the
    ///     built-ins. Without a repo, only the global library and built-ins.
    /// </summary>
    /// <param name="repo">The repo, or null.</param>
    /// <returns>The library.</returns>
    public WorkflowLibrary Workflows(RepoRecord? repo)
    {
        return new WorkflowLibrary(this.Paths, repo?.Path);
    }

    /// <summary>
    ///     Registers the git repo containing a folder and selects it.
    /// </summary>
    /// <param name="folder">Any folder inside the repo.</param>
    /// <returns>The repo.</returns>
    public RepoRecord AddRepo(string folder)
    {
        var repo = this.Repos.EnsureRegistered(folder);
        this.Load();
        this.SelectedRepoItem = this.RepoItems.First(r => r.Id == repo.Id);
        return repo;
    }

    /// <summary>
    ///     Unregisters a repo. Its runs and files are kept.
    /// </summary>
    /// <param name="repo">The repo.</param>
    public void RemoveRepo(RepoRecord repo)
    {
        this.Repos.Remove(repo.Id);
        this.Load();
    }

    /// <summary>
    ///     Reloads the sidebar's repos, keeping the selection.
    /// </summary>
    protected override void Load()
    {
        var selectedId = this.SelectedRepoItem?.Id;
        var repos = this.Repos.All();
        this._syncingRepos = true;
        try
        {
            this.RepoItems.Clear();
            this.RepoItems.Add(AllRepos);
            foreach (var repo in repos)
            {
                this.RepoItems.Add(repo);
            }

            this.SelectedRepoItem = this.RepoItems.FirstOrDefault(r => r.Id == selectedId) ?? AllRepos;
        }
        finally
        {
            this._syncingRepos = false;
        }

        // Always raise: the record is a fresh copy that may carry changed settings even when the id is the same.
        this.RaisePropertyChanged(nameof(this.SelectedRepoItem));
        this.RaisePropertyChanged(nameof(this.SelectedRepo));
    }

    /// <inheritdoc />
    protected override void OnDisposed()
    {
        this.Driver.StopAll(TimeSpan.FromSeconds(5));
        this.Repos.Dispose();
        this.Runs.Dispose();
        this.Authoring.Sessions.Dispose();
        this.JiraInstances.Dispose();
        this.Agents.Dispose();
    }
}
