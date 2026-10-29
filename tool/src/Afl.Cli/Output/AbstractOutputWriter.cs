using System.Globalization;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Steps;
using Afl.Core.Workflows;

namespace Afl.Cli.Output;

/// <summary>
///     Base for how commands write results, errors and live progress. Commands hand over data plus a text renderer;
///     <see cref="TextOutputWriter" /> renders it for people and <see cref="JsonOutputWriter" /> serializes it for
///     scripts and the plugin.
/// </summary>
public abstract class AbstractOutputWriter : IProgress<ExecutorEvent>
{
    /// <summary>
    ///     Gets whether the user can be asked questions on the console.
    /// </summary>
    public abstract bool IsInteractive { get; }

    /// <summary>
    ///     Writes a command's result.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="data">The result.</param>
    /// <param name="renderText">Renders the result as text.</param>
    public abstract void Result<T>(T data, Action<T, TextWriter> renderText);

    /// <summary>
    ///     Writes an error.
    /// </summary>
    /// <param name="kind">The kind of error, if known.</param>
    /// <param name="message">The message.</param>
    public abstract void Error(AflErrorKind? kind, string message);

    /// <summary>
    ///     Writes a live progress event.
    /// </summary>
    /// <param name="value">The event.</param>
    public abstract void Report(ExecutorEvent value);

    /// <summary>
    ///     Writes an informational message that isn't part of the result.
    /// </summary>
    /// <param name="message">The message.</param>
    public virtual void Info(string message)
    {
        this.Report(new ExecutorEvent(ExecutorEventKind.Status, message));
    }

    /// <summary>
    ///     Asks the user a yes/no question.
    /// </summary>
    /// <param name="question">The question.</param>
    /// <param name="defaultAnswer">The answer when the user just presses enter, or can't be asked.</param>
    /// <returns>The answer.</returns>
    public virtual bool Confirm(string question, bool defaultAnswer)
    {
        if (!this.IsInteractive)
        {
            return defaultAnswer;
        }

        Console.Write($"{question} {(defaultAnswer ? "[Y/n]" : "[y/N]")} ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(answer) ? defaultAnswer : answer.StartsWith('y');
    }

    /// <summary>
    ///     Asks the user for a line of text.
    /// </summary>
    /// <param name="question">The question.</param>
    /// <returns>The answer, or null when empty or the user can't be asked.</returns>
    public virtual string? Ask(string question)
    {
        if (!this.IsInteractive)
        {
            return null;
        }

        Console.Write($"{question} ");
        var answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) ? null : answer;
    }

    /// <summary>
    ///     Asks the user for a step input with the control its type uses, asking again until the answer is valid.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="type">The input's type.</param>
    /// <returns>The answer in canonical form, or null when it was left blank or the user can't be asked.</returns>
    public virtual string? AskInput(StepInput input, AbstractStepInputType type)
    {
        if (!this.IsInteractive)
        {
            return null;
        }

        var optional = input.Required ? string.Empty : " (optional)";
        var fallback = string.IsNullOrWhiteSpace(input.Default) ? string.Empty : $" [{input.Default}]";
        Console.WriteLine();
        Console.WriteLine($"{input.Label}{optional}");
        if (!string.IsNullOrWhiteSpace(input.Help))
        {
            Console.WriteLine($"  {input.Help}");
        }

        while (true)
        {
            var raw = type.Editor switch
            {
                StepInputEditor.MultilineText => ReadLines($"  Type it, then an empty line to finish{fallback}:"),
                StepInputEditor.Choice => ReadOptions(input, $"  Pick a number{fallback}:"),
                StepInputEditor.MultiChoice => ReadOptions(input, $"  Pick numbers, comma-separated{fallback}:"),
                StepInputEditor.Toggle => this.Ask($"  y/n{fallback}:"),
                _ => this.Ask($"  >{fallback}"),
            };

            try
            {
                return type.Resolve(input, raw);
            }
            catch (AflException ex)
            {
                Console.WriteLine($"  {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Reads a secret without echoing it. When stdin is redirected, the first line of stdin is used instead, so
    ///     scripts can pipe a token in.
    /// </summary>
    /// <param name="prompt">The prompt, written to stderr.</param>
    /// <returns>The secret, or null if nothing was entered.</returns>
    public virtual string? ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            var piped = Console.In.ReadLine()?.Trim();
            return string.IsNullOrEmpty(piped) ? null : piped;
        }

        Console.Error.Write($"{prompt} ");
        var secret = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        var value = secret.ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    ///     Reads lines until an empty one.
    /// </summary>
    /// <param name="prompt">The prompt.</param>
    /// <returns>The lines, or null when none were typed.</returns>
    private static string? ReadLines(string prompt)
    {
        Console.WriteLine(prompt);
        var lines = new List<string>();
        while (Console.ReadLine() is { } line && line.Length > 0)
        {
            lines.Add(line);
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    ///     Lists an input's options by number and reads the pick. Numbers are turned into the options they stand for;
    ///     typed option names are kept as they are.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="prompt">The prompt.</param>
    /// <returns>The picked options, comma-separated, or null when nothing was typed.</returns>
    private static string? ReadOptions(StepInput input, string prompt)
    {
        var options = input.Options ?? [];
        for (var i = 0; i < options.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {options[i]}");
        }

        Console.Write($"{prompt} ");
        var answer = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var picks = MultiChoiceInputType.Split(answer).Select(p =>
            int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= options.Count
                ? options[n - 1]
                : p);
        return string.Join(",", picks);
    }
}
