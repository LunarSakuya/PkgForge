using System.Collections.Concurrent;

namespace PkgForge;

public sealed record InstallRequest(
    PackageSelector Selector,
    string? InstallDirectory,
    bool AssumeYes,
    bool Force,
    int Threads,
    bool Unmanaged = false);

public sealed record InstallResult(
    string InstallId,
    string PackageId,
    int FileCount,
    long TotalBytes,
    bool ReplacedExistingDirectory,
    string Target,
    bool Unmanaged = false)
{
    /// <summary>The persisted manifest for a managed install; null for an unmanaged download.</summary>
    public InstallManifest? ManagedManifest { get; init; }

    public InstallManifest Manifest => ManagedManifest ??
        throw new InvalidOperationException("An unmanaged result does not have a managed manifest.");
}

public sealed class InstallService
{
    private static readonly string[] ClientSkeleton =
        ["mods", "resourcepacks", "datapacks", "saves", "config", "logs", "crash-reports", "screenshots"];

    private static readonly string[] ServerSkeleton = ["logs"];
    private static readonly TimeSpan InstallLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InstallLockStaleAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(6);
    private const long NativeExtractionReserveBytes = 256L * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly LoadedIndex _index;
    private readonly IndexSource _source;
    private readonly IndexDatabase _database;
    private readonly ContentCache _cache;
    private readonly InstallManifestStore _manifests;
    private readonly IConfirmation _confirmation;
    private readonly ILog _log;

    public InstallService(AppPaths paths, LoadedIndex index, IndexSource source, IndexDatabase database,
        ContentCache cache, InstallManifestStore manifests, IConfirmation confirmation, ILog log)
    {
        _paths = paths;
        _index = index;
        _source = source;
        _database = database;
        _cache = cache;
        _manifests = manifests;
        _confirmation = confirmation;
        _log = log;
    }

    public async Task<InstallResult> InstallAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        if (request.Threads is < 1 or > 32)
        {
            throw PkgException.Usage("--threads must be between 1 and 32.");
        }

        var plan = await _database.ResolvePlanAsync(request.Selector, cancellationToken).ConfigureAwait(false);
        if (request.Unmanaged)
        {
            return await InstallUnmanagedAsync(plan, request, cancellationToken).ConfigureAwait(false);
        }

        var (target, relativeTarget) = ResolveTarget(plan, request.InstallDirectory);
        await ValidateNoOverlappingInstallAsync(plan, target, cancellationToken).ConfigureAwait(false);
        var existingManifest = await _manifests.TryLoadByIdAsync(plan.InstallId, cancellationToken)
            .ConfigureAwait(false);
        if (existingManifest is not null)
        {
            if (!string.Equals(existingManifest.InstallDirectory, relativeTarget, PathComparison))
            {
                throw PkgException.Conflict(
                    $"Package is already installed at '{existingManifest.InstallDirectory}'. " +
                    $"Remove install '{existingManifest.InstallId}' before selecting another directory.");
            }

            if (!string.Equals(existingManifest.PackageId, plan.PackageId, StringComparison.Ordinal))
            {
                throw PkgException.Conflict(
                    $"Install id '{plan.InstallId}' already belongs to '{existingManifest.PackageId}'.");
            }
        }

        var targetState = await InspectTargetAsync(plan, target, relativeTarget, request.Force, cancellationToken)
            .ConfigureAwait(false);
        EnsureDiskSpace(plan, _paths.InstallRoot);

        _log.Info($"Package : {plan.PackageId}");
        _log.Info($"Version : {plan.VersionId} ({plan.VersionType})");
        _log.Info($"Kind    : {plan.Kind.ToToken()}{(plan.Kind == PackageKind.Client ? "/" + plan.Platform : string.Empty)}");
        _log.Info($"Target  : {target}");
        _log.Info($"Files   : {plan.DownloadCount} downloads" +
                  (plan.Links.Count > 0 ? $", {plan.Links.Count} asset links" : string.Empty));
        _log.Info($"Size    : {SizeFormatter.Format(plan.TotalBytes)}");
        if (targetState.HasContent)
        {
            _log.Warn("The target directory already has content and will be replaced atomically after staging.");
            if (request.AssumeYes && request.Force)
            {
                _log.Warn("--yes with --force: replacing an existing managed target without an interactive confirmation.");
            }
        }

