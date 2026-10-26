namespace PkgForge;

public sealed class InstallManifestStore
{
    public const string MarkerFileName = ".mcpkg-install.json";
    private const long MaxManifestBytes = 64L * 1024 * 1024;
    private readonly AppPaths _paths;

    public InstallManifestStore(AppPaths paths, ILog log)
    {
        _paths = paths;
        _ = log;
    }

    public async Task<InstallManifest?> TryLoadByIdAsync(string installId, CancellationToken cancellationToken)
    {
        if (!AppPaths.IsSafeInstallId(installId))
        {
            throw PkgException.Usage($"Invalid install id '{installId}'.");
        }

        var path = _paths.InstallManifestPath(installId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstallManifest> LoadByIdRequiredAsync(string installId, CancellationToken cancellationToken)
    {
        var manifest = await TryLoadByIdAsync(installId, cancellationToken).ConfigureAwait(false);
        return manifest ?? throw PkgException.NotFound(ErrorCodes.ManifestMissing,
            $"Installation record '{installId}' does not exist at '{_paths.InstallManifestPath(installId)}'.");
    }

    public async Task<InstallManifest> FindAsync(string identifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw PkgException.Usage("An install id or package id is required.");
        }

        if (identifier.Length > 256)
        {
            throw PkgException.Usage("The install id or package id exceeds the 256 character limit.");
        }

        if (AppPaths.IsSafeInstallId(identifier))
        {
            var exact = await TryLoadByIdAsync(identifier, cancellationToken).ConfigureAwait(false);
            if (exact is not null)
            {
                return exact;
            }
        }

        var matches = new List<InstallManifest>();
        foreach (var manifest in await LoadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(manifest.InstallId, identifier, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(manifest.PackageId, identifier, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(manifest);
            }
        }

        return matches.Count switch
        {
            0 => throw PkgException.NotFound(ErrorCodes.ManifestMissing,
                $"No installation record matches '{identifier}' in this program root."),
            1 => matches[0],
            _ => throw PkgException.Conflict(
                $"Multiple installation records match '{identifier}'; use the exact install id."),
        };
    }

    public async Task<IReadOnlyList<InstallManifest>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.ManifestsRoot))
        {
            return [];
        }

        PathSafety.EnsureNoReparsePoints(_paths.ManifestsRoot, _paths.ManifestsRoot,
            "managed manifest directory");
        var result = new List<InstallManifest>();
        foreach (var path in Directory.EnumerateFiles(_paths.ManifestsRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result.Add(await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is PkgException or IOException or UnauthorizedAccessException or
                                                  System.Text.Json.JsonException)
            {
                throw new PkgException(ErrorCodes.Integrity, ExitCode.Verification,
                    $"Installation record '{path}' is invalid; refusing to continue: {exception.Message}", exception);
            }
        }

        return result;
    }

