using System.Collections.ObjectModel;
using Afl.Core.GitHosts;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     The Git hosts tab: GitHub and GitLab sites (github.com and gitlab.com built in, plus Enterprise and self-managed
///     ones) and their tokens. A token goes straight from the password box to <see cref="GitHostStore" /> and is
///     cleared once saved; only whether one is set is ever shown.
/// </summary>
public partial class SettingsPageDataContext
{
    /// <summary>
    ///     Gets the git host connections, built-in ones first.
    /// </summary>
    public ObservableCollection<GitHostConnection> GitHostConnections { get; } = [];

    /// <summary>
    ///     Gets or sets the connection being edited. Selecting a different one fills the form.
    /// </summary>
    public GitHostConnection? SelectedGitHost
    {
        get;
        set
        {
            var changed = value?.Id != field?.Id;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.CanRemoveGitHost));
            this.RaisePropertyChanged(nameof(this.IsGitHostFormLocked));
            if (changed && !this._loading)
            {
                this.FillGitHostForm(value);
            }
        }
    }

    /// <summary>
    ///     Gets whether the selected connection can be removed: added ones can, built-in ones can't.
    /// </summary>
    public bool CanRemoveGitHost => this.SelectedGitHost is { IsBuiltIn: false };

    /// <summary>
    ///     Gets whether only the token can be changed, because the selected connection is built in.
    /// </summary>
    public bool IsGitHostFormLocked => this.SelectedGitHost is { IsBuiltIn: true };

    /// <summary>
    ///     Gets the kinds of git host, e.g. <c>github</c> and <c>gitlab</c>.
    /// </summary>
    public IReadOnlyList<string> GitHostKinds { get; } = GitHostClientRegistry.Instance.Keys.Order().ToList();

    /// <summary>
    ///     Gets or sets the git host form's id.
    /// </summary>
    public string GitHostId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the git host form's display name.
    /// </summary>
    public string GitHostName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the git host form's kind.
    /// </summary>
    public string? GitHostKind
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.GitHostTokenUrl));
        }
    }

    /// <summary>
    ///     Gets or sets the git host form's web address, e.g. <c>https://git.acme.com</c>.
    /// </summary>
    public string GitHostBaseUrl
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.GitHostTokenUrl));
        }
    } = string.Empty;

    /// <summary>
    ///     Gets or sets a new token typed into the git host form. It's cleared as soon as it's saved.
    /// </summary>
    public string? GitHostToken
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets where the form connection's sign-in comes from, e.g. "Token set" or "Using gh's sign-in".
    /// </summary>
    public string? GitHostTokenStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the result of the last git host test or save.
    /// </summary>
    public string? GitHostStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets the page on the form's site where a personal access token is made, or null until the form names a site.
    /// </summary>
    public Uri? GitHostTokenUrl
    {
        get
        {
            if (this.GitHostKind is not { } kind || !GitHostClientRegistry.Instance.Contains(kind)
                                                 || !Uri.TryCreate(this.GitHostBaseUrl.Trim(), UriKind.Absolute, out _))
            {
                return null;
            }

            var connection = this.FormGitHost();
            return Uri.TryCreate(this.Shell.GitHosts.CreateClient(connection, null).CreateTokenUrl, UriKind.Absolute, out var url) ? url : null;
        }
    }

    /// <summary>
    ///     Clears the git host form to add a new connection.
    /// </summary>
    public void NewGitHost()
    {
        this.SelectedGitHost = null;
        this.FillGitHostForm(null);
    }

    /// <summary>
    ///     Saves the git host form. A newly typed token is tested against the site first, and only stored if it works.
    ///     A connection with no token still saves: it borrows the CLI's sign-in, or reads public projects anonymously.
    /// </summary>
    /// <returns>True if it was saved.</returns>
    public async Task<bool> SaveGitHostAsync()
    {
        var connection = this.FormGitHost();
        var token = string.IsNullOrWhiteSpace(this.GitHostToken) ? null : this.GitHostToken.Trim();
        var store = this.Shell.GitHosts;
        this.GitHostStatus = null;

        GitHostUser? user = null;
        var saved = await this.TryAsync(async () =>
        {
            if (store.Find(connection.Id) is not { IsBuiltIn: true })
            {
                GitHostStore.Validate(connection);
            }

            if (token is not null)
            {
                user = await store.CreateClient(store.Find(connection.Id) is { IsBuiltIn: true } builtIn ? builtIn : connection, token,
                    this.Shell.TicketSources.GitHostHttp).GetCurrentUserAsync();
            }

            store.Save(connection, token);
        });

        if (!saved)
        {
            return false;
        }

        this.GitHostToken = null;
        this.GitHostStatus = user is null ? $"Saved {connection.Id}." : $"Saved {connection.Id}; signed in as {user.Login}.";
        this.Reload();
        this.SelectedGitHost = this.GitHostConnections.FirstOrDefault(c => c.Id == connection.Id);
        return true;
    }

    /// <summary>
    ///     Signs in to the selected connection with whatever token it has, and says as whom.
    /// </summary>
    /// <returns>A task that completes when the test has finished.</returns>
    public async Task TestGitHostAsync()
    {
        if (this.SelectedGitHost is not { } connection)
        {
            return;
        }

        this.GitHostStatus = $"Testing {connection.Id}…";
        GitHostUser? user = null;
        var ok = await this.TryAsync(async () =>
        {
            var client = await this.Shell.GitHosts.CreateClientAsync(connection, this.Shell.TicketSources.GitHostHttp);
            user = await client.GetCurrentUserAsync();
        });
        this.GitHostStatus = ok ? $"{connection.Id}: signed in as {user!.Login}." : null;
    }

    /// <summary>
    ///     Removes the selected connection and its token. Built-in connections can't be removed.
    /// </summary>
    /// <returns>True if it was removed.</returns>
    public bool RemoveGitHost()
    {
        if (this.SelectedGitHost is not { IsBuiltIn: false } connection || !this.Try(() => this.Shell.GitHosts.Remove(connection.Id)))
        {
            return false;
        }

        this.GitHostStatus = $"Removed {connection.Id}.";
        this.NewGitHost();
        this.Reload();
        return true;
    }

    /// <summary>
    ///     Clears the selected connection's stored token, so it falls back to the CLI's sign-in.
    /// </summary>
    /// <returns>True if a token was cleared.</returns>
    public bool ClearGitHostToken()
    {
        if (this.SelectedGitHost is not { } connection || !this.Try(() => this.Shell.GitHosts.SetToken(connection.Id, null)))
        {
            return false;
        }

        this.GitHostStatus = $"Cleared {connection.Id}'s token.";
        _ = this.RefreshGitHostTokenStatusAsync();
        return true;
    }

    /// <summary>
    ///     Lists the git host connections, keeping the selection.
    /// </summary>
    private void LoadGitHosts()
    {
        var selectedId = this.SelectedGitHost?.Id;
        this.GitHostConnections.Clear();
        foreach (var connection in this.Shell.GitHosts.All())
        {
            this.GitHostConnections.Add(connection);
        }

        this.SelectedGitHost = this.GitHostConnections.FirstOrDefault(c => c.Id == selectedId);
        if (this.SelectedGitHost is null && (selectedId is not null || this.GitHostKind is null))
        {
            this.FillGitHostForm(null);
        }
        else
        {
            _ = this.RefreshGitHostTokenStatusAsync();
        }
    }

    /// <summary>
    ///     Fills the git host form from a connection, or clears it.
    /// </summary>
    /// <param name="connection">The connection, or null.</param>
    private void FillGitHostForm(GitHostConnection? connection)
    {
        this.GitHostId = connection?.Id ?? string.Empty;
        this.GitHostName = connection?.Name ?? string.Empty;
        this.GitHostKind = connection?.Kind ?? this.GitHostKinds.FirstOrDefault();
        this.GitHostBaseUrl = connection?.BaseUrl ?? string.Empty;
        this.GitHostToken = null;
        _ = this.RefreshGitHostTokenStatusAsync();
    }

    /// <summary>
    ///     Builds a connection from the git host form.
    /// </summary>
    /// <returns>The connection.</returns>
    private GitHostConnection FormGitHost()
    {
        return new GitHostConnection
        {
            Id = this.GitHostId.Trim(),
            Name = string.IsNullOrWhiteSpace(this.GitHostName) ? this.GitHostId.Trim() : this.GitHostName.Trim(),
            Kind = this.GitHostKind ?? string.Empty,
            BaseUrl = this.GitHostBaseUrl.Trim(),
        };
    }

    /// <summary>
    ///     Describes where the form connection's sign-in comes from, which can mean asking its CLI.
    /// </summary>
    /// <returns>A task that completes when <see cref="GitHostTokenStatus" /> is set.</returns>
    private async Task RefreshGitHostTokenStatusAsync()
    {
        if (this.SelectedGitHost is not { } connection)
        {
            this.GitHostTokenStatus = "New site: enter a token, or leave it empty to use the gh or glab sign-in.";
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

        // Another connection may have been picked while the CLI answered.
        if (this.SelectedGitHost?.Id != connection.Id)
        {
            return;
        }

        var variable = GitHostStore.TokenVariable(connection.Id);
        this.GitHostTokenStatus = status == "Not signed in"
            ? $"Not signed in. Enter a token, set {variable}, or sign in with the site's CLI. Public projects still list."
            : this.Shell.GitHosts.HasToken(connection.Id)
                ? $"{status}. Enter a new one to replace it."
                : $"{status}.";
    }
}
