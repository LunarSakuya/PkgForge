using System.Security.Cryptography;

namespace PkgForge;

public sealed record StagedFile(string RelativePath, long Size, string Sha1, string Role);

public static class FileMaterializer
{
    public static async Task<StagedFile> CopyVerifiedAsync(
        string source,
        string destination,
        long expectedSize,
        string expectedSha1,
        string relativePath,
        string role,
        CancellationToken cancellationToken)
    {
        var sourceFull = PathSafety.NormalizeAbsolutePath(source, "cache file");
        var destinationFull = PathSafety.NormalizeAbsolutePath(destination, "staged file");
        var parent = Path.GetDirectoryName(destinationFull)
                     ?? throw PkgException.Unsafe("A staged file has no parent directory.");
        Directory.CreateDirectory(parent);
        if (File.Exists(destinationFull))
        {
            throw PkgException.Conflict($"Duplicate staged file path '{relativePath}'.");
        }

        if (!File.Exists(sourceFull))
        {
            throw PkgException.Integrity($"Cache object is missing: {sourceFull}");
        }

        DiskSpace.EnsureAvailable(parent, expectedSize, "staged file");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long total = 0;
        try
        {
            await using var input = new FileStream(sourceFull, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destinationFull, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            DiskSpace.Preallocate(output, expectedSize, "staged file");
            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > expectedSize)
                {
                    throw PkgException.Integrity($"Staged file '{relativePath}' exceeds its indexed size.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(true);
        }
        catch
        {
            TryDelete(destinationFull);
            throw;
        }

        if (total != expectedSize)
        {
            TryDelete(destinationFull);
            throw PkgException.Integrity(
                $"Size mismatch while staging '{relativePath}': expected {expectedSize}, got {total}.");
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, expectedSha1, StringComparison.Ordinal))
        {
            TryDelete(destinationFull);
            throw PkgException.Integrity(
                $"Hash mismatch while staging '{relativePath}': expected {expectedSha1}, got {actual}.");
        }

        return new StagedFile(relativePath, total, actual, role);
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
