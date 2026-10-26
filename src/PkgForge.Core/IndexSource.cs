using System.Text.Json;

namespace PkgForge;

/// <summary>
/// The index root comes only from the local program-directory configuration.
/// No user profile, environment variable, or command-line source override exists.
/// </summary>
public sealed record IndexSource
{
    private IndexSource(Uri baseUri)
    {
        BaseUri = baseUri;
        Display = WithoutCredentials(baseUri);
    }

    public Uri BaseUri { get; }

    public string Display { get; }

    public Uri IndexUri => new(BaseUri, "index.json");

    public static IndexSource Parse(string value, string context = "index source")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage, $"{context} is empty.");
        }

        if (value.Trim().Length > 2048)
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"{context} exceeds the 2048 character limit.");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"{context} is not an absolute URL: '{SecureText.RedactSecrets(value)}'.");
        }

        if (!parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"{context} must use HTTP or HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"{context} has no host.");
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"{context} must not contain a query or fragment.");
        }

        var builder = new UriBuilder(parsed)
        {
            Path = parsed.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return new IndexSource(builder.Uri);
    }

    public Uri ResolveIndexRelative(string relativeUrl, string context)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl))
        {
            throw new PkgException(ErrorCodes.InvalidIndex, ExitCode.Verification,
                $"{context}: an empty URL is not allowed.");
        }

        if (!Uri.TryCreate(BaseUri, relativeUrl, out var resolved) || !resolved.IsAbsoluteUri)
        {
            throw new PkgException(ErrorCodes.InvalidIndex, ExitCode.Verification,
                $"{context}: invalid download URL '{relativeUrl}'.");
        }

        if (!resolved.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !resolved.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new PkgException(ErrorCodes.InvalidIndex, ExitCode.Verification,
                $"{context}: download URL must use HTTP or HTTPS.");
        }

        return resolved;
    }

    private static string WithoutCredentials(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }
}

/// <summary>
/// Loads the sole source configuration from <c>pkgforge.config.json</c> beside
/// the executable. There is intentionally no user-level fallback.
/// </summary>
public static class LocalSourceConfig
{
    public const string FileName = "pkgforge.config.json";
    public const long MaxBytes = 64 * 1024;

    public static IndexSource Load(string programDirectory)
    {
        var fullPath = Path.Combine(programDirectory, FileName);
        if (!File.Exists(fullPath))
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"Source configuration is missing: '{fullPath}'. " +
                $"Create it beside the executable with schema 1 and indexSource.");
        }

        var info = new FileInfo(fullPath);
        if (info.Length <= 0 || info.Length > MaxBytes)
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"Source configuration has an invalid size: '{fullPath}'.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                schema.GetInt32() != 1 ||
                !root.TryGetProperty("indexSource", out var sourceElement) ||
                sourceElement.ValueKind != JsonValueKind.String)
            {
                throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                    $"Source configuration must contain schema=1 and a string indexSource: '{fullPath}'.");
            }

            return IndexSource.Parse(sourceElement.GetString()!, "local indexSource");
        }
        catch (JsonException exception)
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"Source configuration is not valid JSON: '{fullPath}'.", exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"Source configuration has an invalid schema or indexSource: '{fullPath}'.", exception);
        }
        catch (IOException exception)
        {
            throw new PkgException(ErrorCodes.InvalidSource, ExitCode.Usage,
                $"Source configuration could not be read: '{fullPath}'.", exception);
        }
    }
}
