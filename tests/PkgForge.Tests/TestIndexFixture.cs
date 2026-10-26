using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PkgForge.Tests;

internal sealed class TestPlanData
{
    public required int PlanId { get; init; }

    public required string VersionId { get; init; }

    public required string VersionKey { get; init; }

    public required PackageKind Kind { get; init; }

    public required string Platform { get; init; }

    public required long TotalBytes { get; init; }

    public string PackageId => PackageNaming.CanonicalPackageId(VersionKey, Kind,
        Kind == PackageKind.Server ? null : Platform);

    public string InstallId => PackageNaming.InstallId(VersionKey, Kind,
        Kind == PackageKind.Server ? null : Platform);
}

internal sealed class TestVersionData
{
    public required string Id { get; init; }

    public required string Key { get; init; }

    public required string Type { get; init; }

    public required string ReleaseTime { get; init; }

    public required string AssetId { get; init; }

    public required string AssetMode { get; init; }

    public required string ClientJarPath { get; init; }

    public required string ClientJarSha1 { get; init; }

    public required string NativeJarSha1 { get; init; }

    public required string ServerJarSha1 { get; init; }

    public required string AssetIndexPath { get; init; }

    public required string AssetIndexSha1 { get; init; }

    public required string? LinkTarget { get; init; }

    public required long TotalBytes { get; init; }

    public required Dictionary<string, TestPlanData> ClientPlans { get; init; }

    public required TestPlanData ServerPlan { get; init; }

    public TestPlanData Client(string platform) =>
        ClientPlans.TryGetValue(platform, out var plan)
            ? plan
            : throw new InvalidOperationException($"No fixture client plan for {platform}.");
}

