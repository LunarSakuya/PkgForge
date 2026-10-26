namespace PkgForge;

/// <summary>
/// Contract of the published SQLite-shard index (schema 2).
/// </summary>
public static class IndexContract
{
    public const int ManifestSchema = 2;
    public const string ManifestFormat = "sqlite-shards";
    public const int DatabaseSchema = 1;
    public const int ApplicationId = 1296253511;
    public const long MaxShardBytes = 512L * 1024 * 1024;
    public static readonly string[] ShardIds = ["catalog", "launch", "assets"];

    public static string ValidateManifest(IndexManifest manifest, string context)
    {
        if (manifest.Schema != ManifestSchema)
        {
            throw Invalid($"{context}: unsupported manifest schema {manifest.Schema}; expected {ManifestSchema}.");
        }

        if (!string.Equals(manifest.Format, ManifestFormat, StringComparison.Ordinal))
        {
            throw Invalid($"{context}: unsupported manifest format '{manifest.Format}'.");
        }

        if (manifest.Shards is null)
        {
            throw Invalid($"{context}: the shard list is missing.");
        }

        if (manifest.Counts is null)
        {
            throw Invalid($"{context}: the counts object is missing.");
        }

        if (manifest.Shards.Count != ShardIds.Length)
        {
            throw Invalid($"{context}: expected exactly {ShardIds.Length} shards, got {manifest.Shards.Count}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shard in manifest.Shards)
        {
            if (shard is null)
            {
                throw Invalid($"{context}: a shard entry is null.");
            }

            if (!ShardIds.Contains(shard.Id, StringComparer.Ordinal))
            {
                throw Invalid($"{context}: unknown shard id '{shard.Id}'.");
            }

            if (!seen.Add(shard.Id))
            {
                throw Invalid($"{context}: duplicate shard id '{shard.Id}'.");
            }

            if (shard.Size <= 0 || shard.Size > MaxShardBytes)
            {
                throw Invalid($"{context}: shard '{shard.Id}' has an invalid size.");
            }

            if (!Hashing.IsLowerHex(shard.Sha256, 64))
            {
                throw Invalid($"{context}: shard '{shard.Id}' has an invalid SHA-256.");
            }

            if (shard.UserVersion != DatabaseSchema)
            {
                throw Invalid($"{context}: shard '{shard.Id}' has unsupported userVersion {shard.UserVersion}.");
            }

            ValidateUrlPath(shard.Path, $"{context}: shard '{shard.Id}' path");
            if (shard.Urls is null)
            {
                throw Invalid($"{context}: shard '{shard.Id}' has no URL list.");
            }

            if (shard.Urls.Count > 8)
            {
                throw Invalid($"{context}: shard '{shard.Id}' has too many fallback URLs.");
            }

            foreach (var url in shard.Urls)
            {
                if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 ||
                    url.Any(character => char.IsControl(character)))
                {
                    throw Invalid($"{context}: shard '{shard.Id}' has an invalid URL entry.");
                }
            }
        }

        if (manifest.Counts.Versions < 0 || manifest.Counts.ClientPlans < 0 ||
            manifest.Counts.ServerPlans < 0 || manifest.Counts.AssetDocs < 0)
        {
            throw Invalid($"{context}: negative counts in the manifest.");
        }

        if (!string.IsNullOrEmpty(manifest.SourceManifestSha256) &&
            !Hashing.IsLowerHex(manifest.SourceManifestSha256, 64))
        {
            throw Invalid($"{context}: sourceManifestSha256 is not lowercase SHA-256.");
        }

        if (string.IsNullOrWhiteSpace(manifest.LatestRelease))
        {
            throw Invalid($"{context}: latestRelease is empty.");
        }

        return Hashing.BundleId(manifest.Shards.Select(shard => (shard.Id, shard.Sha256)));
    }

    public static void ValidateUrlPath(string value, string context)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
        {
            throw Invalid($"{context}: invalid URL path.");
        }

        if (value[0] is '/' or '\\' || value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            throw Invalid($"{context}: URL path must be a relative path without a query or fragment.");
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw Invalid($"{context}: URL path contains a control character.");
            }
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw Invalid($"{context}: URL path contains an unsafe segment.");
            }
        }
    }

    private static PkgException Invalid(string message) =>
        new(ErrorCodes.InvalidIndex, ExitCode.Verification, message);
}
