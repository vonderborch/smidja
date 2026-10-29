using System.Diagnostics;
using System.Text;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Registration;

namespace Afl.Core.Terminals;

/// <summary>
///     Registers a terminal launcher with the <see cref="TerminalLauncherRegistry" />.
/// </summary>
/// <param name="name">The launcher name.</param>
public class TerminalLauncherRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     Opens an interactive step's command (<see cref="InteractiveLaunch" />) in a new system terminal window, for front-ends
///     such as the desktop UI that don't own a terminal themselves. The command is written to a small script next to the
///     step's log and the terminal runs that script, which avoids quoting a long, multi-line prompt through the terminal's
///     own command line. A new platform is one subclass with a <see cref="TerminalLauncherRegistrationAttribute" />.
/// </summary>
/// <param name="name">The launcher name.</param>
public abstract class AbstractTerminalLauncher(string name)
{
    /// <summary>
    ///     Gets the launcher name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets whether this launcher works on the current operating system.
    /// </summary>
    public abstract bool IsSupported { get; }

    /// <summary>
    ///     Gets the extension of the script this launcher writes, including the dot.
    /// </summary>
    protected abstract string ScriptExtension { get; }

    /// <summary>
    ///     Writes the launch script and opens a terminal running it.
    /// </summary>
    /// <param name="launch">The command to run.</param>
    /// <param name="scriptPathWithoutExtension">Where to write the script; the launcher adds its extension.</param>
    /// <returns>The script path.</returns>
    /// <exception cref="AflException">The terminal couldn't be started.</exception>
    public string Open(InteractiveLaunch launch, string scriptPathWithoutExtension)
    {
        return this.OpenScript(this.BuildScript(launch), scriptPathWithoutExtension, launch.WorkingDirectory);
    }

    /// <summary>
    ///     Writes a wrapper script for one headless turn and opens a terminal running it, so you can watch the command
    ///     work. The script reads stdin from <paramref name="filesBase" /> plus <see cref="StepConstants.TerminalInputExtension" />,
    ///     copies stdout to <see cref="StepConstants.TerminalOutputExtension" />, sends stderr to
    ///     <see cref="StepConstants.TerminalErrorExtension" />, writes a process id afl can stop to
    ///     <see cref="StepConstants.TerminalProcessExtension" />, and finally writes the exit code to
    ///     <see cref="StepConstants.TerminalExitExtension" />.
    /// </summary>
    /// <param name="launch">The command to run.</param>
    /// <param name="filesBase">The path, without extension, of the files the script and afl share.</param>
    /// <returns>The script path.</returns>
    /// <exception cref="AflException">The terminal couldn't be started.</exception>
    public string OpenWatched(ProcessLaunch launch, string filesBase)
    {
        return this.OpenScript(this.BuildWatchedScript(launch, filesBase), filesBase, launch.WorkingDirectory);
    }

    /// <summary>
    ///     Builds the wrapper script <see cref="OpenWatched" /> runs.
    /// </summary>
    /// <param name="launch">The command to run.</param>
    /// <param name="filesBase">The path, without extension, of the files the script and afl share.</param>
    /// <returns>The script's text.</returns>
    public abstract string BuildWatchedScript(ProcessLaunch launch, string filesBase);

    /// <summary>
    ///     Writes a script and opens a terminal running it.
    /// </summary>
    /// <param name="script">The script's text.</param>
    /// <param name="scriptPathWithoutExtension">Where to write the script; the launcher adds its extension.</param>
    /// <param name="workingDirectory">The terminal's working directory.</param>
    /// <returns>The script path.</returns>
    /// <exception cref="AflException">The terminal couldn't be started.</exception>
    protected string OpenScript(string script, string scriptPathWithoutExtension, string workingDirectory)
    {
        var scriptPath = scriptPathWithoutExtension + this.ScriptExtension;
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        this.PrepareScript(scriptPath);

        var (executable, arguments) = this.BuildTerminalCommand(scriptPath, workingDirectory);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AflException(AflErrorKind.ExternalTool, $"Couldn't open a terminal with {executable}: {ex.Message}");
        }

