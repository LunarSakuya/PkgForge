namespace PkgForge;

/// <summary>
/// One portable program root owns all pkgforge state: index, database, cache,
/// manifests, locks, and managed instances. There is no user-profile state or
/// per-user fallback. <c>MCPKG_HOME</c> is an explicit portable-root override for
/// tests and prepared portable deployments; it is not a user-state location.
/// </summary>
public sealed class AppPaths
{
    public const string PortableHomeVariable = "MCPKG_HOME";
    public const string InstancesRelativeRoot = "data/instances";
    public const string ManifestsRelativeRoot = "data/manifests";

    public AppPaths(string programRoot, string workingDirectory, string globalRootKind = "portable")
    {
        ProgramRoot = PathSafety.NormalizeAbsolutePath(programRoot, "program root");
        WorkingDirectory = PathSafety.NormalizeAbsolutePath(workingDirectory, "working directory");
        GlobalRootKind = globalRootKind;
        GlobalRoot = Path.Combine(ProgramRoot, "data");
        IndexRoot = Path.Combine(GlobalRoot, "index");
        StatePath = Path.Combine(IndexRoot, "current.json");
        DbRoot = Path.Combine(GlobalRoot, "db");
        CacheRoot = Path.Combine(GlobalRoot, "cache");
        CacheObjects = Path.Combine(CacheRoot, "objects");
        CacheTemporary = Path.Combine(CacheRoot, "tmp");
        CacheLocks = Path.Combine(CacheRoot, "locks");
        GlobalTemporary = Path.Combine(GlobalRoot, "tmp");
        GlobalLocks = Path.Combine(GlobalRoot, "locks");
        ManifestsRoot = Path.Combine(GlobalRoot, "manifests");
        InstallRoot = Path.Combine(GlobalRoot, "instances");
        IndexLockPath = Path.Combine(GlobalLocks, "index.lock");
    }

    public string ProgramRoot { get; }

    public string GlobalRoot { get; }

    public string GlobalRootKind { get; }

    public string WorkingDirectory { get; }

    public string IndexRoot { get; }

    public string StatePath { get; }

    public string DbRoot { get; }

    public string CacheRoot { get; }

    public string CacheObjects { get; }

    public string CacheTemporary { get; }

    public string CacheLocks { get; }

    public string GlobalTemporary { get; }

    public string GlobalLocks { get; }

    public string ManifestsRoot { get; }

    public string InstallRoot { get; }

    public string IndexLockPath { get; }

