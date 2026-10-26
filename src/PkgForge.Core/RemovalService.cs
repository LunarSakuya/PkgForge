namespace PkgForge;

public sealed record RemoveRequest(string Identifier, bool AssumeYes, bool Force);

public sealed record RemoveResult(InstallManifest Manifest, int FileCount, long TotalBytes);

public sealed class RemovalService
{
    private static readonly TimeSpan RemoveLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RemoveLockStaleAfter = TimeSpan.FromMinutes(10);

    private readonly AppPaths _paths;
    private readonly InstallManifestStore _manifests;
    private readonly IConfirmation _confirmation;
    private readonly ILog _log;

    public RemovalService(AppPaths paths, InstallManifestStore manifests, IConfirmation confirmation, ILog log)
    {
        _paths = paths;
        _manifests = manifests;
        _confirmation = confirmation;
        _log = log;
    }

    public async Task<RemoveResult> RemoveAsync(RemoveRequest request, CancellationToken cancellationToken)
    {
        var manifest = await _manifests.FindAsync(request.Identifier, cancellationToken).ConfigureAwait(false);
        var target = ValidateManifestTarget(manifest);
        await EnsureTargetNotSharedAsync(manifest, cancellationToken).ConfigureAwait(false);
        var marker = await ReadAndValidateMarkerAsync(manifest, target, cancellationToken).ConfigureAwait(false);
        _ = marker;

        var inspection = await InspectAsync(manifest, target, request.Force, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.HasMismatch && !request.Force)
        {
            throw PkgException.Conflict(
                $"Installation '{manifest.InstallId}' does not exactly match its manifest " +
                $"({inspection.ExtraFiles.Count} extra, {inspection.MissingFiles.Count} missing, " +
                $"{inspection.ModifiedFiles.Count} modified). Re-run with --force to remove the whole folder.");
        }

        _log.Info($"Install : {manifest.InstallId}");
        _log.Info($"Package : {manifest.PackageId}");
        _log.Info($"Target  : {target}");
        _log.Info($"Files   : {manifest.FileCount} ({SizeFormatter.Format(manifest.TotalBytes)})");
        if (inspection.HasMismatch)
        {
            _log.Warn($"Manifest mismatch: {inspection.ExtraFiles.Count} extra, " +
                      $"{inspection.MissingFiles.Count} missing, {inspection.ModifiedFiles.Count} modified. " +
                      "--force will remove the entire folder.");
        }

        if (request.AssumeYes && request.Force)
        {
            _log.Warn("--yes with --force: removing the entire folder without an interactive confirmation.");
        }

        if (!await _confirmation.ConfirmAsync($"Remove the entire installation folder '{target}'?",
                cancellationToken).ConfigureAwait(false))
        {
            throw new PkgException(ErrorCodes.Cancelled, ExitCode.Cancelled, "Removal cancelled.");
        }

        var lockPath = _paths.InstallLockPath(target);
        await using var removeLock = await ProcessLock.AcquireAsync(lockPath, RemoveLockTimeout,
            RemoveLockStaleAfter, _log, cancellationToken).ConfigureAwait(false);

        target = ValidateManifestTarget(manifest);
        _ = await ReadAndValidateMarkerAsync(manifest, target, cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(target))
        {
            throw PkgException.NotFound(ErrorCodes.ManifestMissing,
                $"The recorded installation directory no longer exists: {target}");
        }

        var managedRoot = _paths.InstallRoot;
        var trashRoot = Path.Combine(managedRoot, ".mcpkg-trash");
        SafeDirectory.CreateControlledDirectory(trashRoot, managedRoot, "removal trash root");
        var trashPath = Path.Combine(trashRoot, manifest.InstallId + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(target, trashPath);

        try
        {
            await _manifests.DeleteAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RestoreTarget(target, trashPath);
            await RestoreManifestAsync(manifest).ConfigureAwait(false);
            throw;
        }

        try
        {
            PathSafety.EnsureTreeHasNoReparsePoints(trashPath, "removal trash");
            SafeDirectory.DeleteControlledTree(trashPath, trashRoot, "removed installation");
        }
        catch
        {
            RestoreTarget(target, trashPath);
            await RestoreManifestAsync(manifest).ConfigureAwait(false);
            throw;
        }

        _log.Info($"Removed installation '{manifest.InstallId}'.");
        return new RemoveResult(manifest, manifest.FileCount, manifest.TotalBytes);
    }

    private static void RestoreTarget(string target, string trashPath)
    {
        if (!Directory.Exists(trashPath) || Directory.Exists(target))
        {
            return;
        }

        try
        {
            Directory.Move(trashPath, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
        {
            throw new PkgException(ErrorCodes.FileSystem, ExitCode.RuntimeFailure,
                $"Removal failed and the folder could not be restored. Recovery path: '{trashPath}'. " +
                exception.Message, exception);
        }
    }

    private async Task RestoreManifestAsync(InstallManifest manifest)
    {
        try
        {
            await _manifests.SaveAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PkgException or IOException or UnauthorizedAccessException)
        {
            _log.Error($"Removal restored the folder but could not restore its manifest: {exception.Message}. " +
                       $"Recovery manifest data: installId='{manifest.InstallId}', " +
                       $"directory='{manifest.InstallDirectory}'.");
        }
    }

    private async Task EnsureTargetNotSharedAsync(InstallManifest manifest, CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var other in await _manifests.LoadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(other.InstallId, manifest.InstallId, StringComparison.Ordinal) &&
                string.Equals(other.InstallDirectory, manifest.InstallDirectory, comparison))
            {
                throw PkgException.Conflict(
                    $"Installation '{manifest.InstallId}' and '{other.InstallId}' reference the same directory " +
                    $"'{manifest.InstallDirectory}'. Refusing to remove either record.");
            }
        }
    }

    private string ValidateManifestTarget(InstallManifest manifest)
    {
        var target = _paths.ResolveManagedInstall(manifest.InstallDirectory, "installation directory");
        var managedRoot = _paths.InstallRoot;
        if (!PathSafety.IsDirectChild(managedRoot, target))
        {
            throw PkgException.Unsafe("Removal refused: the install directory is not a direct child of its managed root.");
        }

        PathSafety.EnsureNotProtected(target, "removal");
        _paths.EnsureNotApplicationState(target, "removal", allowManagedInstallRoot: true);

        if (!Directory.Exists(target))
        {
            throw PkgException.NotFound(ErrorCodes.ManifestMissing,
                $"The recorded installation directory no longer exists: {target}");
        }

        PathSafety.EnsureTreeHasNoReparsePoints(target, "removal");
        if (PathSafety.HasProjectMarkers(target))
        {
            throw PkgException.Protected("Removal refused: the target contains project markers.");
        }

        PathSafety.EnsureNoReparsePoints(managedRoot, target, "removal");
        return target;
    }

    private async Task<InstallMarker> ReadAndValidateMarkerAsync(InstallManifest manifest, string target,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(target, InstallManifestStore.MarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw PkgException.NotFound(ErrorCodes.MarkerMissing,
                $"Installation marker is missing: '{markerPath}'. Refusing to delete anything.");
        }

        var marker = await _manifests.TryReadMarkerAsync(target, cancellationToken).ConfigureAwait(false)
                     ?? throw PkgException.NotFound(ErrorCodes.MarkerMissing,
                         $"Installation marker is missing or empty: '{markerPath}'.");
        InstallManifestStore.ValidateMarker(marker, manifest);
        return marker;
    }

    private async Task<RemovalInspection> InspectAsync(InstallManifest manifest, string target, bool force,
        CancellationToken cancellationToken)
    {
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var actual = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(target, file).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, InstallManifestStore.MarkerFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var safe = PathSafety.ValidateRelativePath(relative, "installed file");
            actual[safe] = new FileInfo(file).Length;
        }

        var extras = actual.Keys.Where(path => !expected.ContainsKey(path)).OrderBy(path => path,
            StringComparer.Ordinal).ToList();
        var missing = expected.Keys.Where(path => !actual.ContainsKey(path)).OrderBy(path => path,
            StringComparer.Ordinal).ToList();
        var modified = new List<string>();
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(entry.Path, out var size))
            {
                continue;
            }

            if (size != entry.Size)
            {
                modified.Add(entry.Path);
                continue;
            }

            if (force)
            {
                continue;
            }

            var fullPath = PathSafety.CombineUnderRoot(target, entry.Path, "installed file");
            var actualHash = await Hashing.Sha1FileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, entry.Sha1, StringComparison.Ordinal))
            {
                modified.Add(entry.Path);
            }
        }

        return new RemovalInspection(extras, missing, modified);
    }

    private sealed record RemovalInspection(
        IReadOnlyList<string> ExtraFiles,
        IReadOnlyList<string> MissingFiles,
        IReadOnlyList<string> ModifiedFiles)
    {
        public bool HasMismatch => ExtraFiles.Count > 0 || MissingFiles.Count > 0 || ModifiedFiles.Count > 0;
    }
}
