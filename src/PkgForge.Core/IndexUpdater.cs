namespace PkgForge;

public sealed record IndexUpdateResult(LoadedIndex Index, bool Downloaded, bool Changed);

public sealed class IndexUpdater
{
    private static readonly TimeSpan IndexLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IndexLockStaleAfter = TimeSpan.FromMinutes(15);

    private readonly AppPaths _paths;
    private readonly IndexStore _store;
    private readonly HttpDownloader _downloader;
    private readonly ILog _log;

    public IndexUpdater(AppPaths paths, IndexStore store, HttpDownloader downloader, ILog log)
    {
        _paths = paths;
        _store = store;
        _downloader = downloader;
        _log = log;
    }

    public async Task<IndexUpdateResult> UpdateAsync(IndexSource source, CancellationToken cancellationToken)
    {
        _paths.EnsureGlobalLayout();
        _store.CleanStaleTemporaryDirectories();

        await using var indexLock = await ProcessLock.AcquireAsync(_paths.IndexLockPath, IndexLockTimeout,
            IndexLockStaleAfter, _log, cancellationToken).ConfigureAwait(false);

        _log.Info($"Fetching index manifest from {source.Display}");
        var manifestBytes = await _downloader.FetchBytesAsync(source.IndexUri,
            HttpDownloader.MaxManifestBytes, "index manifest", cancellationToken).ConfigureAwait(false);
        var manifest = JsonCodec.ParseIndexManifest(manifestBytes);
        var bundleId = IndexContract.ValidateManifest(manifest, "index manifest");
        _log.Info(
            $"Index manifest: {manifest.Counts.Versions} versions, {manifest.Counts.ClientPlans} client plans, " +
            $"{manifest.Counts.ServerPlans} server plans.");

        var current = await _store.TryLoadCurrentAsync(verifyHashes: false, cancellationToken).ConfigureAwait(false);
        if (current is not null && string.Equals(current.BundleId, bundleId, StringComparison.Ordinal))
        {
            if (string.Equals(current.Source.Display, source.Display, StringComparison.Ordinal))
            {
                _log.Info("Index is already up to date.");
                return new IndexUpdateResult(current, Downloaded: false, Changed: false);
            }

            // The verified shard bytes are unchanged. Keep the verified manifest
            // object (and therefore the matching verified.json record) and only
            // update the source metadata in the pointer file.
            var updatedState = new IndexState
            {
                Schema = 1,
                Source = source.Display,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                BundleId = bundleId,
                Manifest = current.Manifest,
            };
            await _store.WriteStateAsync(updatedState, cancellationToken).ConfigureAwait(false);
            var updated = await _store.LoadRequiredAsync(verifyHashes: false, cancellationToken).ConfigureAwait(false);
            _log.Info("Index shards were unchanged; refreshed the active source metadata.");
            return new IndexUpdateResult(updated, Downloaded: false, Changed: true);
        }

        var stagedBundleDirectory = Path.Combine(_paths.GlobalTemporary,
            "index-update-" + Guid.NewGuid().ToString("N"));
        var requiredShardBytes = manifest.Shards.Aggregate(0L, (total, shard) => checked(total + shard.Size));
        DiskSpace.EnsureAvailable(_paths.GlobalTemporary, requiredShardBytes, "index update");
        SafeDirectory.CreateControlledDirectory(stagedBundleDirectory, _paths.GlobalTemporary,
            "index staging directory");

        var downloaded = false;
        try
        {
            var downloads = manifest.Shards.Select(shard =>
                DownloadAndValidateShardAsync(source, shard, manifest.Counts, stagedBundleDirectory,
                    cancellationToken)).ToArray();
            await Task.WhenAll(downloads).ConfigureAwait(false);
            downloaded = true;

            var finalBundle = _paths.BundleDirectory(bundleId);
            var existingUsable = false;
            if (Directory.Exists(finalBundle))
            {
                existingUsable = await ExistingBundleIsUsableAsync(finalBundle, manifest, cancellationToken)
                    .ConfigureAwait(false);
                if (!existingUsable)
                {
                    _log.Warn($"Removing an invalid database bundle at '{finalBundle}'.");
                    SafeDirectory.DeleteControlledTree(finalBundle, _paths.DbRoot, "invalid index bundle");
                }
            }

            if (!existingUsable)
            {
                if (Directory.Exists(finalBundle))
                {
                    throw PkgException.Conflict($"Index bundle path became occupied: {finalBundle}");
                }

                Directory.Move(stagedBundleDirectory, finalBundle);
            }

            var verified = new VerifiedBundle
            {
                Schema = 1,
                BundleId = bundleId,
                ManifestSha256 = IndexStore.ManifestHash(manifest),
                VerifiedAtUtc = DateTimeOffset.UtcNow,
                Shards = manifest.Shards.Select(shard => new IndexShard
                {
                    Id = shard.Id,
                    Path = shard.Path,
                    Sha256 = shard.Sha256,
                    Size = shard.Size,
                    UserVersion = shard.UserVersion,
                    Urls = [.. shard.Urls],
                }).ToList(),
            };
            await _store.WriteVerifiedAsync(finalBundle, verified, cancellationToken).ConfigureAwait(false);

            var state = new IndexState
            {
                Schema = 1,
                Source = source.Display,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                BundleId = bundleId,
                Manifest = manifest,
            };
            await _store.WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
            var loaded = await _store.LoadRequiredAsync(verifyHashes: true, cancellationToken).ConfigureAwait(false);
            _log.Info($"Index updated atomically: bundle {bundleId[..16]}...");
            return new IndexUpdateResult(loaded, Downloaded: downloaded, Changed: true);
        }
        finally
        {
            if (Directory.Exists(stagedBundleDirectory))
            {
                try
                {
                    SafeDirectory.DeleteControlledTree(stagedBundleDirectory, _paths.GlobalTemporary,
                        "index staging directory cleanup");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
                {
                    _log.Warn($"Could not clean index staging directory: {exception.Message}");
                }
            }
        }
    }

    private async Task DownloadAndValidateShardAsync(IndexSource source, IndexShard shard, IndexCounts counts,
        string stagedBundleDirectory, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(stagedBundleDirectory, shard.Id + ".idx");
        var candidates = shard.Urls.Count > 0 ? shard.Urls : [shard.Path];
        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            var uri = source.ResolveIndexRelative(candidate, $"shard '{shard.Id}'");
            try
            {
                _log.Progress($"Downloading index shard {shard.Id} ({SizeFormatter.Format(shard.Size)})");
                await _downloader.DownloadToFileSha256Async(uri, destination, shard.Size, shard.Sha256,
                    $"index shard '{shard.Id}'", cancellationToken).ConfigureAwait(false);
                await IndexDatabaseValidator.ValidateShardAsync(destination, shard, counts, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (PkgException exception) when (exception.ExitCode is ExitCode.Network or ExitCode.Verification)
            {
                lastError = exception;
                _log.Warn($"Index URL failed for shard '{shard.Id}': {exception.Message}");
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
        }

        if (lastError is PkgException pkg)
        {
            throw new PkgException(pkg.Code, pkg.ExitCode,
                $"All index URLs failed for shard '{shard.Id}': {pkg.Message}", pkg);
        }

        throw PkgException.Network(
            $"All index URLs failed for shard '{shard.Id}': {lastError?.Message ?? "unknown error"}", lastError);
    }

    private async Task<bool> ExistingBundleIsUsableAsync(string bundleDirectory, IndexManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!await _store.VerifyBundleFilesAsync(bundleDirectory, manifest, verifyHashes: true, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            foreach (var shard in manifest.Shards)
            {
                await IndexDatabaseValidator.ValidateShardAsync(Path.Combine(bundleDirectory, shard.Id + ".idx"),
                    shard, manifest.Counts, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (PkgException exception)
        {
            _log.Warn($"Existing index bundle failed validation: {exception.Message}");
            return false;
        }
    }
}
