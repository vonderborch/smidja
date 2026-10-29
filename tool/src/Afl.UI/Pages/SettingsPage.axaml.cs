using Afl.UI.DataContexts;
using Avalonia.Interactivity;
using Niddy.Avalonia.PageSystem;
using Niddy.Avalonia.Toasts;

namespace Afl.UI.Pages;

/// <summary>
///     Settings shared by every repo, in tabs: Jira instances, git hosts, AI agents, MCP servers and how runs execute. Opened from the sidebar's Settings button rather than the page
///     menu; each repo's own settings are on <see cref="RepoSettingsPage" />.
/// </summary>
[PageRegistration(DisplayName = "Settings", TopLevel = false)]
[PageIcon(PageIcons.Settings)]
public partial class SettingsPage : AbstractAflPage<SettingsPageDataContext>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="SettingsPage" /> class.
    /// </summary>
    public SettingsPage()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Gets the page's data context.
    /// </summary>
    private SettingsPageDataContext Settings => this.PageDataContext;

    /// <inheritdoc />
    protected override SettingsPageDataContext CreatePageDataContext()
    {
        return new SettingsPageDataContext(this.AppDataContext);
    }

    /// <summary>
    ///     Clears the instance form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnNewInstance(object? sender, RoutedEventArgs e)
    {
        this.Settings.NewInstance();
    }

    /// <summary>
    ///     Tests the selected instance's connection.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnTestInstance(object? sender, RoutedEventArgs e)
    {
        if (this.Settings is { } settings)
        {
            await settings.TestInstanceAsync();
        }
    }

    /// <summary>
    ///     Tests and saves the instance form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnSaveInstance(object? sender, RoutedEventArgs e)
    {
        if (this.Settings is { } settings && await settings.SaveInstanceAsync())
        {
            this.ShowToast("Jira instance saved.");
        }
    }

    /// <summary>
    ///     Removes the selected instance after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRemoveInstance(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SelectedInstance is not { } instance)
        {
            return;
        }

        if (await this.WarnAsync("Remove Jira instance",
                $"Remove {instance.Name} ({instance.Id}) and its stored token? Repos linked to it stop listing Jira tickets.",
                "Remove")
            && this.Settings.RemoveInstance())
        {
            this.ShowToast("Jira instance removed.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Clears the git host form to add a new site.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnNewGitHost(object? sender, RoutedEventArgs e)
    {
        this.Settings.NewGitHost();
    }

    /// <summary>
    ///     Signs in to the selected git host.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnTestGitHost(object? sender, RoutedEventArgs e)
    {
        await this.Settings.TestGitHostAsync();
    }

    /// <summary>
    ///     Tests and saves the git host form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnSaveGitHost(object? sender, RoutedEventArgs e)
    {
        if (await this.Settings.SaveGitHostAsync())
        {
            this.ShowToast("Git host saved.");
        }
    }

    /// <summary>
    ///     Clears the selected git host's stored token.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnClearGitHostToken(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.ClearGitHostToken())
        {
            this.ShowToast("Token cleared.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Removes the selected git host after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRemoveGitHost(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SelectedGitHost is not { } connection)
        {
            return;
        }

        if (await this.WarnAsync("Remove git host",
                $"Remove {connection.DisplayName} ({connection.Id}) and its stored token? Repos linked to it stop listing its issues.",
                "Remove")
            && this.Settings.RemoveGitHost())
        {
            this.ShowToast("Git host removed.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Starts a new agent in the editor.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnNewAgent(object? sender, RoutedEventArgs e)
    {
        this.Settings.NewAgent();
    }

    /// <summary>
    ///     Adds the picked preset as an agent.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddPresetAgent(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.AddPresetAgent())
        {
            this.ShowToast("Agent added.");
        }
    }

    /// <summary>
    ///     Saves the agent in the editor.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveAgent(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SaveAgent())
        {
            this.ShowToast("Agent saved.");
        }
    }

    /// <summary>
    ///     Tests the agent in the editor.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnTestAgent(object? sender, RoutedEventArgs e)
    {
        if (this.Settings is { } settings)
        {
            await settings.TestAgentAsync();
        }
    }

    /// <summary>
    ///     Removes the selected agent after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRemoveAgent(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SelectedAgent is not { } agent)
        {
            return;
        }

        if (await this.WarnAsync("Remove agent",
                $"Remove {agent.Name} ({agent.Id})? Workflow steps that pick it can't run until it's added again.", "Remove")
            && this.Settings.RemoveAgent())
        {
            this.ShowToast("Agent removed.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Lists the tools of the form agent's MCP server.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnListFormTools(object? sender, RoutedEventArgs e)
    {
        if (this.Settings is { } settings)
        {
            await settings.LoadFormToolsAsync();
        }
    }

    /// <summary>
    ///     Moves the picked agent up the preference order.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnPreferenceUp(object? sender, RoutedEventArgs e)
    {
        this.Settings.MovePreference(-1);
    }

    /// <summary>
    ///     Moves the picked agent down the preference order.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnPreferenceDown(object? sender, RoutedEventArgs e)
    {
        this.Settings.MovePreference(1);
    }

    /// <summary>
    ///     Looks for MCP servers other apps have configured.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnFindImports(object? sender, RoutedEventArgs e)
    {
        this.Settings.FindImports();
    }

    /// <summary>
    ///     Imports the ticked servers.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnImportServers(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.ImportServers() > 0)
        {
            this.ShowToast("MCP servers imported.");
        }
    }

    /// <summary>
    ///     Closes the import list.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnCancelImport(object? sender, RoutedEventArgs e)
    {
        this.Settings.CancelImport();
    }

    /// <summary>
    ///     Clears the server form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnNewServer(object? sender, RoutedEventArgs e)
    {
        this.Settings.NewServer();
    }

    /// <summary>
    ///     Adds a secret row to the server form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddSecret(object? sender, RoutedEventArgs e)
    {
        this.Settings.AddSecret();
    }

    /// <summary>
    ///     Saves the server form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveServer(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SaveServer())
        {
            this.ShowToast("MCP server saved.");
        }
    }

    /// <summary>
    ///     Connects to the selected server and lists its tools.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnTestServer(object? sender, RoutedEventArgs e)
    {
        if (this.Settings is { } settings)
        {
            await settings.TestServerAsync();
        }
    }

    /// <summary>
    ///     Removes the selected server after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRemoveServer(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SelectedServer is not { } server)
        {
            return;
        }

        if (await this.WarnAsync("Remove MCP server",
                $"Remove {server.Name} ({server.Id}) and its stored secrets? Agents and steps that use it can't run until it's added again.",
                "Remove")
            && this.Settings.RemoveServer())
        {
            this.ShowToast("MCP server removed.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Drafts an agent from the picked tool and shows it on the Agents tab.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnCreateAgentFromTool(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.CreateAgentFromTool())
        {
            this.Tabs.SelectedIndex = 1;
        }
    }

    /// <summary>
    ///     Saves the execution settings.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveExecution(object? sender, RoutedEventArgs e)
    {
        this.Settings.SaveExecution();
    }

    /// <summary>
    ///     Saves which run states notify.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveNotifications(object? sender, RoutedEventArgs e)
    {
        this.Settings.SaveNotifications();
    }

    /// <summary>
    ///     Shows a test notification.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnSendTestNotification(object? sender, RoutedEventArgs e)
    {
        await this.Settings.SendTestNotificationAsync();
    }

    /// <summary>
    ///     Adds a custom capability.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddCapability(object? sender, RoutedEventArgs e)
    {
        this.Settings.AddCapability();
    }

    /// <summary>
    ///     Removes the picked custom capability.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRemoveCapability(object? sender, RoutedEventArgs e)
    {
        this.Settings.RemoveCapability();
    }
}