        if (!await _confirmation.ConfirmAsync($"Install {plan.PackageId} into '{target}'?", cancellationToken)
                .ConfigureAwait(false))
        {
            throw new PkgException(ErrorCodes.Cancelled, ExitCode.Cancelled, "Installation cancelled.");
        }

        var lockPath = _paths.InstallLockPath(target);
        await using var installLock = await ProcessLock.AcquireAsync(lockPath, InstallLockTimeout,
            InstallLockStaleAfter, _log, cancellationToken).ConfigureAwait(false);

        targetState = await InspectTargetAsync(plan, target, relativeTarget, request.Force, cancellationToken)
            .ConfigureAwait(false);

        var stagingRoot = Path.Combine(_paths.InstallRoot, ".mcpkg-staging");
        SafeDirectory.CreateControlledDirectory(stagingRoot, _paths.InstallRoot, "install staging root");
        CleanupStaleChildren(stagingRoot, plan.InstallId + "-", StaleStagingAge,
            "stale installation staging directory");
        var stagingPath = Path.Combine(stagingRoot, plan.InstallId + "-" + Guid.NewGuid().ToString("N"));
        SafeDirectory.CreateControlledDirectory(stagingPath, stagingRoot, "installation staging directory");

        var backupRoot = Path.Combine(_paths.InstallRoot, ".mcpkg-backup");
        var backupPath = Path.Combine(backupRoot, plan.InstallId + "-" + Guid.NewGuid().ToString("N"));
        string? movedOldTarget = null;
        string? movedNewTarget = null;
        var replaced = targetState.Exists;
        try
        {
            _cache.CleanStaleTemporaryFiles();
            _log.Info($"Downloading {plan.DownloadCount} files with {request.Threads} workers.");
            var cachePaths = await DownloadAllAsync(plan, request.Threads, cancellationToken)
                .ConfigureAwait(false);

            _log.Info("Verifying and staging files.");
            var records = new Dictionary<string, StagedFile>(StringComparer.OrdinalIgnoreCase);
            CreatePlanDirectories(stagingPath, plan);
            await CopyDownloadsAsync(plan, cachePaths, stagingPath, records, cancellationToken)
                .ConfigureAwait(false);
            await CopyAssetLinksAsync(plan, stagingPath, records, cancellationToken).ConfigureAwait(false);
            var nativeCount = await ExtractNativesAsync(plan, stagingPath, records, cancellationToken)
                .ConfigureAwait(false);

            var marker = new InstallMarker
            {
                ManifestVersion = 2,
                InstallId = plan.InstallId,
                PackageId = plan.PackageId,
                VersionKey = plan.VersionKey,
                VersionType = NormalizeUnknown(plan.VersionType),
                Kind = plan.Kind.ToToken(),
                Platform = NormalizeUnknown(plan.Platform),
                InstallDirectory = relativeTarget,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            var markerPath = Path.Combine(stagingPath, InstallManifestStore.MarkerFileName);
            await AtomicFile.WriteAllTextAsync(markerPath,
                JsonCodec.SerializeInstallMarker(marker) + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);

            PathSafety.EnsureTreeHasNoReparsePoints(stagingPath, "installation staging");

            var orderedFiles = records.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
            long totalBytes = 0;
            foreach (var file in orderedFiles)
            {
                totalBytes = checked(totalBytes + file.Size);
            }

            var manifest = new InstallManifest
            {
                ManifestVersion = 2,
                InstallId = plan.InstallId,
                PackageId = plan.PackageId,
                VersionId = plan.VersionId,
                VersionKey = plan.VersionKey,
                VersionType = NormalizeUnknown(plan.VersionType),
                Kind = plan.Kind.ToToken(),
                Platform = NormalizeUnknown(plan.Platform),
                InstallDirectory = relativeTarget,
                ManagedRoot = AppPaths.InstancesRelativeRoot,
                MarkerFile = InstallManifestStore.MarkerFileName,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                IndexSource = _source.Display,
                IndexBundleId = _index.BundleId,
                PlanId = plan.PlanId,
                FileCount = orderedFiles.Count,
                TotalBytes = totalBytes,
                Files = orderedFiles.Select(file => new InstalledFile
                {
                    Path = file.RelativePath,
                    Size = file.Size,
                    Sha1 = file.Sha1,
                    Role = file.Role,
                }).ToList(),
            };
            InstallManifestStore.Validate(manifest);

            if (Directory.Exists(target))
            {
                SafeDirectory.CreateControlledDirectory(backupRoot, _paths.InstallRoot, "install backup root");
                if (Directory.Exists(backupPath))
                {
                    throw PkgException.Conflict("A backup path collision occurred; retry the operation.");
                }

                Directory.Move(target, backupPath);
                movedOldTarget = backupPath;
            }

            if (Directory.Exists(target))
            {
                throw PkgException.Conflict("The installation target appeared while staging; retry.");
            }

            Directory.Move(stagingPath, target);
            movedNewTarget = target;

            await _manifests.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);

            if (movedOldTarget is not null && Directory.Exists(movedOldTarget))
            {
                try
                {
                    SafeDirectory.DeleteControlledTree(movedOldTarget, backupRoot, "replaced installation backup");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
                {
                    _log.Warn($"Could not remove the previous installation backup '{movedOldTarget}': " +
                              exception.Message);
                }
            }

            _log.Info($"Installed '{plan.PackageId}' as '{plan.InstallId}'.");
            _log.Info($"Files: {manifest.FileCount} ({SizeFormatter.Format(manifest.TotalBytes)}), " +
                      $"natives extracted: {nativeCount}.");
            return new InstallResult(manifest.InstallId, manifest.PackageId, manifest.FileCount,
                manifest.TotalBytes, replaced, target)
            {
                ManagedManifest = manifest,
            };
        }
        catch
        {
            if (movedNewTarget is not null)
            {
                Rollback(target, stagingRoot, movedOldTarget);
            }
            else if (movedOldTarget is not null && Directory.Exists(movedOldTarget) && !Directory.Exists(target))
            {
                Directory.Move(movedOldTarget, target);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                try
                {
                    SafeDirectory.DeleteControlledTree(stagingPath, stagingRoot, "installation staging cleanup");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
                {
                    _log.Warn($"Could not clean installation staging directory '{stagingPath}': {exception.Message}");
                }
            }
        }
    }

    private void EnsureDiskSpace(ResolvedPlan plan, string managedRoot)
    {
        long downloadBytes;
        long linkBytes;
        try
        {
            downloadBytes = plan.Downloads.Aggregate(0L, (total, item) => checked(total + item.Size));
            var sizes = plan.Downloads.ToDictionary(item => item.Path, item => item.Size,
                StringComparer.OrdinalIgnoreCase);
            linkBytes = plan.Links.Aggregate(0L, (total, link) =>
                checked(total + (sizes.TryGetValue(link.FromPath, out var size) ? size : 0)));
        }
        catch (OverflowException exception)
        {
            throw PkgException.DiskSpace("The package size exceeds the supported disk-space range.", exception);
        }

        long stagingBytes;
        try
        {
            stagingBytes = checked(downloadBytes + linkBytes + NativeExtractionReserveBytes);
        }
        catch (OverflowException exception)
        {
            throw PkgException.DiskSpace("The staging size exceeds the supported disk-space range.", exception);
        }

        var cacheVolume = Path.GetPathRoot(_paths.CacheRoot);
        var stagingVolume = Path.GetPathRoot(managedRoot);
        if (!string.IsNullOrEmpty(cacheVolume) && !string.IsNullOrEmpty(stagingVolume) &&
            string.Equals(cacheVolume, stagingVolume, StringComparison.OrdinalIgnoreCase))
        {
            long combined;
            try
            {
                combined = checked(downloadBytes + stagingBytes);
            }
            catch (OverflowException exception)
            {
                throw PkgException.DiskSpace("The combined disk-space requirement is too large.", exception);
            }

            DiskSpace.EnsureAvailable(managedRoot, combined, "installation cache and staging");
            return;
        }

        DiskSpace.EnsureAvailable(_paths.CacheRoot, downloadBytes, "content cache");
        DiskSpace.EnsureAvailable(managedRoot, stagingBytes, "installation staging");
    }

    private (string Target, string RelativeTarget) ResolveTarget(ResolvedPlan plan, string? directory)
    {
        EnsureManagedRoot(_paths.InstallRoot, allowCreate: true);
        string relativeTarget;
        if (string.IsNullOrWhiteSpace(directory))
        {
            relativeTarget = AppPaths.InstancesRelativeRoot + "/" + plan.InstallId;
        }
        else
        {
            if (Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw PkgException.Usage(
                    "--dir must be a relative path under the program root for a managed install. " +
                    "Use --unmanaged with an absolute path for an unmanaged download.");
            }

            relativeTarget = PathSafety.ValidateRelativePath(directory, "--dir");
        }

        var target = _paths.ResolveManagedInstall(relativeTarget, "install directory");
        PathSafety.EnsureNotProtected(target, "install directory");
        _paths.EnsureNotApplicationState(target, "install directory", allowManagedInstallRoot: true);
        PathSafety.EnsureNoReparsePoints(_paths.InstallRoot, target, "install directory");
        if (target.Length > PathSafety.MaxAbsolutePathLength)
        {
            throw PkgException.Unsafe("The install directory path is too long.");
        }

        return (target, relativeTarget);
    }

    private async Task<InstallTargetState> InspectTargetAsync(ResolvedPlan plan, string target,
        string relativeTarget, bool force, CancellationToken cancellationToken)
    {
        if (File.Exists(target))
        {
            throw PkgException.Conflict($"The install target is a file, not a directory: {target}");
        }

        var exists = Directory.Exists(target);
        if (!exists)
        {
            return new InstallTargetState(false, false);
        }

        PathSafety.EnsureNoReparsePoints(_paths.InstallRoot, target, "install target");
        if (PathSafety.HasProjectMarkers(target))
        {
            throw PkgException.Protected("Refusing to install over a directory containing project markers.");
        }

        var hasContent = Directory.EnumerateFileSystemEntries(target).Any();
        if (!hasContent)
        {
            return new InstallTargetState(true, false);
        }

        if (!force)
        {
            throw PkgException.Conflict(
                $"Target '{target}' already contains files. Re-run with --force after reviewing the plan.");
        }

        var marker = await _manifests.TryReadMarkerAsync(target, cancellationToken).ConfigureAwait(false);
        if (marker is null)
        {
            throw PkgException.Protected(
                "Refusing to overwrite a non-empty directory without a valid pkgforge installation marker, even with --force.");
        }

        if (!string.Equals(marker.InstallId, plan.InstallId, StringComparison.Ordinal) ||
            !string.Equals(marker.PackageId, plan.PackageId, StringComparison.Ordinal) ||
            !string.Equals(marker.InstallDirectory, relativeTarget, PathComparison))
        {
            throw PkgException.Integrity("The existing installation marker belongs to a different installation.");
        }

        PathSafety.EnsureTreeHasNoReparsePoints(target, "existing install target");
        return new InstallTargetState(true, true);
    }

    private async Task ValidateNoOverlappingInstallAsync(ResolvedPlan plan, string target,
        CancellationToken cancellationToken)
    {
        var records = await _manifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            if (string.Equals(record.InstallId, plan.InstallId, StringComparison.Ordinal))
            {
                continue;
            }

            var recordTarget = _paths.ResolveManagedInstall(record.InstallDirectory,
                $"installation record '{record.InstallId}'");
            if (PathSafety.IsWithin(recordTarget, target, allowEqual: true) ||
                PathSafety.IsWithin(target, recordTarget, allowEqual: true))
            {
                throw PkgException.Conflict(
                    $"Target '{target}' overlaps existing installation '{record.InstallId}' at " +
                    $"'{record.InstallDirectory}'.");
            }
        }
    }

