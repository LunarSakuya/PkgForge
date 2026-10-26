namespace PkgForge.Tests;

internal sealed class InstallTestHarness : IAsyncDisposable
{
    private readonly HttpDownloader _downloader;
    private readonly ILog _log = NullLog.Instance;

    private InstallTestHarness(TestSandbox sandbox, TestIndexFixture fixture, LoadedIndex index,
        HttpDownloader downloader)
    {
        Sandbox = sandbox;
        Fixture = fixture;
        Index = index;
        _downloader = downloader;
        Cache = new ContentCache(sandbox.Paths, downloader, _log);
        Manifests = new InstallManifestStore(sandbox.Paths, _log);
        Database = new IndexDatabase(index,
            uri => new Uri(fixture.SourceUrl + "files/" + Path.GetFileName(uri.AbsolutePath)));
    }

    public TestSandbox Sandbox { get; }

    public TestIndexFixture Fixture { get; }

    public LoadedIndex Index { get; }

    public ContentCache Cache { get; }

    public InstallManifestStore Manifests { get; }

    public IndexDatabase Database { get; }

    public static async Task<InstallTestHarness> CreateAsync(TestSandbox sandbox, bool unsafePath = false)
    {
        var fixture = await TestIndexFixture.CreateAsync(sandbox, unsafePath).ConfigureAwait(false);
        var downloader = new HttpDownloader(NullLog.Instance);
        try
        {
            var store = new IndexStore(sandbox.Paths, NullLog.Instance);
            var updater = new IndexUpdater(sandbox.Paths, store, downloader, NullLog.Instance);
            var source = IndexSource.Parse(fixture.SourceUrl, "test source");
            var update = await updater.UpdateAsync(source, CancellationToken.None).ConfigureAwait(false);
            return new InstallTestHarness(sandbox, fixture, update.Index, downloader);
        }
        catch
        {
            downloader.Dispose();
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<InstallResult> InstallAsync(string package, string? directory = null, bool force = false,
        int threads = 4, IConfirmation? confirmation = null, bool unmanaged = false)
    {
        var selector = PackageSelector.Parse(package, "windows-x64");
        var service = new InstallService(Sandbox.Paths, Index, Index.Source, Database, Cache, Manifests,
            confirmation ?? new AlwaysConfirm(), _log);
        return await service.InstallAsync(new InstallRequest(selector, directory, true, force, threads,
            unmanaged), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<RemoveResult> RemoveAsync(string identifier, bool force = false,
        IConfirmation? confirmation = null)
    {
        var service = new RemovalService(Sandbox.Paths, Manifests, confirmation ?? new AlwaysConfirm(), _log);
        return await service.RemoveAsync(new RemoveRequest(identifier, true, force), CancellationToken.None)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _downloader.Dispose();
        return Fixture.DisposeAsync();
    }
}

internal static class InstallAssertions
{
    public static async Task VerifyClientLayoutAsync(InstallTestHarness harness, string versionId,
        string platform, string target)
    {
        var version = harness.Fixture.Version(versionId);
        AssertEx.FileExists(Path.Combine(target, version.ClientJarPath), "client jar installed");
        await AssertEx.AssertSha1Async(Path.Combine(target, version.ClientJarPath), version.ClientJarSha1,
            "client jar hash").ConfigureAwait(false);
        AssertEx.FileExists(Path.Combine(target, version.AssetIndexPath), "asset index installed");
        AssertEx.FileExists(Path.Combine(target, "libraries", "org", "example", versionId,
            $"natives-{versionId}.jar"), "native jar installed");
        foreach (var skeleton in new[]
                 {
                     "mods", "resourcepacks", "datapacks", "saves", "config", "logs", "crash-reports",
                     "screenshots", "natives",
                 })
        {
            AssertEx.True(Directory.Exists(Path.Combine(target, skeleton)), $"skeleton {skeleton} exists");
        }

        AssertEx.FileExists(Path.Combine(target, "natives", "native.dat"), "native.dat extracted");
        AssertEx.FileExists(Path.Combine(target, "natives", "sub", "extra.dll"), "extra native extracted");
        AssertEx.FileDoesNotExist(Path.Combine(target, "natives", "META-INF", "MANIFEST.MF"),
            "native exclusion applied");

        if (version.LinkTarget is not null)
        {
            AssertEx.FileExists(Path.Combine(target, version.LinkTarget.Replace('/', Path.DirectorySeparatorChar)),
                "asset link materialized");
        }

        var markerPath = Path.Combine(target, InstallManifestStore.MarkerFileName);
        AssertEx.FileExists(markerPath, "marker exists");
        var marker = JsonCodec.DeserializeInstallMarker(await File.ReadAllTextAsync(markerPath)
            .ConfigureAwait(false));
        AssertEx.True(marker is not null, "marker parses");
        AssertEx.Equal(versionId, marker!.VersionKey, "marker version");

        var manifest = await harness.Manifests.LoadByIdRequiredAsync(
            version.Client(platform).InstallId, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(target, harness.Sandbox.Paths.ResolveManagedInstall(manifest.InstallDirectory,
            "test manifest target"), "manifest target");
        AssertEx.True(manifest.FileCount > 0, "manifest file count");
        AssertEx.True(manifest.TotalBytes > 0, "manifest total bytes");
        foreach (var file in manifest.Files)
        {
            var path = PathSafety.CombineUnderRoot(target, file.Path, "installed manifest file");
            AssertEx.FileExists(path, $"manifest file {file.Path} exists");
            AssertEx.Equal(file.Size, new FileInfo(path).Length, $"manifest size {file.Path}");
        }

        var stagingRoot = Path.Combine(Path.GetDirectoryName(target)!, ".mcpkg-staging");
        if (Directory.Exists(stagingRoot))
        {
            AssertEx.Equal(0, Directory.EnumerateFileSystemEntries(stagingRoot).Count(),
                "staging directory cleaned");
        }
    }
}
