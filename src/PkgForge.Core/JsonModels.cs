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

public sealed class LauncherVersionJson
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string? MainClass { get; set; }

    public string? Assets { get; set; }

    public LauncherAssetIndex? AssetIndex { get; set; }

    public LauncherDownloads? Downloads { get; set; }

    public List<LauncherLibrary> Libraries { get; set; } = [];

    public LauncherArguments? Arguments { get; set; }

    public LauncherLogging? Logging { get; set; }

    public LauncherJavaVersion? JavaVersion { get; set; }

    public int MinimumLauncherVersion { get; set; } = 21;

    public int ComplianceLevel { get; set; } = 1;

    public string ReleaseTime { get; set; } = string.Empty;

    public string Time { get; set; } = string.Empty;
}

public sealed class LauncherAssetIndex
{
    public string Id { get; set; } = string.Empty;

    public string Sha1 { get; set; } = string.Empty;

    public long Size { get; set; }

    public long TotalSize { get; set; }

    public string? Url { get; set; }
}

public sealed class LauncherDownloads
{
    public LauncherDownloadFile? Client { get; set; }
}

public sealed class LauncherLibrary
{
    public string Name { get; set; } = string.Empty;

    public LauncherLibraryDownloads? Downloads { get; set; }

    public List<LauncherRule>? Rules { get; set; }
}

public sealed class LauncherLibraryDownloads
{
    public LauncherDownloadFile? Artifact { get; set; }
}

public sealed class LauncherDownloadFile
{
    public string? Path { get; set; }

    public string Sha1 { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Url { get; set; } = string.Empty;
}

public sealed class LauncherRule
{
    public string Action { get; set; } = "allow";

    public LauncherOs? Os { get; set; }
}

public sealed class LauncherOs
{
    public string Name { get; set; } = string.Empty;
}

public sealed class LauncherArguments
{
    public List<string> Game { get; set; } = [];

    public List<string> Jvm { get; set; } = [];

    [JsonPropertyName("default-user-jvm")]
    public List<LauncherArgumentValue> DefaultUserJvm { get; set; } = [];
}

public sealed class LauncherArgumentValue
{
    public List<string> Value { get; set; } = [];
}

public sealed class LauncherLogging
{
    public LauncherLogClient? Client { get; set; }
}

public sealed class LauncherLogClient
{
    public string Argument { get; set; } = string.Empty;

    public LauncherLogFile File { get; set; } = new();

    public string Type { get; set; } = "log4j2-xml";
}

public sealed class LauncherLogFile
{
    public string Id { get; set; } = string.Empty;

    public string Sha1 { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Url { get; set; } = string.Empty;
}

public sealed class LauncherJavaVersion
{
    public int MajorVersion { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LauncherVersionJson))]
[JsonSerializable(typeof(IndexManifest))]
[JsonSerializable(typeof(IndexState))]
[JsonSerializable(typeof(VerifiedBundle))]
[JsonSerializable(typeof(InstallManifest))]
[JsonSerializable(typeof(InstallMarker))]
internal sealed partial class PkgForgeJsonContext : JsonSerializerContext
{
}
