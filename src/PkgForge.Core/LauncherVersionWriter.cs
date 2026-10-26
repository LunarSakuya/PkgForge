namespace PkgForge;

/// <summary>
/// Materializes the launcher-facing version JSON from the package plan. The
/// SQLite index is the source of truth; no sibling checkout or Mojang JSON
/// download is required.
/// </summary>
public static class LauncherVersionWriter
{
    public static async Task<StagedFile?> WriteAsync(string stagingRoot, ResolvedPlan plan, ILog log,
        CancellationToken cancellationToken)
    {
        if (plan.Kind != PackageKind.Client || string.IsNullOrWhiteSpace(plan.MainClass))
        {
            return null;
        }

        var version = Build(plan);
        var relativePath = $"versions/{plan.VersionKey}/{plan.VersionKey}.json";
        var destination = PathSafety.CombineUnderRoot(stagingRoot, relativePath, "launcher version JSON");
        await AtomicFile.WriteAllTextAsync(destination, JsonCodec.SerializeLauncherVersion(version) +
            Environment.NewLine, cancellationToken).ConfigureAwait(false);
        var size = new FileInfo(destination).Length;
        var sha1 = await Hashing.Sha1FileAsync(destination, cancellationToken).ConfigureAwait(false);
        log.Progress($"Wrote launcher version JSON for {plan.VersionKey}.");
        return new StagedFile(relativePath, size, sha1, "launcher-json");
    }

    private static LauncherVersionJson Build(ResolvedPlan plan)
    {
        var downloads = plan.Downloads.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var version = new LauncherVersionJson
        {
            Id = plan.VersionKey,
            Type = string.IsNullOrWhiteSpace(plan.VersionType) ? "release" : plan.VersionType,
            MainClass = plan.MainClass,
            Assets = plan.AssetIndex,
            ReleaseTime = plan.ReleaseTime,
            Time = plan.ReleaseTime,
            MinimumLauncherVersion = 21,
            ComplianceLevel = 1,
        };

        if (!string.IsNullOrWhiteSpace(plan.AssetIndex) && downloads.TryGetValue(
                $"assets/indexes/{plan.AssetIndex}.json", out var assetIndex))
        {
            version.AssetIndex = new LauncherAssetIndex
            {
                Id = plan.AssetIndex,
                Sha1 = plan.AssetsSha1 ?? assetIndex.Sha1,
                Size = assetIndex.Size,
                TotalSize = plan.AssetsTotalSize ?? 0,
                Url = FirstUrl(assetIndex),
            };
        }

        if (downloads.TryGetValue($"versions/{plan.VersionKey}/{plan.VersionKey}.jar", out var client))
        {
            version.Downloads = new LauncherDownloads
            {
                Client = new LauncherDownloadFile
                {
                    Path = client.Path,
                    Sha1 = client.Sha1,
                    Size = client.Size,
                    Url = FirstUrl(client),
                },
            };
        }

        foreach (var library in plan.Downloads.Where(item =>
                     item.Role.Equals("library", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var name = LibraryName(library.Path);
            var entry = new LauncherLibrary
            {
                Name = name,
                Downloads = new LauncherLibraryDownloads
                {
                    Artifact = new LauncherDownloadFile
                    {
                        Path = library.Path,
                        Sha1 = library.Sha1,
                        Size = library.Size,
                        Url = FirstUrl(library),
                    },
                },
            };

            var classifier = Classifier(library.Path, name);
            if (classifier is not null && classifier.StartsWith("natives-", StringComparison.Ordinal))
            {
                var os = classifier["natives-".Length..].Split('-')[0];
                entry.Rules =
                [
                    new LauncherRule
                    {
                        Action = "allow",
                        Os = new LauncherOs { Name = os },
                    },
                ];
            }

            version.Libraries.Add(entry);
        }

        var arguments = new LauncherArguments();
        var jvmDefaults = new List<string>();
        foreach (var argument in plan.LauncherArguments)
        {
            var value = NormalizeArgument(argument.Value);
            switch (argument.Phase)
            {
                case "game":
                    arguments.Game.Add(value);
                    break;
                case "jvm":
                    arguments.Jvm.Add(value);
                    break;
                case "jvm_defaults":
                    jvmDefaults.Add(value);
                    break;
            }
        }

        if (jvmDefaults.Count > 0)
        {
            arguments.DefaultUserJvm.Add(new LauncherArgumentValue { Value = jvmDefaults });
        }

        version.Arguments = arguments;

        if (!string.IsNullOrWhiteSpace(plan.LoggingArgument) &&
            !string.IsNullOrWhiteSpace(plan.LoggingPath) &&
            downloads.TryGetValue(plan.LoggingPath, out var logging))
        {
            version.Logging = new LauncherLogging
            {
                Client = new LauncherLogClient
                {
                    Argument = NormalizeArgument(plan.LoggingArgument),
                    File = new LauncherLogFile
                    {
                        Id = Path.GetFileName(plan.LoggingPath),
                        Sha1 = logging.Sha1,
                        Size = logging.Size,
                        Url = FirstUrl(logging),
                    },
                },
            };
        }

        if (plan.JavaMajor > 0)
        {
            version.JavaVersion = new LauncherJavaVersion { MajorVersion = plan.JavaMajor };
        }

        return version;
    }

    private static string FirstUrl(DownloadItem item)
    {
        var url = item.Urls.FirstOrDefault()
                  ?? throw PkgException.Integrity($"No download URL for '{item.Path}'.");
        return url.AbsoluteUri;
    }

    private static string NormalizeArgument(string value) =>
        value.Replace("${gameDir}", "${game_directory}", StringComparison.Ordinal)
            .Replace("${assetsDir}", "${assets_root}", StringComparison.Ordinal)
            .Replace("${authToken}", "${auth_access_token}", StringComparison.Ordinal)
            .Replace("${nativesDir}", "${natives_directory}", StringComparison.Ordinal)
            .Replace("${logConfig}", "${path}", StringComparison.Ordinal);

    private static string LibraryName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Length < 5 || !segments[0].Equals("libraries", StringComparison.OrdinalIgnoreCase))
        {
            throw PkgException.Integrity($"Library path is not a Maven layout: '{path}'.");
        }

        var artifact = segments[^3];
        var libraryVersion = segments[^2];
        var group = string.Join('.', segments[1..^3]);
        if (group.Length == 0 || artifact.Length == 0 || libraryVersion.Length == 0)
        {
            throw PkgException.Integrity($"Library path has an incomplete Maven coordinate: '{path}'.");
        }

        var name = $"{group}:{artifact}:{libraryVersion}";
        var classifier = Classifier(path, name);
        return classifier is null ? name : $"{name}:{classifier}";
    }

    private static string? Classifier(string path, string name)
    {
        var artifact = name.Split(':')[1];
        var libraryVersion = name.Split(':')[2];
        var file = Path.GetFileNameWithoutExtension(path);
        var prefix = $"{artifact}-{libraryVersion}";
        if (!file.StartsWith(prefix, StringComparison.Ordinal) || file.Length == prefix.Length)
        {
            return null;
        }

        return file[prefix.Length] == '-' ? file[(prefix.Length + 1)..] : null;
    }
}
