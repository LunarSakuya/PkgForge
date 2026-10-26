using System.Text.RegularExpressions;

namespace PkgForge.Tests;

internal static class CliE2ETests
{
    [PkgTest("E2E")]
    public static async Task CliUpdateListSearchInstallInstalledRemove(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        var update = await CliRunner.RunAsync(sandbox,
            ["update"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(update, 0, "update");
        AssertEx.Contains("Bundle", update.StandardOutput, "update bundle output");

        var list = await CliRunner.RunAsync(sandbox, ["list", "--limit", "8", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(list, 0, "list");
        AssertEx.Contains("PACKAGE", list.StandardOutput, "list header");
        AssertEx.Contains("KIND", list.StandardOutput, "list kind header");
        AssertEx.Contains("@server", list.StandardOutput, "list server package");
        AssertEx.Contains("@client", list.StandardOutput, "list client package");
        AssertEx.True(Regex.IsMatch(list.StandardOutput, @"\d+(?:\.\d+)? (?:B|KB|MB|GB)"),
            "list uses human-readable sizes");

        var search = await CliRunner.RunAsync(sandbox, ["search", "1.20", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(search, 0, "search");
        AssertEx.Contains("1.20.1@server", search.StandardOutput, "search includes server");
        AssertEx.Contains("1.20.1@client:windows-x64", search.StandardOutput, "search includes client");
        AssertEx.Contains("Search '1.20'", search.StandardError, "search summary");

        var plan = fixture.Version("1.20.1").ServerPlan;
        var target = Path.Combine(sandbox.Paths.InstallRoot, plan.InstallId);
        var install = await CliRunner.RunAsync(sandbox,
            ["install", "1.20.1@server", "-y", "--no-update", "--threads", "2"]).ConfigureAwait(false);
        AssertExit(install, 0, "install");
        AssertEx.FileExists(Path.Combine(target, "versions", "1.20.1", "server.jar"), "server jar installed");
        AssertEx.FileExists(Path.Combine(target, InstallManifestStore.MarkerFileName), "marker installed");
        AssertEx.Contains(plan.InstallId, install.StandardOutput, "install id output");

        var installed = await CliRunner.RunAsync(sandbox, ["installed"]).ConfigureAwait(false);
        AssertExit(installed, 0, "installed");
        AssertEx.Contains(plan.InstallId, installed.StandardOutput, "installed table record");

        var extra = Path.Combine(target, "PCL", "residue.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        await File.WriteAllTextAsync(extra, "launcher residue").ConfigureAwait(false);
        var refused = await CliRunner.RunAsync(sandbox,
            ["remove", plan.InstallId, "-y", "--no-update"]).ConfigureAwait(false);
        AssertExit(refused, 6, "remove requires force for extra files");
        AssertEx.True(Directory.Exists(target), "remove refusal keeps folder");
        AssertEx.Contains("--force", refused.StandardError, "remove refusal explains force");

        var removed = await CliRunner.RunAsync(sandbox,
            ["remove", plan.InstallId, "-y", "--force", "--no-update"]).ConfigureAwait(false);
        AssertExit(removed, 0, "forced remove");
        AssertEx.False(Directory.Exists(target), "whole folder removed");
        AssertEx.False(File.Exists(sandbox.Paths.InstallManifestPath(plan.InstallId)), "manifest removed");
    }

    [PkgTest("E2E")]
    public static async Task VersionIdWithSpacesAndQuotedDirInstall(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox, includeSpaceVersion: true)
            .ConfigureAwait(false);
        var update = await CliRunner.RunAsync(sandbox,
            ["update"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(update, 0, "space-version update");

        var version = fixture.Version("26.3 Preview 1");
        var plan = version.ServerPlan;
        var target = Path.Combine(sandbox.Paths.InstallRoot, "Instance With Spaces");
        var relativeTarget = AppPaths.InstancesRelativeRoot + "/Instance With Spaces";
        var install = await CliRunner.RunAsync(sandbox,
        [
            "install",
            "26.3 Preview 1@server",
            "--dir",
            relativeTarget,
            "-y",
            "--no-update",
            "--threads",
            "2",
        ]).ConfigureAwait(false);
        AssertExit(install, 0, "space-version quoted install");
        AssertEx.FileExists(Path.Combine(target, "versions", "26.3 Preview 1", "server.jar"),
            "space-version server jar");
        AssertEx.FileExists(Path.Combine(target, InstallManifestStore.MarkerFileName),
            "space-version marker");
        var stored = JsonCodec.DeserializeInstallManifest(
            await File.ReadAllTextAsync(sandbox.Paths.InstallManifestPath(plan.InstallId))
                .ConfigureAwait(false))!;
        AssertEx.Equal("26.3 Preview 1", stored.VersionId, "original version id retained");
        AssertEx.Equal("26.3_Preview_1", stored.VersionKey, "safe version key used in package manifest");
        AssertEx.Equal(plan.InstallId, stored.InstallId, "safe install id used for paths");
        AssertEx.Equal(relativeTarget, stored.InstallDirectory, "relative install directory recorded");

        var remove = await CliRunner.RunAsync(sandbox,
            ["remove", plan.InstallId, "-y", "--no-update"]).ConfigureAwait(false);
        AssertExit(remove, 0, "space-version remove");
        AssertEx.False(Directory.Exists(target), "space path removed");
    }

    [PkgTest("E2E")]
    public static async Task NonInteractiveInstallWithoutYesFailsClosed(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        var update = await CliRunner.RunAsync(sandbox,
            ["update"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(update, 0, "update before noninteractive install");
        var plan = fixture.Version("1.16.5").ServerPlan;
        var target = Path.Combine(sandbox.Paths.InstallRoot, plan.InstallId);

        var result = await CliRunner.RunAsync(sandbox,
            ["install", "1.16.5@server", "--no-update"]).ConfigureAwait(false);
        AssertExit(result, 9, "non-interactive confirmation refusal");
        AssertEx.Contains("stdin is not interactive", result.StandardError, "non-interactive explanation");
        AssertEx.False(Directory.Exists(target), "no target created without confirmation");
        AssertEx.False(File.Exists(sandbox.Paths.InstallManifestPath(plan.InstallId)),
            "no manifest created without confirmation");
    }

    [PkgTest("E2E")]
    public static async Task ManagedAndUnmanagedDirectoryBoundary(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        var update = await CliRunner.RunAsync(sandbox,
            ["update"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(update, 0, "boundary update");

        var plan = fixture.Version("1.16.5").ServerPlan;
        var absolute = Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "cli-unmanaged-" +
            Guid.NewGuid().ToString("N"));
        var unmanaged = await CliRunner.RunAsync(sandbox,
            ["install", "1.16.5@server", "--dir", absolute, "--unmanaged", "-y", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(unmanaged, 0, "unmanaged install");
        AssertEx.Contains("UNMANAGED", unmanaged.StandardError, "unmanaged warning");
        AssertEx.FileExists(Path.Combine(absolute, "versions", "1.16.5", "server.jar"),
            "unmanaged jar exists");
        AssertEx.FileDoesNotExist(Path.Combine(absolute, InstallManifestStore.MarkerFileName),
            "unmanaged marker absent");
        AssertEx.False(File.Exists(sandbox.Paths.InstallManifestPath(plan.InstallId)),
            "unmanaged manifest absent");

        var installed = await CliRunner.RunAsync(sandbox, ["installed"]).ConfigureAwait(false);
        AssertExit(installed, 0, "installed after unmanaged");
        AssertEx.False(installed.StandardOutput.Contains(plan.InstallId, StringComparison.Ordinal),
            "unmanaged install is not listed");
        var remove = await CliRunner.RunAsync(sandbox,
            ["remove", plan.InstallId, "-y", "--no-update"]).ConfigureAwait(false);
        AssertExit(remove, 3, "unmanaged remove refusal");
        AssertEx.True(Directory.Exists(absolute), "unmanaged target remains");

        var managedAbsolute = Path.Combine(sandbox.WorkingDirectory, "managed-absolute");
        var rejected = await CliRunner.RunAsync(sandbox,
            ["install", "1.16.5@server", "--dir", managedAbsolute, "-y", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(rejected, 2, "absolute managed install rejection");
        AssertEx.False(Directory.Exists(managedAbsolute), "absolute managed target absent");

        var downloadOnly = Path.Combine(Path.GetDirectoryName(sandbox.Root)!, "download-only-" +
            Guid.NewGuid().ToString("N"));
        var downloadOnlyResult = await CliRunner.RunAsync(sandbox,
            ["install", "1.16.5@server", "--dir", downloadOnly, "--download-only", "-y", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(downloadOnlyResult, 0, "download-only alias");
        AssertEx.FileExists(Path.Combine(downloadOnly, "versions", "1.16.5", "server.jar"),
            "download-only jar exists");
        AssertEx.False(File.Exists(Path.Combine(sandbox.Paths.ManifestsRoot, plan.InstallId + ".json")),
            "download-only has no managed record");
    }

    [PkgTest("E2E")]
    public static async Task CommandSpecificFilterMatrix(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        var update = await CliRunner.RunAsync(sandbox,
            ["update"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(update, 0, "filter update");

        var serverPlatform = await CliRunner.RunAsync(sandbox,
            ["search", "1.2", "--kind", "server", "--platform", "unknown", "--limit", "5", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(serverPlatform, 0, "server unknown platform filter");
        AssertEx.Contains("@server", serverPlatform.StandardOutput, "unknown platform returns servers");

        var listServerPlatform = await CliRunner.RunAsync(sandbox,
            ["list", "--kind", "server", "--platform", "unknown", "--limit", "5", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(listServerPlatform, 0, "server unknown list filter");
        AssertEx.Contains("@server", listServerPlatform.StandardOutput, "server list returns servers");

        var noLinuxServer = await CliRunner.RunAsync(sandbox,
            ["list", "--kind", "server", "--platform", "linux-x64", "--limit", "5", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(noLinuxServer, 0, "server linux platform filter");
        AssertEx.False(noLinuxServer.StandardOutput.Contains("@server", StringComparison.Ordinal),
            "empty server platform is unknown, not linux-x64");

        var anyPlatform = await CliRunner.RunAsync(sandbox,
            ["list", "--platform", "any", "--no-update"]).ConfigureAwait(false);
        AssertExit(anyPlatform, 2, "platform any is not a special value");

        var releaseSearch = await CliRunner.RunAsync(sandbox,
            ["search", "26.4-snapshot-5", "--type", "snapshot", "--limit", "0", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(releaseSearch, 0, "snapshot type filter");
        AssertEx.Contains("@server", releaseSearch.StandardOutput, "snapshot filter matches snapshot");

        var wrongType = await CliRunner.RunAsync(sandbox,
            ["search", "26.4-snapshot-5", "--type", "release", "--limit", "0", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(wrongType, 0, "release type filter");
        AssertEx.False(wrongType.StandardOutput.Contains("26.4-snapshot-5", StringComparison.Ordinal),
            "release filter excludes snapshot");

        var releaseInstall = await CliRunner.RunAsync(sandbox,
            ["install", "1.16.5@server", "-y", "--no-update"]).ConfigureAwait(false);
        AssertExit(releaseInstall, 0, "release install");
        var snapshotInstall = await CliRunner.RunAsync(sandbox,
            ["install", "26.4-snapshot-5@server", "-y", "--no-update"]).ConfigureAwait(false);
        AssertExit(snapshotInstall, 0, "snapshot install");

        var installedRelease = await CliRunner.RunAsync(sandbox,
            ["installed", "--kind", "server", "--platform", "unknown", "--type", "release"])
            .ConfigureAwait(false);
        AssertExit(installedRelease, 0, "installed release filter");
        AssertEx.Contains("1.16.5-server", installedRelease.StandardOutput, "release record shown");
        AssertEx.False(installedRelease.StandardOutput.Contains("26.4-snapshot-5-server", StringComparison.Ordinal),
            "snapshot record excluded");

        var installedSnapshot = await CliRunner.RunAsync(sandbox,
            ["installed", "--kind", "server", "--platform", "unknown", "--type", "snapshot"])
            .ConfigureAwait(false);
        AssertExit(installedSnapshot, 0, "installed snapshot filter");
        AssertEx.Contains("26.4-snapshot-5-server", installedSnapshot.StandardOutput,
            "snapshot record shown");
        AssertEx.False(installedSnapshot.StandardOutput.Contains("1.16.5-server", StringComparison.Ordinal),
            "release record excluded");

        var installedLinux = await CliRunner.RunAsync(sandbox,
            ["installed", "--platform", "linux-x64"]).ConfigureAwait(false);
        AssertExit(installedLinux, 0, "installed platform filter is supported");
        AssertEx.Contains("No installations match", installedLinux.StandardOutput,
            "server records are unknown, not linux-x64");

        var invalidType = await CliRunner.RunAsync(sandbox, ["list", "--type", "bogus", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(invalidType, 2, "invalid type filter");
        var updateType = await CliRunner.RunAsync(sandbox, ["update", "--type", "release"])
            .ConfigureAwait(false);
        AssertExit(updateType, 2, "type not accepted by update");
        var installType = await CliRunner.RunAsync(sandbox,
            ["install", "1.16.5@server", "--type", "release", "-y", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(installType, 2, "type not accepted by install");
        var removeType = await CliRunner.RunAsync(sandbox,
            ["remove", "1.16.5-server", "--type", "release", "-y", "--no-update"]).ConfigureAwait(false);
        AssertExit(removeType, 2, "type not accepted by remove");
        var installPlatform = await CliRunner.RunAsync(sandbox,
            ["install", "1.16.5@server", "--platform", "linux-x64", "-y", "--no-update"])
            .ConfigureAwait(false);
        AssertExit(installPlatform, 2, "platform not accepted by install");
    }

    [PkgTest("E2E")]
    public static async Task LocalConfigSourceDrivesAutoUpdate(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        var list = await CliRunner.RunAsync(sandbox, ["list", "--limit", "3"],
            sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(list, 0, "list with local config source and auto update");
        AssertEx.Contains("@client", list.StandardOutput, "local config source index used");
    }

    [PkgTest("E2E")]
    public static async Task FailedAutoUpdateFallsBackToCachedIndex(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        var update = await CliRunner.RunAsync(sandbox,
            ["update"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(update, 0, "prime index cache");
        fixture.Server.AddFailure("index.json", MockFailureKind.InternalServerError, int.MaxValue);

        var result = await CliRunner.RunAsync(sandbox,
            ["list", "--limit", "0"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(result, 0, "cached fallback");
        AssertEx.Contains("Index update failed", result.StandardError, "fallback warning");
        AssertEx.Contains("Using cached index", result.StandardError, "cached index notice");
        AssertEx.Contains("@server", result.StandardOutput, "cached data still available");
    }

    private static void AssertExit(TestResult result, int expected, string operation)
    {
        if (result.ExitCode != expected)
        {
            throw new InvalidOperationException(
                $"{operation} exited {result.ExitCode}, expected {expected}.\nSTDOUT:\n{result.StandardOutput}\n" +
                $"STDERR:\n{result.StandardError}");
        }
    }
}
