using System.IO.Compression;
using System.Security.Cryptography;

namespace PkgForge;

/// <summary>
/// Extracts native JARs into the staging directory with zip-slip, symlink,
/// bomb, and path-encoding defenses. No archive path is trusted.
/// </summary>
public static class NativeExtractor
{
    private const int MaxEntries = 100_000;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    public static async Task<IReadOnlyList<StagedFile>> ExtractAsync(
        string archivePath,
        string nativeRoot,
        string nativeRelativeRoot,
        IReadOnlyList<string> excludes,
        ILog log,
        CancellationToken cancellationToken)
    {
        var archiveFull = PathSafety.NormalizeAbsolutePath(archivePath, "native archive");
        var nativeRootFull = PathSafety.NormalizeAbsolutePath(nativeRoot, "natives directory");
        var nativeRelative = PathSafety.ValidateRelativePath(nativeRelativeRoot, "natives directory");
        if (!File.Exists(archiveFull))
        {
            throw PkgException.Integrity($"Native archive is missing: {archiveFull}");
        }

        Directory.CreateDirectory(nativeRootFull);
        PathSafety.EnsureNoReparsePoints(nativeRootFull, nativeRootFull, "natives directory");
        var excludedPrefixes = excludes.Select(value => value.TrimEnd('/')).Where(value => value.Length > 0)
            .ToArray();
        var extracted = new List<StagedFile>();
        long extractedBytes = 0;
        try
        {
            await using var stream = new FileStream(archiveFull, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxEntries)
            {
                throw PkgException.Integrity($"Native archive contains more than {MaxEntries} entries.");
            }

            var entries = new List<(ZipArchiveEntry Entry, string Name)>();
            long plannedBytes = 0;
            foreach (var candidate in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                var candidateName = ValidateEntryName(candidate.FullName);
                if (IsExcluded(candidateName, excludedPrefixes))
                {
                    continue;
                }

                if (IsSymlink(candidate))
                {
                    throw PkgException.Integrity(
                        $"Native archive contains a symbolic link entry: {candidate.FullName}");
                }

                plannedBytes = checked(plannedBytes + candidate.Length);
                if (plannedBytes > MaxExtractedBytes)
                {
                    throw PkgException.Integrity("Native archive expands beyond the configured size limit.");
                }

                entries.Add((candidate, candidateName));
            }

            DiskSpace.EnsureAvailable(nativeRootFull, plannedBytes, "native extraction");
            foreach (var (entry, entryName) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PathSafety.CombineUnderRoot(nativeRootFull, entryName, "native archive entry");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var relativePath = nativeRelative + "/" + entryName;
                if (File.Exists(destination))
                {
                    var existingHash = await Hashing.Sha1FileAsync(destination, cancellationToken).ConfigureAwait(false);
                    var newHash = await HashEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(existingHash, newHash, StringComparison.Ordinal))
                    {
                        throw PkgException.Integrity(
                            $"Two native archives produce different files at '{relativePath}'.");
                    }

                    continue;
                }

                var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
                    "." + Path.GetFileName(destination) + ".tmp-" + Guid.NewGuid().ToString("N"));
                long size = 0;
                string hash;
                try
                {
                    using var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                     FileShare.None, 128 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                    {
                        DiskSpace.Preallocate(output, entry.Length, "native archive entry");
                        var buffer = new byte[128 * 1024];
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            size = checked(size + read);
                            if (size > entry.Length || extractedBytes + size > MaxExtractedBytes)
                            {
                                throw PkgException.Integrity(
                                    $"Native archive entry '{entry.FullName}' expands beyond its declared size.");
                            }

                            hashAlgorithm.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }

                        if (size != entry.Length)
                        {
                            throw PkgException.Integrity(
                                $"Native archive entry '{entry.FullName}' was truncated.");
                        }

                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        output.Flush(true);
                    }

                    hash = Convert.ToHexStringLower(hashAlgorithm.GetHashAndReset());
                }
                catch
                {
                    TryDelete(temporary);
                    throw;
                }

                extractedBytes = checked(extractedBytes + size);
                if (File.Exists(destination))
                {
                    TryDelete(temporary);
                    throw PkgException.Conflict($"Native archive entry collided at '{relativePath}'.");
                }

                try
                {
                    File.Move(temporary, destination, overwrite: false);
                }
                catch
                {
                    TryDelete(temporary);
                    throw;
                }

                extracted.Add(new StagedFile(relativePath, size, hash, "native-extracted"));
            }
        }
        catch (InvalidDataException exception)
        {
            throw PkgException.Integrity($"Native archive is corrupt: {exception.Message}", exception);
        }

        log.Progress($"Extracted {extracted.Count} native files.");
        return extracted;
    }

    private static string ValidateEntryName(string fullName)
    {
        if (fullName.Contains('\\', StringComparison.Ordinal))
        {
            throw PkgException.Integrity($"Native archive entry uses a backslash: {fullName}");
        }

        var trimmed = fullName.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            throw PkgException.Integrity("Native archive contains an empty entry name.");
        }

        return PathSafety.ValidateRelativePath(trimmed, "native archive entry");
    }

    private static bool IsExcluded(string entryName, IReadOnlyList<string> prefixes) =>
        prefixes.Any(prefix =>
            entryName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            entryName.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var input = entry.Open();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
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
