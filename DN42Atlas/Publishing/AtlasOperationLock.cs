using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DN42Atlas.Publishing;

// The inode is permanent; ownership belongs to the open descriptor, never its existence.
public sealed class AtlasOperationLock : IDisposable
{
    private FileStream? stream;
    public string Path { get; }

    private AtlasOperationLock(string path, FileStream stream) { Path = path; this.stream = stream; }

    public static string GetPath(string statePath, string publishedDirectory)
    {
        var path = PublicationState.ValidateLocation(statePath, publishedDirectory) + ".operation-lock";
        PublicationState.EnsureOutsidePublicRoot(path, publishedDirectory);
        return path;
    }

    public static async Task<AtlasOperationLock> AcquireAsync(string statePath, string publishedDirectory,
        CancellationToken cancellationToken = default, Action? onContention = null) =>
        (await TryAcquireAsync(statePath, publishedDirectory, Timeout.InfiniteTimeSpan, cancellationToken, onContention))!;

    public static async Task<AtlasOperationLock?> TryAcquireAsync(string statePath, string publishedDirectory,
        TimeSpan timeout, CancellationToken cancellationToken = default, Action? onContention = null)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var path = GetPath(statePath, publishedDirectory);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var elapsed = Stopwatch.StartNew();
        var reported = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? candidate = null;
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
                    Share = FileShare.None, BufferSize = 1
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                candidate = new FileStream(path, options);
                if (OperatingSystem.IsLinux())
                {
                    // Verify flock ourselves: .NET's FileShare implementation is best-effort on Unix.
                    // LOCK_EX | LOCK_NB. Any error except EAGAIN fails closed.
                    if (Flock(candidate.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error != 11) throw new InvalidOperationException($"Atlas operation flock failed (errno {error}).");
                        candidate.Dispose();
                        candidate = null;
                    }
                }
                else if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Atlas operation locking supports Linux and Windows.");
                if (candidate != null) return new AtlasOperationLock(path, candidate);
            }
            catch (IOException ex) when (
                (OperatingSystem.IsLinux() && (ex.HResult & 0xffff) == 11) ||
                (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) is 32 or 33))
            {
                candidate?.Dispose();
            }
            catch { candidate?.Dispose(); throw; }
            if (!reported) { onContention?.Invoke(); reported = true; }
            if (timeout != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= timeout) return null;
            var delay = timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromMilliseconds(50)
                : TimeSpan.FromMilliseconds(Math.Min(50, Math.Max(0, (timeout - elapsed.Elapsed).TotalMilliseconds)));
            await Task.Delay(delay, cancellationToken);
        }
    }

    public void EnsureOwnership(string statePath, string publishedDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (stream == null || !Path.Equals(GetPath(statePath, publishedDirectory), comparison))
            throw new InvalidOperationException("This operation requires ownership of its Atlas operation lock.");
    }

    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);
}
