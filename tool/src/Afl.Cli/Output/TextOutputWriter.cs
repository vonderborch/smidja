using Afl.Core.Exceptions;
using Afl.Core.Execution;

namespace Afl.Cli.Output;

/// <summary>
///     Writes human-readable output to the console.
/// </summary>
public class TextOutputWriter : AbstractOutputWriter
{
    /// <inheritdoc />
    public override bool IsInteractive => !Console.IsInputRedirected;

    /// <inheritdoc />
    public override void Result<T>(T data, Action<T, TextWriter> renderText)
    {
        renderText(data, Console.Out);
    }

    /// <inheritdoc />
    public override void Error(AflErrorKind? kind, string message)
    {
        WriteColoured(Console.Error, ConsoleColor.Red, $"error: {message}");
    }

    /// <inheritdoc />
    public override void Report(ExecutorEvent value)
    {
        switch (value.Kind)
        {
            case ExecutorEventKind.Text:
                Console.WriteLine(value.Text);
                break;
            case ExecutorEventKind.ToolUse:
                WriteColoured(Console.Out, ConsoleColor.DarkGray, $"  > {value.Text}");
                break;
            case ExecutorEventKind.Error:
                WriteColoured(Console.Error, ConsoleColor.Yellow, value.Text);
                break;
            default:
                WriteColoured(Console.Out, ConsoleColor.Cyan, $"[afl] {value.Text}");
                break;
        }
    }

    /// <summary>
    ///     Writes a line in a colour, when the output is a terminal.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="colour">The colour.</param>
    /// <param name="text">The text.</param>
    private static void WriteColoured(TextWriter writer, ConsoleColor colour, string text)
    {
        var redirected = writer == Console.Error ? Console.IsErrorRedirected : Console.IsOutputRedirected;
        if (redirected)
        {
            writer.WriteLine(text);
            return;
        }

        Console.ForegroundColor = colour;
        writer.WriteLine(text);
        Console.ResetColor();
    }
}