    private async Task<Dictionary<string, string>> DownloadAllAsync(ResolvedPlan plan, int threads,
        CancellationToken cancellationToken)
    {
        var cachePaths = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        try
        {
            await Parallel.ForEachAsync(plan.Downloads,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = threads,
                    CancellationToken = cancellationToken,
                },
                async (item, token) =>
                {
                    var path = await _cache.GetOrDownloadAsync(item, token).ConfigureAwait(false);
                    cachePaths[item.Path] = path;
                    var current = Interlocked.Increment(ref completed);
                    if (current == plan.Downloads.Count || current % 100 == 0)
                    {
                        _log.Progress($"Acquired {current}/{plan.Downloads.Count} objects.");
                    }
                }).ConfigureAwait(false);
        }
        catch (AggregateException aggregate)
        {
            var flattened = aggregate.Flatten().InnerExceptions;
            if (flattened.Count == 1)
            {
                throw FileSystemErrors.Map(flattened[0], "download");
            }

            throw FileSystemErrors.Map(aggregate, "download");
        }
        catch (Exception exception) when (exception is not PkgException and not OperationCanceledException)
        {
            throw FileSystemErrors.Map(exception, "download");
        }

        if (cachePaths.Count != plan.Downloads.Count)
        {
            throw PkgException.Integrity("Not all planned files were acquired.");
        }

