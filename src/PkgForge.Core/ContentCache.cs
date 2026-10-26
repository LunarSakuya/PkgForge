namespace PkgForge;

public sealed record DownloadItem(
    string Path,
    string Sha1,
    long Size,
    string Role,
    IReadOnlyList<Uri> Urls);

public sealed class ContentCache
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockStaleAfter = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan TemporaryRetention = TimeSpan.FromHours(24);

    private readonly AppPaths _paths;
    private readonly HttpDownloader _downloader;
    private readonly ILog _log;

    public ContentCache(AppPaths paths, HttpDownloader downloader, ILog log)
    {
        _paths = paths;
        _downloader = downloader;
        _log = log;
    }

    public async Task<string> GetOrDownloadAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        ValidateItem(item);
        _paths.EnsureGlobalLayout();
        var cachePath = _paths.CacheObjectPath(item.Sha1);
        PathSafety.EnsureNoReparsePoints(_paths.CacheObjects, cachePath, "cache object");
        if (await IsValidAsync(cachePath, item, cancellationToken).ConfigureAwait(false))
        {
            return cachePath;
        }

        var lockPath = Path.Combine(_paths.CacheLocks, item.Sha1 + ".lock");
        await using var cacheLock = await ProcessLock.AcquireAsync(lockPath, LockTimeout, LockStaleAfter,
            _log, cancellationToken).ConfigureAwait(false);

        if (await IsValidAsync(cachePath, item, cancellationToken).ConfigureAwait(false))
        {
            return cachePath;
        }

        if (File.Exists(cachePath))
        {
            _log.Warn($"Discarding corrupt cache object {item.Sha1}.");
            File.Delete(cachePath);
        }

        DiskSpace.EnsureAvailable(_paths.CacheTemporary, item.Size, "content download");
        var temporaryPath = Path.Combine(_paths.CacheTemporary, item.Sha1 + "-" + Guid.NewGuid().ToString("N") + ".part");
        PathSafety.EnsureNoReparsePoints(_paths.CacheTemporary, temporaryPath, "cache temporary file");
        Exception? lastError = null;
        try
        {
            foreach (var uri in item.Urls)
            {
                try
                {
                    await _downloader.DownloadToFileAsync(uri, temporaryPath, item.Size, item.Sha1,
                        item.Path, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (PkgException exception) when (exception.ExitCode is ExitCode.Network or ExitCode.Verification)
                {
                    lastError = exception;
                    _log.Warn($"Download source failed for {Sanitize(uri)}: " +
                              $"{SecureText.RedactSecrets(exception.Message)}");
                }
            }

            if (!File.Exists(temporaryPath))
            {
                throw PkgException.Network(
                    $"All download sources failed for {item.Path}: " +
                    $"{SecureText.RedactSecrets(lastError?.Message ?? "no URL returned content")}",
                    lastError);
            }

            await VerifyAsync(temporaryPath, item, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporaryPath, cachePath, overwrite: false);
            }
            catch (IOException) when (File.Exists(cachePath))
            {
                if (!await IsValidAsync(cachePath, item, cancellationToken).ConfigureAwait(false))
                {
                    throw PkgException.Integrity($"A concurrent cache write left an invalid object for {item.Sha1}.");
                }
            }

            return cachePath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void CleanStaleTemporaryFiles()
    {
        if (!Directory.Exists(_paths.CacheTemporary))
        {
            return;
        }

        PathSafety.EnsureNoReparsePoints(_paths.CacheTemporary, _paths.CacheTemporary,
            "cache temporary directory");
        var cutoff = DateTime.UtcNow - TemporaryRetention;
        foreach (var path in Directory.EnumerateFiles(_paths.CacheTemporary, "*.part",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<bool> IsValidAsync(string path, DownloadItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (new FileInfo(path).Length != item.Size)
            {
                return false;
            }

            return string.Equals(await Hashing.Sha1FileAsync(path, cancellationToken).ConfigureAwait(false),
                item.Sha1, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task VerifyAsync(string path, DownloadItem item, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length != item.Size)
        {
            throw PkgException.Integrity(
                $"Size mismatch for {item.Path}: expected {item.Size}, got {length}.");
        }

        var hash = await Hashing.Sha1FileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, item.Sha1, StringComparison.Ordinal))
        {
            throw PkgException.Integrity(
                $"SHA-1 mismatch for {item.Path}: expected {item.Sha1}, got {hash}.");
        }
    }

    private static void ValidateItem(DownloadItem item)
    {
        PathSafety.ValidateRelativePath(item.Path, "index file path");
        if (!Hashing.IsLowerHex(item.Sha1, 40))
        {
            throw PkgException.Integrity($"Invalid SHA-1 for '{item.Path}'.");
        }

        if (item.Size < 0 || item.Size > HttpDownloader.MaxGameFileBytes)
        {
            throw PkgException.Integrity($"Invalid size for '{item.Path}'.");
        }

        if (item.Urls.Count == 0)
        {
            throw PkgException.Integrity($"No download URL for '{item.Path}'.");
        }
    }

    private static string Sanitize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }
}
