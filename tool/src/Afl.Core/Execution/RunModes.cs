using System.Diagnostics;
using System.Text;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Registration;
using Afl.Core.Terminals;

namespace Afl.Core.Execution;

/// <summary>
///     Registers an <see cref="AbstractRunMode" /> under the name runs and settings use for it.
/// </summary>
/// <param name="name">The mode name.</param>
public class RunModeRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     How a command-line agent's process runs for one headless turn. Every mode hands back the command's output line by
///     line as it arrives, then its exit code, stdout and stderr, so the agent reads its answer the same way whichever
///     mode ran it. A new mode is one subclass with <see cref="RunModeRegistrationAttribute" />.
/// </summary>
/// <param name="name">The mode name.</param>
public abstract class AbstractRunMode(string name)
{
    /// <summary>
    ///     Gets the mode name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets the name shown in front-ends.
    /// </summary>
    public abstract string DisplayName { get; }

    /// <summary>
    ///     Gets a line describing the mode, shown in front-ends.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    ///     Gets a value indicating whether a person watches the command's output as it runs, so an agent should prefer
    ///     output meant for people over output meant for afl when it has the choice.
    /// </summary>
    public virtual bool IsWatched => false;

    /// <summary>
    ///     Runs the command to the end.
    /// </summary>
    /// <param name="launch">The command.</param>
    /// <param name="filesBase">A private path, without extension, the mode may create files beside; the caller deletes them.</param>
    /// <param name="terminal">The terminal to open, for modes that need one; null for this platform's.</param>
    /// <param name="onOutputLine">Receives each line of standard output as it arrives.</param>
    /// <param name="ct">A cancellation token; cancelling stops the command.</param>
    /// <returns>The command's result.</returns>
    public abstract Task<ProcessResult> RunAsync(
        ProcessLaunch launch,
        string filesBase,
        AbstractTerminalLauncher? terminal,
        Action<string>? onOutputLine,
        CancellationToken ct);
}

/// <summary>
///     Runs the command out of sight, capturing its output. This is the default.
/// </summary>
[RunModeRegistration(AgentConstants.RunModeBackground)]
public class BackgroundRunMode() : AbstractRunMode(AgentConstants.RunModeBackground)
{
    /// <inheritdoc />
    public override string DisplayName => "Background";

    /// <inheritdoc />
    public override string Description => "Agents' commands run out of sight; follow them in the step log.";

    /// <inheritdoc />
    public override Task<ProcessResult> RunAsync(
        ProcessLaunch launch,
        string filesBase,
        AbstractTerminalLauncher? terminal,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        return ProcessRunner.RunAsync(launch.Executable, launch.Arguments, launch.WorkingDirectory, onOutputLine, ct,
            launch.StandardInput, launch.Environment);
    }
}

/// <summary>
///     Runs the command in a terminal window you can watch. The terminal runs a wrapper script (see
///     <see cref="AbstractTerminalLauncher.OpenWatched" />) that copies the output to a file, which afl follows, and
///     writes the exit code to another once the command is done. Cancelling kills the command through the process id
///     the script records. Closing the window early fails the turn.
/// </summary>
[RunModeRegistration(AgentConstants.RunModeTerminal)]
public class TerminalRunMode() : AbstractRunMode(AgentConstants.RunModeTerminal)
{
    /// <summary>
    ///     Gets or sets how long the terminal has to start the command before afl gives up on it.
    /// </summary>
    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Gets or sets how long afl waits for the exit code once the command's process has gone, before deciding the window was closed.
    /// </summary>
    public static TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Gets or sets how often afl checks the files the script writes.
    /// </summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc />
    public override string DisplayName => "Terminal";

    /// <inheritdoc />
    public override string Description => "Agents' commands run in a terminal window you can watch.";

    /// <inheritdoc />
    public override bool IsWatched => true;

