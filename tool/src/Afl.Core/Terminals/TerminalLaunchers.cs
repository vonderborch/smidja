using Afl.Core.Constants;
using Afl.Core.Execution;

namespace Afl.Core.Terminals;

/// <summary>
///     Opens macOS Terminal through AppleScript.
/// </summary>
[TerminalLauncherRegistration(LauncherName)]
public class MacTerminalLauncher() : AbstractUnixTerminalLauncher(LauncherName)
{
    /// <summary>
    ///     The launcher name.
    /// </summary>
    public const string LauncherName = "macos-terminal";

    /// <inheritdoc />
    public override bool IsSupported => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    protected override (string Executable, IReadOnlyList<string> Arguments) BuildTerminalCommand(string scriptPath, string workingDirectory)
    {
        var command = Quote(scriptPath).Replace("\\", "\\\\").Replace("\"", "\\\"");
        return ("osascript",
        [
            "-e", $"tell application \"Terminal\" to do script \"{command}\"",
            "-e", "tell application \"Terminal\" to activate",
        ]);
    }
}

/// <summary>
///     Opens the desktop's default terminal on Linux: <c>$TERMINAL</c> if set, else <c>x-terminal-emulator</c>.
/// </summary>
[TerminalLauncherRegistration(LauncherName)]
public class LinuxTerminalLauncher() : AbstractUnixTerminalLauncher(LauncherName)
{
    /// <summary>
    ///     The launcher name.
    /// </summary>
    public const string LauncherName = "linux-terminal";

    /// <inheritdoc />
    public override bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD();

    /// <inheritdoc />
    protected override (string Executable, IReadOnlyList<string> Arguments) BuildTerminalCommand(string scriptPath, string workingDirectory)
    {
        var terminal = Environment.GetEnvironmentVariable("TERMINAL");
        return (string.IsNullOrWhiteSpace(terminal) ? "x-terminal-emulator" : terminal, ["-e", scriptPath]);
    }
}

/// <summary>
///     Opens a PowerShell window on Windows.
/// </summary>
[TerminalLauncherRegistration(LauncherName)]
public class WindowsTerminalLauncher() : AbstractTerminalLauncher(LauncherName)
{
    /// <summary>
    ///     The launcher name.
    /// </summary>
    public const string LauncherName = "windows-powershell";

    /// <inheritdoc />
    public override bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    protected override string ScriptExtension => ".ps1";

    /// <inheritdoc />
    public override string BuildScript(InteractiveLaunch launch)
    {
        var arguments = string.Join(" ", launch.Arguments.Select(Quote));
        var variables = string.Concat((launch.Environment ?? []).Select(e => $"$env:{e.Key} = {Quote(e.Value)}\n"));
        return $"Set-Location -LiteralPath {Quote(launch.WorkingDirectory)}\n{variables}& {Quote(launch.Executable)} {arguments}\n";
    }

    /// <inheritdoc />
    public override string BuildWatchedScript(ProcessLaunch launch, string filesBase)
    {
        var arguments = string.Join(" ", launch.Arguments.Select(Quote));
        var variables = string.Concat((launch.Environment ?? []).Select(e => $"$env:{e.Key} = {Quote(e.Value)}\n"));
        // PowerShell's own pid is recorded: killing its tree stops the command too.
        return $$"""
            Set-Location -LiteralPath {{Quote(launch.WorkingDirectory)}}
            {{variables}}$b = {{Quote(filesBase)}}
            Set-Content -LiteralPath "$b{{StepConstants.TerminalProcessExtension}}" -Value $PID
            Get-Content -LiteralPath "$b{{StepConstants.TerminalInputExtension}}" -Raw | & {{Quote(launch.Executable)}} {{arguments}} 2> "$b{{StepConstants.TerminalErrorExtension}}" | Tee-Object -FilePath "$b{{StepConstants.TerminalOutputExtension}}"
            $code = $LASTEXITCODE
            Set-Content -LiteralPath "$b{{StepConstants.TerminalExitExtension}}.tmp" -Value $code
            Move-Item -LiteralPath "$b{{StepConstants.TerminalExitExtension}}.tmp" -Destination "$b{{StepConstants.TerminalExitExtension}}" -Force
            Write-Host "afl: finished with exit code $code. You can close this window."

            """;
    }

    /// <summary>
    ///     Quotes a value for PowerShell: wraps it in single quotes, doubling any single quotes inside.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The quoted value.</returns>
    public static string Quote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    /// <inheritdoc />
    protected override (string Executable, IReadOnlyList<string> Arguments) BuildTerminalCommand(string scriptPath, string workingDirectory)
    {
        return ("cmd.exe", ["/c", "start", "afl", "powershell.exe", "-NoExit", "-ExecutionPolicy", "Bypass", "-File", scriptPath]);
    }
}
