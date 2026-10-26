using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PkgForge.Tests;

internal static class UnitTests
{
    [PkgTest("Unit")]
    public static Task SizeFormatterFormatsHumanUnits(TestSandbox sandbox)
    {
        AssertEx.Equal("0 B", SizeFormatter.Format(0), "zero bytes");
        AssertEx.Equal("512 B", SizeFormatter.Format(512), "bytes");
        AssertEx.Equal("1 KB", SizeFormatter.Format(1024), "one KiB");
        AssertEx.Equal("1.5 KB", SizeFormatter.Format(1536), "fractional KiB");
        AssertEx.Equal("1 MB", SizeFormatter.Format(1024 * 1024), "one MiB");
        AssertEx.Equal("5 GB", SizeFormatter.Format(5L * 1024 * 1024 * 1024), "GiB");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task PackageSelectorParsesForms(TestSandbox sandbox)
    {
        var client = PackageSelector.Parse("1.20.1", "windows-x64");
        AssertEx.Equal(PackageKind.Client, client.Kind, "default kind");
        AssertEx.Equal("windows-x64", client.Platform, "default platform");
        var server = PackageSelector.Parse("1.20.1@server", "windows-x64");
        AssertEx.Equal(PackageKind.Server, server.Kind, "server kind");
        AssertEx.Equal(null, server.Platform, "server platform");
        var explicitClient = PackageSelector.Parse("26.3@client:linux-arm64", "windows-x64");
        AssertEx.Equal("linux-arm64", explicitClient.Platform, "explicit platform");
        AssertEx.Equal("1.20.1@client:windows-x64",
            PackageNaming.CanonicalPackageId("1.20.1", PackageKind.Client, "windows-x64"), "canonical client");
        AssertEx.Equal("1.20.1-server", PackageNaming.InstallId("1.20.1", PackageKind.Server, null),
            "server install id");
        AssertEx.Throws<PkgException>(() => PackageSelector.Parse("1.20.1@client:../x", "windows-x64"),
            "path-like platform must fail");
        AssertEx.Throws<PkgException>(() => PackageSelector.Parse("../1.20.1", "windows-x64"),
            "path-like version must fail");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task PackageSelectorProbesPlatformLazily(TestSandbox sandbox)
    {
        static string FailProbe() => throw new InvalidOperationException("host platform must not be probed");

        var server = PackageSelector.Parse("1.20.1@server", FailProbe);
        AssertEx.Equal(PackageKind.Server, server.Kind, "server selector avoids host probe");
        AssertEx.Equal(null, server.Platform, "server platform remains empty");
        var explicitClient = PackageSelector.Parse("1.20.1@client:linux-x64", FailProbe);
        AssertEx.Equal("linux-x64", explicitClient.Platform, "explicit platform avoids host probe");
        var defaultClient = PackageSelector.Parse("1.20.1", () => "windows-x64");
        AssertEx.Equal("windows-x64", defaultClient.Platform, "default client lazily probes host");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task IndexSourceRedactsCredentials(TestSandbox sandbox)
    {
        var source = IndexSource.Parse("https://alice:secret@example.com/source");
        AssertEx.Equal("https://example.com/source/", source.Display, "source display strips credentials");
        AssertEx.False(source.Display.Contains("alice", StringComparison.Ordinal), "username is hidden");
        AssertEx.False(source.Display.Contains("secret", StringComparison.Ordinal), "password is hidden");
        AssertEx.Contains("alice:secret", source.IndexUri.UserInfo, "request URI keeps authentication");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task InstallIdRejectsWindowsDeviceNames(TestSandbox sandbox)
    {
        foreach (var invalid in new[] { "CON", "con", "PRN", "NUL", "COM1", "CON.json", "aux.txt" })
        {
            AssertEx.False(AppPaths.IsSafeInstallId(invalid), $"device name must be rejected: {invalid}");
        }

        AssertEx.True(AppPaths.IsSafeInstallId("console"), "ordinary name remains valid");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task NestedProjectMarkersAreDetectedAndProtected(TestSandbox sandbox)
    {
        var parent = Path.Combine(sandbox.Root, "marker-parent");
        var target = Path.Combine(parent, "target");
        var nested = Path.Combine(target, "sub", ".git");
        Directory.CreateDirectory(nested);
        AssertEx.True(PathSafety.HasProjectMarkers(target), "nested .git is detected");
        var error = AssertEx.Throws<PkgException>(
            () => SafeDirectory.DeleteControlledTree(target, parent, "test nested marker"),
            "nested project marker must block recursive deletion");
        AssertEx.Equal(ExitCode.UnsafePath, error.ExitCode, "nested marker protected exit code");
        AssertEx.True(Directory.Exists(target), "nested marker target survives");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task PathSafetyRejectsTraversalAndReservedNames(TestSandbox sandbox)
    {
        AssertEx.Equal("libraries/a.jar", PathSafety.ValidateRelativePath("libraries/a.jar", "test"),
            "valid path");
        AssertEx.Equal("versions/1.14 Pre-Release 1/1.14 Pre-Release 1.jar",
            PathSafety.ValidateRelativePath("versions/1.14 Pre-Release 1/1.14 Pre-Release 1.jar",
                "space-bearing plan path"), "internal spaces in plan path are allowed");
        foreach (var invalid in new[]
                 {
                     "../escape", "/absolute", "a\\b", "a//b", "a/./b", "CON", "con.txt",
                     "bad:name", "trailing.", "dir/trailing ", "a/\u202Eevil", "",
                 })
        {
            AssertEx.Throws<PkgException>(() => PathSafety.ValidateRelativePath(invalid, "test"),
                $"invalid path should fail: {invalid}");
        }

        var root = Path.Combine(sandbox.Root, "path-root");
        Directory.CreateDirectory(root);
        AssertEx.Throws<PkgException>(() => PathSafety.CombineUnderRoot(root, "../x", "test"),
            "combine traversal must fail");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task IndexSourceAllowsAnyHostAndBothHttpSchemes(TestSandbox sandbox)
    {
        var https = IndexSource.Parse("https://metadata.example/source");
        AssertEx.Equal("https://metadata.example/source/", https.Display, "canonical source");
        AssertEx.Equal("https://metadata.example/source/data/a.idx",
            https.ResolveIndexRelative("data/a.idx", "shard").AbsoluteUri, "relative shard");
        AssertEx.Equal("https://other.example/data/a.idx",
            https.ResolveIndexRelative("https://other.example/data/a.idx", "shard").AbsoluteUri,
            "arbitrary shard host accepted");
        AssertEx.Equal("https://metadata.example/data/a.idx",
            https.ResolveIndexRelative("../data/a.idx", "shard").AbsoluteUri,
            "relative parent path accepted");

        var http = IndexSource.Parse("http://remote.example:8080/");
        AssertEx.Equal("http://remote.example:8080/", http.Display, "remote HTTP source accepted");
        AssertEx.Throws<PkgException>(() => IndexSource.Parse("https://example.com/?x=1"), "query rejected");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static async Task ManifestStoreRoundTripsAndFailsClosed(TestSandbox sandbox)
    {
        var store = new InstallManifestStore(sandbox.Paths, NullLog.Instance);
        var manifest = CreateManifest(sandbox);
        await store.SaveAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        var loaded = await store.LoadByIdRequiredAsync(manifest.InstallId, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(manifest.PackageId, loaded.PackageId, "manifest round trip");
        AssertEx.Equal(manifest.InstallDirectory, loaded.InstallDirectory, "manifest directory");

        var path = sandbox.Paths.InstallManifestPath(manifest.InstallId);
        await File.WriteAllTextAsync(path, "{broken", CancellationToken.None).ConfigureAwait(false);
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => store.LoadByIdRequiredAsync(manifest.InstallId, CancellationToken.None),
            "broken manifest must fail").ConfigureAwait(false);
        AssertEx.Equal(ExitCode.Verification, error.ExitCode, "broken manifest exit code");
        AssertEx.Equal(ErrorCodes.Integrity, error.Code, "broken manifest code");
    }

    [PkgTest("Unit")]
    public static async Task ManifestLookupAcceptsCaseVariant(TestSandbox sandbox)
    {
        var store = new InstallManifestStore(sandbox.Paths, NullLog.Instance);
        var manifest = CreateManifest(sandbox);
        await store.SaveAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        var loaded = await store.LoadByIdRequiredAsync(manifest.InstallId.ToUpperInvariant(),
            CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(manifest.InstallId, loaded.InstallId, "case-variant manifest lookup");
    }

    [PkgTest("Unit")]
    public static async Task ManifestValidationRejectsEscapingFileAndTarget(TestSandbox sandbox)
    {
        var manifest = CreateManifest(sandbox);
        manifest.Files[0].Path = "../escape";
        var error = AssertEx.Throws<PkgException>(() => InstallManifestStore.Validate(manifest),
            "escaping manifest file");
        AssertEx.Equal(ErrorCodes.UnsafePath, error.Code, "escaping manifest code");

        var valid = CreateManifest(sandbox);
        valid.ManagedRoot = "data/other";
        valid.InstallDirectory = "data/other/example";
        AssertEx.Throws<PkgException>(() => InstallManifestStore.Validate(valid),
            "unexpected managed root must fail");
        await Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task FileSystemErrorsMapCommonFailures(TestSandbox sandbox)
    {
        AssertEx.Equal(ExitCode.Permission,
            FileSystemErrors.Map(new UnauthorizedAccessException("denied"), "test").ExitCode,
            "permission mapping");
        AssertEx.Equal(ExitCode.UnsafePath,
            FileSystemErrors.Map(new PathTooLongException("long"), "test").ExitCode,
            "path length mapping");
        var diskFull = FileSystemErrors.Map(new IOException("There is not enough space on the disk."), "test");
        AssertEx.Equal(ExitCode.RuntimeFailure, diskFull.ExitCode, "disk full mapping");
        AssertEx.Equal(ErrorCodes.FileSystem, diskFull.Code, "disk full code");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static async Task ProcessLockTimesOutAndReclaimsStale(TestSandbox sandbox)
    {
        var lockDirectory = Path.Combine(sandbox.Root, "locks");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, "test.lock");
        await using (var first = await ProcessLock.AcquireAsync(lockPath, TimeSpan.FromSeconds(1),
                         TimeSpan.FromMinutes(1), NullLog.Instance, CancellationToken.None).ConfigureAwait(false))
        {
            var error = await AssertEx.ThrowsAsync<PkgException>(
                () => ProcessLock.AcquireAsync(lockPath, TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMinutes(1), NullLog.Instance, CancellationToken.None),
                "second lock must time out").ConfigureAwait(false);
            AssertEx.Equal(ExitCode.LockTimeout, error.ExitCode, "lock timeout exit code");
        }

        await File.WriteAllTextAsync(lockPath, "stale", CancellationToken.None).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromHours(1));
        await using var reclaimed = await ProcessLock.AcquireAsync(lockPath, TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), NullLog.Instance, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(File.Exists(lockPath), "reclaimed lock file exists");
    }

    [PkgTest("Unit")]
    public static async Task ProcessLockReclaimsDeadOwnerImmediately(TestSandbox sandbox)
    {
        var lockDirectory = Path.Combine(sandbox.Root, "dead-lock");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, "dead.lock");
        await File.WriteAllTextAsync(lockPath,
            $"pid={int.MaxValue}\ntoken=deadbeef\ncreated={DateTimeOffset.UtcNow:O}\n").ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        await using var acquired = await ProcessLock.AcquireAsync(lockPath, TimeSpan.FromSeconds(2),
            TimeSpan.FromHours(1), NullLog.Instance, CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();
        AssertEx.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5),
            "dead-owner lock was reclaimed before the normal lock timeout");
        AssertEx.True(File.Exists(lockPath), "replacement lock exists");
    }

    [PkgTest("Unit")]
    public static async Task NativeExtractorRejectsZipSlipAndExtractsSafely(TestSandbox sandbox)
    {
        var archive = Path.Combine(sandbox.Root, "native.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var bad = zip.CreateEntry("../escape.txt");
            await using var writer = new StreamWriter(bad.Open(), new UTF8Encoding(false));
            await writer.WriteAsync("escape").ConfigureAwait(false);
        }

        var nativeRoot = Path.Combine(sandbox.Root, "natives");
        Directory.CreateDirectory(nativeRoot);
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => NativeExtractor.ExtractAsync(archive, nativeRoot, "natives", [], NullLog.Instance,
                CancellationToken.None), "zip slip must fail").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.UnsafePath, error.Code, "zip slip error code");
        AssertEx.FileDoesNotExist(Path.Combine(sandbox.Root, "escape.txt"), "zip slip wrote outside");

        var goodArchive = Path.Combine(sandbox.Root, "native-good.zip");
        using (var zip = ZipFile.Open(goodArchive, ZipArchiveMode.Create))
        {
            WriteZip(zip, "sub/extra.dll", "dll");
            WriteZip(zip, "META-INF/MANIFEST.MF", "manifest");
        }

        var goodRoot = Path.Combine(sandbox.Root, "natives-good");
        Directory.CreateDirectory(goodRoot);
        var extracted = await NativeExtractor.ExtractAsync(goodArchive, goodRoot, "natives", ["META-INF"],
            NullLog.Instance, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(extracted.Count == 1, "only one native file extracted");
        AssertEx.FileExists(Path.Combine(goodRoot, "sub", "extra.dll"), "native file extracted");
        AssertEx.FileDoesNotExist(Path.Combine(goodRoot, "META-INF", "MANIFEST.MF"), "excluded entry absent");

        static void WriteZip(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    [PkgTest("Unit")]
    public static async Task NativeExtractorCleansTemporaryOnMoveCollision(TestSandbox sandbox)
    {
        var archive = Path.Combine(sandbox.Root, "native-collision.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("collision.txt");
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await writer.WriteAsync("content").ConfigureAwait(false);
        }

        var nativeRoot = Path.Combine(sandbox.Root, "natives-collision");
        Directory.CreateDirectory(nativeRoot);
        Directory.CreateDirectory(Path.Combine(nativeRoot, "collision.txt"));
        await AssertEx.ThrowsAsync<IOException>(
            () => NativeExtractor.ExtractAsync(archive, nativeRoot, "natives", [], NullLog.Instance,
                CancellationToken.None), "final move collision must fail").ConfigureAwait(false);
        AssertEx.Equal(0, Directory.EnumerateFiles(nativeRoot, "*.tmp-*", SearchOption.AllDirectories).Count(),
            "temporary extraction file was cleaned");
    }

    [PkgTest("Unit")]
    public static Task SafeDirectoryRefusesUncontrolledTargets(TestSandbox sandbox)
    {
        var managed = Path.Combine(sandbox.Root, "managed");
        var outsideParent = Path.Combine(sandbox.Root, "other");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(outsideParent);
        var target = Path.Combine(managed, "install");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "data");

        AssertEx.Throws<PkgException>(() => SafeDirectory.DeleteControlledTree(target, outsideParent, "test"),
            "wrong allowed parent must fail");
        AssertEx.True(Directory.Exists(target), "rejected target remains");

        SafeDirectory.DeleteControlledTree(target, managed, "test");
        AssertEx.False(Directory.Exists(target), "controlled target removed");

        var project = Path.Combine(managed, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        AssertEx.Throws<PkgException>(() => SafeDirectory.DeleteControlledTree(project, managed, "test"),
            "project marker must fail");
        AssertEx.True(Directory.Exists(project), "project directory remains");
        Directory.Delete(Path.Combine(project, ".git"));
        Directory.Delete(project);
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task SymlinkTraversalIsRejected(TestSandbox sandbox)
    {
        var managed = Path.Combine(sandbox.Root, "managed-link");
        var outside = Path.Combine(sandbox.Root, "outside");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(managed, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                              PlatformNotSupportedException)
        {
            throw new SkipTestException("symbolic links are not permitted for this account");
        }

        try
        {
            var error = AssertEx.Throws<PkgException>(
                () => PathSafety.EnsureTreeHasNoReparsePoints(managed, "test"),
                "symlink must be rejected");
            AssertEx.Equal(ErrorCodes.UnsafePath, error.Code, "symlink error code");
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside);
            Directory.Delete(managed);
        }

        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task CanonicalSchemaResourcesAreEmbedded(TestSandbox sandbox)
    {
        foreach (var shard in new[] { "catalog", "launch", "assets" })
        {
            var name = "PkgForge.Tests.Schema." + shard + ".sql";
            using var stream = typeof(TestIndexFixture).Assembly.GetManifestResourceStream(name);
            AssertEx.True(stream is not null, $"embedded schema {shard} exists");
            using var reader = new StreamReader(stream!);
            var schema = reader.ReadToEnd();
            AssertEx.Contains("STRICT", schema, $"{shard} schema is the canonical STRICT schema");
            AssertEx.Contains("meta", schema, $"{shard} schema contains meta");
        }

        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static async Task PreallocatedFileIsReservedAndExclusive(TestSandbox sandbox)
    {
        var path = Path.Combine(sandbox.Root, "reserved.bin");
        await using var first = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        DiskSpace.Preallocate(first, 4096, "test reservation");
        AssertEx.Equal(4096L, first.Length, "preallocated length");
        AssertEx.Throws<IOException>(() =>
        {
            using var second = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }, "a second handle must not be able to open an exclusively reserved file");
    }

    [PkgTest("Unit")]
    public static Task DiskSpaceReportsInsufficientFreeSpace(TestSandbox sandbox)
    {
        DiskSpace.EnsureAvailable(sandbox.Root, 1, "test");
        var error = AssertEx.Throws<PkgException>(
            () => DiskSpace.EnsureAvailable(sandbox.Root, long.MaxValue, "test"),
            "impossible reservation must fail");
        AssertEx.Equal(ErrorCodes.DiskSpace, error.Code, "disk-space error code");
        AssertEx.Equal(ExitCode.RuntimeFailure, error.ExitCode, "disk-space exit code");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task LocalSourceConfigAcceptsHttpAndHttpsWithoutHostPolicy(TestSandbox sandbox)
    {
        var programDirectory = Path.Combine(sandbox.Root, "program-config");
        Directory.CreateDirectory(programDirectory);
        var configPath = Path.Combine(programDirectory, LocalSourceConfig.FileName);
        File.WriteAllText(configPath, """
                                    {
                                      "schema": 1,
                                      "indexSource": "https://any.example/mcpkg/"
                                    }
                                    """);
        var https = LocalSourceConfig.Load(programDirectory);
        AssertEx.Equal("https://any.example/mcpkg/", https.Display, "HTTPS source loaded");

        File.WriteAllText(configPath, """
                                    {
                                      "schema": 1,
                                      "indexSource": "http://127.0.0.1:1234/mcpkg/"
                                    }
                                    """);
        var http = LocalSourceConfig.Load(programDirectory);
        AssertEx.Equal("http://127.0.0.1:1234/mcpkg/", http.Display, "HTTP source loaded");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task LocalSourceConfigMissingFailsClosed(TestSandbox sandbox)
    {
        var error = AssertEx.Throws<PkgException>(
            () => LocalSourceConfig.Load(Path.Combine(sandbox.Root, "missing-config")),
            "missing source config must fail");
        AssertEx.Equal(ErrorCodes.InvalidSource, error.Code, "missing source config code");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task LocalSourceConfigRejectsInvalidSchema(TestSandbox sandbox)
    {
        var programDirectory = Path.Combine(sandbox.Root, "invalid-config");
        Directory.CreateDirectory(programDirectory);
        File.WriteAllText(Path.Combine(programDirectory, LocalSourceConfig.FileName), """
                                                                                      {
                                                                                        "schema": 2,
                                                                                        "indexSource": "https://any.example/"
                                                                                      }
                                                                                      """);
        var error = AssertEx.Throws<PkgException>(() => LocalSourceConfig.Load(programDirectory),
            "invalid source schema must fail");
        AssertEx.Equal(ErrorCodes.InvalidSource, error.Code, "invalid source schema code");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task PortableProgramRootOwnsAllState(TestSandbox sandbox)
    {
        var paths = new AppPaths(sandbox.Root, sandbox.WorkingDirectory);
        AssertEx.Equal(Path.Combine(sandbox.Root, "data"), paths.GlobalRoot, "data lives under program root");
        AssertEx.Equal(Path.Combine(paths.GlobalRoot, "manifests"), paths.ManifestsRoot,
            "manifests live under program root");
        AssertEx.Equal(Path.Combine(paths.GlobalRoot, "instances"), paths.InstallRoot,
            "instances live under program root");
        AssertEx.Equal(Path.Combine(paths.GlobalRoot, "locks"), paths.GlobalLocks,
            "locks live under program root");
        AssertEx.Equal(Path.Combine(paths.GlobalRoot, "cache"), paths.CacheRoot,
            "cache lives under program root");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task ReadOnlyProgramRootFailsClosed(TestSandbox sandbox)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipTestException("real ACL read-only test requires Windows");
        }

        var readOnlyRoot = Path.Combine(sandbox.Root, "readonly-program-root");
        Directory.CreateDirectory(readOnlyRoot);
        var currentUser = WindowsIdentity.GetCurrent().User
                          ?? throw new SkipTestException("current Windows SID is unavailable");
        var denyRule = new FileSystemAccessRule(currentUser,
            FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.WriteData,
            AccessControlType.Deny);
        var security = new DirectoryInfo(readOnlyRoot).GetAccessControl();
        security.AddAccessRule(denyRule);
        new DirectoryInfo(readOnlyRoot).SetAccessControl(security);
        try
        {
            var paths = new AppPaths(Path.Combine(readOnlyRoot, "nested-program"), sandbox.WorkingDirectory);
            var error = AssertEx.Throws<PkgException>(() => paths.EnsureGlobalLayout(),
                "real read-only program root must fail closed");
            AssertEx.True(error.Code is ErrorCodes.Permission or ErrorCodes.FileSystem,
                "read-only root has explicit error");
            AssertEx.False(Directory.Exists(Path.Combine(sandbox.Root, ".mcpkg")),
                "read-only root did not fall back to user state");
        }
        finally
        {
            security.RemoveAccessRule(denyRule);
            new DirectoryInfo(readOnlyRoot).SetAccessControl(security);
        }

        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task UnavailableProgramRootFailsClosed(TestSandbox sandbox)
    {
        var blocker = Path.Combine(sandbox.Root, "program-root-blocker");
        File.WriteAllText(blocker, "not a directory");
        var paths = new AppPaths(Path.Combine(blocker, "nested"), sandbox.WorkingDirectory);
        var error = AssertEx.Throws<PkgException>(() => paths.EnsureGlobalLayout(),
            "unavailable program root must fail closed");
        AssertEx.True(error.Code is ErrorCodes.Permission or ErrorCodes.FileSystem,
            "unavailable root error is explicit");
        AssertEx.False(Directory.Exists(Path.Combine(sandbox.Root, ".mcpkg")),
            "no user-directory fallback was attempted");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task ManagedRelativePathBoundaryIsEnforced(TestSandbox sandbox)
    {
        var paths = new AppPaths(sandbox.Root, sandbox.WorkingDirectory);
        var target = paths.ResolveManagedInstall("data/instances/example", "test");
        AssertEx.Equal(Path.Combine(sandbox.Root, "data", "instances", "example"), target,
            "relative managed target resolves under program root");
        var unicodeTarget = paths.ResolveManagedInstall("data/instances/中文实例", "test");
        AssertEx.Contains("中文实例", unicodeTarget, "Unicode managed target accepted");
        AssertEx.Throws<PkgException>(() => paths.ResolveManagedInstall("../escape", "test"),
            "parent traversal rejected");
        AssertEx.Throws<PkgException>(() => paths.ResolveManagedInstall("C:\\outside", "test"),
            "absolute path rejected");
        AssertEx.Throws<PkgException>(() => paths.ResolveManagedInstall("data/instances/a/b", "test"),
            "nested managed target rejected");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task PathSafetyProtectsSystemRootsAndLongPaths(TestSandbox sandbox)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AssertEx.Throws<PkgException>(() => PathSafety.EnsureNotProtected(home, "test"),
            "user home must be protected");
        AssertEx.Throws<PkgException>(() => PathSafety.EnsureNotProtected(Environment.SystemDirectory, "test"),
            "system directory must be protected");
        var root = Path.GetPathRoot(sandbox.Root)!;
        AssertEx.Throws<PkgException>(() => PathSafety.EnsureNotProtected(root, "test"),
            "filesystem root must be protected");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            AssertEx.Throws<PkgException>(() =>
                    PathSafety.EnsureNotProtected(Path.Combine(windows, "Temp", "pkgforge-test"), "test"),
                "descendant of the Windows directory must be protected");
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            AssertEx.Throws<PkgException>(() =>
                    PathSafety.EnsureNotProtected(Path.Combine(programData, "pkgforge-test"), "test"),
                "descendant of ProgramData must be protected");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            PathSafety.EnsureNotProtected(Path.Combine(localAppData, "MCPKG", "instances", "sample"),
                "test");
        }

        var longPath = Path.Combine(sandbox.Root, new string('a', PathSafety.MaxAbsolutePathLength));
        AssertEx.Throws<PkgException>(() => PathSafety.NormalizeAbsolutePath(longPath, "test"),
            "overlong path must be rejected");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static async Task NativeExtractorRejectsSymlinkEntries(TestSandbox sandbox)
    {
        var archive = Path.Combine(sandbox.Root, "symlink-native.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await writer.WriteAsync("/etc/passwd").ConfigureAwait(false);
        }

        var nativeRoot = Path.Combine(sandbox.Root, "natives-symlink");
        Directory.CreateDirectory(nativeRoot);
        var error = await AssertEx.ThrowsAsync<PkgException>(
            () => NativeExtractor.ExtractAsync(archive, nativeRoot, "natives", [], NullLog.Instance,
                CancellationToken.None), "symlink zip entry must fail").ConfigureAwait(false);
        AssertEx.Equal(ErrorCodes.Integrity, error.Code, "symlink zip error code");
    }

    [PkgTest("Unit")]
    public static Task MalformedIndexManifestFailsWithIntegrityError(TestSandbox sandbox)
    {
        const string json = """
                            {
                              "schema": 2,
                              "format": "sqlite-shards",
                              "latestRelease": "1.20.1",
                              "shards": null,
                              "counts": { "versions": 1, "clientPlans": 1, "serverPlans": 0, "assetDocs": 1 }
                            }
                            """;
        var manifest = JsonCodec.ParseIndexManifest(Encoding.UTF8.GetBytes(json));
        var error = AssertEx.Throws<PkgException>(() => IndexContract.ValidateManifest(manifest, "test"),
            "null shard list must fail");
        AssertEx.Equal(ErrorCodes.InvalidIndex, error.Code, "null shard list code");

        var install = CreateManifest(sandbox);
        install.Files = null!;
        var installError = AssertEx.Throws<PkgException>(() => InstallManifestStore.Validate(install),
            "null manifest file list must fail");
        AssertEx.Equal(ErrorCodes.Integrity, installError.Code, "null file list code");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task PathSafetyRejectsOuterWhitespace(TestSandbox sandbox)
    {
        AssertEx.Throws<PkgException>(() => PathSafety.ResolveAbsoluteOrRelative(
                " C:\\ leading", sandbox.WorkingDirectory, "test"),
            "leading whitespace path rejected");
        AssertEx.Throws<PkgException>(() => PathSafety.ResolveAbsoluteOrRelative(
                "C:\\trailing ", sandbox.WorkingDirectory, "test"),
            "trailing whitespace path rejected");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task ConsoleLogSanitizesControlAndAnsiCharacters(TestSandbox sandbox)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        output.NewLine = "\n";
        error.NewLine = "\n";
        var log = new ConsoleLog(output, error);
        log.Info("before \u001b[31mred\u001b[0m after\r\nnext");
        log.Data("data \u001b]0;title\u0007 text");
        var combined = output.ToString() + error.ToString();
        AssertEx.False(combined.Contains('\u001b'), "ANSI escape removed");
        AssertEx.False(combined.Contains('\u0007'), "BEL removed");
        AssertEx.False(combined.Contains('\r'), "CR removed");
        AssertEx.Contains("before", combined, "ordinary text preserved");
        return Task.CompletedTask;
    }

    [PkgTest("Unit")]
    public static Task ProductVersionIsGenerated(TestSandbox sandbox)
    {
        AssertEx.True(BuildConfig.ProductVersion.Length > 0, "product version is generated");
        return Task.CompletedTask;
    }

    private static InstallManifest CreateManifest(TestSandbox sandbox)
    {
        var installId = PackageNaming.InstallId("1.20.1", PackageKind.Client, "windows-x64");
        var target = Path.Combine(sandbox.Paths.InstallRoot, installId);
        Directory.CreateDirectory(target);
        return new InstallManifest
        {
            ManifestVersion = 2,
            InstallId = installId,
            PackageId = PackageNaming.CanonicalPackageId("1.20.1", PackageKind.Client, "windows-x64"),
            VersionId = "1.20.1",
            VersionKey = "1.20.1",
            Kind = "client",
            Platform = "windows-x64",
            InstallDirectory = AppPaths.InstancesRelativeRoot + "/" + installId,
            ManagedRoot = AppPaths.InstancesRelativeRoot,
            MarkerFile = InstallManifestStore.MarkerFileName,
            InstalledAtUtc = DateTimeOffset.UtcNow,
            IndexSource = "https://test.invalid/",
            IndexBundleId = new string('a', 64),
            PlanId = 1,
            FileCount = 1,
            TotalBytes = 1,
            Files =
            [
                new InstalledFile
                {
                    Path = "versions/1.20.1/1.20.1.jar",
                    Size = 1,
                    Sha1 = new string('b', 40),
                    Role = "client",
                },
            ],
        };
    }
}
