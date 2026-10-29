namespace Afl.Core.Helpers;

/// <summary>
///     Passes progress reports on to another receiver after changing them, e.g. to label the events of one of several
///     agents working at once.
/// </summary>
/// <typeparam name="T">The report type.</typeparam>
/// <param name="inner">The receiver.</param>
/// <param name="map">Changes each report.</param>
public class MappedProgress<T>(IProgress<T> inner, Func<T, T> map) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value)
    {
        inner.Report(map(value));
    }
}