internal sealed class TestIndexFixture : IAsyncDisposable
{
    private static readonly string[] PlatformTokens =
        ["windows-x64", "windows-x86", "windows-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

    private readonly MockHttpServer _server;

    private TestIndexFixture(MockHttpServer server, IndexManifest manifest, string bundleId, string webRoot,
        Dictionary<string, TestVersionData> versions)
    {
        _server = server;
        Manifest = manifest;
        BundleId = bundleId;
        WebRoot = webRoot;
        Versions = versions;
    }

    public IndexManifest Manifest { get; }

    public string BundleId { get; }

    public string WebRoot { get; }

    public MockHttpServer Server => _server;

    public Uri SourceUri => _server.BaseUri;

    public string SourceUrl => _server.BaseUrl;

    public IReadOnlyList<string> Platforms => PlatformTokens;

    public IReadOnlyDictionary<string, TestVersionData> Versions { get; }

    public TestVersionData Version(string id) => Versions[id];

    public static IReadOnlyList<(string Id, string Type, string ReleaseTime, string Mode, string AssetId)> VersionSpecs =>
    [
        ("26.4-snapshot-5", "snapshot", "2026-10-20T10:00:00Z", "objects", "31"),
        ("26.3", "release", "2026-09-01T10:00:00Z", "objects", "30"),
        ("1.20.1", "release", "2023-06-12T10:00:00Z", "objects", "5"),
        ("1.16.5", "release", "2021-01-14T10:00:00Z", "objects", "1.16"),
        ("1.12.2", "release", "2017-09-18T10:00:00Z", "virtual", "1.12"),
        ("1.7.10", "release", "2014-05-14T10:00:00Z", "resources", "1.7.10"),
    ];

    public static async Task<TestIndexFixture> CreateAsync(TestSandbox sandbox, bool unsafePath = false,
        bool includeSpaceVersion = false, bool includeUnknownTypeVersion = false)
    {
        Directory.CreateDirectory(sandbox.WebRoot);
        var server = new MockHttpServer(sandbox.WebRoot);
        try
        {
            var staging = Path.Combine(sandbox.FixtureRoot, "shards-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var content = new ContentWriter(sandbox.WebRoot, server.BaseUrl);
            var versions = new Dictionary<string, TestVersionData>(StringComparer.Ordinal);
            var plans = new List<(TestPlanData Plan, List<FixtureFile> Files)>();
            var assets = new List<FixtureAssetDocument>();
            var versionSpecs = VersionSpecs.ToList();
            if (includeSpaceVersion)
            {
                versionSpecs.Insert(0, ("26.3 Preview 1", "release", "2026-09-15T10:00:00Z",
                    "objects", "30b"));
            }

            if (includeUnknownTypeVersion)
            {
                versionSpecs.Insert(0, ("1.0-unknown-type", "", "2020-01-01T00:00:00Z", "objects", "5u"));
            }

            foreach (var spec in versionSpecs)
            {
                var version = BuildVersionData(content, spec, plans, assets, sandbox, unsafePath);
                versions.Add(version.Id, version);
            }

            var catalogPath = Path.Combine(staging, "catalog.idx");
            var launchPath = Path.Combine(staging, "launch.idx");
            var assetsPath = Path.Combine(staging, "assets.idx");
            var counts = new IndexCounts
            {
                Versions = versions.Count,
                ClientPlans = plans.Count(item => item.Plan.Kind == PackageKind.Client),
                ServerPlans = plans.Count(item => item.Plan.Kind == PackageKind.Server),
                AssetDocs = assets.Count,
            };
            BuildCatalog(catalogPath, versions, plans, assets, counts);
            BuildLaunch(launchPath, plans, counts);
            BuildAssets(assetsPath, assets, counts);

            var shardSpecs = new[] { ("catalog", catalogPath), ("launch", launchPath), ("assets", assetsPath) };
            var shards = new List<IndexShard>();
            var hashPairs = new List<(string Id, string Sha256)>();
            foreach (var (id, path) in shardSpecs)
            {
                var bytesHash = SHA256.HashData(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
                var sha = Convert.ToHexStringLower(bytesHash);
                hashPairs.Add((id, sha));
                shards.Add(new IndexShard
                {
                    Id = id,
                    Path = string.Empty,
                    Sha256 = sha,
                    Size = new FileInfo(path).Length,
                    UserVersion = 1,
                    Urls = [],
                });
            }

            var bundleId = Hashing.BundleId(hashPairs);
            var bundleDirectory = Path.Combine(sandbox.WebRoot, "data", bundleId);
            Directory.CreateDirectory(bundleDirectory);
            foreach (var shard in shards)
            {
                var sourcePath = Path.Combine(staging, shard.Id + ".idx");
                var destinationPath = Path.Combine(bundleDirectory, shard.Id + ".idx");
                File.Copy(sourcePath, destinationPath, overwrite: true);
                shard.Path = $"data/{bundleId}/{shard.Id}.idx";
                shard.Urls = [shard.Path];
            }

            var latestRelease = versionSpecs.First(value => value.Type == "release").Id;
            var latestSnapshot = versionSpecs.First(value => value.Type == "snapshot").Id;
            var manifest = new IndexManifest
            {
                Schema = 2,
                Format = "sqlite-shards",
                GeneratedAt = "2026-10-25T00:00:00Z",
                LatestRelease = latestRelease,
                LatestSnapshot = latestSnapshot,
                Shards = shards,
                Counts = counts,
                SourceManifestSha256 = new string('a', 64),
            };
            await File.WriteAllTextAsync(Path.Combine(sandbox.WebRoot, "index.json"),
                JsonCodec.SerializeIndexManifest(manifest) + "\n").ConfigureAwait(false);
            return new TestIndexFixture(server, manifest, bundleId, sandbox.WebRoot, versions);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public string ContentRelativePath(string sha1) => "files/" + sha1;

    public void CorruptShardOnServer(string shardId)
    {
        var shard = Manifest.Shards.Single(value => value.Id == shardId);
        var path = Path.Combine(WebRoot, shard.Path.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not a sqlite database"));
    }

    public string ShardFile(string shardId)
    {
        var shard = Manifest.Shards.Single(value => value.Id == shardId);
        return Path.Combine(WebRoot, shard.Path.Replace('/', Path.DirectorySeparatorChar));
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync().ConfigureAwait(false);

    private static TestVersionData BuildVersionData(ContentWriter content,
        (string Id, string Type, string ReleaseTime, string Mode, string AssetId) spec,
        List<(TestPlanData Plan, List<FixtureFile> Files)> plans, List<FixtureAssetDocument> assets,
        TestSandbox sandbox, bool unsafePath)
    {
        var id = spec.Id;
        var key = id.Replace(' ', '_');
        var assetObjects = new List<FixtureAssetObject>
        {
            new("sounds/note.ogg", content.Write(Encoding.UTF8.GetBytes("audio-" + id))),
            new("lang/en_us.lang", content.Write(Encoding.UTF8.GetBytes("lang-" + id))),
        };
        var assetTotal = assetObjects.Sum(value => value.Content.Size);
        var linkRoot = spec.Mode switch
        {
            "virtual" => $"assets/virtual/{spec.AssetId}",
            "resources" => "resources",
            _ => null,
        };
        var links = linkRoot is null
            ? []
            : assetObjects.Select(value => (From: $"assets/objects/{value.Content.Sha1[..2]}/{value.Content.Sha1}",
                To: $"{linkRoot}/{value.Name}")).ToList();
        var indexJson = JsonSerializer.Serialize(new
        {
            objects = assetObjects.ToDictionary(value => value.Name,
                value => new { hash = value.Content.Sha1, size = value.Content.Size }),
            @virtual = spec.Mode == "virtual",
            map_to_resources = spec.Mode == "resources",
        });
        var indexBytes = Encoding.UTF8.GetBytes(indexJson);
        var indexContent = content.Write(indexBytes);
        var assetIndexPath = $"assets/indexes/{spec.AssetId}.json";
        var clientJar = content.Write(Encoding.UTF8.GetBytes("client-jar-" + id));
        var logConfig = content.Write(Encoding.UTF8.GetBytes("log-config-" + id));
        var library = content.Write(Encoding.UTF8.GetBytes("library-" + id));
        var nativeZip = content.Write(BuildNativeZip(id));
        var serverJar = content.Write(Encoding.UTF8.GetBytes("server-jar-" + id));
        var clientPath = $"versions/{id}/{id}.jar";
        var libraryPath = $"libraries/org/example/{id}/example-{id}.jar";
        var nativePath = $"libraries/org/example/{id}/natives-{id}.jar";
        var logPath = $"assets/log_configs/client-{id}.xml";
        var commonFiles = new List<FixtureFile>
        {
            new(clientPath, "client", clientJar),
            new(logPath, "log", logConfig),
            new(assetIndexPath, "assetIndex", indexContent),
            new(libraryPath, "library", library),
            new(nativePath, "native", nativeZip),
        };
        var serverFiles = new List<FixtureFile> { new($"versions/{id}/server.jar", "server", serverJar) };
        if (unsafePath && id == "1.20.1")
        {
            var escape = content.Write(Encoding.UTF8.GetBytes("escape-payload"));
            commonFiles.Add(new FixtureFile("../escape.txt", "client", escape));
        }

        var totalBytes = commonFiles.Sum(value => value.Content.Size) + assetTotal;
        var clientPlans = new Dictionary<string, TestPlanData>(StringComparer.Ordinal);
        foreach (var platform in PlatformTokens)
        {
            var planId = plans.Count + 1;
            var plan = new TestPlanData
            {
                PlanId = planId,
                VersionId = id,
                VersionKey = key,
                Kind = PackageKind.Client,
                Platform = platform,
                TotalBytes = totalBytes,
            };
            plans.Add((plan, commonFiles));
            clientPlans.Add(platform, plan);
        }

        var serverPlan = new TestPlanData
        {
            PlanId = plans.Count + 1,
            VersionId = id,
            VersionKey = key,
            Kind = PackageKind.Server,
            Platform = string.Empty,
            TotalBytes = serverFiles.Sum(value => value.Content.Size),
        };
        plans.Add((serverPlan, serverFiles));
        assets.Add(new FixtureAssetDocument(spec.AssetId, spec.Mode,
            spec.Mode == "resources" ? "resources" : spec.Mode == "virtual" ? $"assets/virtual/{spec.AssetId}" : "assets",
            indexContent, assetTotal, assetObjects, links));

        return new TestVersionData
        {
            Id = id,
            Key = key,
            Type = spec.Type,
            ReleaseTime = spec.ReleaseTime,
            AssetId = spec.AssetId,
            AssetMode = spec.Mode,
            ClientJarPath = clientPath,
            ClientJarSha1 = clientJar.Sha1,
            NativeJarSha1 = nativeZip.Sha1,
            ServerJarSha1 = serverJar.Sha1,
            AssetIndexPath = assetIndexPath,
            AssetIndexSha1 = indexContent.Sha1,
            LinkTarget = links.Count > 0 ? links[0].To : null,
            TotalBytes = totalBytes,
            ClientPlans = clientPlans,
            ServerPlan = serverPlan,
        };
    }

    private static byte[] BuildNativeZip(string id)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "native.dat", "native-" + id);
            WriteEntry(archive, "sub/extra.dll", "extra-" + id);
            WriteEntry(archive, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0");
        }

        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void BuildCatalog(string path, IReadOnlyDictionary<string, TestVersionData> versions,
        IReadOnlyList<(TestPlanData Plan, List<FixtureFile> Files)> plans,
        IReadOnlyList<FixtureAssetDocument> assets, IndexCounts counts)
    {
        using var connection = CreateDatabase(path, "catalog");
        using var transaction = connection.BeginTransaction();
        var insertVersion = Command(connection, transaction,
            """
            INSERT INTO versions (id,key,type,release_time,java_major,java_defaulted,assets_index,
                server_available,revision,source_manifest_sha1,schema_version)
            VALUES ($id,$key,$type,$release,$java,0,$assets,1,$revision,$manifest,2);
            """);
        foreach (var version in versions.Values)
        {
            Bind(insertVersion, ("$id", version.Id), ("$key", version.Key), ("$type", version.Type),
                ("$release", version.ReleaseTime),
                ("$java", 17), ("$assets", version.AssetId), ("$revision", new string('b', 64)),
                ("$manifest", new string('c', 40)));
            insertVersion.ExecuteNonQuery();
        }

        var contents = new Dictionary<string, int>(StringComparer.Ordinal);
        var contentId = 0;
        var insertContent = Command(connection, transaction,
            "INSERT INTO contents (id,sha1,size) VALUES ($id,$sha1,$size);");
        var insertUrl = Command(connection, transaction,
            "INSERT INTO content_urls (content_id,ordinal,url) VALUES ($id,0,$url);");
        var insertPlan = Command(connection, transaction,
            """
            INSERT INTO plans (id,version_id,kind,platform,revision,total_bytes,main_class,
                assets_index,assets_mode,assets_root,assets_document,assets_sha1,assets_size,
                logging_argument,logging_path,native_dir)
            VALUES ($id,$version,$kind,$platform,$revision,$total,$main,$assets,$mode,$root,$document,
                $assetSha,$assetSize,$logArg,$logPath,$native);
            """);
        var insertFile = Command(connection, transaction,
            "INSERT INTO files (plan_id,role,path,content_id) VALUES ($plan,$role,$path,$content);");
        var insertNative = Command(connection, transaction,
            "INSERT INTO natives (plan_id,dir) VALUES ($plan,$dir);");
        var insertExclude = Command(connection, transaction,
            "INSERT OR IGNORE INTO extract_exclude (plan_id,path) VALUES ($plan,$path);");

        foreach (var (plan, files) in plans)
        {
            var version = versions[plan.VersionId];
            var asset = assets.Single(value => value.Id == version.AssetId);
            Bind(insertPlan, ("$id", plan.PlanId), ("$version", plan.VersionId),
                ("$kind", plan.Kind.ToToken()), ("$platform", plan.Platform),
                ("$revision", new string('d', 64)), ("$total", plan.TotalBytes), ("$main", "net.minecraft.Main"),
                ("$assets", plan.Kind == PackageKind.Client ? version.AssetId : DBNull.Value),
                ("$mode", plan.Kind == PackageKind.Client ? version.AssetMode : DBNull.Value),
                ("$root", plan.Kind == PackageKind.Client ? asset.Root : DBNull.Value),
                ("$document", plan.Kind == PackageKind.Client ? $"v1/assets/{version.AssetId}.json" : DBNull.Value),
                ("$assetSha", plan.Kind == PackageKind.Client ? version.AssetIndexSha1 : DBNull.Value),
                ("$assetSize", plan.Kind == PackageKind.Client ? asset.TotalBytes : DBNull.Value),
                ("$logArg", plan.Kind == PackageKind.Client ? "-Dlog4j.configurationFile=${path}" : DBNull.Value),
                ("$logPath", plan.Kind == PackageKind.Client ? $"assets/log_configs/client-{version.Id}.xml" : DBNull.Value),
                ("$native", plan.Kind == PackageKind.Client ? "natives" : DBNull.Value));
            insertPlan.ExecuteNonQuery();

            if (plan.Kind == PackageKind.Client)
            {
                Bind(insertNative, ("$plan", plan.PlanId), ("$dir", "natives"));
                insertNative.ExecuteNonQuery();
                Bind(insertExclude, ("$plan", plan.PlanId), ("$path", "META-INF"));
                insertExclude.ExecuteNonQuery();
            }

            foreach (var file in files)
            {
                Bind(insertFile, ("$plan", plan.PlanId), ("$role", file.Role), ("$path", file.Path),
                    ("$content", EnsureContent(connection, transaction, contents, ref contentId, file.Content,
                        insertContent, insertUrl)));
                insertFile.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        WriteMeta(connection, "catalog", counts);
        connection.Close();
    }

    private static void BuildLaunch(string path,
        IReadOnlyList<(TestPlanData Plan, List<FixtureFile> Files)> plans, IndexCounts counts)
    {
        using var connection = CreateDatabase(path, "launch");
        using var transaction = connection.BeginTransaction();
        var insertClasspath = Command(connection, transaction,
            "INSERT INTO plan_classpath (plan_id,ordinal,path) VALUES ($plan,$ordinal,$path);");
        var insertDirectory = Command(connection, transaction,
            "INSERT INTO plan_directories (plan_id,ordinal,path) VALUES ($plan,$ordinal,$path);");
        var insertArg = Command(connection, transaction,
            "INSERT INTO launch_args (plan_id,phase,ordinal,value) VALUES ($plan,'game',$ordinal,$value);");
        foreach (var (plan, files) in plans)
        {
            if (plan.Kind == PackageKind.Server)
            {
                continue;
            }

            var ordinal = 0;
            foreach (var file in files.Where(value => value.Role == "library"))
            {
                Bind(insertClasspath, ("$plan", plan.PlanId), ("$ordinal", ordinal++), ("$path", file.Path));
                insertClasspath.ExecuteNonQuery();
            }

            Bind(insertDirectory, ("$plan", plan.PlanId), ("$ordinal", 0), ("$path", "natives"));
            insertDirectory.ExecuteNonQuery();
            Bind(insertArg, ("$plan", plan.PlanId), ("$ordinal", 0), ("$value", "--demo"));
            insertArg.ExecuteNonQuery();
        }

        transaction.Commit();
        WriteMeta(connection, "launch", counts);
        connection.Close();
    }

    private static void BuildAssets(string path, IReadOnlyList<FixtureAssetDocument> assets, IndexCounts counts)
    {
        using var connection = CreateDatabase(path, "assets");
        using var transaction = connection.BeginTransaction();
        var insertDoc = Command(connection, transaction,
            """
            INSERT INTO asset_docs (index_id,mode,root,sha1,total_bytes,revision,schema_version)
            VALUES ($id,$mode,$root,$sha,$total,$revision,2);
            """);
        var insertObject = Command(connection, transaction,
            "INSERT INTO asset_objects (doc_id,sha1,size) VALUES ($doc,$sha,$size);");
        var insertLink = Command(connection, transaction,
            "INSERT INTO asset_links (doc_id,ordinal,from_path,to_path) VALUES ($doc,$ordinal,$from,$to);");
        foreach (var asset in assets)
        {
            Bind(insertDoc, ("$id", asset.Id), ("$mode", asset.Mode), ("$root", asset.Root),
                ("$sha", asset.Index.Sha1), ("$total", asset.TotalBytes), ("$revision", new string('e', 64)));
            insertDoc.ExecuteNonQuery();
            foreach (var obj in asset.Objects)
            {
                Bind(insertObject, ("$doc", asset.Id), ("$sha", obj.Content.Sha1), ("$size", obj.Content.Size));
                insertObject.ExecuteNonQuery();
            }

            var ordinal = 0;
            foreach (var link in asset.Links)
            {
                Bind(insertLink, ("$doc", asset.Id), ("$ordinal", ordinal++), ("$from", link.From),
                    ("$to", link.To));
                insertLink.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        WriteMeta(connection, "assets", counts);
        connection.Close();
        SqliteConnection.ClearAllPools();
    }

    private static int EnsureContent(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, int> contents, ref int contentId, ContentData data, SqliteCommand insertContent,
        SqliteCommand insertUrl)
    {
        if (contents.TryGetValue(data.Sha1, out var existing))
        {
            return existing;
        }

        contentId++;
        Bind(insertContent, ("$id", contentId), ("$sha1", data.Sha1), ("$size", data.Size));
        insertContent.ExecuteNonQuery();
        Bind(insertUrl, ("$id", contentId), ("$url", data.Url));
        insertUrl.ExecuteNonQuery();
        contents.Add(data.Sha1, contentId);
        return contentId;
    }

    private static SqliteConnection CreateDatabase(string path, string shard)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection,
            "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; " +
            "PRAGMA application_id=1296253511; PRAGMA user_version=1;");
        Execute(connection, ReadSchema(shard));
        return connection;
    }

    private static void WriteMeta(SqliteConnection connection, string shard, IndexCounts counts)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema"] = "2",
            ["format"] = "sqlite",
            ["database_schema"] = "1",
            ["resolver"] = "1",
            ["latest_release"] = "26.3",
            ["latest_snapshot"] = "26.4-snapshot-5",
            ["source_manifest_sha256"] = new string('a', 64),
            ["count_versions"] = counts.Versions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["count_client_plans"] = counts.ClientPlans.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["count_server_plans"] = counts.ServerPlans.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["count_asset_docs"] = counts.AssetDocs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["shard"] = shard,
        };
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta (key,value) VALUES ($key,$value);";
        foreach (var (key, value) in values)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    private static string ReadSchema(string shard)
    {
        var name = "PkgForge.Tests.Schema." + shard + ".sql";
        using var stream = typeof(TestIndexFixture).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded schema '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        command.Parameters.Clear();
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ContentWriter
    {
        private readonly string _root;
        private readonly string _baseUrl;

        public ContentWriter(string root, string baseUrl)
        {
            _root = root;
            _baseUrl = baseUrl;
        }

        public ContentData Write(byte[] bytes)
        {
            var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
            var path = Path.Combine(_root, "files", sha1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, bytes);
            }

            var url = _baseUrl + "files/" + sha1;
            return new ContentData(sha1, bytes.LongLength, url);
        }
    }

    private sealed record ContentData(string Sha1, long Size, string Url);

    private sealed record FixtureFile(string Path, string Role, ContentData Content);

    private sealed record FixtureAssetObject(string Name, ContentData Content);

    private sealed record FixtureAssetDocument(string Id, string Mode, string Root, ContentData Index,
        long TotalBytes, IReadOnlyList<FixtureAssetObject> Objects,
        IReadOnlyList<(string From, string To)> Links);
}
