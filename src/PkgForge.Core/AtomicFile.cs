using System.Text;

namespace PkgForge;

public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw PkgException.Unsafe("An atomic write target has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static Task WriteUtf8BytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        WriteStreamAsync(path, async stream => await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false),
            cancellationToken);

    public static async Task WriteStreamAsync(string path, Func<FileStream, Task> write,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw PkgException.Unsafe("An atomic write target has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
