using System.Collections;
using System.Net;
using System.Text;

namespace PkgForge;

/// <summary>
/// Resolves the effective HTTP proxy once per command. Priority:
/// explicit --proxy / --no-proxy, then standard environment variables, then
/// the operating-system proxy, then direct.
/// </summary>
public sealed class ProxyConfiguration
{
    private ProxyConfiguration(bool useProxy, IWebProxy? proxy, string description)
    {
        UseProxy = useProxy;
        Proxy = proxy;
        Description = description;
    }

    public static ProxyConfiguration Direct { get; } =
        new(useProxy: false, proxy: null, description: "direct");

    public bool UseProxy { get; }

    public IWebProxy? Proxy { get; }

    public string Description { get; }

    public static ProxyConfiguration Resolve(
        string? explicitProxy,
        bool noProxy,
        ILog log,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool detectSystemProxy = true)
    {
        if (noProxy && !string.IsNullOrWhiteSpace(explicitProxy))
        {
            throw new PkgException(ErrorCodes.InvalidProxy, ExitCode.Usage,
                "--proxy and --no-proxy cannot be used together.");
        }

        var env = environment ?? ReadProcessEnvironment();
        var noProxyValue = ReadEnvironmentValue(env, "NO_PROXY");
        var bypass = NoProxyMatcher.Parse(noProxyValue, log);

        if (noProxy)
        {
            return new ProxyConfiguration(false, null, "direct (--no-proxy)");
        }

        if (!string.IsNullOrWhiteSpace(explicitProxy))
        {
            var endpoint = ProxyEndpoint.Parse(explicitProxy, "--proxy");
            var proxy = new CompositeWebProxy(
                http: endpoint,
                https: endpoint,
                all: null,
                noProxy: bypass,
                system: null);
            return new ProxyConfiguration(true, proxy,
                $"explicit {endpoint.Display}{endpoint.AuthDescription}");
        }

        var httpValue = ReadEnvironmentValue(env, "HTTP_PROXY");
        var httpsValue = ReadEnvironmentValue(env, "HTTPS_PROXY");
        var allValue = ReadEnvironmentValue(env, "ALL_PROXY");
        var httpEndpoint = ParseEnvironmentEndpoint(httpValue, "HTTP_PROXY");
        var httpsEndpoint = ParseEnvironmentEndpoint(httpsValue, "HTTPS_PROXY");
        var allEndpoint = ParseEnvironmentEndpoint(allValue, "ALL_PROXY");

        IWebProxy? system = null;
        if (detectSystemProxy)
        {
            try
            {
                system = WebRequest.GetSystemWebProxy();
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or
                                                  NotSupportedException or InvalidOperationException)
            {
                log.Warn($"System proxy detection is unavailable: {exception.Message}");
            }
        }

        if (httpEndpoint is null && httpsEndpoint is null && allEndpoint is null && system is null)
        {
            return Direct;
        }

        var composite = new CompositeWebProxy(httpEndpoint, httpsEndpoint, allEndpoint, bypass, system);
        var parts = new List<string>();
        if (httpsEndpoint is not null) parts.Add($"https={httpsEndpoint.Display}");
        if (httpEndpoint is not null) parts.Add($"http={httpEndpoint.Display}");
        if (allEndpoint is not null) parts.Add($"all={allEndpoint.Display}");
        if (noProxyValue is not null) parts.Add("no_proxy=set");
        if (system is not null) parts.Add("system=auto");
        return new ProxyConfiguration(true, composite,
            parts.Count == 0 ? "system proxy (automatic)" : "environment " + string.Join("; ", parts));
    }

    private static ProxyEndpoint? ParseEnvironmentEndpoint(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ProxyEndpoint.Parse(value, name);
    }

    private static string? ReadEnvironmentValue(IReadOnlyDictionary<string, string?> environment, string name)
    {
        // On case-sensitive systems the lowercase spelling wins. Empty or
        // whitespace-only values are treated as unset so a partial environment
        // can still fall through to the next source.
        foreach (var candidate in new[] { name.ToLowerInvariant(), name.ToUpperInvariant() })
        {
            if (environment.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static Dictionary<string, string?> ReadProcessEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                result[key] = entry.Value?.ToString();
            }
        }

        return result;
    }
}

