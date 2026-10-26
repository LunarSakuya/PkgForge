namespace PkgForge;

public sealed record PackageRow(
    int PlanId,
    string VersionKey,
    string VersionId,
    string VersionType,
    string ReleaseTime,
    PackageKind Kind,
    string Platform,
    long TotalBytes,
    int FileCount,
    int AssetCount,
    string? AssetIndex)
{
    public string PackageId => PackageNaming.CanonicalPackageId(VersionKey, Kind,
        Kind == PackageKind.Server ? null : Platform);

    public int ResourceCount => checked(FileCount + AssetCount);
}

public sealed record PackageListResult(IReadOnlyList<PackageRow> Rows, int TotalMatched);

public sealed record AssetLinkOperation(string FromPath, string ToPath);

public sealed record LaunchArgument(string Phase, string Value);

public sealed record ResolvedPlan(
    int PlanId,
    string VersionId,
    string VersionKey,
    string VersionType,
    PackageKind Kind,
    string Platform,
    string PackageId,
    string InstallId,
    long TotalBytes,
    string? AssetIndex,
    string? AssetMode,
    string? AssetRoot,
    string? NativeDirectory,
    IReadOnlyList<DownloadItem> Downloads,
    IReadOnlyList<AssetLinkOperation> Links,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> ExtractExcludes,
    string? MainClass = null,
    string? AssetsSha1 = null,
    long? AssetsTotalSize = null,
    string? LoggingArgument = null,
    string? LoggingPath = null,
    int JavaMajor = 0,
    string ReleaseTime = "",
    IReadOnlyList<string>? Classpath = null,
    IReadOnlyList<LaunchArgument>? LaunchArguments = null)
{
    public int DownloadCount => Downloads.Count;

    public IReadOnlyList<string> ClasspathFiles => Classpath ?? [];

    public IReadOnlyList<LaunchArgument> LauncherArguments => LaunchArguments ?? [];
}