    public static AppPaths FromEnvironment()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(PortableHomeVariable);
        string programRoot;
        string kind;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            programRoot = AppContext.BaseDirectory;
            kind = "portable default";
        }
        else
        {
            programRoot = RequireAbsoluteEnvironmentPath(configuredRoot, PortableHomeVariable);
            kind = PortableHomeVariable;
        }

        return new AppPaths(programRoot, Environment.CurrentDirectory, kind);
    }

    public void EnsureGlobalLayout()
    {
        CreateDirectoryChecked(ProgramRoot, "program root");
        CreateDirectoryChecked(IndexRoot, "global index directory");
        CreateDirectoryChecked(DbRoot, "global database directory");
        CreateDirectoryChecked(CacheRoot, "global cache directory");
        CreateDirectoryChecked(CacheObjects, "global object cache");
        CreateDirectoryChecked(CacheTemporary, "global cache temporary directory");
        CreateDirectoryChecked(CacheLocks, "global cache lock directory");
        CreateDirectoryChecked(GlobalTemporary, "global temporary directory");
        CreateDirectoryChecked(GlobalLocks, "global lock directory");
        CreateDirectoryChecked(ManifestsRoot, "managed manifest directory");
        CreateDirectoryChecked(InstallRoot, "managed instances directory");
    }

    public void EnsureManifestLayout() => CreateDirectoryChecked(ManifestsRoot, "managed manifest directory");

    /// <summary>
    /// Rejects a path that overlaps the program root. Used for unmanaged targets,
    /// which are intentionally outside every portable program-root boundary.
    /// </summary>
    public void EnsureOutsideProgramRoot(string path, string context)
    {
        var full = PathSafety.NormalizeAbsolutePath(path, context);
        if (PathSafety.IsWithin(ProgramRoot, full, allowEqual: true))
        {
            throw PkgException.Protected(
                $"{context}: refusing '{full}' because it is inside the program root '{ProgramRoot}'.");
        }
    }

    /// <summary>
    /// Rejects paths that overlap index, database, cache, manifest, temporary or
    /// lock state. Managed targets may live below <see cref="InstallRoot"/> only
    /// when <paramref name="allowManagedInstallRoot"/> is true.
    /// </summary>
    public void EnsureNotApplicationState(string path, string context, bool allowManagedInstallRoot)
    {
        var full = PathSafety.NormalizeAbsolutePath(path, context);
        foreach (var root in new[]
                 {
                     IndexRoot, DbRoot, CacheRoot, ManifestsRoot, GlobalTemporary, GlobalLocks,
                 })
        {
            if (PathSafety.EqualsPath(full, root) ||
                PathSafety.IsWithin(root, full, allowEqual: true) ||
                PathSafety.IsWithin(full, root, allowEqual: true))
            {
                throw PkgException.Protected($"{context}: '{full}' overlaps pkgforge application state at '{root}'.");
            }
        }

        if (!allowManagedInstallRoot &&
            (PathSafety.EqualsPath(full, InstallRoot) ||
             PathSafety.IsWithin(InstallRoot, full, allowEqual: true)))
        {
            throw PkgException.Protected(
                $"{context}: '{full}' overlaps the managed instances root '{InstallRoot}'.");
        }
    }

    public string BundleDirectory(string bundleId) =>
        Path.Combine(DbRoot, PathSafety.ValidateRelativePath(bundleId, "bundle id"));

    public string ShardPath(string bundleId, string shardId)
    {
        if (shardId is not ("catalog" or "launch" or "assets"))
        {
            throw PkgException.Unsafe($"Unknown shard id '{shardId}'.");
        }

        return Path.Combine(BundleDirectory(bundleId), shardId + ".idx");
    }

    public string CacheObjectPath(string sha1)
    {
        if (!Hashing.IsLowerHex(sha1, 40))
        {
            throw PkgException.Integrity($"Invalid SHA-1 for a cache key: '{sha1}'.");
        }

        return Path.Combine(CacheObjects, sha1);
    }

    public string InstallManifestPath(string installId)
    {
        if (!IsSafeInstallId(installId))
        {
            throw PkgException.Unsafe($"Invalid install id '{installId}'.");
        }

        return Path.Combine(ManifestsRoot, installId + ".json");
    }

    public string InstallLockPath(string canonicalTarget)
    {
        var key = Hashing.Sha256Text(canonicalTarget);
        return Path.Combine(GlobalLocks, "install-" + key + ".lock");
    }

    /// <summary>
    /// Resolves a managed relative install directory to an absolute path and
    /// verifies that it remains inside the controlled instances root.
    /// </summary>
    public string ResolveManagedInstall(string relativePath, string context, bool requireDirectChild = true)
    {
        var normalizedRelative = PathSafety.ValidateRelativePath(relativePath, context);
        var absolute = Path.GetFullPath(Path.Combine(ProgramRoot,
            normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathSafety.IsWithin(InstallRoot, absolute, allowEqual: false))
        {
            throw PkgException.Unsafe(
                $"{context}: managed installs must stay inside '{InstancesRelativeRoot}'.");
        }

        if (requireDirectChild && !PathSafety.IsDirectChild(InstallRoot, absolute))
        {
            throw PkgException.Unsafe(
                $"{context}: managed installs must be direct children of '{InstancesRelativeRoot}'.");
        }

        return absolute;
    }

    public static bool IsSafeInstallId(string value)
    {
        if (value.Length is 0 or > 120)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return value[0] is not ('.' or '-') &&
               !value.EndsWith('.') &&
               !PackageNaming.IsReservedWindowsName(value);
    }

    private static void CreateDirectoryChecked(string path, string context)
    {
        var fullPath = PathSafety.NormalizeAbsolutePath(path, context);
        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PkgException(ErrorCodes.Permission, ExitCode.Permission,
                $"{context}: access denied creating '{fullPath}'. " +
                "Run the program from a writable portable folder or fix the directory permissions.",
                exception);
        }
        catch (IOException exception)
        {
            throw new PkgException(ErrorCodes.FileSystem, ExitCode.RuntimeFailure,
                $"{context}: could not create '{fullPath}': {exception.Message}", exception);
        }

        if (!Directory.Exists(fullPath))
        {
            throw new PkgException(ErrorCodes.FileSystem, ExitCode.RuntimeFailure,
                $"{context}: could not create '{fullPath}'.");
        }

        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root))
        {
            PathSafety.EnsureNoReparsePoints(root, fullPath, context);
        }
    }

    private static string RequireAbsoluteEnvironmentPath(string value, string name)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw PkgException.Usage($"{name} must be an absolute path: '{value}'.");
        }

        return value;
    }
}
