using Afl.UI.DataContexts;
using Avalonia.Interactivity;
using Niddy.Avalonia.PageSystem;
using Niddy.Avalonia.Toasts;

namespace Afl.UI.Pages;

/// <summary>
///     The settings of the repo picked in the sidebar: name, default workflow, Jira link, projects and saved filters.
///     Opened from the gear next to the sidebar's repo list rather than the page menu.
/// </summary>
[PageRegistration(DisplayName = "Repo settings", TopLevel = false)]
[PageIcon(PageIcons.Settings)]
public partial class RepoSettingsPage : AbstractAflPage<RepoSettingsPageDataContext>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RepoSettingsPage" /> class.
    /// </summary>
    public RepoSettingsPage()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Gets the page's data context.
    /// </summary>
    private RepoSettingsPageDataContext Settings => this.PageDataContext;

    /// <inheritdoc />
    protected override RepoSettingsPageDataContext CreatePageDataContext()
    {
        return new RepoSettingsPageDataContext(this.AppDataContext);
    }

    /// <summary>
    ///     Lists the linked Jira instance's projects and boards to pick from.
    /// </summary>
    /// <param name="e">The navigation.</param>
    protected override async void OnNavigatedTo(PageNavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await this.Settings.LoadFromJiraAsync();
    }

    /// <summary>
    ///     Saves the repo's name.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveName(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SaveName())
        {
            this.ShowToast("Repo renamed.");
        }
    }

    /// <summary>
    ///     Saves what the repo is built with.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveStack(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SaveStack())
        {
            this.ShowToast("Project info saved.");
        }
    }

    /// <summary>
    ///     Stops tracking the repo after confirming, then shows the runs of every repo.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRemoveRepo(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.Repo is not { } repo)
        {
            return;
        }

        if (await this.WarnAsync("Remove repo",
                $"Stop tracking {repo.Name}? Its runs and files are kept, and running afl in it registers it again.",
                "Remove")
            && this.Settings.RemoveRepo())
        {
            this.ShowToast($"Removed {repo.Name}.", ToastType.Info);
            this.NavigateTo<RunsPage>(false);
        }
    }

    /// <summary>
    ///     Lists the linked Jira instance's projects and boards again.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnLoadFromJira(object? sender, RoutedEventArgs e)
    {
        await this.Settings.LoadFromJiraAsync();
    }

    /// <summary>
    ///     Links the repo to the typed GitHub repository or GitLab project.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveHostLink(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SaveHostLink())
        {
            this.ShowToast($"Linked to {this.Settings.Repo?.Host}.");
        }
    }

    /// <summary>
    ///     Removes the repo's GitHub or GitLab link.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnUnlinkHost(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.UnlinkHost())
        {
            this.ShowToast("Unlinked.", ToastType.Info);
        }
    }

    /// <summary>
    ///     Links the repo to the project its remotes point at.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnDetectHost(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.DetectHostLink())
        {
            this.ShowToast($"Linked to {this.Settings.Repo?.Host}.");
        }
        else if (this.Settings.ErrorMessage is null)
        {
            this.ShowToast("No remote is on a known GitHub or GitLab site.", ToastType.Warning);
        }
    }

    /// <summary>
    ///     Adds the typed project key to the choices.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnAddProject(object? sender, RoutedEventArgs e)
    {
        this.Settings.AddProjectKey();
    }

    /// <summary>
    ///     Saves the repo's Jira projects.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveProjects(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SaveProjects())
        {
            this.ShowToast("Jira projects saved.");
        }
    }

    /// <summary>
    ///     Clears the filter form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnNewFilter(object? sender, RoutedEventArgs e)
    {
        this.Settings.NewFilter();
    }

    /// <summary>
    ///     Saves the filter form.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSaveFilter(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SaveFilter())
        {
            this.ShowToast("Filter saved.");
        }
    }

    /// <summary>
    ///     Makes the selected filter the default.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnMakeDefaultFilter(object? sender, RoutedEventArgs e)
    {
        this.Settings.MakeDefaultFilter();
    }

    /// <summary>
    ///     Clears the default filter.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnClearDefaultFilter(object? sender, RoutedEventArgs e)
    {
        this.Settings.ClearDefaultFilter();
    }

    /// <summary>
    ///     Removes the selected filter after confirming.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRemoveFilter(object? sender, RoutedEventArgs e)
    {
        if (this.Settings.SelectedFilter is { } filter
            && await this.WarnAsync("Remove filter", $"Remove the filter '{filter.Name}'?", "Remove")
            && this.Settings.RemoveFilter())
        {
            this.ShowToast("Filter removed.", ToastType.Info);
        }
    }
}