        return scriptPath;
    }

    /// <summary>
    ///     Builds the script that changes to the working directory and runs the command.
    /// </summary>
    /// <param name="launch">The command to run.</param>
    /// <returns>The script text.</returns>
    public abstract string BuildScript(InteractiveLaunch launch);

    /// <summary>
    ///     Builds the command that opens a terminal running the script.
    /// </summary>
    /// <param name="scriptPath">The script path.</param>
    /// <param name="workingDirectory">The directory to start in.</param>
    /// <returns>The executable and its arguments.</returns>
    protected abstract (string Executable, IReadOnlyList<string> Arguments) BuildTerminalCommand(string scriptPath, string workingDirectory);

    /// <summary>
    ///     Called after the script is written, e.g. to make it executable.
    /// </summary>
    /// <param name="scriptPath">The script path.</param>
    protected virtual void PrepareScript(string scriptPath)
    {
    }
}

/// <summary>
///     Base for launchers on Unix-like systems: they share a <c>sh</c> script with single-quoted arguments.
/// </summary>
/// <param name="name">The launcher name.</param>
public abstract class AbstractUnixTerminalLauncher(string name) : AbstractTerminalLauncher(name)
{
    /// <inheritdoc />
    protected override string ScriptExtension => ".sh";

    /// <inheritdoc />
    public override string BuildScript(InteractiveLaunch launch)
    {
        var command = string.Join(" ", new[] { launch.Executable }.Concat(launch.Arguments).Select(Quote));
        var exports = string.Concat((launch.Environment ?? []).Select(e => $"export {e.Key}={Quote(e.Value)}\n"));
        return $"#!/bin/sh\ncd {Quote(launch.WorkingDirectory)} || exit 1\n{exports}{command}\n";
    }

    /// <inheritdoc />
    public override string BuildWatchedScript(ProcessLaunch launch, string filesBase)
    {
        var command = string.Join(" ", new[] { launch.Executable }.Concat(launch.Arguments).Select(Quote));
        var exports = string.Concat((launch.Environment ?? []).Select(e => $"export {e.Key}={Quote(e.Value)}\n"));
        // The command runs in the background of a pipeline so its own pid can be recorded (afl kills it to cancel), and
        // its exit code is kept in a file because a pipeline's status is tee's. The exit file is moved into place so
        // afl never reads it half-written; closing the window still reports an exit code.
        return $$"""
            #!/bin/sh
            cd {{Quote(launch.WorkingDirectory)}} || exit 1
            {{exports}}b={{Quote(filesBase)}}
            finish() { printf '%s\n' "$1" > "$b{{StepConstants.TerminalExitExtension}}.tmp" && mv "$b{{StepConstants.TerminalExitExtension}}.tmp" "$b{{StepConstants.TerminalExitExtension}}"; }
            trap 'finish 129; exit 129' HUP
            { {{command}} < "$b{{StepConstants.TerminalInputExtension}}" 2> "$b{{StepConstants.TerminalErrorExtension}}" & echo $! > "$b{{StepConstants.TerminalProcessExtension}}"; wait $!; echo $? > "$b.code"; } | tee "$b{{StepConstants.TerminalOutputExtension}}"
            code=$(cat "$b.code" 2>/dev/null || echo 1)
            if [ -s "$b{{StepConstants.TerminalErrorExtension}}" ]; then cat "$b{{StepConstants.TerminalErrorExtension}}" >&2; fi
            finish "$code"
            printf '\nafl: finished with exit code %s. You can close this window.\n' "$code"

            """;
    }

    /// <summary>
    ///     Quotes a value for <c>sh</c>: wraps it in single quotes, escaping any single quotes inside.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The quoted value.</returns>
    public static string Quote(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <inheritdoc />
    protected override void PrepareScript(string scriptPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>
///     The terminal launchers afl knows about, discovered from <see cref="TerminalLauncherRegistrationAttribute" />.
/// </summary>
public class TerminalLauncherRegistry() : TypeRegistry<AbstractTerminalLauncher, TerminalLauncherRegistrationAttribute>("terminal launcher")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static TerminalLauncherRegistry Instance { get; } = new();

    /// <summary>
    ///     Returns the launcher for the current operating system.
    /// </summary>
    /// <returns>The launcher.</returns>
    /// <exception cref="AflException">No launcher supports this system.</exception>
    public AbstractTerminalLauncher ForCurrentPlatform()
    {
        return this.CreateAll().FirstOrDefault(l => l.IsSupported) ?? throw new AflException(AflErrorKind.InvalidState,
            "afl doesn't know how to open a terminal on this system. Run the step from the CLI instead: afl run resume <id>");
    }
}
