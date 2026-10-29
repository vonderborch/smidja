using System.ComponentModel;
using Afl.Core.Repos;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     Base for page data contexts: built from the <see cref="ShellDataContext" /> that owns the stores, and reloaded when
///     the repo picked in the sidebar changes.
/// </summary>
public abstract class AbstractPageDataContext : AbstractReloadingDataContext
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AbstractPageDataContext" /> class.
    /// </summary>
    /// <param name="shell">The shell that owns the stores.</param>
    protected AbstractPageDataContext(ShellDataContext shell)
    {
        this.Shell = shell;
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(ShellDataContext.SelectedRepo))
            {
                this.OnRepoChanged();
            }
        };
        this.OnDispose<PropertyChangedEventHandler>(h => shell.PropertyChanged += h, h => shell.PropertyChanged -= h, handler);
    }

    /// <summary>
    ///     Gets the shell that owns the stores.
    /// </summary>
    public ShellDataContext Shell { get; }

    /// <summary>
    ///     Gets the repo selected in the sidebar, or null for all repos.
    /// </summary>
    public RepoRecord? Repo => this.Shell.SelectedRepo;

    /// <summary>
    ///     Gets whether a repo is selected.
    /// </summary>
    public bool HasRepo => this.Repo is not null;

    /// <summary>
    ///     Gets the hint shown by repo-specific parts of the page when no repo is selected.
    /// </summary>
    public string NoRepoMessage => "Pick a repo from the list at the top of the sidebar.";

    /// <summary>
    ///     Called when the sidebar's repo selection changes. Reloads the page by default.
    /// </summary>
    protected virtual void OnRepoChanged()
    {
        this.RaisePropertyChanged(nameof(this.Repo));
        this.RaisePropertyChanged(nameof(this.HasRepo));
        this.Reload();
    }

    /// <summary>
    ///     Raises <see cref="INotifyPropertyChanged.PropertyChanged" /> for several properties.
    /// </summary>
    /// <param name="names">The property names.</param>
    protected void RaisePropertiesChanged(params string[] names)
    {
        foreach (var name in names)
        {
            this.RaisePropertyChanged(name);
        }
    }
}
