using Afl.Core.Helpers;
using Afl.UI.Attention;
using Afl.Core.Runs;
using Afl.UI.Pages;
using Afl.UI.DataContexts;
using Afl.UI.Views;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using Niddy.Avalonia.Hosting;
using Niddy.Avalonia.PageSystem;
using Niddy.Core.Logging;

namespace Afl.UI;

/// <summary>
///     The afl desktop application: a Niddy app whose app data context is the <see cref="ShellDataContext" /> and whose
///     layout is the <see cref="ShellLayout" /> sidebar around the page view.
/// </summary>
public class App : NiddyApp
{
    /// <summary>
    ///     The shell handed to every page, created in <see cref="Configure" />.
    /// </summary>
    private ShellDataContext? _shell;

    /// <summary>
    ///     Where the app logs, created in <see cref="Configure" /> and flushed on exit.
    /// </summary>
    private ILoggerFactory? _loggerFactory;

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();

        // afl's shared styles load after the Fluent theme Niddy adds, so they win where both set a property.
        AvaloniaXamlLoader.Load(this);
        ContextClicks.Install();
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) =>
            {
                this._shell?.Dispose();
                this._loggerFactory?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <inheritdoc />
    protected override void Configure(NiddyAppOptions options)
    {
        var shell = this._shell = this.CreateShell();
        options.AppName = "afl";
        options.Title = shell.Title;
        options.StartingPage = PageId.Of<RunsPage>();
        options.AppDataContext = shell;
        options.LoggerFactory = this._loggerFactory = this.CreateLoggerFactory(shell);
        options.Layout = pages => new ShellLayout(pages) { DataContext = shell };
        options.Width = 1280;
        options.Height = 800;
        options.MinWidth = 900;
        options.MinHeight = 560;
    }

    /// <summary>
    ///     Creates the shell the pages share, with an engine that notifies the user when a run needs them or finishes, and
    ///     flashes the taskbar button (or bounces the Dock icon) while the window isn't active. Tests override this to
    ///     point it at a scratch afl home, without notifications or flashing.
    /// </summary>
    /// <returns>The shell.</returns>
    protected virtual ShellDataContext CreateShell()
    {
        var paths = AflPaths.FromEnvironment();
        return new ShellDataContext(paths, new RunEngine(new RunStore(paths)) { Notifier = WindowRunNotifier.ForThisMachine(paths) });
    }

    /// <summary>
    ///     Creates the logger for unhandled exceptions and other app diagnostics: rolling files in the afl home's logs
    ///     folder. Return null to log nothing.
    /// </summary>
    /// <param name="shell">The shell, for its paths.</param>
    /// <returns>The logger factory, or null.</returns>
    protected virtual ILoggerFactory? CreateLoggerFactory(ShellDataContext shell)
    {
        return NiddyLogging.CreateFactory(shell.Paths.LogsDirectory);
    }
}
