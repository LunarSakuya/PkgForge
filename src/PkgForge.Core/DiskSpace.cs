using System.Runtime.InteropServices;

namespace PkgForge;

/// <summary>
/// Bounded, preflight disk-space checks and explicit file reservation. The helper
/// never deletes anything; callers own cleanup on failure.
/// </summary>
public static class DiskSpace
{
    public const long SafetyMarginBytes = 64L * 1024 * 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalBytes,
        out ulong totalFreeBytes);

    public static void EnsureAvailable(string path, long requiredBytes, string context)
    {
        if (requiredBytes < 0)
        {
            throw PkgException.Integrity($"{context}: negative disk-space requirement.");
        }

        if (requiredBytes == 0)
        {
            return;
        }

        var fullPath = PathSafety.NormalizeAbsolutePath(path, context);
        var existingAncestor = FindExistingAncestor(fullPath);
        long available;
        try
        {
            available = GetAvailableBytes(existingAncestor);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              NotSupportedException or ArgumentException)
        {
            throw new PkgException(ErrorCodes.FileSystem, ExitCode.RuntimeFailure,
                $"{context}: could not determine free space at '{existingAncestor}': {exception.Message}",
                exception);
        }

        var margin = Math.Max(SafetyMarginBytes, requiredBytes / 20);
        var needed = requiredBytes > long.MaxValue - margin ? long.MaxValue : requiredBytes + margin;
        if (available < needed)
        {
            throw PkgException.DiskSpace(
                $"{context}: insufficient free space at '{existingAncestor}'. " +
                $"Required at least {SizeFormatter.Format(needed)} (including safety margin), " +
                $"available {SizeFormatter.Format(available)}.");
        }
    }

    public static void Preallocate(FileStream stream, long length, string context)
    {
        if (length < 0)
        {
            throw PkgException.Integrity($"{context}: negative reservation length.");
        }

        try
        {
            stream.SetLength(length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              NotSupportedException)
        {
            throw PkgException.DiskSpace(
                $"{context}: could not reserve {SizeFormatter.Format(length)} in the temporary file.",
                exception);
        }
    }

    private static string FindExistingAncestor(string path)
    {
        var current = path;
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || PathSafety.EqualsPath(parent, current))
            {
                var root = Path.GetPathRoot(current);
                if (string.IsNullOrEmpty(root))
                {
                    throw PkgException.Unsafe($"No existing filesystem ancestor for '{path}'.");
                }

                return root;
            }

            current = parent;
        }

        return current;
    }

    private static long GetAvailableBytes(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetDiskFreeSpaceExW(path, out var available, out _, out _))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"GetDiskFreeSpaceEx failed with Win32 error {error}.");
            }

            return available > long.MaxValue ? long.MaxValue : (long)available;
        }

        var root = Path.GetPathRoot(path) ?? path;
        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace;
    }
}
