namespace PkgForge.Tests;

internal static class InstallTests
{
    [PkgTest("Install")]
    public static async Task LatestReleaseAndSnapshotPlansInstall(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var release = await harness.InstallAsync("26.3@client:windows-x64").ConfigureAwait(false);
        await InstallAssertions.VerifyClientLayoutAsync(harness, "26.3", "windows-x64", release.Target)
            .ConfigureAwait(false);
        var snapshot = await harness.InstallAsync("26.4-snapshot-5@server").ConfigureAwait(false);
        AssertEx.FileExists(Path.Combine(snapshot.Target, "versions", "26.4-snapshot-5", "server.jar"),
            "snapshot server jar installed");
        AssertEx.Equal(PackageKind.Server, PackageKinds.Parse(snapshot.Manifest.Kind), "snapshot server kind");
        AssertEx.True(!PathSafety.EqualsPath(release.Target, snapshot.Target), "isolated latest installs");
    }

    [PkgTest("Install")]
    public static async Task LauncherVersionJsonIsGenerated(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("26.3@client:windows-x64").ConfigureAwait(false);
        var jsonPath = Path.Combine(result.Target, "versions", "26.3", "26.3.json");
        AssertEx.FileExists(jsonPath, "launcher version JSON exists");
        AssertEx.True(result.Manifest.Files.Any(file => file.Role == "launcher-json"),
            "launcher JSON is recorded in the manifest");

        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)
            .ConfigureAwait(false));
        var root = document.RootElement;
        AssertEx.Equal("26.3", root.GetProperty("id").GetString(), "launcher version id");
        AssertEx.Equal("release", root.GetProperty("type").GetString(), "launcher version type");
        AssertEx.False(string.IsNullOrWhiteSpace(root.GetProperty("mainClass").GetString()),
            "launcher main class exists");
        AssertEx.True(root.GetProperty("libraries").GetArrayLength() > 0, "launcher libraries exist");
        AssertEx.True(root.GetProperty("arguments").GetProperty("game").GetArrayLength() > 0,
            "launcher game arguments exist");
        AssertEx.True(root.TryGetProperty("assetIndex", out var assetIndex), "asset index exists");
        AssertEx.True(assetIndex.GetProperty("size").GetInt64() > 0, "asset index size exists");
        AssertEx.True(root.GetProperty("downloads").TryGetProperty("client", out _), "client download exists");
    }

    [PkgTest("Install")]
    public static async Task SuccessfulManagedReinstallReplacesFolder(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var first = await harness.InstallAsync("1.16.5@client:windows-x64").ConfigureAwait(false);
        var extra = Path.Combine(first.Target, "PCL", "old-residue.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        await File.WriteAllTextAsync(extra, "old").ConfigureAwait(false);

        var second = await harness.InstallAsync("1.16.5@client:windows-x64", force: true)
            .ConfigureAwait(false);
        AssertEx.True(second.ReplacedExistingDirectory, "reinstall reports replacement");
        AssertEx.False(File.Exists(extra), "old untracked residue removed by atomic replacement");
        await InstallAssertions.VerifyClientLayoutAsync(harness, "1.16.5", "windows-x64", second.Target)
            .ConfigureAwait(false);
        AssertEx.Equal(second.Manifest.InstallId, (await harness.Manifests.LoadByIdRequiredAsync(
            second.Manifest.InstallId, CancellationToken.None).ConfigureAwait(false)).InstallId,
            "reinstall manifest readable");
    }

    [PkgTest("Install")]
    public static async Task LegacyResourcesModeMaterializesLinksAndNatives(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var version = harness.Fixture.Version("1.7.10");
        var plan = version.Client("windows-x64");
        var expectedTarget = Path.Combine(sandbox.Paths.InstallRoot, plan.InstallId);

        var result = await harness.InstallAsync("1.7.10@client:windows-x64").ConfigureAwait(false);
        AssertEx.Equal(expectedTarget, result.Target, "default install target");
        await InstallAssertions.VerifyClientLayoutAsync(harness, "1.7.10", "windows-x64", expectedTarget)
            .ConfigureAwait(false);
        AssertEx.FileExists(Path.Combine(expectedTarget, "resources", "sounds", "note.ogg"),
            "resources mode asset link exists");
        AssertEx.FileExists(Path.Combine(expectedTarget, "resources", "lang", "en_us.lang"),
            "resources mode second link exists");
        AssertEx.False(Directory.Exists(Path.Combine(expectedTarget, "assets", "virtual")),
            "resources mode must not create virtual assets");
        AssertEx.True(result.Manifest.FileCount >= 10, "manifest includes downloads, links, and natives");
    }

    [PkgTest("Install")]
    public static async Task VirtualModeMaterializesVirtualAssets(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var version = harness.Fixture.Version("1.12.2");
        var plan = version.Client("windows-x64");
        var result = await harness.InstallAsync("1.12.2@client:windows-x64").ConfigureAwait(false);
        await InstallAssertions.VerifyClientLayoutAsync(harness, "1.12.2", "windows-x64", result.Target)
            .ConfigureAwait(false);
        AssertEx.FileExists(Path.Combine(result.Target, "assets", "virtual", "1.12", "sounds", "note.ogg"),
            "virtual asset link exists");
        AssertEx.False(Directory.Exists(Path.Combine(result.Target, "resources", "sounds")),
            "virtual mode must not create resources links");
        AssertEx.True(plan.TotalBytes > 0, "plan total bytes");
    }

    [PkgTest("Install")]
    public static async Task CustomDirectoryClientAndNormalRemoval(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var relativeTarget = AppPaths.InstancesRelativeRoot + "/custom-instance";
        var target = Path.Combine(sandbox.Paths.InstallRoot, "custom-instance");
        var result = await harness.InstallAsync("1.16.5@client:windows-x64", relativeTarget).ConfigureAwait(false);
        AssertEx.Equal(target, result.Target, "custom target");
        AssertEx.Equal(AppPaths.InstancesRelativeRoot, result.Manifest.ManagedRoot, "managed root");
        await InstallAssertions.VerifyClientLayoutAsync(harness, "1.16.5", "windows-x64", target)
            .ConfigureAwait(false);

        var remove = await harness.RemoveAsync(result.Manifest.InstallId).ConfigureAwait(false);
        AssertEx.Equal(result.Manifest.InstallId, remove.Manifest.InstallId, "removed id");
        AssertEx.False(Directory.Exists(target), "custom target removed");
        AssertEx.False(File.Exists(sandbox.Paths.InstallManifestPath(result.Manifest.InstallId)),
            "custom manifest removed");
    }

    [PkgTest("Install")]
    public static async Task ServerPlanInstallsAndRemovesIndependently(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var client = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var server = await harness.InstallAsync("1.20.1@server").ConfigureAwait(false);

        AssertEx.True(!PathSafety.EqualsPath(client.Target, server.Target), "client and server targets differ");
        AssertEx.FileExists(Path.Combine(server.Target, "versions", "1.20.1", "server.jar"),
            "server jar installed");
        AssertEx.True(Directory.Exists(Path.Combine(server.Target, "logs")), "server logs skeleton");
        AssertEx.False(Directory.Exists(Path.Combine(server.Target, "libraries")),
            "server plan has no libraries");
        AssertEx.Equal(1, server.Manifest.FileCount, "server manifest one file");

        await harness.RemoveAsync(server.Manifest.InstallId).ConfigureAwait(false);
        AssertEx.False(Directory.Exists(server.Target), "server folder removed");
        AssertEx.True(Directory.Exists(client.Target), "client folder still exists");
        AssertEx.True(File.Exists(sandbox.Paths.InstallManifestPath(client.Manifest.InstallId)),
            "client manifest still exists");
    }

    [PkgTest("Install")]
    public static async Task RemoveRequiresForceForExtraFilesAndDeletesWholeFolder(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.16.5@client:windows-x64").ConfigureAwait(false);
        var extra = Path.Combine(result.Target, "PCL", "leftover.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        await File.WriteAllBytesAsync(extra, [1, 2, 3]).ConfigureAwait(false);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.RemoveAsync(result.Manifest.InstallId),
            "remove with extra files must fail without force").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.Conflict, error.ExitCode, "extra file conflict exit code");
        AssertEx.True(Directory.Exists(result.Target), "folder intact after refused remove");
        AssertEx.FileExists(extra, "extra file intact after refused remove");
        AssertEx.FileExists(sandbox.Paths.InstallManifestPath(result.Manifest.InstallId),
            "manifest intact after refused remove");

        await harness.RemoveAsync(result.Manifest.InstallId, force: true).ConfigureAwait(false);
        AssertEx.False(Directory.Exists(result.Target), "whole folder removed with force");
        AssertEx.FileDoesNotExist(extra, "extra file removed with whole folder");
        AssertEx.False(File.Exists(sandbox.Paths.InstallManifestPath(result.Manifest.InstallId)),
            "manifest removed");
    }

    [PkgTest("Install")]
    public static async Task RemoveMissingManifestFailsClosed(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var manifestPath = sandbox.Paths.InstallManifestPath(result.Manifest.InstallId);
        File.Delete(manifestPath);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.RemoveAsync(result.Manifest.InstallId),
            "missing manifest must fail").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.ManifestMissing, error.Code, "missing manifest code");
        AssertEx.True(Directory.Exists(result.Target), "target untouched without manifest");
        AssertEx.FileExists(Path.Combine(result.Target, "versions", "1.20.1", "1.20.1.jar"),
            "client jar untouched");
    }

    [PkgTest("Install")]
    public static async Task RemoveMarkerMismatchFailsClosed(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var markerPath = Path.Combine(result.Target, InstallManifestStore.MarkerFileName);
        var marker = JsonCodec.DeserializeInstallMarker(await File.ReadAllTextAsync(markerPath)
            .ConfigureAwait(false))!;
        marker.InstallId = "someone-elses-install";
        await File.WriteAllTextAsync(markerPath, JsonCodec.SerializeInstallMarker(marker))
            .ConfigureAwait(false);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.RemoveAsync(result.Manifest.InstallId),
            "marker mismatch must fail").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.Integrity, error.Code, "marker mismatch code");
        AssertEx.True(Directory.Exists(result.Target), "target untouched after marker mismatch");
    }

    [PkgTest("Install")]
    public static async Task RemoveTamperedManifestPathFailsClosed(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var manifestPath = sandbox.Paths.InstallManifestPath(result.Manifest.InstallId);
        var manifest = JsonCodec.DeserializeInstallManifest(await File.ReadAllTextAsync(manifestPath)
            .ConfigureAwait(false))!;
        manifest.InstallDirectory = "../escape";
        await File.WriteAllTextAsync(manifestPath, JsonCodec.SerializeInstallManifest(manifest))
            .ConfigureAwait(false);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.RemoveAsync(result.Manifest.InstallId),
            "tampered manifest must fail").ConfigureAwait(false);
        AssertEx.True(error.Code is ErrorCodes.Integrity or ErrorCodes.UnsafePath or ErrorCodes.ProtectedPath,
            "tampered manifest error code");
        AssertEx.True(Directory.Exists(result.Target), "actual target untouched");
    }

    [PkgTest("Install")]
    public static async Task ExistingUnmanagedDirectoryIsNeverOverwritten(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var target = Path.Combine(Path.GetDirectoryName(sandbox.Root)!,
            "unmanaged-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var userFile = Path.Combine(target, "user-data.txt");
        await File.WriteAllTextAsync(userFile, "must survive").ConfigureAwait(false);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.InstallAsync("1.20.1@client:windows-x64", target, force: true, unmanaged: true),
            "existing unmanaged target must be refused").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.Conflict, error.ExitCode, "unmanaged target exit code");
        AssertEx.FileExists(userFile, "user file survives refusal");
        AssertEx.Equal("must survive", await File.ReadAllTextAsync(userFile).ConfigureAwait(false),
            "user file content survives");
    }

    [PkgTest("Install")]
    public static async Task IndexPathTraversalIsRejectedBeforeWrites(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox, unsafePath: true)
            .ConfigureAwait(false);
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.InstallAsync("1.20.1@client:windows-x64"),
            "malicious index path must fail").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.UnsafePath, error.ExitCode, "unsafe index path exit code");
        var plan = harness.Fixture.Version("1.20.1").Client("windows-x64");
        var target = Path.Combine(sandbox.Paths.InstallRoot, plan.InstallId);
        AssertEx.False(Directory.Exists(target), "no install directory created");
        AssertEx.FileDoesNotExist(Path.Combine(sandbox.InstallRoot, "escape.txt"),
            "no path escape file created");
        AssertEx.FileDoesNotExist(Path.Combine(sandbox.Root, "escape.txt"),
            "no file outside install root created");
    }

    [PkgTest("Install")]
    public static async Task TruncatedDownloadCleansStagingAndManifest(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var version = harness.Fixture.Version("1.20.1");
        var plan = version.Client("windows-x64");
        harness.Fixture.Server.AddFailure(
            harness.Fixture.ContentRelativePath(version.ClientJarSha1),
            MockFailureKind.Truncate, int.MaxValue);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.InstallAsync("1.20.1@client:windows-x64"),
            "truncated download must fail").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.Network, error.ExitCode, "truncated download exit code");
        var target = Path.Combine(sandbox.Paths.InstallRoot, plan.InstallId);
        AssertEx.False(Directory.Exists(target), "no target after download failure");
        AssertEx.False(File.Exists(sandbox.Paths.InstallManifestPath(plan.InstallId)),
            "no manifest after download failure");
        var stagingRoot = Path.Combine(sandbox.Paths.InstallRoot, ".mcpkg-staging");
        AssertEx.True(!Directory.Exists(stagingRoot) ||
                      !Directory.EnumerateFileSystemEntries(stagingRoot).Any(), "staging cleaned");
        AssertEx.False(File.Exists(sandbox.Paths.CacheObjectPath(version.ClientJarSha1)),
            "partial cache object removed");
    }

    [PkgTest("Install")]
    public static async Task DirtyStagingAndCorruptCacheAreRecovered(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var version = harness.Fixture.Version("1.16.5");
        var plan = version.Client("windows-x64");
        var target = Path.Combine(sandbox.Paths.InstallRoot, plan.InstallId);
        var cachePath = sandbox.Paths.CacheObjectPath(version.ClientJarSha1);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, "corrupt-cache").ConfigureAwait(false);

        var stagingRoot = Path.Combine(sandbox.Paths.InstallRoot, ".mcpkg-staging");
        var stale = Path.Combine(stagingRoot, plan.InstallId + "-stale");
        SafeDirectory.CreateControlledDirectory(stale, stagingRoot, "test stale staging");
        await File.WriteAllTextAsync(Path.Combine(stale, "junk.txt"), "junk").ConfigureAwait(false);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(1));

        var result = await harness.InstallAsync("1.16.5@client:windows-x64").ConfigureAwait(false);
        await InstallAssertions.VerifyClientLayoutAsync(harness, "1.16.5", "windows-x64", result.Target)
            .ConfigureAwait(false);
        AssertEx.False(Directory.Exists(stale), "stale staging removed");
        AssertEx.FileExists(cachePath, "cache object repaired");
        AssertEx.Equal(version.ClientJarSha1,
            await Hashing.Sha1FileAsync(cachePath, CancellationToken.None).ConfigureAwait(false),
            "cache object hash after repair");
    }

    [PkgTest("Install")]
    public static async Task FailedReinstallKeepsPreviousInstallAndManifest(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var version = harness.Fixture.Version("1.20.1");
        var first = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var manifestPath = sandbox.Paths.InstallManifestPath(first.Manifest.InstallId);
        var oldManifest = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        var oldJar = Path.Combine(first.Target, "versions", "1.20.1", "1.20.1.jar");
        AssertEx.FileExists(oldJar, "initial jar exists");

        var cachePath = sandbox.Paths.CacheObjectPath(version.ClientJarSha1);
        File.Delete(cachePath);
        harness.Fixture.Server.AddFailure(
            harness.Fixture.ContentRelativePath(version.ClientJarSha1),
            MockFailureKind.Truncate, int.MaxValue);
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.InstallAsync("1.20.1@client:windows-x64", force: true),
            "failed reinstall must fail").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.Network, error.ExitCode, "failed reinstall exit code");
        AssertEx.FileExists(oldJar, "old jar survives failed reinstall");
        var newManifest = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        AssertEx.True(oldManifest.SequenceEqual(newManifest), "old manifest survives failed reinstall");
        AssertEx.FileExists(Path.Combine(first.Target, InstallManifestStore.MarkerFileName),
            "old marker survives failed reinstall");
    }

    [PkgTest("Concurrency")]
    public static async Task ConcurrentCacheRequestsDownloadOnce(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var version = harness.Fixture.Version("1.20.1");
        var plan = await harness.Database.ResolvePlanAsync(
            PackageSelector.Parse("1.20.1@client:windows-x64", "windows-x64"), CancellationToken.None)
            .ConfigureAwait(false);
        var item = plan.Downloads.Single(value => value.Path == version.ClientJarPath);
        var relative = harness.Fixture.ContentRelativePath(item.Sha1);
        var first = harness.Cache.GetOrDownloadAsync(item, CancellationToken.None);
        var second = harness.Cache.GetOrDownloadAsync(item, CancellationToken.None);
        var paths = await Task.WhenAll(first, second).ConfigureAwait(false);
        AssertEx.Equal(paths[0], paths[1], "both requests resolve the same cache object");
        AssertEx.Equal(1, harness.Fixture.Server.Count(relative), "one network download for duplicate cache requests");
        AssertEx.Equal(item.Sha1, await Hashing.Sha1FileAsync(paths[0], CancellationToken.None)
            .ConfigureAwait(false), "cached object hash");
    }

    [PkgTest("Install")]
    public static async Task UnmanagedInstallWritesNoManifestOrMarker(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var target = Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "unmanaged-" +
            Guid.NewGuid().ToString("N"));
        var result = await harness.InstallAsync("1.20.1@server", target, unmanaged: true)
            .ConfigureAwait(false);

        AssertEx.True(result.Unmanaged, "result is marked unmanaged");
        AssertEx.True(Directory.Exists(target), "unmanaged target exists");
        AssertEx.FileExists(Path.Combine(target, "versions", "1.20.1", "server.jar"),
            "unmanaged server jar exists");
        AssertEx.FileDoesNotExist(Path.Combine(target, InstallManifestStore.MarkerFileName),
            "unmanaged target has no marker");
        AssertEx.False(File.Exists(sandbox.Paths.InstallManifestPath(result.InstallId)),
            "unmanaged target has no manifest");
        AssertEx.Equal(0, (await harness.Manifests.LoadAllAsync(CancellationToken.None)
            .ConfigureAwait(false)).Count, "no managed records after unmanaged download");
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.RemoveAsync(result.InstallId),
            "unmanaged target cannot be removed by pkgforge").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.ManifestMissing, error.Code, "unmanaged remove is not managed");
        AssertEx.True(Directory.Exists(target), "unmanaged target remains after refused removal");
    }

    [PkgTest("Install")]
    public static async Task UnmanagedTargetInsideProgramStateIsRejected(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var target = Path.Combine(sandbox.Paths.ManifestsRoot, "unmanaged-state-" +
            Guid.NewGuid().ToString("N"));
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.InstallAsync("1.20.1@server", target, unmanaged: true),
            "unmanaged state target must be refused").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.UnsafePath, error.ExitCode, "unmanaged state target exit code");
        AssertEx.False(Directory.Exists(target), "no unmanaged directory created below manifests");
    }

    [PkgTest("Install")]
    public static async Task FailedManifestDeleteRestoresTarget(TestSandbox sandbox)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipTestException("read-only manifest deletion is only deterministic on Windows");
        }

        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var manifestPath = sandbox.Paths.InstallManifestPath(result.InstallId);
        File.SetAttributes(manifestPath, File.GetAttributes(manifestPath) | FileAttributes.ReadOnly);
        try
        {
            await AssertEx.ThrowsAsync<UnauthorizedAccessException>(
                () => harness.RemoveAsync(result.InstallId),
                "read-only manifest deletion must fail").ConfigureAwait(false);
            AssertEx.True(Directory.Exists(result.Target), "target is restored after manifest failure");
            AssertEx.FileExists(manifestPath, "manifest remains after failed deletion");
        }
        finally
        {
            File.SetAttributes(manifestPath, FileAttributes.Normal);
        }
    }

    [PkgTest("Install")]
    public static async Task SharedManifestTargetIsRejected(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var manifest = await harness.Manifests.LoadByIdRequiredAsync(result.InstallId,
            CancellationToken.None).ConfigureAwait(false);
        var duplicate = JsonCodec.DeserializeInstallManifest(
            JsonCodec.SerializeInstallManifest(manifest))!;
        duplicate.InstallId = "duplicate-install";
        await harness.Manifests.SaveAsync(duplicate, CancellationToken.None).ConfigureAwait(false);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.RemoveAsync(result.InstallId),
            "shared target must be refused").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.Conflict, error.ExitCode, "shared target exit code");
        AssertEx.True(Directory.Exists(result.Target), "shared target remains untouched");
    }

    [PkgTest("Install")]
    public static async Task ManagedAbsoluteTargetIsRejected(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var target = Path.Combine(sandbox.WorkingDirectory, "absolute-target");
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.InstallAsync("1.20.1@server", target),
            "absolute managed target must be rejected").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.Usage, error.Code, "absolute target error code");
        AssertEx.False(Directory.Exists(target), "absolute target was not created");
    }

    [PkgTest("Install")]
    public static async Task CorruptManifestStopsInsteadOfBeingIgnored(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.20.1@server").ConfigureAwait(false);
        var manifestPath = sandbox.Paths.InstallManifestPath(result.Manifest.InstallId);
        await File.WriteAllTextAsync(manifestPath, "{broken").ConfigureAwait(false);

        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => harness.Manifests.LoadAllAsync(CancellationToken.None),
            "corrupt manifest must stop the operation").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.Integrity, error.Code, "corrupt manifest code");
    }

    [PkgTest("Isolation")]
    public static async Task ManagedStateLivesUnderProgramRoot(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var result = await harness.InstallAsync("1.20.1@client:windows-x64").ConfigureAwait(false);
        var manifestPath = sandbox.Paths.InstallManifestPath(result.Manifest.InstallId);

        AssertEx.True(PathSafety.IsWithin(sandbox.Paths.InstallRoot, result.Target, allowEqual: false),
            "target is under the program-root instances directory");
        AssertEx.True(PathSafety.IsWithin(sandbox.Paths.ManifestsRoot, manifestPath, allowEqual: false),
            "manifest is under the program-root manifests directory");
        AssertEx.False(Directory.Exists(Path.Combine(sandbox.Root, ".mcpkg")),
            "no hidden per-user state directory is created");
        AssertEx.False(File.Exists(Path.Combine(sandbox.Root, "config.json")),
            "source config remains the program-root pkgforge.config.json");
    }

    [PkgTest("Concurrency")]
    public static async Task ConcurrentInstallOnSameTargetDoesNotCorrupt(TestSandbox sandbox)
    {
        await using var harness = await InstallTestHarness.CreateAsync(sandbox).ConfigureAwait(false);
        var version = harness.Fixture.Version("1.20.1");
        harness.Fixture.Server.AddFailure(
            harness.Fixture.ContentRelativePath(version.ClientJarSha1),
            MockFailureKind.Delay, int.MaxValue, TimeSpan.FromSeconds(2));

        var firstTask = harness.InstallAsync("1.20.1@client:windows-x64");
        var secondTask = harness.InstallAsync("1.20.1@client:windows-x64");
        var results = await Task.WhenAll(Capture(firstTask), Capture(secondTask)).ConfigureAwait(false);
        AssertEx.Equal(1, results.Count(value => value is null), "one concurrent install succeeds");
        AssertEx.Equal(1, results.Count(value => value is not null), "one concurrent install is rejected");

        var plan = version.Client("windows-x64");
        var target = Path.Combine(sandbox.Paths.InstallRoot, plan.InstallId);
        AssertEx.True(Directory.Exists(target), "target exists after concurrent attempts");
        AssertEx.FileExists(Path.Combine(target, "versions", "1.20.1", "1.20.1.jar"), "target is complete");
        var manifest = await harness.Manifests.LoadByIdRequiredAsync(plan.InstallId, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(AppPaths.InstancesRelativeRoot + "/" + plan.InstallId, manifest.InstallDirectory,
            "manifest target after concurrency");

        static async Task<Exception?> Capture(Task<InstallResult> task)
        {
            try
            {
                await task.ConfigureAwait(false);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
