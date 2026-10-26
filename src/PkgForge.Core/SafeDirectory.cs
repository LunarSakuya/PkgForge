namespace PkgForge;

/// <summary>
/// Recursive deletion is implemented centrally, never with wildcard or shell
/// deletion. A tree can only be removed when it is a direct child of an
/// explicitly allowed parent, is not protected, has no reparse points, and is
/// not an existing source/project directory.
/// </summary>
public static class SafeDirectory
{
    public static void DeleteControlledTree(string target, string allowedParent, string context)
    {
        var targetFull = PathSafety.NormalizeAbsolutePath(target, context);
        var parentFull = PathSafety.NormalizeAbsolutePath(allowedParent, context);
        if (!PathSafety.IsDirectChild(parentFull, targetFull))
        {
            throw PkgException.Unsafe(
                $"{context}: refusing to delete '{targetFull}' because it is not a direct child of '{parentFull}'.");
        }

        PathSafety.EnsureNotProtected(targetFull, context);
        if (PathSafety.HasProjectMarkers(targetFull))
        {
            throw PkgException.Protected($"{context}: refusing to delete a directory that contains project markers.");
        }

        if (!Directory.Exists(targetFull))
        {
            return;
        }

        PathSafety.EnsureTreeHasNoReparsePoints(targetFull, context);
        var files = new List<string>();
        var directories = new List<(string Path, int Depth)>();
        Collect(targetFull, files, directories, context, 0);

        foreach (var file in files)
        {
            if (!PathSafety.IsWithin(targetFull, file, allowEqual: false))
            {
                throw PkgException.Unsafe($"{context}: a file escaped the deletion root.");
            }

            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(file);
        }

        foreach (var directory in directories.OrderByDescending(item => item.Depth))
        {
            if (!PathSafety.IsWithin(targetFull, directory.Path, allowEqual: true))
            {
                throw PkgException.Unsafe($"{context}: a directory escaped the deletion root.");
            }

            Directory.Delete(directory.Path, recursive: false);
        }
    }

    public static void CreateControlledDirectory(string path, string allowedParent, string context)
    {
        var fullPath = PathSafety.NormalizeAbsolutePath(path, context);
        var parentFull = PathSafety.NormalizeAbsolutePath(allowedParent, context);
        if (!PathSafety.IsWithin(parentFull, fullPath, allowEqual: true))
        {
            throw PkgException.Unsafe($"{context}: directory escapes its managed root.");
        }

        var existingAncestor = fullPath;
        while (!Directory.Exists(existingAncestor))
        {
            var next = Path.GetDirectoryName(existingAncestor);
            if (string.IsNullOrEmpty(next) || PathSafety.EqualsPath(next, existingAncestor))
            {
                break;
            }

            existingAncestor = next;
        }

        PathSafety.EnsureNoReparsePoints(existingAncestor, fullPath, context);
        Directory.CreateDirectory(fullPath);
        PathSafety.EnsureNoReparsePoints(parentFull, fullPath, context);
    }

    private static void Collect(string directory, List<string> files, List<(string Path, int Depth)> directories,
        string context, int depth)
    {
        if (depth > PathSafety.MaxTreeDepth)
        {
            throw PkgException.Unsafe($"{context}: directory nesting exceeds {PathSafety.MaxTreeDepth} levels.");
        }

        directories.Add((directory, depth));
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw PkgException.Unsafe($"{context}: refusing to recurse through a reparse point: {entry}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Collect(entry, files, directories, context, depth + 1);
            }
            else
            {
                files.Add(entry);
            }
        }
    }
}
