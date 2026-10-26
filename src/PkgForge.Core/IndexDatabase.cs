using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PkgForge;

/// <summary>
/// Read-only access to the published SQLite shards. The table and column
/// names, application id and user version are the existing Relay contract;
/// no Mojang JSON is parsed here.
/// </summary>
public sealed class IndexDatabase
{
    private const int MaxPlanFiles = 2_000_000;
    private const int MaxPlanAssets = 2_000_000;
    private readonly LoadedIndex _index;
    private readonly Func<Uri, Uri>? _assetUrlMapper;

    public IndexDatabase(LoadedIndex index, Func<Uri, Uri>? assetUrlMapper = null)
    {
        _index = index;
        _assetUrlMapper = assetUrlMapper;
    }

    public Task<PackageListResult> ListPackagesAsync(PackageKind? kind, string? platform, string? search,
        int limit, CancellationToken cancellationToken) =>
        ListPackagesAsync(kind, platform, null, search, limit, cancellationToken);

    public Task<PackageListResult> ListPackagesAsync(PackageKind? kind, string? platform, string? versionType,
        string? search, int limit, CancellationToken cancellationToken) =>
        Task.Run(() => ListPackages(kind, platform, versionType, search, limit, cancellationToken),
            cancellationToken);

    public Task<ResolvedPlan> ResolvePlanAsync(PackageSelector selector, CancellationToken cancellationToken) =>
        Task.Run(() => ResolvePlan(selector, cancellationToken), cancellationToken);

    public IReadOnlyList<string> GetVersionTypes(CancellationToken cancellationToken = default)
    {
        using var catalog = SqliteSupport.OpenReadOnly(_index.ShardPath("catalog"), "catalog shard");
        using var command = catalog.CreateCommand();
        command.CommandText = "SELECT DISTINCT type FROM versions ORDER BY type ASC;";
        using var reader = command.ExecuteReader();
        var types = new SortedSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            types.Add(NormalizeField(reader.GetString(0)));
        }

