using Afl.UI.Pages;
using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Niddy.Avalonia;
using Niddy.Avalonia.PageSystem;
using Niddy.Avalonia.Toasts;

namespace Afl.UI.Views;

/// <summary>
///     The app's layout around the Niddy page view: a sidebar with the repo picker
///     (and its settings), the page menu, and buttons to add a repo and open the settings shared by every repo.
/// </summary>
public partial class ShellLayout : UserControl
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ShellLayout" /> class. Only for the XAML previewer.
    /// </summary>
    public ShellLayout()
        : this(new PageView())
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ShellLayout" /> class.
    /// </summary>
    /// <param name="pages">The page view to lay out.</param>
    public ShellLayout(PageView pages)
    {
        this.InitializeComponent();
        this.Pages = pages;
        this.PageHost.Content = pages;
        this.Menu.Target = pages;
        this.AddHandler(KeyDownEvent, this.OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    ///     Gets the page view.
    /// </summary>
    public PageView Pages { get; }

    /// <summary>
    ///     Gets the shell whose repos the sidebar lists.
    /// </summary>
    private ShellDataContext? Shell => this.DataContext as ShellDataContext;

    /// <summary>
    ///     Registers a repo picked from a folder dialog.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnAddRepo(object? sender, RoutedEventArgs e)
    {
        if (this.Shell is null || await FilePicker.OpenFolderPath(this, "Select a folder inside a git repo") is not { } folder)
        {
            return;
        }

        try
        {
            var repo = this.Shell.AddRepo(folder);
            Toast.Show(this, $"Added {repo.Name}.", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show(this, ex.Message, ToastType.Error, TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>
    ///     Opens the settings of the repo picked in the sidebar.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnRepoSettings(object? sender, RoutedEventArgs e)
    {
        if (this.Shell?.SelectedRepo is not null)
        {
            this.Pages.NavigateTo<RepoSettingsPage>(addToBackStack: true);
        }
    }

    /// <summary>
    ///     Opens the settings shared by every repo.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        this.Pages.NavigateTo<SettingsPage>(addToBackStack: true);
    }

    /// <summary>
    ///     Opens the settings on the platform's settings shortcut (Cmd+, on macOS, Ctrl+, elsewhere). The settings
    ///     pages aren't in the page menu, so the page view's own shortcut handling doesn't cover them.
    /// </summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">The key press.</param>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var commandModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e is { Handled: false, Key: Key.OemComma } && e.KeyModifiers == commandModifiers)
        {
            this.Pages.NavigateTo<SettingsPage>(addToBackStack: true);
            e.Handled = true;
        }
    }
}
