using System.Security.Cryptography;
using System.Text;

namespace PkgForge.Tests;

internal static class IndexTests
{
    [PkgTest("Integration")]
    public static async Task IndexUpdateAndQueriesListBothKinds(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        using var downloader = new HttpDownloader(NullLog.Instance);
        var store = new IndexStore(sandbox.Paths, NullLog.Instance);
        var updater = new IndexUpdater(sandbox.Paths, store, downloader, NullLog.Instance);
        var result = await updater.UpdateAsync(Resolve(fixture), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(fixture.BundleId, result.Index.BundleId, "active bundle id");
        AssertEx.Equal(6, result.Index.Manifest.Counts.Versions, "version count");
        AssertEx.Equal(42, result.Index.Manifest.Counts.ClientPlans, "client plan count");
        AssertEx.Equal(6, result.Index.Manifest.Counts.ServerPlans, "server plan count");
        AssertEx.True(Directory.Exists(result.Index.BundleDirectory), "bundle directory exists");
        AssertEx.True(File.Exists(sandbox.Paths.StatePath), "state file exists");
        foreach (var shard in result.Index.Manifest.Shards)
        {
            AssertEx.True(File.Exists(Path.Combine(result.Index.BundleDirectory, shard.Id + ".idx")),
                $"shard {shard.Id} exists");
        }

        var database = new IndexDatabase(result.Index);
        var all = await database.ListPackagesAsync(null, null, null, 0, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(48, all.TotalMatched, "all package plans");
        AssertEx.Equal(42, all.Rows.Count(row => row.Kind == PackageKind.Client), "client rows");
        AssertEx.Equal(6, all.Rows.Count(row => row.Kind == PackageKind.Server), "server rows");
        AssertEx.True(all.Rows.All(row => row.TotalBytes > 0), "all sizes are positive");
        AssertEx.True(all.Rows.All(row => row.ResourceCount > 0), "all resource counts are positive");

        var serverSearch = await database.ListPackagesAsync(null, null, "1.20 server", 0, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(1, serverSearch.TotalMatched, "server search count");
        AssertEx.Equal(PackageKind.Server, serverSearch.Rows[0].Kind, "server search kind");
        AssertEx.Equal("1.20.1@server", serverSearch.Rows[0].PackageId, "server package id");

        var clientSearch = await database.ListPackagesAsync(PackageKind.Client, "linux-arm64", "26.3", 0,
            CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(1, clientSearch.TotalMatched, "client search count");
        AssertEx.Equal("26.3@client:linux-arm64", clientSearch.Rows[0].PackageId, "client package id");

        var limited = await database.ListPackagesAsync(null, null, null, 5, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(5, limited.Rows.Count, "limit rows");
        AssertEx.Equal(48, limited.TotalMatched, "limit total");
    }

    [PkgTest("Integration")]
    public static async Task SameShardSetRemainsLoadableAfterManifestRefresh(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        using var downloader = new HttpDownloader(NullLog.Instance);
        var store = new IndexStore(sandbox.Paths, NullLog.Instance);
        var updater = new IndexUpdater(sandbox.Paths, store, downloader, NullLog.Instance);
        var first = await updater.UpdateAsync(Resolve(fixture), CancellationToken.None).ConfigureAwait(false);

        var refreshed = CloneManifest(fixture.Manifest);
        refreshed.GeneratedAt = "2026-10-26T00:00:00Z";
        await File.WriteAllTextAsync(Path.Combine(fixture.WebRoot, "index.json"),
            JsonCodec.SerializeIndexManifest(refreshed) + "\n").ConfigureAwait(false);
        var second = await updater.UpdateAsync(Resolve(fixture), CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(first.Index.BundleId, second.Index.BundleId, "same shard bundle reused");
        AssertEx.False(second.Changed, "same shard manifest refresh is a no-op");
        var loaded = await store.LoadRequiredAsync(verifyHashes: true, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(first.Index.BundleId, loaded.BundleId, "verified state remains loadable");
    }

    [PkgTest("Integration")]
    public static async Task CorruptIndexShardKeepsPreviousIndex(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        using var downloader = new HttpDownloader(NullLog.Instance);
        var store = new IndexStore(sandbox.Paths, NullLog.Instance);
        var updater = new IndexUpdater(sandbox.Paths, store, downloader, NullLog.Instance);
        var first = await updater.UpdateAsync(Resolve(fixture), CancellationToken.None).ConfigureAwait(false);
        var originalBundle = first.Index.BundleId;

        var corruptBytes = Encoding.UTF8.GetBytes("this is not a sqlite database");
        var corruptDirectory = Path.Combine(fixture.WebRoot, "data", "corrupted");
        Directory.CreateDirectory(corruptDirectory);
        await File.WriteAllBytesAsync(Path.Combine(corruptDirectory, "catalog.idx"), corruptBytes)
            .ConfigureAwait(false);
        var corruptHash = Convert.ToHexStringLower(SHA256.HashData(corruptBytes));
        var modified = CloneManifest(fixture.Manifest);
        var catalog = modified.Shards.Single(shard => shard.Id == "catalog");
        catalog.Path = "data/corrupted/catalog.idx";
        catalog.Urls = [catalog.Path];
        catalog.Sha256 = corruptHash;
        catalog.Size = corruptBytes.Length;
        await File.WriteAllTextAsync(Path.Combine(fixture.WebRoot, "index.json"),
            JsonCodec.SerializeIndexManifest(modified) + "\n").ConfigureAwait(false);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => updater.UpdateAsync(Resolve(fixture), CancellationToken.None),
            "corrupt SQLite shard must fail").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.Verification, error.ExitCode, "corrupt shard exit code");

        var cached = await store.LoadRequiredAsync(verifyHashes: true, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(originalBundle, cached.BundleId, "previous index remains active");
        var database = new IndexDatabase(cached);
        var all = await database.ListPackagesAsync(null, null, null, 0, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(48, all.TotalMatched, "previous index remains queryable");
    }

    [PkgTest("Integration")]
    public static async Task EmptyVersionTypeNormalizesToUnknown(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox,
            includeUnknownTypeVersion: true).ConfigureAwait(false);
        using var downloader = new HttpDownloader(NullLog.Instance);
        var store = new IndexStore(sandbox.Paths, NullLog.Instance);
        var updater = new IndexUpdater(sandbox.Paths, store, downloader, NullLog.Instance);
        var update = await updater.UpdateAsync(Resolve(fixture), CancellationToken.None).ConfigureAwait(false);
        var database = new IndexDatabase(update.Index);

        var unknown = await database.ListPackagesAsync(null, null, "unknown", null, 0, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.True(unknown.TotalMatched > 0, "unknown type includes the legacy version plans");
        AssertEx.True(unknown.Rows.All(row => row.VersionType == "unknown"),
            "unknown type is normalized before projection");
        AssertEx.True(unknown.Rows.All(row => row.VersionKey == "1.0-unknown-type"),
            "unknown type belongs to the legacy fixture version");

        var search = await database.ListPackagesAsync(null, null, "unknown", "1.0-unknown-type", 0,
            CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(search.TotalMatched > 0, "search sees normalized type");
    }

    [PkgTest("Integration")]
    public static async Task MissingManifestIsReportedClearly(TestSandbox sandbox)
    {
        var store = new IndexStore(sandbox.Paths, NullLog.Instance);
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => store.LoadRequiredAsync(verifyHashes: false, CancellationToken.None),
            "missing index must fail").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.IndexMissing, error.Code, "missing index code");
        AssertEx.Contains("update", error.Message, "missing index hint");
    }

    private static IndexSource Resolve(TestIndexFixture fixture)
    {
        return IndexSource.Parse(fixture.SourceUrl, "test source");
    }

    private static IndexManifest CloneManifest(IndexManifest source) =>
        JsonCodec.ParseIndexManifest(Encoding.UTF8.GetBytes(JsonCodec.SerializeIndexManifest(source)));
}
