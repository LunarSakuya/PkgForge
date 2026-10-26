using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PkgForge.Tests;

/// <summary>
/// Minimal forward proxy for HTTP (absolute-URI) requests. It supports Basic
/// proxy authentication and forwards to the requested absolute URL. HTTPS
/// CONNECT is deliberately not implemented; tests use loopback HTTP sources
/// and proxy behavior is otherwise covered at the configuration layer.
/// </summary>
internal sealed class MockHttpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptTask;
    private readonly HttpClient _forwarder;
    private readonly string? _expectedAuthorization;
    private int _requestCount;
    private bool _disposed;

    public MockHttpProxy(string? username = null, string? password = null)
    {
        if (username is not null)
        {
            var raw = Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}");
            _expectedAuthorization = "Basic " + Convert.ToBase64String(raw);
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
        _forwarder = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _acceptTask = AcceptLoopAsync(_stop.Token);
    }

    public string BaseUrl { get; }

    public int RequestCount => Volatile.Read(ref _requestCount);

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

        _forwarder.Dispose();
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

            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                Interlocked.Increment(ref _requestCount);
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var (method, target, headers) = request.Value;
                if (_expectedAuthorization is not null &&
                    (!headers.TryGetValue("Proxy-Authorization", out var authorization) ||
                     !string.Equals(authorization, _expectedAuthorization, StringComparison.Ordinal)))
                {
                    var challenge = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                        "Proxy-Authenticate: Basic realm=\"pkgforge-test\"\r\n" +
                        "Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(challenge, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(target, UriKind.Absolute, out var destination))
                {
                    var bad = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(bad, cancellationToken).ConfigureAwait(false);
                    return;
                }

                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, destination);
                using var response = await _forwarder.SendAsync(requestMessage,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var reason = response.ReasonPhrase ?? "OK";
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(int)response.StatusCode} {reason}\r\n" +
                    $"Content-Length: {bytes.Length}\r\nConnection: close\r\n" +
                    $"Content-Type: {response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream"}\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or
                                                  OperationCanceledException or ObjectDisposedException or
                                                  SocketException)
            {
            }
        }
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadRequestAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
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

            var lines = text[..end].Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            return (parts[0], parts[1], headers);
        }

        return null;
    }
}
