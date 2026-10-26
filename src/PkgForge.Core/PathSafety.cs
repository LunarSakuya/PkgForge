using System.Text;

namespace PkgForge;

/// <summary>
/// Central path guard. Every path that is created, written, or deleted passes
/// through this type. The rules intentionally reject more than Windows rejects
/// so an index produced on one platform cannot escape the install root on
/// another.
/// </summary>
public static class PathSafety
{
    public const int MaxRelativePathLength = 1024;
    public const int MaxAbsolutePathLength = 2048;
    public const int MaxSegmentLength = 255;
    public const int MaxTreeDepth = 128;

    private static readonly char[] CrossPlatformInvalidCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string NormalizeAbsolutePath(string path, string context)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw PkgException.Unsafe($"{context}: the path is empty.");
        }

        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
        {
            throw PkgException.Unsafe($"{context}: leading or trailing whitespace is not allowed in an absolute path.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw PkgException.Unsafe($"{context}: the path must be absolute.");
        }

        RejectUnsafeCharacters(path, context, allowSeparators: true);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PkgException(ErrorCodes.UnsafePath, ExitCode.UnsafePath,
                $"{context}: the path cannot be normalized: {exception.Message}", exception);
        }

        fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        if (fullPath.Length > MaxAbsolutePathLength)
        {
            throw PkgException.Unsafe($"{context}: the path exceeds {MaxAbsolutePathLength} characters.");
        }

        return fullPath;
    }

    public static string ResolveAbsoluteOrRelative(string path, string workingDirectory, string context)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw PkgException.Unsafe($"{context}: the path is empty.");
        }

        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
        {
            throw PkgException.Unsafe($"{context}: leading or trailing whitespace is not allowed in a path.");
        }

        RejectUnsafeCharacters(path, context, allowSeparators: true);
        var baseDirectory = NormalizeAbsolutePath(workingDirectory, "working directory");
        try
        {
            var fullPath = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(baseDirectory, path));
            return NormalizeAbsolutePath(fullPath, context);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PkgException(ErrorCodes.UnsafePath, ExitCode.UnsafePath,
                $"{context}: the path cannot be normalized: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Validates a path stored in the index and returns its canonical
    /// forward-slash form.
    /// </summary>
    public static string ValidateRelativePath(string relativePath, string context)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw PkgException.Unsafe($"{context}: the relative path is empty.");
        }

        if (relativePath.Length > MaxRelativePathLength)
        {
            throw PkgException.Unsafe($"{context}: the relative path exceeds {MaxRelativePathLength} characters.");
        }

        if (relativePath[0] is '/' or '\\')
        {
            throw PkgException.Unsafe($"{context}: absolute paths are not allowed.");
        }

        if (relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw PkgException.Unsafe($"{context}: backslashes are not allowed in index paths.");
        }

        if (relativePath.EndsWith('/'))
        {
            throw PkgException.Unsafe($"{context}: a file path cannot end with a separator.");
        }

        var segments = relativePath.Split('/');
        var normalized = new StringBuilder(relativePath.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw PkgException.Unsafe($"{context}: empty path segments are not allowed.");
            }

            if (segment is "." or "..")
            {
                throw PkgException.Unsafe($"{context}: dot segments are not allowed.");
            }

            if (segment.Length > MaxSegmentLength)
            {
                throw PkgException.Unsafe($"{context}: a path segment exceeds {MaxSegmentLength} characters.");
            }

            if (segment[^1] is '.' or ' ')
            {
                throw PkgException.Unsafe($"{context}: a path segment ends with a dot or space.");
            }

            if (segment[0] == ' ' || segment[^1] == ' ')
            {
                throw PkgException.Unsafe($"{context}: leading or trailing spaces in a path segment are not allowed.");
            }

            if (segment.Contains('\\', StringComparison.Ordinal) || segment.Contains('/', StringComparison.Ordinal))
            {
                throw PkgException.Unsafe($"{context}: a segment contains a separator.");
            }

            foreach (var character in segment)
            {
                RejectCharacter(character, context);
            }

            var baseName = segment.Split('.')[0];
            if (ReservedWindowsNames.Contains(baseName))
            {
                throw PkgException.Unsafe($"{context}: '{segment}' is a reserved Windows device name.");
            }

            if (normalized.Length > 0)
            {
                normalized.Append('/');
            }

            normalized.Append(segment);
        }

        return normalized.ToString();
    }

    public static string CombineUnderRoot(string root, string relativePath, string context)
    {
        var rootFull = NormalizeAbsolutePath(root, "allowed root");
        var relative = ValidateRelativePath(relativePath, context);
        var candidate = Path.GetFullPath(Path.Combine(rootFull,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        candidate = Path.TrimEndingDirectorySeparator(candidate);
        if (!IsWithin(rootFull, candidate, allowEqual: false))
        {
            throw PkgException.Unsafe($"{context}: the resolved path escapes its allowed root.");
        }

        EnsureNoReparsePoints(rootFull, candidate, context);
        return candidate;
    }

    public static bool IsWithin(string root, string candidate, bool allowEqual)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidateFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (EqualsPath(rootFull, candidateFull))
        {
            return allowEqual;
        }

        var relative = Path.GetRelativePath(rootFull, candidateFull);
        if (Path.IsPathRooted(relative))
        {
            return false;
        }

        return relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static bool EqualsPath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), comparison);
    }

    public static bool IsDirectChild(string parent, string child)
    {
        var parentFull = NormalizeAbsolutePath(parent, "managed root");
        var childFull = NormalizeAbsolutePath(child, "install directory");
        var relative = Path.GetRelativePath(parentFull, childFull);
        return relative != "." && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refuses ordinary system roots and their descendants, the user's home,
    /// the current directory, and paths that contain a protected directory.
    /// Intentional AppData/profile installation roots remain available.
    /// Custom installs are allowed only through explicit path values.
    /// </summary>
    public static void EnsureNotProtected(string path, string context)
    {
        var target = NormalizeAbsolutePath(path, context);
        var protectedPaths = new List<(string Path, string Label, bool DenyDescendants)>();
        AddSpecialPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "the user home directory", protectedPaths, denyDescendants: false);
        AddSpecialPath(Environment.CurrentDirectory, "the current working directory", protectedPaths,
            denyDescendants: false);
        AddSpecialPath(Environment.SystemDirectory, "the system directory", protectedPaths,
            denyDescendants: true);
        AddSpecialPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "the Windows directory",
            protectedPaths, denyDescendants: true);
        AddSpecialPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files",
            protectedPaths, denyDescendants: true);
        AddSpecialPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)",
            protectedPaths, denyDescendants: true);
        AddSpecialPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData",
            protectedPaths, denyDescendants: true);
        AddSpecialPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "the roaming AppData directory", protectedPaths, denyDescendants: false);
        AddSpecialPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "the local AppData directory", protectedPaths, denyDescendants: false);
        AddSpecialPath(Path.GetPathRoot(target), "a filesystem root", protectedPaths,
            denyDescendants: false);

        foreach (var protectedPath in protectedPaths)
        {
            var equals = EqualsPath(target, protectedPath.Path);
            var contains = IsWithin(target, protectedPath.Path, allowEqual: false);
            var inside = protectedPath.DenyDescendants &&
                         IsWithin(protectedPath.Path, target, allowEqual: false);
            if (!equals && !contains && !inside)
            {
                continue;
            }

            var relation = equals ? "is" : contains ? "contains" : "is inside";
            throw PkgException.Protected(
                $"{context}: refusing to operate on '{target}' because it {relation} {protectedPath.Label}.");
        }
    }

    public static bool HasProjectMarkers(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (depth > MaxTreeDepth)
            {
                throw PkgException.Unsafe(
                    $"Project marker scan exceeds {MaxTreeDepth} directory levels at '{current}'.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (IsProjectMarker(name))
                {
                    return true;
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push((entry, depth + 1));
                }
            }
        }

        return false;
    }

    private static bool IsProjectMarker(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    public static void EnsureNoReparsePoints(string root, string candidate, string context)
    {
        var rootFull = NormalizeAbsolutePath(root, context);
        var candidateFull = NormalizeAbsolutePath(candidate, context);
        if (!IsWithin(rootFull, candidateFull, allowEqual: true))
        {
            throw PkgException.Unsafe($"{context}: path escapes the allowed root.");
        }

        if (Exists(rootFull))
        {
            RejectReparse(rootFull, context);
        }

        var relative = Path.GetRelativePath(rootFull, candidateFull);
        if (relative == ".")
        {
            return;
        }

        var current = rootFull;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Exists(current))
            {
                continue;
            }

            RejectReparse(current, context);
        }
    }

    public static void EnsureTreeHasNoReparsePoints(string root, string context)
    {
        var rootFull = NormalizeAbsolutePath(root, context);
        if (!Directory.Exists(rootFull))
        {
            throw PkgException.NotFound(ErrorCodes.ManifestMissing, $"{context}: directory does not exist: {rootFull}");
        }

        RejectReparse(rootFull, context);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((rootFull, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (depth > MaxTreeDepth)
            {
                throw PkgException.Unsafe($"{context}: directory nesting exceeds {MaxTreeDepth} levels.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (!IsWithin(rootFull, entry, allowEqual: false))
                {
                    throw PkgException.Unsafe($"{context}: an entry escapes the managed root.");
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw PkgException.Unsafe($"{context}: symbolic links and reparse points are not allowed: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, depth + 1));
                }
            }
        }
    }

    private static void AddSpecialPath(string? value, string label,
        List<(string Path, string Label, bool DenyDescendants)> list, bool denyDescendants)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var normalized = NormalizeAbsolutePath(value, label);
            list.Add((normalized, label, denyDescendants));
        }
        catch (PkgException)
        {
            // A special-folder API value that is not a usable absolute path is
            // ignored; the remaining fixed roots are still checked.
        }
    }

    private static void RejectReparse(string path, string context)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw PkgException.Unsafe(
                $"{context}: symbolic links and reparse points are not allowed on the managed path: {path}");
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void RejectUnsafeCharacters(string path, string context, bool allowSeparators)
    {
        foreach (var character in path)
        {
            if (character == '\0' || char.IsControl(character))
            {
                throw PkgException.Unsafe($"{context}: control characters are not allowed in paths.");
            }

            if (!allowSeparators && character is '/' or '\\')
            {
                throw PkgException.Unsafe($"{context}: separators are not allowed here.");
            }

            if (IsBidiOrInvisible(character))
            {
                throw PkgException.Unsafe($"{context}: bidirectional or invisible characters are not allowed.");
            }
        }
    }

    private static void RejectCharacter(char character, string context)
    {
        if (char.IsControl(character))
        {
            throw PkgException.Unsafe($"{context}: control characters are not allowed.");
        }

        if (Array.IndexOf(CrossPlatformInvalidCharacters, character) >= 0)
        {
            throw PkgException.Unsafe($"{context}: invalid character '{character}' in a path.");
        }

        if (IsBidiOrInvisible(character))
        {
            throw PkgException.Unsafe($"{context}: bidirectional or invisible characters are not allowed.");
        }
    }

    private static bool IsBidiOrInvisible(char character) =>
        character is >= '\u202A' and <= '\u202E' or
            >= '\u2066' and <= '\u2069' or
            '\u200E' or '\u200F' or '\uFEFF';
}
