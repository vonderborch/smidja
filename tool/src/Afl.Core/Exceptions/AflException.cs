namespace Afl.Core.Exceptions;

/// <summary>
///     The category of an <see cref="AflException" />; front-ends use it to pick exit codes and messages.
/// </summary>
public enum AflErrorKind
{
    /// <summary>
    ///     A repo, workflow, run, or ticket could not be found.
    /// </summary>
    NotFound,

    /// <summary>
    ///     The user supplied something afl can't use.
    /// </summary>
    InvalidInput,

    /// <summary>
    ///     A workflow definition failed validation.
    /// </summary>
    InvalidWorkflow,

    /// <summary>
    ///     The requested action doesn't apply to the run or step's current state.
    /// </summary>
    InvalidState,

    /// <summary>
    ///     An external tool (<c>git</c>, <c>gh</c>, <c>claude</c>) failed or is missing.
    /// </summary>
    ExternalTool,

    /// <summary>
    ///     Another afl process holds the lock.
    /// </summary>
    Locked,

    /// <summary>
    ///     A stored document could not be read or written.
    /// </summary>
    Storage,
}

/// <summary>
///     The single exception type afl throws for expected failures.
/// </summary>
/// <param name="kind">The error category.</param>
/// <param name="message">A message suitable for showing to the user.</param>
/// <param name="inner">The underlying exception, if any.</param>
public class AflException(AflErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    ///     Gets the error category.
    /// </summary>
    public AflErrorKind Kind { get; } = kind;
}