    /// <inheritdoc />
    public override async Task<ProcessResult> RunAsync(
        ProcessLaunch launch,
        string filesBase,
        AbstractTerminalLauncher? terminal,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        var exitFile = filesBase + StepConstants.TerminalExitExtension;
        var processFile = filesBase + StepConstants.TerminalProcessExtension;
        await File.WriteAllTextAsync(filesBase + StepConstants.TerminalInputExtension, launch.StandardInput ?? string.Empty,
            new UTF8Encoding(false), ct).ConfigureAwait(false);

        (terminal ?? TerminalLauncherRegistry.Instance.ForCurrentPlatform()).OpenWatched(launch, filesBase);

        var output = new OutputFollower(filesBase + StepConstants.TerminalOutputExtension, onOutputLine);
        var started = DateTimeOffset.Now;
        DateTimeOffset? gone = null;
        int? processId = null;
        try
        {
            while (!File.Exists(exitFile))
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                output.ReadAvailable();
                processId ??= ReadProcessId(processFile);
                if (processId is null)
                {
                    if (DateTimeOffset.Now - started > StartTimeout)
                    {
                        throw new AflException(AflErrorKind.ExternalTool,
                            "The terminal didn't start the agent's command. Check that afl may control your terminal, or run in the background.");
                    }

                    continue;
                }

                if (IsRunning(processId.Value))
                {
                    gone = null;
                    continue;
                }

                gone ??= DateTimeOffset.Now;
                if (DateTimeOffset.Now - gone > ExitGrace && !File.Exists(exitFile))
                {
                    output.Finish();
                    return new ProcessResult(-1, output.Text, "The terminal window was closed before the command finished.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Stop(processId ?? ReadProcessId(processFile));
            output.Dispose();
            throw;
        }

        output.Finish();
        var exitCode = int.TryParse((await File.ReadAllTextAsync(exitFile, CancellationToken.None).ConfigureAwait(false)).Trim(),
            out var code) ? code : 1;
        var errorFile = filesBase + StepConstants.TerminalErrorExtension;
        var standardError = File.Exists(errorFile) ? await File.ReadAllTextAsync(errorFile, CancellationToken.None).ConfigureAwait(false) : string.Empty;
        return new ProcessResult(exitCode, output.Text, standardError);
    }

    /// <summary>
    ///     Reads the process id the script wrote, if it has written it yet.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The id, or null.</returns>
    private static int? ReadProcessId(string path)
    {
        try
        {
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Checks whether a process is still running.
    /// </summary>
    /// <param name="processId">The process id.</param>
    /// <returns>True when it is.</returns>
    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Stops the command (and anything it started), if it's still running.
    /// </summary>
    /// <param name="processId">The process id, or null when the script never recorded one.</param>
    private static void Stop(int? processId)
    {
        if (processId is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            process.Kill(true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    ///     Follows a file another process is appending to, handing on each complete line.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="onLine">Receives each line.</param>
    private sealed class OutputFollower(string path, Action<string>? onLine) : IDisposable
    {
        private readonly StringBuilder _text = new();

        private readonly StringBuilder _pending = new();

        private readonly char[] _buffer = new char[8192];

        private StreamReader? _reader;

        /// <summary>
        ///     Gets everything read so far.
        /// </summary>
        public string Text => this._text.ToString();

        /// <summary>
        ///     Reads whatever has been written since the last call.
        /// </summary>
        public void ReadAvailable()
        {
            if (this._reader is null)
            {
                if (!File.Exists(path))
                {
                    return;
                }

                // The script may still be writing (or, on Windows, the file was written with a byte order mark).
                this._reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, true);
            }

            int read;
            while ((read = this._reader.Read(this._buffer, 0, this._buffer.Length)) > 0)
            {
                this._pending.Append(this._buffer, 0, read);
            }

            var pending = this._pending.ToString();
            var end = pending.LastIndexOf('\n');
            if (end < 0)
            {
                return;
            }

            foreach (var line in pending[..end].Split('\n'))
            {
                this.Emit(line);
            }

            this._pending.Clear().Append(pending[(end + 1)..]);
        }

        /// <summary>
        ///     Reads the rest of the file, hands on any last unfinished line and closes it.
        /// </summary>
        public void Finish()
        {
            this.ReadAvailable();
            if (this._pending.Length > 0)
            {
                this.Emit(this._pending.ToString());
                this._pending.Clear();
            }

            this.Dispose();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this._reader?.Dispose();
            this._reader = null;
        }

        /// <summary>
        ///     Records one line and hands it on.
        /// </summary>
        /// <param name="line">The line, possibly ending in a carriage return.</param>
        private void Emit(string line)
        {
            line = line.TrimEnd('\r');
            this._text.AppendLine(line);
            onLine?.Invoke(line);
        }
    }
}

/// <summary>
///     Discovers the <see cref="AbstractRunMode" />s.
/// </summary>
public class RunModeRegistry() : TypeRegistry<AbstractRunMode, RunModeRegistrationAttribute>("run mode")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static RunModeRegistry Instance { get; } = new();

    /// <summary>
    ///     Creates the mode with a name, or the background mode when the name is unset or unknown (a run saved by a newer
    ///     afl, say).
    /// </summary>
    /// <param name="name">The mode name, or null.</param>
    /// <returns>The mode.</returns>
    public AbstractRunMode Resolve(string? name)
    {
        return this.Create(name is not null && this.Contains(name) ? name : AgentConstants.RunModeBackground);
    }
}
