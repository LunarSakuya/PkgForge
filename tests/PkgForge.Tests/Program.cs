using PkgForge.Tests;

var projectRoot = CliRunner.FindProjectRoot();
var testsRoot = Path.Combine(projectRoot, ".artifacts", "tests");
var filter = string.Empty;
var keep = string.Equals(Environment.GetEnvironmentVariable("PKGFORGE_KEEP_TESTS"), "1", StringComparison.Ordinal);
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--filter" && index + 1 < args.Length)
    {
        filter = args[++index];
    }
    else if (args[index] == "--keep")
    {
        keep = true;
    }
    else
    {
        Console.Error.WriteLine($"Unknown test runner argument '{args[index]}'.");
        return 2;
    }
}

using var context = new TestRunContext(testsRoot, filter, keep);
return await context.RunAsync();
