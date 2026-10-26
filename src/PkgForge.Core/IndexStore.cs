namespace PkgForge;

public sealed class LoadedIndex
{
    public LoadedIndex(IndexState state, IndexSource source, string bundleId, string bundleDirectory,
        IReadOnlyDictionary<string, string> shardPaths)
    {
        State = state;
        Source = source;
        BundleId = bundleId;
        BundleDirectory = bundleDirectory;
        ShardPaths = shardPaths;
    }

    public IndexState State { get; }

    public IndexManifest Manifest => State.Manifest;

    public IndexSource Source { get; }

    public string BundleId { get; }

    public string BundleDirectory { get; }

    public IReadOnlyDictionary<string, string> ShardPaths { get; }

    public string ShardPath(string id) =>
        ShardPaths.TryGetValue(id, out var path)
            ? path
            : throw PkgException.Integrity($"The active index has no shard '{id}'.");
}

public sealed class IndexStore
{
    private const long MaxStateBytes = 2L * 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly ILog _log;

    public IndexStore(AppPaths paths, ILog log)
    {
        _paths = paths;
        _log = log;
    }

    public string StatePath => _paths.StatePath;

    public static string ManifestHash(IndexManifest manifest) =>
        Hashing.Sha256Text(JsonCodec.SerializeIndexManifest(manifest));

