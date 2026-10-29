using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;

namespace Afl.Cli.Output;

/// <summary>
///     Writes results as JSON on stdout, for scripts and the plugin. Progress goes to stderr so stdout stays one
///     JSON document; the user is never prompted.
/// </summary>
public class JsonOutputWriter : AbstractOutputWriter
{
    /// <inheritdoc />
    public override bool IsInteractive => false;

    /// <inheritdoc />
    public override void Result<T>(T data, Action<T, TextWriter> renderText)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(data, JsonConstants.Options));
    }

    /// <inheritdoc />
    public override void Error(AflErrorKind? kind, string message)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new { error = message, kind }, JsonConstants.Options));
    }

    /// <inheritdoc />
    public override void Report(ExecutorEvent value)
    {
        if (value.Kind != ExecutorEventKind.Text)
        {
            Console.Error.WriteLine($"[afl] {value.Text}");
        }
    }
}