        return new Dictionary<string, string>(cachePaths, StringComparer.OrdinalIgnoreCase);
    }

    private static void CreatePlanDirectories(string stagingPath, ResolvedPlan plan)
    {
        foreach (var directory in plan.Directories)
        {
            var full = PathSafety.CombineUnderRoot(stagingPath, directory, "plan directory");
            Directory.CreateDirectory(full);
        }

        var skeleton = plan.Kind == PackageKind.Client ? ClientSkeleton : ServerSkeleton;
        foreach (var directory in skeleton)
        {
            var relative = PathSafety.ValidateRelativePath(directory, "skeleton directory");
            var full = PathSafety.CombineUnderRoot(stagingPath, relative, "skeleton directory");
            Directory.CreateDirectory(full);
        }
    }

    private async Task CopyDownloadsAsync(ResolvedPlan plan, IReadOnlyDictionary<string, string> cachePaths,
        string stagingPath, Dictionary<string, StagedFile> records, CancellationToken cancellationToken)
    {
        foreach (var item in plan.Downloads.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cachePaths.TryGetValue(item.Path, out var source))
            {
                throw PkgException.Integrity($"No cache object is mapped for '{item.Path}'.");
            }

            var destination = PathSafety.CombineUnderRoot(stagingPath, item.Path, "staged download");
            var record = await FileMaterializer.CopyVerifiedAsync(source, destination, item.Size, item.Sha1,
                item.Path, item.Role, cancellationToken).ConfigureAwait(false);
            AddRecord(records, record);
        }
    }

    private async Task CopyAssetLinksAsync(ResolvedPlan plan, string stagingPath,
        Dictionary<string, StagedFile> records, CancellationToken cancellationToken)
    {
        if (plan.Links.Count == 0)
        {
            return;
        }

        var downloads = plan.Downloads.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var link in plan.Links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!downloads.TryGetValue(link.FromPath, out var sourceDescription))
            {
                throw PkgException.Integrity($"Asset link source '{link.FromPath}' is not in the download plan.");
            }

            var source = PathSafety.CombineUnderRoot(stagingPath, link.FromPath, "asset link source");
            var destination = PathSafety.CombineUnderRoot(stagingPath, link.ToPath, "asset link target");
            var record = await FileMaterializer.CopyVerifiedAsync(source, destination, sourceDescription.Size,
                sourceDescription.Sha1, link.ToPath, "asset-link", cancellationToken).ConfigureAwait(false);
            AddRecord(records, record);
        }
    }

    private static async Task<int> ExtractNativesAsync(ResolvedPlan plan, string stagingPath,
        Dictionary<string, StagedFile> records, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.NativeDirectory))
        {
            return 0;
        }

        var nativeFiles = plan.Downloads.Where(item => item.Role == "native").ToList();
        if (nativeFiles.Count == 0)
        {
            return 0;
        }

        var nativeRoot = PathSafety.CombineUnderRoot(stagingPath, plan.NativeDirectory, "natives directory");
        Directory.CreateDirectory(nativeRoot);
        var extractedCount = 0;
        foreach (var native in nativeFiles)
        {
            var archive = PathSafety.CombineUnderRoot(stagingPath, native.Path, "native archive");
            var extracted = await NativeExtractor.ExtractAsync(archive, nativeRoot, plan.NativeDirectory,
                plan.ExtractExcludes, NullLog.Instance, cancellationToken).ConfigureAwait(false);
            foreach (var file in extracted)
            {
                AddRecord(records, file);
                extractedCount++;
            }
        }

        return extractedCount;
    }

    private static void AddRecord(Dictionary<string, StagedFile> records, StagedFile record)
    {
        if (!records.TryAdd(record.RelativePath, record))
        {
            throw PkgException.Integrity($"Duplicate staged path '{record.RelativePath}'.");
        }
    }

    private async Task<InstallResult> InstallUnmanagedAsync(ResolvedPlan plan, InstallRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InstallDirectory))
        {
            throw PkgException.Usage(
                "--unmanaged/--download-only requires --dir; the target is not added to the manifest.");
        }

        var target = PathSafety.NormalizeAbsolutePath(Path.GetFullPath(
                Path.IsPathFullyQualified(request.InstallDirectory)
                    ? request.InstallDirectory
                    : Path.Combine(_paths.WorkingDirectory, request.InstallDirectory)),
            "unmanaged target");
        var parent = Path.GetDirectoryName(target)
                     ?? throw PkgException.Unsafe("The unmanaged target has no parent directory.");
        PathSafety.EnsureNotProtected(target, "unmanaged target");
        _paths.EnsureOutsideProgramRoot(target, "unmanaged target");
        _paths.EnsureNotApplicationState(target, "unmanaged target", allowManagedInstallRoot: false);
        if (!Directory.Exists(parent))
        {
            throw PkgException.NotFound(ErrorCodes.FileSystem,
                $"The parent directory for the unmanaged target does not exist: {parent}");
        }

        PathSafety.EnsureNoReparsePoints(parent, target, "unmanaged target");
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw PkgException.Conflict(
                $"The unmanaged target already exists and will not be replaced: {target}");
        }

        _log.Warn("UNMANAGED: this location is outside pkgforge management. It will not be recorded, " +
                  "will not appear in 'installed', cannot be removed with 'pkgforge remove', and will not " +
                  "be cleaned automatically.");
        if (!await _confirmation.ConfirmAsync(
                $"Download and expand {plan.PackageId} into unmanaged location '{target}'?", cancellationToken)
                .ConfigureAwait(false))
        {
            throw new PkgException(ErrorCodes.Cancelled, ExitCode.Cancelled, "Unmanaged download cancelled.");
        }

        EnsureDiskSpace(plan, parent);
        var stagingPath = Path.Combine(parent, ".pkgforge-unmanaged-" + plan.InstallId + "-" +
                                                Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingPath);
        try
        {
            _cache.CleanStaleTemporaryFiles();
            _log.Info($"Downloading {plan.DownloadCount} files with {request.Threads} workers.");
            var cachePaths = await DownloadAllAsync(plan, request.Threads, cancellationToken)
                .ConfigureAwait(false);
            var records = new Dictionary<string, StagedFile>(StringComparer.OrdinalIgnoreCase);
            CreatePlanDirectories(stagingPath, plan);
            await CopyDownloadsAsync(plan, cachePaths, stagingPath, records, cancellationToken)
                .ConfigureAwait(false);
            await CopyAssetLinksAsync(plan, stagingPath, records, cancellationToken).ConfigureAwait(false);
            await ExtractNativesAsync(plan, stagingPath, records, cancellationToken).ConfigureAwait(false);
            PathSafety.EnsureTreeHasNoReparsePoints(stagingPath, "unmanaged staging");
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw PkgException.Conflict(
                    $"The unmanaged target appeared while staging and will not be replaced: {target}");
            }

            Directory.Move(stagingPath, target);
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                try
                {
                    SafeDirectory.DeleteControlledTree(stagingPath, parent, "unmanaged staging cleanup");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
                {
                    _log.Warn($"Could not clean unmanaged staging directory '{stagingPath}': {exception.Message}");
                }
            }

            throw;
        }

        _log.Info($"Unmanaged download completed at '{target}'. It is not tracked by pkgforge.");
        return new InstallResult(plan.InstallId, plan.PackageId, plan.DownloadCount, plan.TotalBytes,
            false, target, Unmanaged: true);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizeUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private void EnsureManagedRoot(string managedRoot, bool allowCreate)
    {
        var full = PathSafety.NormalizeAbsolutePath(managedRoot, "managed install root");
        PathSafety.EnsureNotProtected(full, "managed install root");
        _paths.EnsureNotApplicationState(full, "managed install root", allowManagedInstallRoot: true);
        if (Directory.Exists(full))
        {
            PathSafety.EnsureNoReparsePoints(full, full, "managed install root");
            return;
        }

        if (!allowCreate)
        {
            throw PkgException.NotFound(ErrorCodes.FileSystem,
                $"Managed install root does not exist: {full}");
        }

        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
        {
            throw PkgException.Unsafe("Managed install root has no parent directory.");
        }

        if (!Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        SafeDirectory.CreateControlledDirectory(full, parent, "managed install root");
    }

    private void CleanupStaleChildren(string parent, string prefix, TimeSpan age, string context)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - age;
        foreach (var path in Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    continue;
                }

                SafeDirectory.DeleteControlledTree(path, parent, context);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
            {
                _log.Warn($"Could not clean stale directory '{path}': {exception.Message}");
            }
        }
    }

    private void Rollback(string target, string stagingRoot, string? backupPath)
    {
        _log.Warn("Installation manifest could not be committed; rolling back the target directory.");
        if (Directory.Exists(target))
        {
            var failedPath = Path.Combine(stagingRoot, "failed-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(target, failedPath);
                if (backupPath is not null && Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, target);
                }

                if (Directory.Exists(failedPath))
                {
                    SafeDirectory.DeleteControlledTree(failedPath, stagingRoot, "failed installation");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
            {
                _log.Error($"Rollback failed: {exception.Message}. Backup: '{backupPath ?? "<none>"}', " +
                           $"new data: '{failedPath}'.");
            }
        }
        else if (backupPath is not null && Directory.Exists(backupPath))
        {
            Directory.Move(backupPath, target);
        }
    }

    private sealed record InstallTargetState(bool Exists, bool HasContent);
}