    public async Task<LoadedIndex?> TryLoadCurrentAsync(bool verifyHashes, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_paths.StatePath))
            {
                return null;
            }

            PathSafety.EnsureNoReparsePoints(_paths.IndexRoot, _paths.StatePath, "active index state");
            var file = new FileInfo(_paths.StatePath);
            if (file.Length <= 0 || file.Length > MaxStateBytes)
            {
                throw PkgException.Integrity("The active index state file has an invalid size.");
            }

            var text = await File.ReadAllTextAsync(_paths.StatePath, cancellationToken).ConfigureAwait(false);
            var state = JsonCodec.DeserializeIndexState(text)
                        ?? throw PkgException.Integrity("The active index state is empty.");
            if (state.Schema != 1)
            {
                throw PkgException.Integrity("The active index state has an unsupported schema.");
            }

            if (state.Manifest is null)
            {
                throw PkgException.Integrity("The active index state has no manifest.");
            }

            if (!Hashing.IsLowerHex(state.BundleId, 64))
            {
                throw PkgException.Integrity("The active index bundle id is not a SHA-256 value.");
            }

            var source = IndexSource.Parse(state.Source, "active index source");
            var bundleId = IndexContract.ValidateManifest(state.Manifest, "active index manifest");
            if (!string.Equals(bundleId, state.BundleId, StringComparison.Ordinal))
            {
                throw PkgException.Integrity("The active index bundle id does not match its shard hashes.");
            }

            var bundleDirectory = _paths.BundleDirectory(state.BundleId);
            if (!PathSafety.IsDirectChild(_paths.DbRoot, bundleDirectory))
            {
                throw PkgException.Unsafe("The index bundle is outside the global database root.");
            }

            if (!Directory.Exists(bundleDirectory))
            {
                throw PkgException.NotFound(ErrorCodes.IndexMissing,
                    $"The active index database bundle is missing: {bundleDirectory}");
            }

            PathSafety.EnsureNoReparsePoints(_paths.DbRoot, bundleDirectory, "active index bundle");
            var verifiedPath = Path.Combine(bundleDirectory, "verified.json");
            if (!File.Exists(verifiedPath))
            {
                throw PkgException.Integrity("The active index bundle has no verification record.");
            }

            var verifiedText = await File.ReadAllTextAsync(verifiedPath, cancellationToken).ConfigureAwait(false);
            var verified = JsonCodec.DeserializeVerifiedBundle(verifiedText)
                           ?? throw PkgException.Integrity("The index verification record is empty.");
            if (verified.Schema != 1 ||
                !string.Equals(verified.BundleId, state.BundleId, StringComparison.Ordinal) ||
                !string.Equals(verified.ManifestSha256, ManifestHash(state.Manifest), StringComparison.Ordinal))
            {
                throw PkgException.Integrity("The index verification record does not match the active index.");
            }

            if (verified.Shards is null)
            {
                throw PkgException.Integrity("The index verification record has no shard list.");
            }

            var verifiedShards = new Dictionary<string, IndexShard>(StringComparer.Ordinal);
            foreach (var shard in verified.Shards)
            {
                if (shard is null || string.IsNullOrWhiteSpace(shard.Id) || !verifiedShards.TryAdd(shard.Id, shard))
                {
                    throw PkgException.Integrity("The index verification record has an invalid shard list.");
                }
            }

            if (verifiedShards.Count != IndexContract.ShardIds.Length)
            {
                throw PkgException.Integrity("The index verification record has an unexpected shard set.");
            }

            var shardPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var shard in state.Manifest.Shards)
            {
                if (!verifiedShards.TryGetValue(shard.Id, out var recorded) ||
                    recorded.Size != shard.Size || !string.Equals(recorded.Sha256, shard.Sha256,
                        StringComparison.Ordinal))
                {
                    throw PkgException.Integrity($"The verification record for shard '{shard.Id}' is inconsistent.");
                }

                var shardPath = _paths.ShardPath(state.BundleId, shard.Id);
                if (!File.Exists(shardPath))
                {
                    throw PkgException.NotFound(ErrorCodes.IndexMissing,
                        $"The active index shard is missing: {shardPath}");
                }

                if (new FileInfo(shardPath).Length != shard.Size)
                {
                    throw PkgException.Integrity($"The active index shard '{shard.Id}' has the wrong size.");
                }

                if (verifyHashes)
                {
                    var hash = await Hashing.Sha256FileAsync(shardPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(hash, shard.Sha256, StringComparison.Ordinal))
                    {
                        throw PkgException.Integrity($"The active index shard '{shard.Id}' failed SHA-256 verification.");
                    }
                }

                shardPaths.Add(shard.Id, shardPath);
            }

            return new LoadedIndex(state, source, state.BundleId, bundleDirectory, shardPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PkgException or IOException or UnauthorizedAccessException or
                                              System.Text.Json.JsonException)
        {
            _log.Warn($"Active index is unusable: {exception.Message}");
            return null;
        }
    }

    public async Task<LoadedIndex> LoadRequiredAsync(bool verifyHashes, CancellationToken cancellationToken)
    {
        var index = await TryLoadCurrentAsync(verifyHashes, cancellationToken).ConfigureAwait(false);
        return index ?? throw PkgException.NotFound(ErrorCodes.IndexMissing,
            $"No usable global index exists at '{_paths.StatePath}'. Run 'pkgforge update' first.");
    }

    public async Task WriteStateAsync(IndexState state, CancellationToken cancellationToken)
    {
        _paths.EnsureGlobalLayout();
        PathSafety.EnsureNoReparsePoints(_paths.IndexRoot, _paths.StatePath, "active index state");
        var text = JsonCodec.SerializeIndexState(state);
        await AtomicFile.WriteAllTextAsync(_paths.StatePath, text + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteVerifiedAsync(string bundleDirectory, VerifiedBundle bundle,
        CancellationToken cancellationToken)
    {
        var verifiedPath = Path.Combine(bundleDirectory, "verified.json");
        var text = JsonCodec.SerializeVerifiedBundle(bundle);
        await AtomicFile.WriteAllTextAsync(verifiedPath, text + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> VerifyBundleFilesAsync(string bundleDirectory, IndexManifest manifest,
        bool verifyHashes, CancellationToken cancellationToken)
    {
        foreach (var shard in manifest.Shards)
        {
            var path = Path.Combine(bundleDirectory, shard.Id + ".idx");
            if (!File.Exists(path) || new FileInfo(path).Length != shard.Size)
            {
                return false;
            }

            if (verifyHashes)
            {
                var hash = await Hashing.Sha256FileAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, shard.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public void CleanStaleTemporaryDirectories()
    {
        if (!Directory.Exists(_paths.GlobalTemporary))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var path in Directory.EnumerateDirectories(_paths.GlobalTemporary, "index-update-*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    continue;
                }

                SafeDirectory.DeleteControlledTree(path, _paths.GlobalTemporary, "stale index staging directory");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PkgException)
            {
                _log.Warn($"Could not clean stale index staging directory '{path}': {exception.Message}");
            }
        }
    }
}
