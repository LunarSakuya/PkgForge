namespace PkgForge.Tests;

internal static class CliHardeningTests
{
    [PkgTest("E2E")]
    public static async Task CliRejectsEmptyKeywordsAndOptionLikeValues(TestSandbox sandbox)
    {
        var empty = await CliRunner.RunAsync(sandbox, ["search", ""]).ConfigureAwait(false);
        AssertExit(empty, 2, "empty search");
        AssertEx.Contains("keywords must not be empty", empty.StandardError, "empty search error");

        var whitespace = await CliRunner.RunAsync(sandbox, ["search", "   "]).ConfigureAwait(false);
        AssertExit(whitespace, 2, "whitespace search");

        var optionLikeValue = await CliRunner.RunAsync(sandbox,
            ["install", "1.20.1", "--dir", "--force"]).ConfigureAwait(false);
        AssertExit(optionLikeValue, 2, "option-like --dir value");
        AssertEx.Contains("requires a value", optionLikeValue.StandardError, "option-like value error");
    }

    [PkgTest("E2E")]
    public static async Task CliRejectsConflictingProxyOptionsAndOverlongArguments(TestSandbox sandbox)
    {
        var conflict = await CliRunner.RunAsync(sandbox,
            ["update", "--proxy", "http://127.0.0.1:1", "--no-proxy"]).ConfigureAwait(false);
        AssertExit(conflict, 2, "proxy conflict");

        var overlong = new string('a', 8193);
        var result = await CliRunner.RunAsync(sandbox, ["search", overlong]).ConfigureAwait(false);
        AssertExit(result, 2, "overlong argument");
        AssertEx.Contains("character limit", result.StandardError, "overlong argument error");
    }

    [PkgTest("E2E")]
    public static async Task CliRejectsIncompatibleOptionCombinations(TestSandbox sandbox)
    {
        var cases = new (string[] Arguments, string Fragment)[]
        {
            (["list", "--no-update", "--proxy", "http://127.0.0.1:1"], "--proxy/--no-proxy"),
            (["search", "--no-update", "--no-proxy", "keyword"], "--proxy/--no-proxy"),
            (["remove", "dummy", "--no-update", "--proxy", "http://127.0.0.1:1"],
                "--proxy/--no-proxy"),
            (["install", "dummy", "--proxy", "http://127.0.0.1:1", "--no-proxy"],
                "cannot be used together"),
        };

        foreach (var (arguments, fragment) in cases)
        {
            await AssertUsageAsync(sandbox, arguments, string.Join(' ', arguments), fragment)
                .ConfigureAwait(false);
        }
    }

    [PkgTest("E2E")]
    public static async Task CliRejectsMalformedArgumentShapes(TestSandbox sandbox)
    {
        var malformed = new[]
        {
            new[] { "install", "one", "two" },
            new[] { "remove", "one", "two" },
            new[] { "list", "extra" },
            new[] { "search" },
            new[] { "no-such-command" },
            new[] { "installed", "extra" },
            new[] { "--version", "extra" },
            new[] { "help", "install", "extra" },
            new[] { "help", "no-such-command" },
            new[] { "install", "pkg", "--threads", "0" },
            new[] { "install", "pkg", "--threads", "33" },
            new[] { "list", "--limit", "not-a-number" },
            new[] { "list", "--limit", "1", "--limit", "2" },
            new[] { "list", "--kind", "not-a-kind" },
            new[] { "list", "--platform", "../escape" },
            new[] { "list", "--no-update=false" },
            new[] { "search", "keyword", "--limit", "not-a-number" },
            new[] { "install", "pkg", "--dir", "one", "--dir", "two" },
            new[] { "install", "-y", "--yes", "pkg" },
            new[] { "update", "--no-update" },
            new[] { "installed", "--no-update" },
        };

        foreach (var arguments in malformed)
        {
            await AssertUsageAsync(sandbox, arguments, string.Join(' ', arguments))
                .ConfigureAwait(false);
        }
    }

    [PkgTest("E2E")]
    public static async Task CliAcceptsOptionsInEitherOrderAndHelp(TestSandbox sandbox)
    {
        var helpCommands = new[]
        {
            new[] { "install", "--help", "1.20.1" },
            new[] { "install", "1.20.1", "--help" },
            new[] { "list", "--limit", "1", "--help" },
            new[] { "search", "keyword", "--help" },
            new[] { "remove", "id", "--help" },
            new[] { "update", "--help" },
            new[] { "installed", "--help" },
            new[] { "--help", "install" },
            new[] { "help", "install" },
            new[] { "--help" },
        };

        foreach (var arguments in helpCommands)
        {
            var result = await CliRunner.RunAsync(sandbox, arguments).ConfigureAwait(false);
            AssertExit(result, 0, string.Join(' ', arguments));
        }

        var version = await CliRunner.RunAsync(sandbox, ["--version"]).ConfigureAwait(false);
        AssertExit(version, 0, "--version");
        AssertEx.Contains("pkgforge ", version.StandardOutput, "version output");
        AssertEx.False(version.StandardOutput.Contains("[HINT]", StringComparison.Ordinal),
            "successful help/version must not print a hint");
    }

    private static async Task AssertUsageAsync(TestSandbox sandbox, string[] arguments, string operation,
        string? expectedFragment = null)
    {
        var result = await CliRunner.RunAsync(sandbox, arguments).ConfigureAwait(false);
        AssertExit(result, 2, operation);
        AssertEx.Contains("[HINT]", result.StandardError, $"{operation} generic hint");
        if (expectedFragment is not null)
        {
            AssertEx.Contains(expectedFragment, result.StandardError, $"{operation} error fragment");
        }
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
