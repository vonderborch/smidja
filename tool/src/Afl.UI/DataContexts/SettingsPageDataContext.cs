using System.Collections.ObjectModel;
using Afl.Core.Jira;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     Settings shared by every repo: the Jira instances, the GitHub and GitLab sites, the AI agents, the MCP servers and how runs execute. Tokens go straight from the password box to
///     <see cref="JiraInstanceStore" /> and are never kept once saved. A repo's own settings are on
///     <see cref="RepoSettingsPageDataContext" />.
/// </summary>
public partial class SettingsPageDataContext : AbstractPageDataContext
{
    /// <summary>
    ///     Whether <see cref="Load" /> is filling the page, so the setters that save don't fire.
    /// </summary>
    private bool _loading;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SettingsPageDataContext" /> class.
    /// </summary>
    /// <param name="shell">The shell that owns the stores.</param>
    public SettingsPageDataContext(ShellDataContext shell) : base(shell)
    {
        this.ReloadOnChange(h => shell.JiraInstances.Changed += h, h => shell.JiraInstances.Changed -= h);
        this.ReloadOnChange(h => shell.GitHosts.Changed += h, h => shell.GitHosts.Changed -= h);
        this.ReloadOnChange(h => shell.Agents.Changed += h, h => shell.Agents.Changed -= h);
        this.ReloadOnChange(h => shell.Servers.Changed += h, h => shell.Servers.Changed -= h);
        this.ReloadOnChange(h => shell.Settings.Changed += h, h => shell.Settings.Changed -= h);
        this.ReloadOnChange(h => shell.Permissions.Changed += h, h => shell.Permissions.Changed -= h);
        this.Reload();
    }

    /// <summary>
    ///     Gets the Jira instances.
    /// </summary>
    public ObservableCollection<JiraInstance> Instances { get; } = [];

    /// <summary>
    ///     Gets or sets the instance being edited. Selecting a different one fills the form.
    /// </summary>
    public JiraInstance? SelectedInstance
    {
        get;
        set
        {
            var changed = value?.Id != field?.Id;
            this.RaiseAndSetIfChanged(ref field, value);
            if (changed && !this._loading)
            {
                this.FillInstanceForm(value);
            }
        }
    }

    /// <summary>
    ///     Gets the instance kinds.
    /// </summary>
    public IReadOnlyList<JiraInstanceKind> Kinds { get; } = Enum.GetValues<JiraInstanceKind>();