    public async Task SaveAsync(InstallManifest manifest, CancellationToken cancellationToken)
    {
        Validate(manifest);
        _paths.EnsureManifestLayout();
        var path = _paths.InstallManifestPath(manifest.InstallId);
        EnsureManifestPath(path);
        var text = JsonCodec.SerializeInstallManifest(manifest);
        await AtomicFile.WriteAllTextAsync(path, text + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(InstallManifest manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _paths.InstallManifestPath(manifest.InstallId);
        EnsureManifestPath(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<InstallMarker?> TryReadMarkerAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(installDirectory, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        var info = new FileInfo(markerPath);
        if (info.Length <= 0 || info.Length > 64 * 1024)
        {
            throw PkgException.Integrity("The installation marker has an invalid size.");
        }

        try
        {
            var text = await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false);
            return JsonCodec.DeserializeInstallMarker(text);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw PkgException.Integrity($"The installation marker is not valid JSON: {exception.Message}", exception);
        }
    }

    public static void Validate(InstallManifest manifest)
    {
        if (manifest.ManifestVersion != 2)
        {
            throw PkgException.Integrity("Unsupported installation manifest version.");
        }

        if (string.IsNullOrWhiteSpace(manifest.InstallId) || !AppPaths.IsSafeInstallId(manifest.InstallId))
        {
            throw PkgException.Integrity("The installation record has an invalid install id.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            throw PkgException.Integrity("The installation record has no kind.");
        }

        PackageSelector.ValidateVersionToken(manifest.VersionKey);
        if (string.IsNullOrWhiteSpace(manifest.VersionId))
        {
            throw PkgException.Integrity("The installation record has no version id.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.VersionType) &&
            !PackageNaming.IsSafePlatformToken(manifest.VersionType))
        {
            throw PkgException.Integrity("The installation record has an invalid version type.");
        }

        PackageKind kind;
        try
        {
            kind = PackageKinds.Parse(manifest.Kind);
        }
        catch (PkgException exception)
        {
            throw PkgException.Integrity($"The installation record has an invalid kind: {exception.Message}",
                exception);
        }

        if (kind == PackageKind.Server)
        {
            if (!string.IsNullOrEmpty(manifest.Platform) &&
                !manifest.Platform.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                throw PkgException.Integrity("A server installation record must not have a platform.");
            }
        }
        else if (manifest.Platform is null || !PackageNaming.IsSafePlatformToken(manifest.Platform))
        {
            throw PkgException.Integrity("The installation record has an invalid client platform.");
        }

        var expectedPackageId = PackageNaming.CanonicalPackageId(manifest.VersionKey, kind,
            kind == PackageKind.Server ? null : manifest.Platform);
        if (!string.Equals(manifest.PackageId, expectedPackageId, StringComparison.Ordinal))
        {
            throw PkgException.Integrity("The installation record's package id is inconsistent.");
        }

        var target = PathSafety.ValidateRelativePath(manifest.InstallDirectory, "installation directory");
        var managedRoot = PathSafety.ValidateRelativePath(manifest.ManagedRoot, "managed root");
        if (!string.Equals(target, manifest.InstallDirectory, PathComparison) ||
            !string.Equals(managedRoot, manifest.ManagedRoot, PathComparison))
        {
            throw PkgException.Integrity("The installation record contains a non-canonical path.");
        }

        if (!string.Equals(managedRoot, AppPaths.InstancesRelativeRoot, PathComparison))
        {
            throw PkgException.Integrity("The installation record uses an unexpected managed root.");
        }

        if (!target.StartsWith(managedRoot + "/", PathComparison))
        {
            throw PkgException.Integrity("The installation directory is outside its managed root.");
        }

        var leaf = target[(managedRoot.Length + 1)..];
        if (leaf.Length == 0 || leaf.Contains('/', StringComparison.Ordinal))
        {
            throw PkgException.Integrity("The installation directory is not a direct child of its managed root.");
        }

        if (!string.Equals(manifest.MarkerFile, MarkerFileName, StringComparison.Ordinal))
        {
            throw PkgException.Integrity("The installation record references an unexpected marker file.");
        }

        try
        {
            IndexSource.Parse(manifest.IndexSource, "installation index source");
        }
        catch (PkgException exception)
        {
            throw PkgException.Integrity(
                $"The installation record has an invalid index source: {exception.Message}", exception);
        }

        if (!Hashing.IsLowerHex(manifest.IndexBundleId, 64))
        {
            throw PkgException.Integrity("The installation record has an invalid index bundle id.");
        }

        if (manifest.PlanId <= 0 || manifest.InstalledAtUtc == default)
        {
            throw PkgException.Integrity("The installation record has invalid plan metadata.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (manifest.Files is null)
        {
            throw PkgException.Integrity("The installation record has no file list.");
        }

        foreach (var file in manifest.Files)
        {
            if (file is null)
            {
                throw PkgException.Integrity("The installation record contains a null file entry.");
            }

            var relative = PathSafety.ValidateRelativePath(file.Path, "installed file path");
            if (!string.Equals(relative, file.Path, StringComparison.Ordinal))
            {
                throw PkgException.Integrity("An installed file path is not canonical.");
            }

            if (!seen.Add(relative))
            {
                throw PkgException.Integrity($"Duplicate installed file path '{relative}'.");
            }

            if (file.Size < 0 || !Hashing.IsLowerHex(file.Sha1, 40))
            {
                throw PkgException.Integrity($"Installed file '{relative}' has invalid size or hash.");
            }

            total = checked(total + file.Size);
        }

        if (manifest.FileCount != manifest.Files.Count || manifest.TotalBytes != total)
        {
            throw PkgException.Integrity("The installation record summary does not match its file list.");
        }
    }

    public static void ValidateMarker(InstallMarker marker, InstallManifest manifest)
    {
        if (marker.ManifestVersion != 2 ||
            !string.Equals(marker.InstallId, manifest.InstallId, StringComparison.Ordinal) ||
            !string.Equals(marker.PackageId, manifest.PackageId, StringComparison.Ordinal) ||
            !string.Equals(marker.VersionKey, manifest.VersionKey, StringComparison.Ordinal) ||
            !string.Equals(marker.VersionType, manifest.VersionType, StringComparison.Ordinal) ||
            !string.Equals(marker.Kind, manifest.Kind, StringComparison.Ordinal) ||
            !string.Equals(marker.Platform, manifest.Platform, StringComparison.Ordinal) ||
            !string.Equals(marker.InstallDirectory, manifest.InstallDirectory, PathComparison))
        {
            throw PkgException.Integrity("The installation marker does not match the user installation record.");
        }
    }

    private async Task<InstallManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        EnsureManifestPath(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxManifestBytes)
        {
            throw PkgException.Integrity($"Installation record '{path}' has an invalid size.");
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        InstallManifest? manifest;
        try
        {
            manifest = JsonCodec.DeserializeInstallManifest(text);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw PkgException.Integrity(
                $"Installation record '{path}' is not valid JSON: {exception.Message}", exception);
        }

        if (manifest is null)
        {
            throw PkgException.Integrity($"Installation record '{path}' is empty.");
        }
        Validate(manifest);
        var expectedName = manifest.InstallId + ".json";
        if (!string.Equals(Path.GetFileName(path), expectedName, PathComparison))
        {
            throw PkgException.Integrity($"Installation record filename does not match install id '{manifest.InstallId}'.");
        }

        return manifest;
    }

    private void EnsureManifestPath(string path)
    {
        if (!PathSafety.IsDirectChild(_paths.ManifestsRoot, path))
        {
            throw PkgException.Unsafe("An installation record is outside the managed manifest directory.");
        }

        PathSafety.EnsureNoReparsePoints(_paths.ManifestsRoot, path, "installation record");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
