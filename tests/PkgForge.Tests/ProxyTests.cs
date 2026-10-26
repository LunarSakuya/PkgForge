namespace PkgForge.Tests;

internal static class ProxyTests
{
    [PkgTest("Proxy")]
    public static Task EnvironmentProxyAndNoProxyResolution(TestSandbox sandbox)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HTTPS_PROXY"] = "http://https-proxy.example:8080",
            ["HTTP_PROXY"] = "http://http-proxy.example:8081",
            ["ALL_PROXY"] = "http://all-proxy.example:8082",
            ["NO_PROXY"] = ".example.com,127.0.0.1:1234",
        };
        var configuration = ProxyConfiguration.Resolve(null, false, NullLog.Instance, env,
            detectSystemProxy: false);
        AssertEx.True(configuration.UseProxy, "environment proxy enabled");
        AssertEx.Equal("http://https-proxy.example:8080/",
            configuration.Proxy!.GetProxy(new Uri("https://other.test/x"))!.AbsoluteUri,
            "HTTPS proxy selected");
        AssertEx.Equal("http://http-proxy.example:8081/",
            configuration.Proxy.GetProxy(new Uri("http://other.test/x"))!.AbsoluteUri,
            "HTTP proxy selected");
        AssertEx.True(configuration.Proxy.IsBypassed(new Uri("https://www.example.com/x")),
            "NO_PROXY suffix bypass");
        AssertEx.True(configuration.Proxy.IsBypassed(new Uri("https://example.com/x")),
            "NO_PROXY suffix also matches apex");
        AssertEx.Equal("http://https-proxy.example:8080/",
            configuration.Proxy.GetProxy(new Uri("https://www.not-example.com/x"))!.AbsoluteUri,
            "non-matching host proxied");
        AssertEx.True(configuration.Proxy.IsBypassed(new Uri("http://127.0.0.1:1234/x")),
            "NO_PROXY port bypass");
        AssertEx.Equal("http://http-proxy.example:8081/",
            configuration.Proxy.GetProxy(new Uri("http://127.0.0.1:1235/x"))!.AbsoluteUri,
            "NO_PROXY port mismatch not bypassed");

        var lowerCaseWins = ProxyConfiguration.Resolve(null, false, NullLog.Instance,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["https_proxy"] = "http://lower.example:9000",
                ["HTTPS_PROXY"] = "http://upper.example:9001",
            }, detectSystemProxy: false);
        AssertEx.Equal("http://lower.example:9000/",
            lowerCaseWins.Proxy!.GetProxy(new Uri("https://target.test/"))!.AbsoluteUri,
            "lowercase proxy wins");

        var allOnly = ProxyConfiguration.Resolve(null, false, NullLog.Instance,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ALL_PROXY"] = "socks5://proxy.example:1080",
            }, detectSystemProxy: false);
        AssertEx.Equal("socks5://proxy.example:1080/",
            allOnly.Proxy!.GetProxy(new Uri("https://target.test/"))!.AbsoluteUri,
            "ALL_PROXY fallback supports SOCKS5");

        var bypassAll = ProxyConfiguration.Resolve(null, false, NullLog.Instance,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HTTPS_PROXY"] = "http://proxy.example:8080",
                ["NO_PROXY"] = "*",
            }, detectSystemProxy: false);
        AssertEx.True(bypassAll.Proxy!.IsBypassed(new Uri("https://target.test/")),
            "NO_PROXY=* bypasses");

        var direct = ProxyConfiguration.Resolve(null, true, NullLog.Instance, env,
            detectSystemProxy: false);
        AssertEx.False(direct.UseProxy, "no-proxy wins");
        AssertEx.Contains("no-proxy", direct.Description, "no-proxy description");
        var conflict = AssertEx.Throws<PkgException>(() =>
            ProxyConfiguration.Resolve("http://proxy.example:8080", true, NullLog.Instance, env,
                detectSystemProxy: false), "proxy and no-proxy conflict must fail");
        AssertEx.Equal(ErrorCodes.InvalidProxy, conflict.Code, "proxy conflict error code");

        var invalid = AssertEx.Throws<PkgException>(() => ProxyConfiguration.Resolve(null, false,
            NullLog.Instance, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HTTP_PROXY"] = "not a proxy",
            }, detectSystemProxy: false), "invalid environment proxy must fail");
        AssertEx.Equal(ErrorCodes.InvalidProxy, invalid.Code, "invalid proxy error code");
        return Task.CompletedTask;
    }

    [PkgTest("Proxy")]
    public static Task ProxyCredentialsAreRedacted(TestSandbox sandbox)
    {
        var configuration = ProxyConfiguration.Resolve(
            "http://alice:super-secret@127.0.0.1:8080", false, NullLog.Instance,
            new Dictionary<string, string?>(StringComparer.Ordinal), detectSystemProxy: false);
        AssertEx.False(configuration.Description.Contains("alice", StringComparison.Ordinal),
            "proxy username redacted");
        AssertEx.False(configuration.Description.Contains("super-secret", StringComparison.Ordinal),
            "proxy password redacted");
        AssertEx.Contains("auth=yes", configuration.Description, "auth state shown without credentials");
        var redacted = SecureText.RedactSecrets("failed via http://alice:super-secret@proxy:8080/");
        AssertEx.False(redacted.Contains("super-secret", StringComparison.Ordinal),
            "exception message redaction");
        return Task.CompletedTask;
    }

    [PkgTest("Proxy")]
    public static async Task HttpDownloadUsesAuthenticatedProxyAndNoProxyBypass(TestSandbox sandbox)
    {
        var content = "proxied-content";
        var contentPath = Path.Combine(sandbox.WebRoot, "resource.txt");
        await File.WriteAllTextAsync(contentPath, content).ConfigureAwait(false);
        await using var backend = new MockHttpServer(sandbox.WebRoot);
        await using var proxy = new MockHttpProxy("alice", "secret");
        var proxyUrl = proxy.BaseUrl.Replace("http://", "http://alice:secret@", StringComparison.Ordinal);
        var configuration = ProxyConfiguration.Resolve(proxyUrl, false, NullLog.Instance,
            new Dictionary<string, string?>(StringComparer.Ordinal), detectSystemProxy: false);

        using (var downloader = new HttpDownloader(NullLog.Instance, configuration))
        {
            var bytes = await downloader.FetchBytesAsync(new Uri(backend.BaseUrl + "resource.txt"),
                1024, "proxied fetch", CancellationToken.None).ConfigureAwait(false);
            AssertEx.Equal(content, System.Text.Encoding.UTF8.GetString(bytes), "proxied content");
        }

        AssertEx.True(proxy.RequestCount >= 1, "proxy received the request");

        var noProxyConfiguration = ProxyConfiguration.Resolve(null, false, NullLog.Instance,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HTTP_PROXY"] = proxy.BaseUrl,
                ["NO_PROXY"] = "127.0.0.1",
            }, detectSystemProxy: false);
        var beforeBypass = proxy.RequestCount;
        using (var directDownloader = new HttpDownloader(NullLog.Instance, noProxyConfiguration))
        {
            var bytes = await directDownloader.FetchBytesAsync(new Uri(backend.BaseUrl + "resource.txt"),
                1024, "bypassed fetch", CancellationToken.None).ConfigureAwait(false);
            AssertEx.Equal(content, System.Text.Encoding.UTF8.GetString(bytes), "bypassed content");
        }

        AssertEx.Equal(beforeBypass, proxy.RequestCount, "NO_PROXY bypasses the proxy");
        await AssertEx.ThrowsAsync<PkgException>(
            async () =>
            {
                using var bad = new HttpDownloader(NullLog.Instance,
                    ProxyConfiguration.Resolve(
                        proxy.BaseUrl.Replace("http://", "http://alice:wrong@", StringComparison.Ordinal),
                        false, NullLog.Instance, new Dictionary<string, string?>(StringComparer.Ordinal),
                        detectSystemProxy: false));
                _ = await bad.FetchBytesAsync(new Uri(backend.BaseUrl + "resource.txt"), 1024,
                    "bad proxy auth", CancellationToken.None).ConfigureAwait(false);
            }, "wrong proxy credentials must fail").ConfigureAwait(false);
    }

    [PkgTest("Proxy")]
    public static async Task ProxyAuthFailureLeavesNoCacheState(TestSandbox sandbox)
    {
        var content = "must-not-cache";
        var contentPath = Path.Combine(sandbox.WebRoot, "resource.txt");
        await File.WriteAllTextAsync(contentPath, content).ConfigureAwait(false);
        await using var backend = new MockHttpServer(sandbox.WebRoot);
        await using var proxy = new MockHttpProxy("alice", "secret");
        var wrongProxy = proxy.BaseUrl.Replace("http://", "http://alice:wrong@", StringComparison.Ordinal);
        var configuration = ProxyConfiguration.Resolve(wrongProxy, false, NullLog.Instance,
            new Dictionary<string, string?>(StringComparer.Ordinal), detectSystemProxy: false);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes));
        using var downloader = new HttpDownloader(NullLog.Instance, configuration);
        var cache = new ContentCache(sandbox.Paths, downloader, NullLog.Instance);
        var item = new DownloadItem("resource.txt", sha1, bytes.Length, "asset",
            [new Uri(backend.BaseUrl + "resource.txt")]);
        await AssertEx.ThrowsAsync<PkgException>(
            () => cache.GetOrDownloadAsync(item, CancellationToken.None),
            "proxy authentication failure must fail download").ConfigureAwait(false);
        AssertEx.FileDoesNotExist(sandbox.Paths.CacheObjectPath(sha1), "no cache object after proxy failure");
        var temporaryFiles = Directory.Exists(sandbox.Paths.CacheTemporary)
            ? Directory.EnumerateFiles(sandbox.Paths.CacheTemporary, "*.part", SearchOption.TopDirectoryOnly).ToArray()
            : [];
        AssertEx.Equal(0, temporaryFiles.Length, "no partial files after proxy failure");
    }

    [PkgTest("Proxy")]
    public static async Task HttpDownloadFollowsRedirects(TestSandbox sandbox)
    {
        const string content = "redirected-content";
        await File.WriteAllTextAsync(Path.Combine(sandbox.WebRoot, "target.txt"), content).ConfigureAwait(false);
        await using var server = new MockHttpServer(sandbox.WebRoot);
        server.AddRedirect("start.txt", "/target.txt");
        using var downloader = new HttpDownloader(NullLog.Instance);
        var bytes = await downloader.FetchBytesAsync(new Uri(server.BaseUrl + "start.txt"), 1024,
            "redirected fetch", CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(content, System.Text.Encoding.UTF8.GetString(bytes), "redirect target content");
        AssertEx.True(server.Count("target.txt") >= 1, "redirect target was requested");
    }

    [PkgTest("E2E")]
    public static async Task CliEnvironmentProxyAndNoProxyFlag(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        await using var proxy = new MockHttpProxy();
        var proxied = await CliRunner.RunAsync(sandbox,
            ["update"],
            sourceOverride: fixture.SourceUrl,
            environmentOverrides: new Dictionary<string, string?>
            {
                ["HTTP_PROXY"] = proxy.BaseUrl,
                ["HTTPS_PROXY"] = null,
                ["ALL_PROXY"] = null,
                ["NO_PROXY"] = null,
                ["no_proxy"] = null,
            }).ConfigureAwait(false);
        AssertExit(proxied, 0, "environment proxy update");
        AssertEx.True(proxy.RequestCount >= 1, "environment proxy routed update");

        var direct = await CliRunner.RunAsync(sandbox,
            ["update", "--no-proxy"],
            sourceOverride: fixture.SourceUrl,
            environmentOverrides: new Dictionary<string, string?>
            {
                ["HTTP_PROXY"] = "http://127.0.0.1:1",
                ["NO_PROXY"] = null,
                ["no_proxy"] = null,
            }).ConfigureAwait(false);
        AssertExit(direct, 0, "no-proxy overrides environment");
    }

    [PkgTest("E2E")]
    public static async Task CliProxyFlagAndInvalidProxy(TestSandbox sandbox)
    {
        await using var fixture = await TestIndexFixture.CreateAsync(sandbox).ConfigureAwait(false);
        await using var proxy = new MockHttpProxy("alice", "super-secret");
        var proxyUrl = proxy.BaseUrl.Replace("http://", "http://alice:super-secret@", StringComparison.Ordinal);
        var update = await CliRunner.RunAsync(sandbox,
            ["update", "--proxy", proxyUrl],
            sourceOverride: fixture.SourceUrl,
            environmentOverrides: new Dictionary<string, string?>
            {
                ["NO_PROXY"] = null,
                ["no_proxy"] = null,
            }).ConfigureAwait(false);
        AssertExit(update, 0, "CLI proxy update");
        AssertEx.True(proxy.RequestCount >= 1, "CLI routed update through proxy");
        AssertEx.False(update.StandardError.Contains("super-secret", StringComparison.Ordinal),
            "CLI logs proxy password redacted");
        AssertEx.False(update.StandardError.Contains("alice", StringComparison.Ordinal),
            "CLI logs proxy username redacted");

        var invalid = await CliRunner.RunAsync(sandbox,
            ["update", "--proxy", "not-a-proxy"], sourceOverride: fixture.SourceUrl).ConfigureAwait(false);
        AssertExit(invalid, 2, "CLI invalid proxy");
        AssertEx.Contains(ErrorCodes.InvalidProxy, invalid.StandardError, "invalid proxy error code");
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
