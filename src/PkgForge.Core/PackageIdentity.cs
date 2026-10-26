using System.Text.RegularExpressions;

namespace PkgForge;

public enum PackageKind
{
    Client,
    Server,
}

public static class PackageKinds
{
    public static PackageKind Parse(string value) => value.ToLowerInvariant() switch
    {
        "client" => PackageKind.Client,
        "server" => PackageKind.Server,
        _ => throw PkgException.Usage($"Unknown package kind '{value}'. Expected client or server."),
    };

    public static string ToToken(this PackageKind kind) => kind == PackageKind.Client ? "client" : "server";
}

public sealed record PackageSelector(string VersionToken, PackageKind Kind, string? Platform)
{
    public static PackageSelector Parse(string value, string? currentPlatform) =>
        ParseCore(value, () => currentPlatform);

    /// <summary>
    /// Parses a selector without probing the host platform until it is actually
    /// needed. A server selector or an explicit client platform never invokes
    /// the provider, so those installs remain portable across host architectures.
    /// </summary>
    public static PackageSelector Parse(string value, Func<string?> currentPlatformProvider) =>
        ParseCore(value, currentPlatformProvider);

    private static PackageSelector ParseCore(string value, Func<string?> currentPlatformProvider)
    {
        ArgumentNullException.ThrowIfNull(currentPlatformProvider);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw PkgException.Usage("A package name is required.");
        }

        var separator = value.IndexOf('@', StringComparison.Ordinal);
        var versionToken = separator < 0 ? value : value[..separator];
        var suffix = separator < 0 ? string.Empty : value[(separator + 1)..];
        ValidateVersionToken(versionToken);

        if (suffix.Length == 0)
        {
            return new PackageSelector(versionToken, PackageKind.Client,
                RequireCurrentPlatform(currentPlatformProvider));
        }

        var colon = suffix.IndexOf(':', StringComparison.Ordinal);
        var kindToken = colon < 0 ? suffix : suffix[..colon];
        var platformToken = colon < 0 ? null : suffix[(colon + 1)..];
        var kind = PackageKinds.Parse(kindToken);
        if (kind == PackageKind.Server)
        {
            if (platformToken is not null)
            {
                throw PkgException.Usage("The server package has no platform suffix.");
            }

            return new PackageSelector(versionToken, kind, null);
        }

        if (platformToken is not null)
        {
            if (!PackageNaming.IsSafePlatformToken(platformToken))
            {
                throw PkgException.Usage($"Invalid platform token '{platformToken}'.");
            }

            return new PackageSelector(versionToken, kind, platformToken);
        }

        return new PackageSelector(versionToken, kind, RequireCurrentPlatform(currentPlatformProvider));
    }

    private static string RequireCurrentPlatform(Func<string?> currentPlatformProvider)
    {
        var platform = currentPlatformProvider();
        if (string.IsNullOrWhiteSpace(platform) || !PackageNaming.IsSafePlatformToken(platform))
        {
            throw PkgException.Usage("The current platform could not be resolved for a client package.");
        }

        return platform;
    }

    public static void ValidateVersionToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw PkgException.Usage("The version token must contain 1..128 visible characters.");
        }

        if (value is "." or "..")
        {
            throw PkgException.Usage("The version token cannot be '.' or '..'.");
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) ||
                character is '/' or '\\' or ':' or '@' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                throw PkgException.Usage($"The version token contains an invalid character: U+{(int)character:X4}.");
            }
        }
    }
}

public static partial class PackageNaming
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformPattern();

    public static bool IsSafePlatformToken(string value) => PlatformPattern().IsMatch(value);

    public static bool IsReservedWindowsName(string value) =>
        ReservedWindowsNames.Contains(value.Split('.')[0]);

    public static string InstallId(string versionKey, PackageKind kind, string? platform)
    {
        if (string.IsNullOrWhiteSpace(versionKey))
        {
            throw PkgException.Usage("The package version key is empty.");
        }

        var builder = new System.Text.StringBuilder();
        foreach (var character in versionKey)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? char.ToLowerInvariant(character)
                : '-');
        }

        var safeVersion = builder.ToString().Trim('-');
        if (safeVersion.Length == 0)
        {
            safeVersion = "package";
        }

        var id = kind == PackageKind.Server
            ? $"{safeVersion}-server"
            : $"{safeVersion}-client-{platform}";
        id = id.ToLowerInvariant();
        if (id.Length > 120)
        {
            id = id[..120].TrimEnd('.', ' ');
        }

        if (IsReservedWindowsName(id))
        {
            id += "-package";
        }

        return id;
    }

    public static string CanonicalPackageId(string versionKey, PackageKind kind, string? platform) =>
        kind == PackageKind.Server ? $"{versionKey}@server" : $"{versionKey}@client:{platform}";

    public static string DetectCurrentPlatform()
    {
        var os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "osx"
                    : throw new PkgException(ErrorCodes.Usage, ExitCode.Usage,
                        "The current operating system has no index platform token.");

        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var arch = architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new PkgException(ErrorCodes.Usage, ExitCode.Usage,
                $"The current CPU architecture '{architecture}' has no index platform token."),
        };

        var token = $"{os}-{arch}";
        if (!IsSafePlatformToken(token))
        {
            throw new PkgException(ErrorCodes.Usage, ExitCode.Usage, $"Derived an invalid platform token '{token}'.");
        }

        return token;
    }
}
