using System.Globalization;
using System.Text;

namespace PkgForge.Cli;

internal static class CliApplication
{
    private static readonly HashSet<string> UpdateFlags = new(StringComparer.Ordinal)
        { "help", "no-proxy" };
    private static readonly HashSet<string> UpdateValues = new(StringComparer.Ordinal)
        { "proxy" };
    private static readonly HashSet<string> ListFlags = new(StringComparer.Ordinal)
        { "help", "no-update", "no-proxy" };
    private static readonly HashSet<string> ListValues = new(StringComparer.Ordinal)
        { "kind", "platform", "type", "limit", "proxy" };
    private static readonly HashSet<string> SearchValues = new(StringComparer.Ordinal)
        { "kind", "platform", "type", "limit", "proxy" };
    private static readonly HashSet<string> InstallFlags = new(StringComparer.Ordinal)
        { "help", "no-update", "yes", "force", "no-proxy", "unmanaged", "download-only" };
    private static readonly HashSet<string> InstallValues = new(StringComparer.Ordinal)
        { "dir", "threads", "proxy" };
    private static readonly HashSet<string> RemoveFlags = new(StringComparer.Ordinal)
        { "help", "no-update", "yes", "force", "no-proxy" };
    private static readonly HashSet<string> RemoveValues = new(StringComparer.Ordinal)
        { "proxy" };
    private static readonly HashSet<string> InstalledFlags = new(StringComparer.Ordinal) { "help" };
    private static readonly HashSet<string> InstalledValues = new(StringComparer.Ordinal)
        { "kind", "platform", "type" };
    private static readonly HashSet<string> KnownCommands = new(StringComparer.Ordinal)
    {
        "update", "list", "search", "install", "remove", "installed",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ConfigureConsole();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await DispatchAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            PrintError(ErrorCodes.Cancelled, "operation cancelled.");
            return (int)ExitCode.Cancelled;
        }
        catch (PkgException exception)
        {
            PrintError(exception.Code, exception.Message);
            return (int)exception.ExitCode;
        }
        catch (Exception exception)
        {
            var mapped = FileSystemErrors.Map(exception, "command");
            PrintError(mapped.Code, mapped.Message);
            return (int)mapped.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task<int> DispatchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return (int)ExitCode.Usage;
        }

        if (args[0] is "--version" or "-v" or "version")
        {
            if (args.Length != 1)
            {
                throw PkgException.Usage("version takes no arguments.");
            }

            Console.Out.WriteLine($"pkgforge {BuildConfig.ProductVersion}");
            return (int)ExitCode.Success;
        }

        if (args[0] is "--help" or "-h" or "help")
        {
            if (args.Length > 2)
            {
                throw PkgException.Usage("help accepts at most one command name.");
            }

            if (args.Length == 2)
            {
                var helpCommand = args[1].ToLowerInvariant();
                if (!KnownCommands.Contains(helpCommand))
                {
                    throw PkgException.Usage($"Unknown command '{args[1]}'.");
                }

                PrintCommandHelp(helpCommand);
            }
            else
            {
                PrintHelp();
            }

            return (int)ExitCode.Success;
        }