internal sealed class ProxyEndpoint
{
    private ProxyEndpoint(Uri uri, NetworkCredential? credentials)
    {
        Uri = uri;
        Credentials = credentials;
    }

    public Uri Uri { get; }

    public NetworkCredential? Credentials { get; }

    public string Display
    {
        get
        {
            var builder = new UriBuilder(Uri) { UserName = string.Empty, Password = string.Empty };
            return builder.Uri.GetLeftPart(UriPartial.Authority);
        }
    }

    public string AuthDescription => Credentials is null ? string.Empty : " (auth=yes)";

    public static ProxyEndpoint Parse(string value, string context)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw Invalid($"{context} is empty.");
        }

        if (trimmed.Length > 2048)
        {
            throw Invalid($"{context} exceeds the 2048 character limit.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw Invalid($"{context} is not an absolute proxy URL.");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            throw Invalid($"{context} uses unsupported proxy scheme '{scheme}'.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw Invalid($"{context} has no proxy host.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Invalid($"{context} must not contain a query or fragment.");
        }

        if (uri.AbsolutePath is not ("" or "/"))
        {
            throw Invalid($"{context} must not contain a path.");
        }

        NetworkCredential? credentials = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var split = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            var username = Uri.UnescapeDataString(split < 0 ? uri.UserInfo : uri.UserInfo[..split]);
            var password = split < 0 ? string.Empty : Uri.UnescapeDataString(uri.UserInfo[(split + 1)..]);
            if (username.Length == 0)
            {
                throw Invalid($"{context} has an empty proxy username.");
            }

            credentials = new NetworkCredential(username, password);
        }

        var cleanBuilder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return new ProxyEndpoint(cleanBuilder.Uri, credentials);
    }

    private static PkgException Invalid(string message) =>
        new(ErrorCodes.InvalidProxy, ExitCode.Usage, message);
}

internal sealed class NoProxyMatcher
{
    private readonly bool _bypassAll;
    private readonly IReadOnlyList<NoProxyRule> _rules;

    private NoProxyMatcher(bool bypassAll, IReadOnlyList<NoProxyRule> rules)
    {
        _bypassAll = bypassAll;
        _rules = rules;
    }

    public static NoProxyMatcher Parse(string? value, ILog log)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new NoProxyMatcher(false, []);
        }

        var rules = new List<NoProxyRule>();
        var bypassAll = false;
        foreach (var raw in value.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (token == "*")
            {
                bypassAll = true;
                continue;
            }

            if (!NoProxyRule.TryParse(token, out var rule, out var error))
            {
                log.Warn($"Ignoring invalid NO_PROXY entry '{SecureText.ForConsole(token)}': {error}");
                continue;
            }

            rules.Add(rule);
        }

        return new NoProxyMatcher(bypassAll, rules);
    }

    public bool Matches(Uri destination)
    {
        if (_bypassAll)
        {
            return true;
        }

        var host = destination.IdnHost.TrimEnd('.').ToLowerInvariant();
        var port = destination.Port;
        return _rules.Any(rule => rule.Matches(host, port));
    }

    private sealed record NoProxyRule(string Host, int? Port, bool Suffix)
    {
        public bool Matches(string host, int destinationPort)
        {
            if (Port is not null && Port.Value != destinationPort)
            {
                return false;
            }

            return Suffix
                ? host.Equals(Host, StringComparison.OrdinalIgnoreCase) ||
                  host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase)
                : host.Equals(Host, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryParse(string token, out NoProxyRule rule, out string error)
        {
            rule = null!;
            error = string.Empty;
            var host = token;
            int? port = null;

            if (token.StartsWith("[", StringComparison.Ordinal))
            {
                var close = token.IndexOf(']', StringComparison.Ordinal);
                if (close < 0)
                {
                    error = "unclosed IPv6 bracket";
                    return false;
                }

                host = token[1..close];
                if (close + 1 < token.Length)
                {
                    if (token[close + 1] != ':' ||
                        !TryParsePort(token[(close + 2)..], out port))
                    {
                        error = "invalid port";
                        return false;
                    }
                }
            }
            else
            {
                var colon = token.LastIndexOf(':');
                if (colon > 0 && token.IndexOf(':') == colon)
                {
                    host = token[..colon];
                    if (!TryParsePort(token[(colon + 1)..], out port))
                    {
                        error = "invalid port";
                        return false;
                    }
                }
            }

            if (host.Length == 0 || host.Contains('/', StringComparison.Ordinal) ||
                host.Contains('?', StringComparison.Ordinal) || host.Contains('#', StringComparison.Ordinal) ||
                host.Contains('@', StringComparison.Ordinal) || host.Any(char.IsWhiteSpace) ||
                host.Any(char.IsControl))
            {
                error = "invalid host";
                return false;
            }

            var suffix = host.StartsWith(".", StringComparison.Ordinal) ||
                         host.StartsWith("*.", StringComparison.OrdinalIgnoreCase);
            host = host.TrimStart('*').TrimStart('.').TrimEnd('.');
            if (host.Length == 0)
            {
                error = "empty host";
                return false;
            }

            rule = new NoProxyRule(host.ToLowerInvariant(), port, suffix);
            return true;
        }

        private static bool TryParsePort(string value, out int? port)
        {
            port = null;
            if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                parsed is < 1 or > 65535)
            {
                return false;
            }

            port = parsed;
            return true;
        }
    }
}

