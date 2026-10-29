using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Afl.Core.Exceptions;

namespace Afl.Core.Helpers;

/// <summary>
///     The outcome of a finished process.
/// </summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="StandardOutput">Everything written to stdout.</param>
/// <param name="StandardError">Everything written to stderr.</param>
public record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    ///     Gets whether the process exited with code 0.
    /// </summary>
    public bool Success => this.ExitCode == 0;
}

/// <summary>
///     Runs external tools (<c>git</c>, <c>gh</c>, <c>claude</c>) with arguments passed as a list, never through a shell.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    ///     Runs a process to completion, capturing its output.
    /// </summary>
    /// <param name="executable">The executable name or path.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The working directory, or null for the current one.</param>
    /// <param name="onOutputLine">Called for each stdout line as it arrives, if set.</param>
    /// <param name="ct">A cancellation token; cancelling kills the process tree.</param>
    /// <param name="standardInput">Text written to the process's stdin before it is closed, if any.</param>
    /// <param name="environment">Environment variables to set for the process, if any.</param>
    /// <returns>The result.</returns>
    /// <exception cref="AflException">The executable could not be started.</exception>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments, workingDirectory, true) };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            process.StartInfo.Environment[name] = value;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        Start(process, executable);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The process exited without reading its input; its exit code says what happened.
            }
        }

        process.StandardInput.Close();

        try
        {
            // No captured context: Run blocks on this, and would otherwise deadlock when called from a UI thread.
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    ///     Runs a process synchronously, capturing its output. Intended for quick commands such as <c>git remote</c>.
    /// </summary>
    /// <param name="executable">The executable name or path.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The working directory, or null for the current one.</param>
    /// <returns>The result.</returns>
    public static ProcessResult Run(string executable, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        return RunAsync(executable, arguments, workingDirectory).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Runs a process attached to the current terminal (inherited stdin/stdout/stderr) and waits for it to exit.
    /// </summary>
    /// <param name="executable">The executable name or path.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The working directory, or null for the current one.</param>
    /// <param name="environment">Environment variables to set for the process, if any.</param>
    /// <returns>The exit code.</returns>
    public static int RunInteractive(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments, workingDirectory, false) };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            process.StartInfo.Environment[name] = value;
        }

        Start(process, executable);
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    ///     Builds the start info for a process.
    /// </summary>
    /// <param name="executable">The executable name or path.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The working directory, or null for the current one.</param>
    /// <param name="redirect">Whether to capture the standard streams.</param>
    /// <returns>The start info.</returns>
    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        bool redirect)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirect,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    /// <summary>
    ///     Starts a process, turning a missing executable into an <see cref="AflException" />.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <param name="executable">The executable, for the error message.</param>
    private static void Start(Process process, string executable)
    {
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new AflException(AflErrorKind.ExternalTool, $"Could not start '{executable}'. Is it installed and on PATH?", ex);
        }
    }
}
