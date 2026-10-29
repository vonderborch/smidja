using Afl.Core.Exceptions;

namespace Afl.Core.Helpers;

/// <summary>
///     An exclusive, cross-process lock backed by a lock file. The operating system releases it if the process dies,
///     so a crashed front-end never leaves a run permanently locked.
/// </summary>
public sealed class FileLock : IDisposable
{
    /// <summary>
    ///     The open lock file.
    /// </summary>
    private readonly FileStream _stream;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FileLock" /> class.
    /// </summary>
    /// <param name="stream">The open lock file.</param>
    private FileLock(FileStream stream)
    {
        this._stream = stream;
    }

    /// <summary>
    ///     Releases the lock.
    /// </summary>
    public void Dispose()
    {
        this._stream.Dispose();
    }

    /// <summary>
    ///     Acquires the lock, waiting up to <paramref name="timeout" /> for another holder to release it.
    /// </summary>
    /// <param name="path">The lock file path.</param>
    /// <param name="timeout">How long to wait; <see cref="TimeSpan.Zero" /> fails immediately.</param>
    /// <param name="description">What is being locked, for the error message.</param>
    /// <returns>The held lock.</returns>
    /// <exception cref="AflException">The lock is still held after the timeout.</exception>
    public static FileLock Acquire(string path, TimeSpan timeout, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new AflException(AflErrorKind.Locked, $"{description} is in use by another afl process.", ex);
            }
        }
    }
}
