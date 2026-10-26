using System.Text.Json.Serialization;

namespace PkgForge;

public sealed class IndexShard
{
    public string Id { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    public int UserVersion { get; set; }

    public List<string> Urls { get; set; } = [];
}

public sealed class IndexCounts
{
    public int Versions { get; set; }

    public int ClientPlans { get; set; }

    public int ServerPlans { get; set; }

    public int AssetDocs { get; set; }
}

public sealed class IndexManifest
{
    public int Schema { get; set; }

    public string Format { get; set; } = string.Empty;

    public string GeneratedAt { get; set; } = string.Empty;

    public string LatestRelease { get; set; } = string.Empty;

    public string LatestSnapshot { get; set; } = string.Empty;

    public List<IndexShard> Shards { get; set; } = [];

    public IndexCounts Counts { get; set; } = new();

    public string SourceManifestSha256 { get; set; } = string.Empty;
}

public sealed class IndexState
{
    public int Schema { get; set; } = 1;

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset FetchedAtUtc { get; set; }

    public string BundleId { get; set; } = string.Empty;

    public IndexManifest Manifest { get; set; } = new();
}

public sealed class VerifiedBundle
{
    public int Schema { get; set; } = 1;

    public string BundleId { get; set; } = string.Empty;

    public string ManifestSha256 { get; set; } = string.Empty;

    public DateTimeOffset VerifiedAtUtc { get; set; }

    public List<IndexShard> Shards { get; set; } = [];
}

public sealed class InstalledFile
{
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha1 { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;
}

public sealed class InstallManifest
{
    public int ManifestVersion { get; set; } = 2;

    public string InstallId { get; set; } = string.Empty;

    public string PackageId { get; set; } = string.Empty;

    public string VersionId { get; set; } = string.Empty;

    public string VersionKey { get; set; } = string.Empty;

    public string VersionType { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    /// <summary>Managed install path relative to the program root.</summary>
    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>Managed instances root relative to the program root.</summary>
    public string ManagedRoot { get; set; } = string.Empty;

    public string MarkerFile { get; set; } = ".mcpkg-install.json";

    public DateTimeOffset InstalledAtUtc { get; set; }

    public string IndexSource { get; set; } = string.Empty;

    public string IndexBundleId { get; set; } = string.Empty;

    public int PlanId { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public List<InstalledFile> Files { get; set; } = [];
}

public sealed class InstallMarker
{
    public int ManifestVersion { get; set; } = 2;

    public string InstallId { get; set; } = string.Empty;

    public string PackageId { get; set; } = string.Empty;

    public string VersionKey { get; set; } = string.Empty;

    public string VersionType { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    /// <summary>Managed install path relative to the program root.</summary>
    public string InstallDirectory { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IndexManifest))]
[JsonSerializable(typeof(IndexState))]
[JsonSerializable(typeof(VerifiedBundle))]
[JsonSerializable(typeof(InstallManifest))]
[JsonSerializable(typeof(InstallMarker))]
internal sealed partial class PkgForgeJsonContext : JsonSerializerContext
{
}