        var command = args[0].ToLowerInvariant();
        var tail = args.Skip(1).ToArray();
        await using var runtime = new CommandRuntime(new ConsoleLog());
        switch (command)
        {
            case "update":
                return await RunUpdateAsync(runtime, tail, cancellationToken).ConfigureAwait(false);
            case "list":
                return await RunListAsync(runtime, tail, cancellationToken).ConfigureAwait(false);
            case "search":
                return await RunSearchAsync(runtime, tail, cancellationToken).ConfigureAwait(false);
            case "install":
                return await RunInstallAsync(runtime, tail, cancellationToken).ConfigureAwait(false);
            case "remove":
                return await RunRemoveAsync(runtime, tail, cancellationToken).ConfigureAwait(false);
            case "installed":
                return await RunInstalledAsync(runtime, tail, cancellationToken).ConfigureAwait(false);
            default:
                throw PkgException.Usage($"Unknown command '{args[0]}'. Run 'pkgforge help'.");
        }
    }

    private static async Task<int> RunUpdateAsync(CommandRuntime runtime, string[] args,
        CancellationToken cancellationToken)
    {
        var parsed = ParsedCommandLine.Parse(args, UpdateValues, UpdateFlags, ParsedCommandLine.CommonAliases);
        if (parsed.Flag("help"))
        {
            PrintCommandHelp("update");
            return (int)ExitCode.Success;
        }

        EnsureNoPositionals(parsed, "update");
        runtime.ConfigureNetwork(parsed.Value("proxy"), parsed.Flag("no-proxy"));
        var source = LocalSourceConfig.Load(runtime.Paths.ProgramRoot);
        runtime.Log.Info($"Source: {source.Display} ({LocalSourceConfig.FileName})");
        var result = await runtime.Updater.UpdateAsync(source, cancellationToken).ConfigureAwait(false);
        var manifest = result.Index.Manifest;
        runtime.Log.Data($"Index source : {result.Index.Source.Display}");
        runtime.Log.Data($"Bundle       : {result.Index.BundleId}");
        runtime.Log.Data($"Versions     : {manifest.Counts.Versions}");
        runtime.Log.Data($"Client plans : {manifest.Counts.ClientPlans}");
        runtime.Log.Data($"Server plans : {manifest.Counts.ServerPlans}");
        runtime.Log.Data($"Asset docs   : {manifest.Counts.AssetDocs}");
        runtime.Log.Data($"Shards       : {string.Join(", ", manifest.Shards.Select(shard =>
            $"{shard.Id} ({SizeFormatter.Format(shard.Size)})"))}");
        runtime.Log.Info(result.Changed ? "Index update committed." : "Index was already current.");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunListAsync(CommandRuntime runtime, string[] args,
        CancellationToken cancellationToken)
    {
        var parsed = ParsedCommandLine.Parse(args, ListValues, ListFlags, ParsedCommandLine.CommonAliases);
        if (parsed.Flag("help"))
        {
            PrintCommandHelp("list");
            return (int)ExitCode.Success;
        }

        EnsureNoPositionals(parsed, "list");
        RejectProxyWithNoUpdate(parsed, "list");
        var kind = ParseKind(parsed.Value("kind"));
        var platform = ParsePlatform(parsed.Value("platform"));
        var versionType = ParseVersionType(parsed.Value("type"));
        var limit = ParseLimit(parsed.Value("limit"), 0);
        if (!parsed.Flag("no-update"))
        {
            runtime.ConfigureNetwork(parsed.Value("proxy"), parsed.Flag("no-proxy"));
        }

        var index = await runtime.OpenIndexAsync(parsed, cancellationToken).ConfigureAwait(false);
        var database = new IndexDatabase(index);
        if (versionType is not null)
        {
            database.EnsureVersionTypeExists(versionType, cancellationToken);
        }

        var result = await database.ListPackagesAsync(kind, platform, versionType, null, limit, cancellationToken)
            .ConfigureAwait(false);
        PrintPackages(runtime.Log, result, limit);
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunSearchAsync(CommandRuntime runtime, string[] args,
        CancellationToken cancellationToken)
    {
        var parsed = ParsedCommandLine.Parse(args, ListValues, ListFlags, ParsedCommandLine.CommonAliases);
        if (parsed.Flag("help"))
        {
            PrintCommandHelp("search");
            return (int)ExitCode.Success;
        }

        if (parsed.Positionals.Count == 0)
        {
            throw PkgException.Usage("search requires one or more keywords.");
        }

        if (parsed.Positionals.Any(string.IsNullOrWhiteSpace))
        {
            throw PkgException.Usage("search keywords must not be empty or whitespace-only.");
        }

        var query = string.Join(' ', parsed.Positionals);
        if (query.Length > 4096)
        {
            throw PkgException.Usage("search query exceeds the 4096 character limit.");
        }

        RejectProxyWithNoUpdate(parsed, "search");
        var kind = ParseKind(parsed.Value("kind"));
        var platform = ParsePlatform(parsed.Value("platform"));
        var versionType = ParseVersionType(parsed.Value("type"));
        var limit = ParseLimit(parsed.Value("limit"), 100);
        if (!parsed.Flag("no-update"))
        {
            runtime.ConfigureNetwork(parsed.Value("proxy"), parsed.Flag("no-proxy"));
        }

        var index = await runtime.OpenIndexAsync(parsed, cancellationToken).ConfigureAwait(false);
        var database = new IndexDatabase(index);
        if (versionType is not null)
        {
            database.EnsureVersionTypeExists(versionType, cancellationToken);
        }

        var result = await database.ListPackagesAsync(kind, platform, versionType, query, limit, cancellationToken)
            .ConfigureAwait(false);
        runtime.Log.Info($"Search '{query}': {result.TotalMatched} matching plan(s).");
        PrintPackages(runtime.Log, result, limit);
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunInstallAsync(CommandRuntime runtime, string[] args,
        CancellationToken cancellationToken)
    {
        var parsed = ParsedCommandLine.Parse(args, InstallValues, InstallFlags,
            ParsedCommandLine.CommonAliases);
        if (parsed.Flag("help"))
        {
            PrintCommandHelp("install");
            return (int)ExitCode.Success;
        }

        if (parsed.Positionals.Count != 1)
        {
            throw PkgException.Usage("install requires exactly one package name.");
        }

        var threads = ParseThreads(parsed.Value("threads"));
        var selector = PackageSelector.Parse(parsed.Positionals[0], PackageNaming.DetectCurrentPlatform);
        runtime.ConfigureNetwork(parsed.Value("proxy"), parsed.Flag("no-proxy"));
        var index = await runtime.OpenIndexAsync(parsed, cancellationToken).ConfigureAwait(false);
        var database = new IndexDatabase(index);
        var cache = new ContentCache(runtime.Paths, runtime.Downloader, runtime.Log);
        cache.CleanStaleTemporaryFiles();
        var manifests = new InstallManifestStore(runtime.Paths, runtime.Log);
        var confirmation = new ConsoleConfirmation(parsed.Flag("yes"));
        var service = new InstallService(runtime.Paths, index, index.Source, database, cache, manifests,
            confirmation, runtime.Log);
        var result = await service.InstallAsync(new InstallRequest(selector, parsed.Value("dir"),
            parsed.Flag("yes"), parsed.Flag("force"), threads,
            parsed.Flag("unmanaged") || parsed.Flag("download-only")), cancellationToken)
            .ConfigureAwait(false);
        runtime.Log.Data($"Installed : {result.InstallId}");
        runtime.Log.Data($"Package   : {result.PackageId}");
        runtime.Log.Data($"Directory : {result.Target}");
        runtime.Log.Data($"Files     : {result.FileCount}");
        runtime.Log.Data($"Size      : {SizeFormatter.Format(result.TotalBytes)}");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunRemoveAsync(CommandRuntime runtime, string[] args,
        CancellationToken cancellationToken)
    {
        var parsed = ParsedCommandLine.Parse(args, RemoveValues, RemoveFlags,
            ParsedCommandLine.CommonAliases);
        if (parsed.Flag("help"))
        {
            PrintCommandHelp("remove");
            return (int)ExitCode.Success;
        }

        if (parsed.Positionals.Count != 1)
        {
            throw PkgException.Usage("remove requires exactly one install id or package id.");
        }

        RejectProxyWithNoUpdate(parsed, "remove");
        if (!parsed.Flag("no-update"))
        {
            runtime.ConfigureNetwork(parsed.Value("proxy"), parsed.Flag("no-proxy"));
            await runtime.TryRefreshIndexForLocalCommandAsync(cancellationToken).ConfigureAwait(false);
        }

        var manifests = new InstallManifestStore(runtime.Paths, runtime.Log);
        var service = new RemovalService(runtime.Paths, manifests,
            new ConsoleConfirmation(parsed.Flag("yes")), runtime.Log);
        var result = await service.RemoveAsync(new RemoveRequest(parsed.Positionals[0],
            parsed.Flag("yes"), parsed.Flag("force")), cancellationToken).ConfigureAwait(false);
        runtime.Log.Data($"Removed   : {result.Manifest.InstallId}");
        runtime.Log.Data($"Package   : {result.Manifest.PackageId}");
        runtime.Log.Data($"Files     : {result.FileCount}");
        runtime.Log.Data($"Size      : {SizeFormatter.Format(result.TotalBytes)}");
        return (int)ExitCode.Success;
    }

    private static async Task<int> RunInstalledAsync(CommandRuntime runtime, string[] args,
        CancellationToken cancellationToken)
    {
        var parsed = ParsedCommandLine.Parse(args, InstalledValues, InstalledFlags,
            ParsedCommandLine.CommonAliases);
        if (parsed.Flag("help"))
        {
            PrintCommandHelp("installed");
            return (int)ExitCode.Success;
        }

        EnsureNoPositionals(parsed, "installed");
        var kind = ParseKind(parsed.Value("kind"));
        var platform = ParsePlatform(parsed.Value("platform"));
        var versionType = ParseVersionType(parsed.Value("type"));
        var manifests = new InstallManifestStore(runtime.Paths, runtime.Log);
        var allRecords = await manifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var records = allRecords;
        if (kind is not null)
        {
            records = records.Where(record =>
                PackageKinds.Parse(record.Kind) == kind).ToList();
        }

        if (platform is not null)
        {
            records = records.Where(record =>
                string.Equals(NormalizeUnknown(record.Platform), platform, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (versionType is not null)
        {
            records = records.Where(record =>
                string.Equals(NormalizeUnknown(record.VersionType), versionType,
                    StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (records.Count == 0)
        {
            Console.Out.WriteLine(allRecords.Count == 0
                ? "No installations are recorded for this program root."
                : "No installations match the specified filters.");
            return (int)ExitCode.Success;
        }

        Console.Out.WriteLine($"{"INSTALL ID",-40} {"PACKAGE",-38} {"KIND",-7} {"PLATFORM",-12} " +
                              $"{"TYPE",-10} {"FILES",8} {"SIZE",10} {"STATE",-8} RELATIVE TARGET");
        foreach (var record in records.OrderBy(item => item.InstallId, StringComparer.Ordinal))
        {
            var target = runtime.Paths.ResolveManagedInstall(record.InstallDirectory,
                $"installation record '{record.InstallId}'");
            var state = Directory.Exists(target) ? "present" : "missing";
            Console.Out.WriteLine(
                $"{record.InstallId,-40} {record.PackageId,-38} {record.Kind,-7} " +
                $"{NormalizeUnknown(record.Platform),-12} {NormalizeUnknown(record.VersionType),-10} " +
                $"{record.FileCount,8} {SizeFormatter.Format(record.TotalBytes),10} {state,-8} " +
                $"{record.InstallDirectory}");
        }

        return (int)ExitCode.Success;
    }

    private static void PrintPackages(ILog log, PackageListResult result, int requestedLimit)
    {
        if (result.Rows.Count == 0)
        {
            Console.Out.WriteLine("No matching packages.");
            return;
        }

        var packageWidth = Math.Clamp(result.Rows.Max(row => row.PackageId.Length), 24, 64);
        Console.Out.WriteLine(
            $"{"PACKAGE".PadRight(packageWidth)} {"VERSION",-18} {"TYPE",-12} {"KIND",-7} " +
            $"{"PLATFORM",-14} {"FILES",7} {"ASSETS",7} {"SIZE",10}");
        foreach (var row in result.Rows)
        {
            Console.Out.WriteLine(
                $"{row.PackageId.PadRight(packageWidth)} {row.VersionKey,-18} {row.VersionType,-12} " +
                $"{row.Kind.ToToken(),-7} {row.Platform,-14} " +
                $"{row.FileCount,7} {row.AssetCount,7} {SizeFormatter.Format(row.TotalBytes),10}");
        }

        if (result.TotalMatched > result.Rows.Count)
        {
            log.Info($"Showing {result.Rows.Count} of {result.TotalMatched} matching packages. " +
                     "Use --limit 0 for all results.");
        }
        else if (requestedLimit > 0)
        {
            log.Info($"{result.Rows.Count} package plan(s).");
        }
    }

    private static PackageKind? ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return PackageKinds.Parse(value);
    }

    private static string? ParsePlatform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            throw PkgException.Usage(
                "--platform any is not supported; omit --platform or use --platform unknown.");
        }

        if (!PackageNaming.IsSafePlatformToken(value))
        {
            throw PkgException.Usage($"Invalid platform token '{value}'.");
        }

        return value;
    }

    private static string NormalizeUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static string? ParseVersionType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!PackageNaming.IsSafePlatformToken(value))
        {
            throw PkgException.Usage($"Invalid version type token '{value}'.");
        }

        return value.ToLowerInvariant();
    }

    private static int ParseLimit(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 0)
        {
            throw PkgException.Usage("--limit must be a non-negative integer.");
        }

        return limit;
    }

    private static int ParseThreads(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 8;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var threads) ||
            threads is < 1 or > 32)
        {
            throw PkgException.Usage("--threads must be an integer between 1 and 32.");
        }

        return threads;
    }

    private static void EnsureNoPositionals(ParsedCommandLine parsed, string command)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw PkgException.Usage($"{command} does not accept positional arguments.");
        }
    }

    private static void RejectProxyWithNoUpdate(ParsedCommandLine parsed, string command)
    {
        if (parsed.Flag("no-update") &&
            (parsed.Value("proxy") is not null || parsed.Flag("no-proxy")))
        {
            throw PkgException.Usage(
                $"{command}: --proxy/--no-proxy cannot be combined with --no-update because " +
                "this command makes no network request; remove one of them.");
        }
    }

    private static void PrintError(string code, string message)
    {
        Console.Error.WriteLine($"[ERROR] {code}: {message}");
        Console.Error.WriteLine($"[HINT] {ErrorHints.For(code)}");
    }

    private static void ConfigureConsole()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
        }
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("""
                              pkgforge - isolated Minecraft package provisioner

                              Usage:
                                pkgforge <command> [options]

                              Commands:
                                update      Refresh the global SQLite index atomically.
                                list        List client and server packages from the active index.
                                search      Search package plans by keyword.
                                install     Download and atomically install one package plan.
                                remove      Remove one recorded installation folder safely.
                                installed   List this program root's installation records.
                                help        Show this help or a command's help.
                                --version   Print the version.

                              Global behavior:
                                - Source is read only from pkgforge.config.json beside pkgforge.exe.
                                - There is no user-level, environment, or command-line source override.
                                - Every list/search/install/remove operation first tries to refresh the index.
                                - Use --no-update to use the cached index while offline.
                                - Use --proxy URL or --no-proxy on network commands.
                                - No TUI, no launch commands, no game upgrade concept.

                              Examples:
                                pkgforge update
                                pkgforge update --proxy http://127.0.0.1:7890
                                pkgforge update --no-proxy
                                pkgforge list --limit 20
                                pkgforge search 1.20 server
                                pkgforge install 1.20.1 -y
                                pkgforge install 1.20.1 --dir data/instances/my-client -y
                                pkgforge install 1.20.1 --dir "D:\Exports\MC 1.20.1" --unmanaged -y
                                pkgforge remove 1.20.1-client-windows-x64 -y

                              Run 'pkgforge help <command>' for command-specific details.
                              """);
    }

    private static void PrintCommandHelp(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "update":
                Console.Out.WriteLine("""
                                      pkgforge update [--proxy URL | --no-proxy]

                                      Downloads /index.json and the catalog.idx, launch.idx and assets.idx
                                      shards into the global data root. Each shard is verified by size,
                                      SHA-256, SQLite application_id/user_version, integrity and counts
                                      before the bundle pointer is replaced atomically.
                                      """);
                break;
            case "list":
                Console.Out.WriteLine("""
                                      pkgforge list [--kind all|client|server] [--platform TOKEN]
                                                    [--type all|release|snapshot|unknown] [--limit N]
                                                    [--no-update]
                                                    [--proxy URL | --no-proxy]

                                      Lists every package plan, server and client, newest first.
                                      Empty platform/type fields display as 'unknown'.
                                      N=0 (the default) lists all rows. Sizes use B/KB/MB/GB.
                                      """);
                break;
            case "search":
                Console.Out.WriteLine("""
                                      pkgforge search <keywords...> [--kind all|client|server]
                                                      [--platform TOKEN]
                                                      [--type all|release|snapshot|unknown] [--limit N]
                                                      [--no-update]
                                                      [--proxy URL | --no-proxy]

                                      Case-insensitive, all-keyword match over version, type, kind,
                                      platform and asset index. Both server and client plans are returned.
                                      Empty platform/type fields display as 'unknown'.
                                      The default limit is 100; use --limit 0 for all matches.
                                      """);
                break;
            case "install":
                Console.Out.WriteLine("""
                                      pkgforge install <package> [--dir DIR] [--threads 1..32]
                                                       [-y|--yes] [--force] [--no-update]
                                                       [--unmanaged|--download-only]
                                                       [--proxy URL | --no-proxy]

                                      Package forms:
                                        <version>                 client, current platform
                                        <version>@client          client, current platform
                                        <version>@client:TOKEN    explicit client platform
                                        <version>@server          server plan

                                      Installation is always fully isolated in its own directory.
                                      Without --dir, the managed target is data/instances/<install-id>.
                                      A managed --dir must be a relative path under data/instances.
                                      --unmanaged/--download-only requires --dir and writes outside the
                                      manifest; the target is not reported by 'installed' and cannot be
                                      removed by pkgforge.
                                      An existing non-empty managed target requires --force and a valid marker.
                                      """);
                break;
            case "remove":
                Console.Out.WriteLine("""
                                      pkgforge remove <install-id|package-id> [-y|--yes] [--force]
                                                      [--no-update]
                                                      [--proxy URL | --no-proxy]

                                      The program-root manifest, install-directory marker, managed root
                                      and recorded file list are all validated before anything is renamed.
                                      Missing records/markers and path escapes fail closed.
                                      --force is required when the folder contains extra, missing or
                                      modified files; the entire validated folder is then removed.
                                      """);
                break;
            case "installed":
                Console.Out.WriteLine("""
                                      pkgforge installed [--kind all|client|server]
                                                         [--platform TOKEN]
                                                         [--type all|release|snapshot|unknown]

                                      Lists installation records in the program-root data/manifests
                                      directory. The same records are visible to every process using
                                      this portable program root.
                                      """);
                break;
            default:
                PrintHelp();
                break;
        }
    }

    private sealed class CommandRuntime : IAsyncDisposable
    {
        private HttpDownloader? _downloader;
        private IndexUpdater? _updater;
        private bool _networkConfigured;

        public CommandRuntime(ILog log)
        {
            Log = log;
            Paths = AppPaths.FromEnvironment();
            Log.Info($"Global data: {Paths.GlobalRoot} ({Paths.GlobalRootKind})");
            Store = new IndexStore(Paths, log);
        }

        public ILog Log { get; }

        public AppPaths Paths { get; }

        public ProxyConfiguration Proxy { get; private set; } = ProxyConfiguration.Direct;

        public IndexStore Store { get; }

        public IndexUpdater Updater =>
            _updater ?? throw new InvalidOperationException("Network subsystem is not configured.");

        public HttpDownloader Downloader =>
            _downloader ?? throw new InvalidOperationException("Network subsystem is not configured.");

        public void ConfigureNetwork(string? proxyValue, bool noProxy)
        {
            if (_networkConfigured)
            {
                throw new InvalidOperationException("Network subsystem was already configured.");
            }

            Proxy = ProxyConfiguration.Resolve(proxyValue, noProxy, Log);
            Log.Info($"Proxy: {Proxy.Description}");
            _downloader = new HttpDownloader(Log, Proxy);
            _updater = new IndexUpdater(Paths, Store, _downloader, Log);
            _networkConfigured = true;
        }

        public async Task<LoadedIndex> OpenIndexAsync(ParsedCommandLine parsed, CancellationToken cancellationToken)
        {
            PkgException? updateFailure = null;
            if (!parsed.Flag("no-update"))
            {
                try
                {
                    var source = LocalSourceConfig.Load(Paths.ProgramRoot);
                    var result = await Updater.UpdateAsync(source, cancellationToken).ConfigureAwait(false);
                    return result.Index;
                }
                catch (PkgException exception) when (exception.Code != ErrorCodes.InvalidSource)
                {
                    updateFailure = FileSystemErrors.Map(exception, "index update");
                    Log.Warn($"Index update failed: {updateFailure.Message}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    updateFailure = FileSystemErrors.Map(exception, "index update");
                    Log.Warn($"Index update failed: {updateFailure.Message}");
                }
            }

            try
            {
                var cached = await Store.LoadRequiredAsync(verifyHashes: false, cancellationToken)
                    .ConfigureAwait(false);
                var message = $"Using cached index from {cached.Source.Display} (bundle {cached.BundleId[..16]}...).";
                if (updateFailure is not null)
                {
                    Log.Warn(message);
                }
                else
                {
                    Log.Info(message);
                }

                return cached;
            }
            catch (PkgException loadFailure) when (updateFailure is not null)
            {
                throw new PkgException(updateFailure.Code, updateFailure.ExitCode,
                    $"{updateFailure.Message} No usable cached index is available: {loadFailure.Message}",
                    updateFailure);
            }
        }

        public async Task TryRefreshIndexForLocalCommandAsync(CancellationToken cancellationToken)
        {
            try
            {
                var source = LocalSourceConfig.Load(Paths.ProgramRoot);
                await Updater.UpdateAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PkgException or IOException or
                                                  UnauthorizedAccessException)
            {
                Log.Warn($"Index refresh skipped for local operation: {exception.Message}");
            }
        }

        public ValueTask DisposeAsync()
        {
            _downloader?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