    /// <summary>
    ///     Gets or sets the instance form's id.
    /// </summary>
    public string InstanceId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the instance form's display name.
    /// </summary>
    public string InstanceName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the instance form's kind.
    /// </summary>
    public JiraInstanceKind InstanceKind
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsCloud));
        }
    }

    /// <summary>
    ///     Gets whether the form is for Jira Cloud, which also needs the account email.
    /// </summary>
    public bool IsCloud => this.InstanceKind == JiraInstanceKind.Cloud;

    /// <summary>
    ///     Gets or sets the instance form's base URL.
    /// </summary>
    public string InstanceBaseUrl
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the instance form's account email (Cloud).
    /// </summary>
    public string? InstanceEmail
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets a new token typed into the form. It's cleared as soon as it's saved.
    /// </summary>
    public string? InstanceToken
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets where the form instance's token comes from, or that it has none.
    /// </summary>
    public string? TokenStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the result of the last connection test or save.
    /// </summary>
    public string? InstanceStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Clears the instance form to add a new instance.
    /// </summary>
    public void NewInstance()
    {
        this.SelectedInstance = null;
        this.FillInstanceForm(null);
    }

    /// <summary>
    ///     Saves the instance form. A newly typed token is tested against Jira first, and only stored if it works.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public async Task<bool> SaveInstanceAsync()
    {
        var instance = new JiraInstance
        {
            Id = this.InstanceId.Trim(),
            Name = string.IsNullOrWhiteSpace(this.InstanceName) ? this.InstanceId.Trim() : this.InstanceName.Trim(),
            Kind = this.InstanceKind,
            BaseUrl = this.InstanceBaseUrl.Trim(),
            Email = this.IsCloud && !string.IsNullOrWhiteSpace(this.InstanceEmail) ? this.InstanceEmail.Trim() : null,
        };
        var token = string.IsNullOrWhiteSpace(this.InstanceToken) ? null : this.InstanceToken.Trim();
        var store = this.Shell.JiraInstances;
        this.InstanceStatus = null;

        JiraUser? user = null;
        var saved = await this.TryAsync(async () =>
        {
            JiraInstanceStore.Validate(instance);
            if (token is not null)
            {
                user = await store.CreateClient(instance, token).GetMyselfAsync();
            }
            else if (!store.HasToken(instance.Id))
            {
                throw new InvalidOperationException("Enter an API token (Cloud) or personal access token (Data Center).");
            }

            store.Save(instance, token);
        });

        if (!saved)
        {
            return false;
        }

        this.InstanceToken = null;
        this.InstanceStatus = user is null ? $"Saved {instance.Id}." : $"Saved {instance.Id}; connected as {user.DisplayName}.";
        this.Reload();
        this.SelectedInstance = this.Instances.FirstOrDefault(i => i.Id == instance.Id);
        return true;
    }

    /// <summary>
    ///     Checks the selected instance's stored token against Jira.
    /// </summary>
    /// <returns>A task that completes when the test has finished.</returns>
    public async Task TestInstanceAsync()
    {
        if (this.SelectedInstance is not { } instance)
        {
            return;
        }

        this.InstanceStatus = $"Testing {instance.Id}…";
        JiraUser? user = null;
        var ok = await this.TryAsync(async () => user = await this.Shell.JiraInstances.CreateClient(instance).GetMyselfAsync());
        this.InstanceStatus = ok ? $"{instance.Id}: connected as {user!.DisplayName}." : null;
    }

    /// <summary>
    ///     Removes the selected instance and its token.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool RemoveInstance()
    {
        if (this.SelectedInstance is not { } instance || !this.Try(() => this.Shell.JiraInstances.Remove(instance.Id)))
        {
            return false;
        }

        this.InstanceStatus = $"Removed {instance.Id}.";
        this.NewInstance();
        this.Reload();
        return true;
    }

    /// <inheritdoc />
    protected override void OnRepoChanged()
    {
        // Nothing here depends on the repo picked in the sidebar.
    }

    /// <inheritdoc />
    protected override void Load()
    {
        this._loading = true;
        try
        {
            this.LoadInstances();
            this.LoadGitHosts();
            this.LoadServers();
            this.LoadAgents();
            this.LoadExecution();
            this.LoadPermissions();
        }
        finally
        {
            this._loading = false;
        }
    }

    /// <summary>
    ///     Lists the Jira instances, keeping the selection.
    /// </summary>
    private void LoadInstances()
    {
        var selectedId = this.SelectedInstance?.Id;
        this.Instances.Clear();
        foreach (var instance in this.Shell.JiraInstances.All())
        {
            this.Instances.Add(instance);
        }

        this.SelectedInstance = this.Instances.FirstOrDefault(i => i.Id == selectedId);
        if (this.SelectedInstance is null && selectedId is not null)
        {
            this.FillInstanceForm(null);
        }
        else
        {
            this.RefreshTokenStatus();
        }
    }

    /// <summary>
    ///     Fills the instance form from an instance, or clears it.
    /// </summary>
    /// <param name="instance">The instance, or null.</param>
    private void FillInstanceForm(JiraInstance? instance)
    {
        this.InstanceId = instance?.Id ?? string.Empty;
        this.InstanceName = instance?.Name ?? string.Empty;
        this.InstanceKind = instance?.Kind ?? JiraInstanceKind.Cloud;
        this.InstanceBaseUrl = instance?.BaseUrl ?? string.Empty;
        this.InstanceEmail = instance?.Email;
        this.InstanceToken = null;
        this.RefreshTokenStatus();
    }

    /// <summary>
    ///     Describes where the form instance's token comes from.
    /// </summary>
    private void RefreshTokenStatus()
    {
        if (this.SelectedInstance is not { } instance)
        {
            this.TokenStatus = "New instance: enter a token.";
            return;
        }

        var variable = JiraInstanceStore.TokenVariable(instance.Id);
        this.TokenStatus = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))
            ? $"Token from {variable}."
            : this.Shell.JiraInstances.HasToken(instance.Id)
                ? "Token stored securely. Enter a new one to replace it."
                : $"No token. Enter one, or set {variable}.";
    }
}