        return types.ToList();
    }

    public void EnsureVersionTypeExists(string versionType, CancellationToken cancellationToken = default)
    {
        if (versionType.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var types = GetVersionTypes(cancellationToken);
        if (!types.Any(type => string.Equals(type, versionType, StringComparison.OrdinalIgnoreCase)))
        {
            var available = types.Contains("unknown", StringComparer.Ordinal)
                ? string.Join(", ", types)
                : string.Join(", ", types) + ", unknown";
            throw PkgException.Usage($"Unknown version type '{versionType}'. Available types: {available}.");
        }
    }

    private PackageListResult ListPackages(PackageKind? kind, string? platform, string? versionType,
        string? search, int limit, CancellationToken cancellationToken)
    {
        var context = "catalog shard";
        using var catalog = SqliteSupport.OpenReadOnly(_index.ShardPath("catalog"), context);
        using var assets = SqliteSupport.OpenReadOnly(_index.ShardPath("assets"), "assets shard");
        var assetSizes = LoadAssetSummaries(assets);

        var sql = """
                  SELECT p.id, v.key, v.id, v.type, v.release_time, p.kind, p.platform,
                         p.total_bytes, p.assets_index, COALESCE(fc.c, 0)
                  FROM plans p
                  JOIN versions v ON v.id = p.version_id
                  LEFT JOIN (SELECT plan_id, COUNT(*) AS c FROM files GROUP BY plan_id) fc ON fc.plan_id = p.id
                  ORDER BY v.release_time DESC, v.key ASC, p.kind ASC, p.platform ASC;
                  """;
        using var command = catalog.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<PackageRow>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowKind = reader.GetString(5) == "server" ? PackageKind.Server : PackageKind.Client;
            if (kind is not null && rowKind != kind)
            {
                continue;
            }

            var rowPlatform = NormalizeField(reader.GetString(6));
            if (!string.IsNullOrWhiteSpace(platform) &&
                !string.Equals(rowPlatform, platform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rowVersionType = NormalizeField(reader.GetString(3));
            if (!string.IsNullOrWhiteSpace(versionType) &&
                !versionType.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rowVersionType, versionType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assetIndex = reader.IsDBNull(8) ? null : reader.GetString(8);
            var assetCount = 0;
            if (rowKind == PackageKind.Client && assetIndex is not null &&
                assetSizes.TryGetValue(assetIndex, out var summary))
            {
                assetCount = summary.Count;
            }

            var row = new PackageRow(
                checked((int)reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetString(2),
                rowVersionType,
                reader.GetString(4),
                rowKind,
                rowPlatform,
                reader.GetInt64(7),
                checked((int)reader.GetInt64(9)),
                assetCount,
                assetIndex);
            if (!MatchesSearch(row, search))
            {
                continue;
            }

            rows.Add(row);
        }

        var total = rows.Count;
        var limited = limit > 0 && rows.Count > limit ? rows.Take(limit).ToList() : rows;
        return new PackageListResult(limited, total);
    }

    private ResolvedPlan ResolvePlan(PackageSelector selector, CancellationToken cancellationToken)
    {
        var catalogPath = _index.ShardPath("catalog");
        using var catalog = SqliteSupport.OpenReadOnly(catalogPath, "catalog shard");

        var version = ResolveVersion(catalog, selector.VersionToken);
        var platform = selector.Kind == PackageKind.Server
            ? string.Empty
            : selector.Platform ?? throw PkgException.Usage("A client platform is required.");
        if (selector.Kind == PackageKind.Client && string.IsNullOrWhiteSpace(platform))
        {
            throw PkgException.Usage("A client platform is required.");
        }

        int planId;
        long totalBytes;
        string? assetsIndex;
        string? assetsMode;
        string? assetsRoot;
        long? assetsSize;
        string? nativeDirectory;
        string? mainClass;
        string? assetsSha1;
        string? loggingArgument;
        string? loggingPath;
        int javaMajor;
        string releaseTime;
        using (var command = catalog.CreateCommand())
        {
            command.CommandText = """
                                  SELECT p.id, p.total_bytes, p.assets_index, p.assets_mode, p.assets_root,
                                         p.assets_size, n.dir, p.main_class, p.assets_sha1,
                                         p.logging_argument, p.logging_path, v.java_major, v.release_time
                                  FROM plans p
                                  JOIN versions v ON v.id = p.version_id
                                  LEFT JOIN natives n ON n.plan_id = p.id
                                  WHERE p.version_id = $version AND p.kind = $kind AND p.platform = $platform;
                                  """;
            command.Parameters.AddWithValue("$version", version.Id);
            command.Parameters.AddWithValue("$kind", selector.Kind.ToToken());
            command.Parameters.AddWithValue("$platform", platform);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw PkgException.NotFound(ErrorCodes.PackageNotFound,
                    $"Package '{version.Key}@{selector.Kind.ToToken()}" +
                    (selector.Kind == PackageKind.Server ? "'" : ":" + platform + "'") +
                    " does not exist.");
            }

            planId = checked((int)reader.GetInt64(0));
            totalBytes = reader.GetInt64(1);
            assetsIndex = reader.IsDBNull(2) ? null : reader.GetString(2);
            assetsMode = reader.IsDBNull(3) ? null : reader.GetString(3);
            assetsRoot = reader.IsDBNull(4) ? null : reader.GetString(4);
            assetsSize = reader.IsDBNull(5) ? null : reader.GetInt64(5);
            nativeDirectory = reader.IsDBNull(6) ? null : reader.GetString(6);
            mainClass = reader.IsDBNull(7) ? null : reader.GetString(7);
            assetsSha1 = reader.IsDBNull(8) ? null : reader.GetString(8);
            loggingArgument = reader.IsDBNull(9) ? null : reader.GetString(9);
            loggingPath = reader.IsDBNull(10) ? null : reader.GetString(10);
            javaMajor = checked((int)reader.GetInt64(11));
            releaseTime = reader.GetString(12);
        }

        if (totalBytes < 0)
        {
            throw PkgException.Integrity("The plan has a negative total size.");
        }

        var downloads = LoadPlanFiles(catalog, planId, cancellationToken);
        var directories = LoadPlanDirectories(planId, cancellationToken);
        var excludes = LoadExtractExcludes(catalog, planId);
        var classpath = LoadPlanClasspath(planId, cancellationToken);
        var launchArguments = LoadLaunchArguments(planId, cancellationToken);
        var links = new List<AssetLinkOperation>();

        if (selector.Kind == PackageKind.Client)
        {
            if (string.IsNullOrWhiteSpace(assetsIndex))
            {
                throw PkgException.Integrity($"Client package '{version.Key}' has no asset index.");
            }

            using var assets = SqliteSupport.OpenReadOnly(_index.ShardPath("assets"), "assets shard");
            AddAssets(assets, assetsIndex, assetsMode, assetsRoot, assetsSize, downloads, links, cancellationToken);
        }
        else if (assetsIndex is not null)
        {
            throw PkgException.Integrity($"Server package '{version.Key}' unexpectedly has an asset index.");
        }

        var uniqueDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedDirectories = new List<string>();
        foreach (var directory in directories)
        {
            var safe = PathSafety.ValidateRelativePath(directory.TrimEnd('/'), "plan directory");
            if (uniqueDirectories.Add(safe))
            {
                normalizedDirectories.Add(safe);
            }
        }

        if (!string.IsNullOrWhiteSpace(nativeDirectory))
        {
            var safeNative = PathSafety.ValidateRelativePath(nativeDirectory.TrimEnd('/'), "natives directory");
            if (uniqueDirectories.Add(safeNative))
            {
                normalizedDirectories.Add(safeNative);
            }
        }

        if (nativeDirectory is not null && downloads.All(item => item.Role != "native"))
        {
            nativeDirectory = null;
        }

        EnsureNoPathConflicts(downloads, links);
        var packageId = PackageNaming.CanonicalPackageId(version.Key, selector.Kind,
            selector.Kind == PackageKind.Server ? null : platform);
        var installId = PackageNaming.InstallId(version.Key, selector.Kind,
            selector.Kind == PackageKind.Server ? null : platform);
        return new ResolvedPlan(planId, version.Id, version.Key, version.Type, selector.Kind, platform,
            packageId, installId, totalBytes, assetsIndex, assetsMode, assetsRoot, nativeDirectory,
            downloads, links, normalizedDirectories, excludes, mainClass, assetsSha1, assetsSize,
            loggingArgument, loggingPath, javaMajor, releaseTime, classpath, launchArguments);
    }

    private List<DownloadItem> LoadPlanFiles(SqliteConnection catalog, int planId, CancellationToken cancellationToken)
    {
        var byPath = new Dictionary<string, (string Role, string Sha1, long Size, List<Uri> Urls)>(
            StringComparer.OrdinalIgnoreCase);
        using var command = catalog.CreateCommand();
        command.CommandText = """
                              SELECT f.path, f.role, c.sha1, c.size, u.ordinal, u.url
                              FROM files f
                              JOIN contents c ON c.id = f.content_id
                              JOIN content_urls u ON u.content_id = c.id
                              WHERE f.plan_id = $plan
                              ORDER BY f.path ASC, u.ordinal ASC;
                              """;
        command.Parameters.AddWithValue("$plan", planId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathSafety.ValidateRelativePath(reader.GetString(0), "plan file path");
            var role = reader.GetString(1);
            var sha1 = reader.GetString(2);
            var size = reader.GetInt64(3);
            var url = ParseDownloadUri(reader.GetString(5), path);
            if (!Hashing.IsLowerHex(sha1, 40) || size < 0)
            {
                throw PkgException.Integrity($"Invalid hash/size for plan file '{path}'.");
            }

            if (byPath.TryGetValue(path, out var existing))
            {
                if (!string.Equals(existing.Sha1, sha1, StringComparison.Ordinal) || existing.Size != size)
                {
                    throw PkgException.Integrity($"Conflicting content for plan path '{path}'.");
                }

                if (!existing.Urls.Contains(url))
                {
                    existing.Urls.Add(url);
                }

                continue;
            }

            byPath.Add(path, (role, sha1, size, [url]));
        }

        if (byPath.Count == 0)
        {
            throw PkgException.Integrity("The selected plan contains no downloadable files.");
        }

        return byPath.Select(pair => new DownloadItem(pair.Key, pair.Value.Sha1, pair.Value.Size, pair.Value.Role,
            pair.Value.Urls)).ToList();
    }

    private void AddAssets(SqliteConnection assets, string indexId, string? mode, string? root, long? assetsSize,
        List<DownloadItem> downloads, List<AssetLinkOperation> links, CancellationToken cancellationToken)
    {
        using (var doc = assets.CreateCommand())
        {
            doc.CommandText = """
                              SELECT mode, root, total_bytes FROM asset_docs WHERE index_id = $id;
                              """;
            doc.Parameters.AddWithValue("$id", indexId);
            using var reader = doc.ExecuteReader();
            if (!reader.Read())
            {
                throw PkgException.Integrity($"Asset document '{indexId}' is missing from the assets shard.");
            }

            var documentMode = reader.GetString(0);
            var documentRoot = reader.GetString(1);
            var documentSize = reader.GetInt64(2);
            if (!string.Equals(documentMode, mode, StringComparison.Ordinal) ||
                !string.Equals(documentRoot, root, StringComparison.Ordinal) ||
                (assetsSize is not null && assetsSize.Value != documentSize))
            {
                throw PkgException.Integrity($"Asset document '{indexId}' does not match the plan metadata.");
            }
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloadPaths = downloads.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        using (var objects = assets.CreateCommand())
        {
            objects.CommandText = """
                                  SELECT path, sha1, size, url FROM v_asset_objects WHERE index_id = $id;
                                  """;
            objects.Parameters.AddWithValue("$id", indexId);
            using var reader = objects.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (downloads.Count >= MaxPlanFiles + MaxPlanAssets)
                {
                    throw PkgException.Integrity("The plan has too many files.");
                }

                var path = PathSafety.ValidateRelativePath(reader.GetString(0), "asset object path");
                var sha1 = reader.GetString(1);
                var size = reader.GetInt64(2);
                var uri = ParseDownloadUri(reader.GetString(3), path);
                if (_assetUrlMapper is not null)
                {
                    uri = _assetUrlMapper(uri);
                    if (!uri.IsAbsoluteUri)
                    {
                        throw PkgException.Integrity("The asset URL mapper returned a non-absolute URL.");
                    }
                }

                if (!Hashing.IsLowerHex(sha1, 40) || size < 0)
                {
                    throw PkgException.Integrity($"Invalid asset object '{path}'.");
                }

                if (!paths.Add(path))
                {
                    throw PkgException.Integrity($"Duplicate asset object path '{path}'.");
                }

                if (downloadPaths.TryGetValue(path, out var duplicate))
                {
                    if (!string.Equals(duplicate.Sha1, sha1, StringComparison.Ordinal) || duplicate.Size != size)
                    {
                        throw PkgException.Integrity($"Asset path conflicts with a plan file: '{path}'.");
                    }

                    continue;
                }

                var download = new DownloadItem(path, sha1, size, "asset", [uri]);
                downloads.Add(download);
                downloadPaths.Add(path, download);
            }
        }

        if (downloads.Count > MaxPlanFiles + MaxPlanAssets)
        {
            throw PkgException.Integrity("The plan has too many files.");
        }

        using (var linkCommand = assets.CreateCommand())
        {
            linkCommand.CommandText = """
                                      SELECT from_path, to_path FROM asset_links
                                      WHERE doc_id = $id ORDER BY ordinal ASC;
                                      """;
            linkCommand.Parameters.AddWithValue("$id", indexId);
            using var reader = linkCommand.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = PathSafety.ValidateRelativePath(reader.GetString(0), "asset link source");
                var to = PathSafety.ValidateRelativePath(reader.GetString(1), "asset link target");
                if (!paths.Contains(from))
                {
                    throw PkgException.Integrity($"Asset link source '{from}' is not a downloaded asset object.");
                }

                links.Add(new AssetLinkOperation(from, to));
            }
        }
    }

    private List<string> LoadPlanClasspath(int planId, CancellationToken cancellationToken)
    {
        using var launch = SqliteSupport.OpenReadOnly(_index.ShardPath("launch"), "launch shard");
        using var command = launch.CreateCommand();
        command.CommandText = "SELECT path FROM plan_classpath WHERE plan_id = $plan ORDER BY ordinal ASC;";
        command.Parameters.AddWithValue("$plan", planId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(PathSafety.ValidateRelativePath(reader.GetString(0), "plan classpath"));
        }

        return result;
    }

    private List<LaunchArgument> LoadLaunchArguments(int planId, CancellationToken cancellationToken)
    {
        using var launch = SqliteSupport.OpenReadOnly(_index.ShardPath("launch"), "launch shard");
        using var command = launch.CreateCommand();
        command.CommandText = """
                              SELECT phase,ordinal,value FROM launch_args
                              WHERE plan_id = $plan ORDER BY phase ASC,ordinal ASC;
                              """;
        command.Parameters.AddWithValue("$plan", planId);
        using var reader = command.ExecuteReader();
        var result = new List<LaunchArgument>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new LaunchArgument(reader.GetString(0), reader.GetString(2)));
        }

        return result;
    }

    private List<string> LoadPlanDirectories(int planId, CancellationToken cancellationToken)
    {
        using var launch = SqliteSupport.OpenReadOnly(_index.ShardPath("launch"), "launch shard");
        using var command = launch.CreateCommand();
        command.CommandText = """
                              SELECT path FROM plan_directories WHERE plan_id = $plan ORDER BY ordinal ASC;
                              """;
        command.Parameters.AddWithValue("$plan", planId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static List<string> LoadExtractExcludes(SqliteConnection catalog, int planId)
    {
        using var command = catalog.CreateCommand();
        command.CommandText = """
                              SELECT path FROM extract_exclude WHERE plan_id = $plan ORDER BY path ASC;
                              """;
        command.Parameters.AddWithValue("$plan", planId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var value = reader.GetString(0).Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') ||
                value.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw PkgException.Integrity("The plan contains an unsafe native extraction exclusion.");
            }

            result.Add(value);
        }

        return result;
    }

    private static (string Id, string Key, string Type) ResolveVersion(SqliteConnection catalog, string token)
    {
        using var command = catalog.CreateCommand();
        command.CommandText = """
                              SELECT DISTINCT id, key, type FROM versions
                              WHERE key = $token OR id = $token;
                              """;
        command.Parameters.AddWithValue("$token", token);
        using var reader = command.ExecuteReader();
        var matches = new List<(string Id, string Key, string Type)>();
        while (reader.Read())
        {
            matches.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return matches.Count switch
        {
            0 => throw PkgException.NotFound(ErrorCodes.PackageNotFound,
                $"Version '{token}' does not exist in the active index."),
            1 => matches[0],
            _ => throw PkgException.Conflict($"Version token '{token}' is ambiguous; use the version key shown by list."),
        };
    }

    private static Dictionary<string, (int Count, long Bytes)> LoadAssetSummaries(SqliteConnection assets)
    {
        using var command = assets.CreateCommand();
        command.CommandText = """
                              SELECT d.index_id, d.total_bytes, COUNT(o.sha1)
                              FROM asset_docs d
                              LEFT JOIN asset_objects o ON o.doc_id = d.index_id
                              GROUP BY d.index_id, d.total_bytes;
                              """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = (checked((int)reader.GetInt64(2)), reader.GetInt64(1));
        }

        return result;
    }

    private Uri ParseDownloadUri(string value, string path)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw PkgException.Integrity($"Invalid download URL for '{path}'.");
        }

        return uri;
    }

    private static string NormalizeField(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static bool MatchesSearch(PackageRow row, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var haystack = string.Join('\n',
            row.PackageId,
            row.VersionKey,
            row.VersionId,
            row.VersionType,
            row.Kind.ToToken(),
            row.Platform,
            row.ReleaseTime,
            row.AssetIndex ?? string.Empty);
        foreach (var token in search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureNoPathConflicts(List<DownloadItem> downloads, List<AssetLinkOperation> links)
    {
        var downloadPaths = new HashSet<string>(downloads.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (!targets.Add(link.ToPath))
            {
                throw PkgException.Integrity($"Duplicate asset link target '{link.ToPath}'.");
            }

            if (downloadPaths.Contains(link.ToPath))
            {
                throw PkgException.Integrity($"Asset link target collides with a downloaded file: '{link.ToPath}'.");
            }
        }

        if (downloads.Count != downloads.Select(item => item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw PkgException.Integrity("The plan contains case-insensitive duplicate paths.");
        }
    }
}
