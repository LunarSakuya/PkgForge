using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PkgForge;

/// <summary>
/// Cross-process lock file with a heartbeat and stale-lock reclamation. Locks
/// are only created below a caller-controlled lock directory.
/// </summary>
public sealed class ProcessLock : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly string _lockPath;
    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _heartbeat;
    private FileStream? _stream;
    private bool _disposed;

    private ProcessLock(string lockPath, string token, FileStream stream)
    {
        _lockPath = lockPath;
        _token = token;
        _stream = stream;
        _heartbeat = Task.Run(HeartbeatAsync);
    }

    public string LockPath => _lockPath;

    public static async Task<ProcessLock> AcquireAsync(
        string lockPath,
        TimeSpan timeout,
        TimeSpan staleAfter,
        ILog log,
        CancellationToken cancellationToken)
    {
        var fullPath = PathSafety.NormalizeAbsolutePath(lockPath, "lock file");
        var directory = System.IO.Path.GetDirectoryName(fullPath)
                        ?? throw PkgException.Unsafe("Lock file has no parent directory.");
        Directory.CreateDirectory(directory);
        PathSafety.EnsureNoReparsePoints(directory, fullPath, "lock file");

        var token = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.Read, 4096, FileOptions.WriteThrough);
                var content = Encoding.UTF8.GetBytes(
                    string.Create(CultureInfo.InvariantCulture,
                        $"pid={Environment.ProcessId}\ntoken={token}\ncreated={DateTimeOffset.UtcNow:O}\n"));
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
                return new ProcessLock(fullPath, token, stream);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                if (IsStale(fullPath, staleAfter, out var age))
                {
                    log.Warn($"Reclaiming stale lock '{fullPath}' (age {FormatAge(age)}).");
                    ReclaimStale(fullPath);
                    continue;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw PkgException.Lock(
                        $"Timed out waiting for lock '{fullPath}'. Another pkgforge process may be running.");
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        try
        {
            await _heartbeat.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Heartbeat failures are best effort and must not make disposal fail.
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        try
        {
            if (File.Exists(_lockPath))
            {
                var content = await File.ReadAllTextAsync(_lockPath).ConfigureAwait(false);
                if (content.Contains("token=" + _token, StringComparison.Ordinal))
                {
                    File.Delete(_lockPath);
                }
            }
        }
        catch (IOException)
        {
            // A lock that cannot be removed is reclaimed by age on the next run.
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, _stop.Token).ConfigureAwait(false);
                if (_stream is not null)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // A transient filesystem/permission error will be corrected by the next beat.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsStale(string path, TimeSpan staleAfter, out TimeSpan age)
    {
        try
        {
            age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            if (TryReadOwner(path, out var pid, out var created) &&
                !IsProcessAlive(pid, created))
            {
                return true;
            }

            return age > staleAfter;
        }
        catch (IOException)
        {
            age = TimeSpan.Zero;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            age = TimeSpan.Zero;
            return false;
        }
    }

    private static bool TryReadOwner(string path, out int pid, out DateTimeOffset created)
    {
        pid = 0;
        created = default;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("pid=", StringComparison.Ordinal) &&
                    int.TryParse(line[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid))
                {
                    pid = parsedPid;
                }
                else if (line.StartsWith("created=", StringComparison.Ordinal) &&
                         DateTimeOffset.TryParse(line[8..], CultureInfo.InvariantCulture,
                             DateTimeStyles.RoundtripKind, out var parsedCreated))
                {
                    created = parsedCreated;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return pid > 0;
    }

    private static bool IsProcessAlive(int pid, DateTimeOffset lockCreated)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            // A recycled PID has a newer process start time than the lock file.
            if (lockCreated != default &&
                process.StartTime.ToUniversalTime() > lockCreated.UtcDateTime.AddSeconds(2))
            {
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception)
        {
            // Access-denied and platform probing failures are not proof that the owner died.
            return true;
        }
    }

    private static void ReclaimStale(string path)
    {
        var stalePath = path + ".stale-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(path, stalePath);
            File.Delete(stalePath);
        }
        catch (IOException)
        {
            // Another process won the race; the normal wait loop will retry.
        }
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{age.TotalMinutes:0.#} min")
            : string.Create(CultureInfo.InvariantCulture, $"{age.TotalSeconds:0.#} s");
}
