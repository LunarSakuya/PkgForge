using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PkgForge.Tests;

internal enum MockFailureKind
{
    InternalServerError,
    NotFound,
    Truncate,
    Delay,
}

internal sealed class MockHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptTask;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _redirects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FailureRule>> _failures = new(StringComparer.Ordinal);
    private readonly object _failureGate = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private bool _disposed;

    public MockHttpServer(string root)
    {
        _root = Path.GetFullPath(root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/";
        BaseUri = new Uri(BaseUrl);
        _acceptTask = AcceptLoopAsync(_stop.Token);
    }

    public Uri BaseUri { get; }

    public string BaseUrl { get; }

    public IReadOnlyDictionary<string, int> RequestCounts => _requestCounts;

    public int Count(string relativePath) =>
        _requestCounts.TryGetValue(NormalizePath(relativePath), out var count) ? count : 0;

    public void AddFailure(string relativePath, MockFailureKind kind, int times, TimeSpan? delay = null)
    {
        lock (_failureGate)
        {
            var key = NormalizePath(relativePath);
            if (!_failures.TryGetValue(key, out var rules))
            {
                rules = [];
                _failures.Add(key, rules);
            }

            rules.Add(new FailureRule(kind, times, delay));
        }
    }

    public void AddRedirect(string relativePath, string location)
    {
        _redirects[NormalizePath(relativePath)] = location;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        var tasks = _connections.ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var task = HandleAsync(client, cancellationToken);
            _connections.Add(task);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var (method, target) = request.Value;
                if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(stream, 405, "Method Not Allowed", [], cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var path = DecodeTarget(target);
                var key = NormalizePath(path);
                _requestCounts.AddOrUpdate(key, 1, (_, current) => current + 1);
                var failure = TakeFailure(key);
                if (failure is { Kind: MockFailureKind.Delay })
                {
                    await Task.Delay(failure.Delay ?? TimeSpan.FromMilliseconds(300), cancellationToken)
                        .ConfigureAwait(false);
                    failure = null;
                }

                if (failure is { Kind: MockFailureKind.InternalServerError })
                {
                    await WriteStatusAsync(stream, 500, "Internal Server Error", [], cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (failure is { Kind: MockFailureKind.NotFound })
                {
                    await WriteStatusAsync(stream, 404, "Not Found", [], cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (_redirects.TryGetValue(key, out var redirectLocation))
                {
                    await WriteRedirectAsync(stream, redirectLocation, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var fullPath = ResolveFile(key);
                if (fullPath is null || !File.Exists(fullPath))
                {
                    await WriteStatusAsync(stream, 404, "Not Found", [], cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (failure is { Kind: MockFailureKind.Truncate })
                {
                    await WriteResponseAsync(stream, bytes, cancellationToken, truncate: true)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, bytes, cancellationToken, truncate: false)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or
                                                  ObjectDisposedException or SocketException)
            {
            }
        }
    }

    private string? ResolveFile(string normalizedPath)
    {
        if (normalizedPath.Length == 0)
        {
            return null;
        }

        if (normalizedPath.Contains('\\', StringComparison.Ordinal) ||
            normalizedPath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(_root, normalizedPath.Replace('/',
            Path.DirectorySeparatorChar)));
        var rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? fullPath
            : null;
    }

    private FailureRule? TakeFailure(string key)
    {
        lock (_failureGate)
        {
            if (!_failures.TryGetValue(key, out var rules))
            {
                return null;
            }

            while (rules.Count > 0)
            {
                var rule = rules[0];
                if (rule.Remaining <= 0)
                {
                    rules.RemoveAt(0);
                    continue;
                }

                if (rule.Remaining != int.MaxValue)
                {
                    rules[0] = rule with { Remaining = rule.Remaining - 1 };
                }

                return rule;
            }

            return null;
        }
    }

    private static async Task<(string Method, string Target)?> ReadRequestAsync(NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var firstLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
            var parts = firstLine.Split(' ');
            return parts.Length >= 2 ? (parts[0], parts[1]) : null;
        }

        return null;
    }

    private static string DecodeTarget(string target)
    {
        var path = target;
        if (Uri.TryCreate("http://localhost" + target, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        path = Uri.UnescapeDataString(path);
        return path.TrimStart('/');
    }

    private static string NormalizePath(string path) => path.TrimStart('/');

    private static async Task WriteStatusAsync(NetworkStream stream, int status, string reason,
        byte[] body, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteRedirectAsync(NetworkStream stream, string location,
        CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, byte[] body,
        CancellationToken cancellationToken, bool truncate)
    {
        var contentType = body.Length > 0 && body[0] == (byte)'{'
            ? "application/json"
            : "application/octet-stream";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n" +
            $"Content-Type: {contentType}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!truncate)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            return;
        }

        var partial = body.Length <= 1 ? 0 : body.Length / 2;
        if (partial > 0)
        {
            await stream.WriteAsync(body.AsMemory(0, partial), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record FailureRule(MockFailureKind Kind, int Remaining, TimeSpan? Delay);
}