internal sealed class CompositeWebProxy : IWebProxy
{
    private readonly ProxyEndpoint? _http;
    private readonly ProxyEndpoint? _https;
    private readonly ProxyEndpoint? _all;
    private readonly IWebProxy? _system;
    private readonly NoProxyMatcher _noProxy;
    private ICredentials? _credentials;

    public CompositeWebProxy(ProxyEndpoint? http, ProxyEndpoint? https, ProxyEndpoint? all,
        NoProxyMatcher noProxy, IWebProxy? system)
    {
        _http = http;
        _https = https;
        _all = all;
        _noProxy = noProxy;
        _system = system;
        _credentials = new ProxyCredentialProvider(this);
    }

    public ICredentials? Credentials
    {
        get => _credentials;
        set => _credentials = value;
    }

    public Uri? GetProxy(Uri destination)
    {
        if (_noProxy.Matches(destination))
        {
            return destination;
        }

        var endpoint = Select(destination);
        if (endpoint is not null)
        {
            return endpoint.Uri;
        }

        return _system?.GetProxy(destination) ?? destination;
    }

    public bool IsBypassed(Uri host)
    {
        if (_noProxy.Matches(host))
        {
            return true;
        }

        var endpoint = Select(host);
        if (endpoint is not null)
        {
            return false;
        }

        return _system?.IsBypassed(host) ?? true;
    }

    public NetworkCredential? FindCredential(Uri proxyUri)
    {
        foreach (var endpoint in new[] { _http, _https, _all })
        {
            if (endpoint is not null && SameAuthority(endpoint.Uri, proxyUri))
            {
                return endpoint.Credentials;
            }
        }

        return null;
    }

    public NetworkCredential? SystemCredential(Uri proxyUri) =>
        _system?.Credentials?.GetCredential(proxyUri, "Basic") as NetworkCredential;

    private ProxyEndpoint? Select(Uri destination) => destination.Scheme.ToLowerInvariant() switch
    {
        "https" => _https ?? _all,
        "http" => _http ?? _all,
        _ => _all,
    };

    private static bool SameAuthority(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}

internal sealed class ProxyCredentialProvider : ICredentials
{
    private readonly CompositeWebProxy _proxy;

    public ProxyCredentialProvider(CompositeWebProxy proxy)
    {
        _proxy = proxy;
    }

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        return _proxy.FindCredential(uri) ?? _proxy.SystemCredential(uri);
    }
}
