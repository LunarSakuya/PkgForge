using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PkgForge.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PkgTestAttribute : Attribute
{
    public PkgTestAttribute(string category)
    {
        Category = category;
    }

    public string Category { get; }
}

internal sealed class TestSandbox : IDisposable
{
    public TestSandbox(string root)
    {
        Root = Path.GetFullPath(root);
        WorkingDirectory = Path.Combine(Root, "work");
        WebRoot = Path.Combine(Root, "web");
        FixtureRoot = Path.Combine(Root, "fixtures");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WorkingDirectory);
        Directory.CreateDirectory(WebRoot);
        Directory.CreateDirectory(FixtureRoot);
        Paths = new AppPaths(Root, WorkingDirectory);
        Paths.EnsureGlobalLayout();
        GlobalRoot = Paths.GlobalRoot;
        ManifestsRoot = Paths.ManifestsRoot;
        InstallRoot = Paths.InstallRoot;
    }

    public string Root { get; }

    public string GlobalRoot { get; }

    public string ManifestsRoot { get; }

    public string InstallRoot { get; }

    public string WorkingDirectory { get; }

    public string WebRoot { get; }

    public string FixtureRoot { get; }

    public AppPaths Paths { get; }

    public void Dispose()
    {
    }

}

internal sealed class TestRunContext : IDisposable
{
    private readonly string _testsRoot;
    private int _passed;
    private int _failed;
    private int _skipped;

    public TestRunContext(string testsRoot, string filter, bool keepArtifacts)
    {
        _testsRoot = testsRoot;
        Filter = filter;
        KeepArtifacts = keepArtifacts;
        Directory.CreateDirectory(_testsRoot);
    }

    public string Filter { get; }

    public bool KeepArtifacts { get; }

    public async Task<int> RunAsync()
    {
        var tests = typeof(TestRunContext).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<PkgTestAttribute>()))
            .Where(item => item.Attribute is not null &&
                           IsTest(item.Method) &&
                           (Filter.Length == 0 ||
                            item.Attribute!.Category.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                            item.Method.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Attribute!.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Method.Name, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"Discovered {tests.Count} tests.");
        var runRoot = Path.Combine(_testsRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                                                           Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runRoot);
        var started = Stopwatch.StartNew();
        foreach (var (method, attribute) in tests)
        {
            var sandboxRoot = Path.Combine(runRoot, method.Name + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(sandboxRoot);
            var sandbox = new TestSandbox(sandboxRoot);
            var testWatch = Stopwatch.StartNew();
            try
            {
                if (method.ReturnType == typeof(Task))
                {
                    await ((Task)method.Invoke(null, [sandbox])!).ConfigureAwait(false);
                }
                else
                {
                    await ((ValueTask)method.Invoke(null, [sandbox])!).ConfigureAwait(false);
                }

                _passed++;
                Console.WriteLine($"[PASS] {attribute!.Category,-12} {method.Name} ({testWatch.ElapsedMilliseconds} ms)");
            }
            catch (SkipTestException skip)
            {
                _skipped++;
                Console.WriteLine($"[SKIP] {attribute!.Category,-12} {method.Name}: {skip.Message}");
            }
            catch (Exception exception)
            {
                _failed++;
                var actual = exception is TargetInvocationException { InnerException: not null } invocation
                    ? invocation.InnerException
                    : exception;
                Console.WriteLine($"[FAIL] {attribute!.Category,-12} {method.Name} ({testWatch.ElapsedMilliseconds} ms)");
                Console.WriteLine(actual);
                if (KeepArtifacts)
                {
                    Console.WriteLine($"       artifacts: {sandboxRoot}");
                }
            }
            finally
            {
                sandbox.Dispose();
            }
        }

        started.Stop();
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed, {_skipped} skipped in {started.Elapsed.TotalSeconds:0.0}s");
        if (KeepArtifacts && _failed > 0)
        {
            Console.WriteLine($"Failure artifacts kept under: {runRoot}");
        }

        return _failed == 0 ? 0 : 1;
    }

    private static bool IsTest(MethodInfo method) =>
        method.IsStatic &&
        (method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask)) &&
        method.GetParameters() is [{ ParameterType: var parameterType }] &&
        parameterType == typeof(TestSandbox);

    public void Dispose()
    {
    }
}

internal sealed class TestResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }
}

internal sealed class SkipTestException : Exception
{
    public SkipTestException(string message) : base(message)
    {
    }
}

internal static class CliRunner
{
    private static readonly Lazy<string> CliAssemblyPath = new(FindCliAssembly);

    public static string CliPath => CliAssemblyPath.Value;

    public static async Task<TestResult> RunAsync(TestSandbox sandbox, IReadOnlyList<string> arguments,
        bool redirectInput = true, string? sourceOverride = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : CliPath,
            WorkingDirectory = sandbox.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        if (startInfo.FileName == "dotnet")
        {
            startInfo.ArgumentList.Add(CliPath);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var configPath = Path.Combine(sandbox.Root, LocalSourceConfig.FileName);
        var configJson = "{\n  \"schema\": 1,\n  \"indexSource\": " +
                         JsonSerializer.Serialize(sourceOverride ?? "https://example.invalid/") + "\n}\n";
        File.WriteAllText(configPath, configJson, new UTF8Encoding(false));

        startInfo.Environment["MCPKG_HOME"] = sandbox.Root;
        startInfo.Environment["NO_PROXY"] = "*";
        startInfo.Environment["no_proxy"] = "*";
        startInfo.Environment.Remove("HTTP_PROXY");
        startInfo.Environment.Remove("HTTPS_PROXY");
        startInfo.Environment.Remove("ALL_PROXY");
        startInfo.Environment.Remove("http_proxy");
        startInfo.Environment.Remove("https_proxy");
        startInfo.Environment.Remove("all_proxy");
        startInfo.Environment.Remove("MCPKG_GLOBAL_HOME");
        startInfo.Environment.Remove("MCPKG_USER_HOME");
        startInfo.Environment.Remove("MCPKG_INSTALL_ROOT");
        startInfo.Environment.Remove("MCPKG_INDEX_SOURCE");

        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start the pkgforge CLI.");
        if (redirectInput)
        {
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"CLI command timed out: {string.Join(' ', arguments)}");
        }

        return new TestResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdout.ConfigureAwait(false),
            StandardError = await stderr.ConfigureAwait(false),
        };
    }

    private static string FindCliAssembly()
    {
        var configured = Environment.GetEnvironmentVariable("PKGFORGE_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var projectRoot = FindProjectRoot();
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(projectRoot, "src", "PkgForge.Cli", "bin", configuration, "net10.0",
                "pkgforge.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("pkgforge.dll was not found; build the solution first.");
    }

    public static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PkgForge.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the pkgforge project root.");
    }
}
