using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PkgForge;

public sealed class HttpDownloader : IDisposable
{
    public const long MaxGameFileBytes = 16L * 1024 * 1024 * 1024;
    public const int MaxManifestBytes = 1024 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(1);

    private readonly HttpClient _client;
    private readonly ILog _log;
    private readonly ProxyConfiguration _proxy;

    public HttpDownloader(ILog log, ProxyConfiguration? proxy = null,
        HttpMessageHandler? handler = null)
    {
        _log = log;
        _proxy = proxy ?? ProxyConfiguration.Direct;
        var effectiveHandler = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
            UseProxy = _proxy.UseProxy,
            Proxy = _proxy.Proxy,
        };
        _client = new HttpClient(effectiveHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pkgforge", BuildConfig.ProductVersion));
    }

    public ProxyConfiguration Proxy => _proxy;

    public async Task<byte[]> FetchBytesAsync(Uri uri, int maxBytes, string context, CancellationToken cancellationToken)
    {
        ValidateUri(uri, context);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (IsTransient(response.StatusCode) && attempt < 3)
                {
                    await DelayForRetryAsync(response, attempt, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw PkgException.Network(
                        $"{context}: HTTP {(int)response.StatusCode} from {SanitizeUrl(uri)}.");
                }

                if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
                {
                    throw PkgException.Integrity(
                        $"{context}: response exceeds the {maxBytes} byte limit.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                var total = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total = checked(total + read);
                    if (total > maxBytes)
                    {
                        throw PkgException.Integrity(
                            $"{context}: response exceeds the {maxBytes} byte limit.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"request timed out after {OperationTimeout.TotalSeconds:0} seconds");
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
            }
            catch (IOException exception)
            {
                lastError = exception;
            }

            if (attempt < 3)
            {
                await Task.Delay(BaseRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw PkgException.Network(
            $"{context}: failed to fetch {SanitizeUrl(uri)} after 3 attempts: " +
            $"{SecureText.RedactSecrets(lastError?.Message ?? "unknown error")}",
            lastError);
    }

    public Task DownloadToFileAsync(
        Uri uri,
        string destination,
        long expectedSize,
        string expectedSha1,
        string context,
        CancellationToken cancellationToken) =>
        DownloadToFileCoreAsync(uri, destination, expectedSize, expectedSha1, HashAlgorithmName.SHA1,
            "SHA-1", context, cancellationToken);

    public Task DownloadToFileSha256Async(
        Uri uri,
        string destination,
        long expectedSize,
        string expectedSha256,
        string context,
        CancellationToken cancellationToken) =>
        DownloadToFileCoreAsync(uri, destination, expectedSize, expectedSha256, HashAlgorithmName.SHA256,
            "SHA-256", context, cancellationToken);

    private async Task DownloadToFileCoreAsync(
        Uri uri,
        string destination,
        long expectedSize,
        string expectedHash,
        HashAlgorithmName hashAlgorithm,
        string hashLabel,
        string context,
        CancellationToken cancellationToken)
    {
        ValidateUri(uri, context);

        if (expectedSize < 0 || expectedSize > MaxGameFileBytes)
        {
            throw PkgException.Integrity($"{context}: invalid expected size {expectedSize}.");
        }

        var hashLength = hashAlgorithm == HashAlgorithmName.SHA1 ? 40 : 64;
        if (!Hashing.IsLowerHex(expectedHash, hashLength))
        {
            throw PkgException.Integrity($"{context}: invalid expected {hashLabel}.");
        }

        var destinationFull = PathSafety.NormalizeAbsolutePath(destination, context);
        var parent = Path.GetDirectoryName(destinationFull)
                     ?? throw PkgException.Unsafe($"{context}: destination has no parent directory.");
        Directory.CreateDirectory(parent);
        PathSafety.EnsureNoReparsePoints(parent, destinationFull, context);

        DiskSpace.EnsureAvailable(parent, expectedSize, context);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationFull))
            {
                File.Delete(destinationFull);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            FileStream? output = null;
            var succeeded = false;
            try
            {
                // Reserve and hold the destination exclusively for the whole
                // request/response. This prevents a second process from replacing
                // or extending the temporary file while it is being written.
                output = new FileStream(destinationFull, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                DiskSpace.Preallocate(output, expectedSize, context);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (IsTransient(response.StatusCode) && attempt < 3)
                {
                    await DelayForRetryAsync(response, attempt, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw PkgException.Network(
                        $"{context}: HTTP {(int)response.StatusCode} from {SanitizeUrl(uri)}.");
                }

                if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedSize)
                {
                    throw new RetryableDownloadException(
                        $"{context}: server reported {contentLength} bytes, index requires {expectedSize}.");
                }

                var hash = IncrementalHash.CreateHash(hashAlgorithm);
                long total = 0;
                using (hash)
                {
                    await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token)
                                     .ConfigureAwait(false))
                    {
                        var buffer = new byte[128 * 1024];
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            total = checked(total + read);
                            if (total > expectedSize)
                            {
                                throw new RetryableDownloadException(
                                    $"{context}: response is larger than the indexed size {expectedSize}.");
                            }

                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                        }
                    }

                    if (total != expectedSize)
                    {
                        throw new RetryableDownloadException(
                            $"{context}: received {total} bytes, expected {expectedSize}.");
                    }

                    var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                    if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    {
                        throw new RetryableDownloadException(
                            $"{context}: {hashLabel} mismatch (expected {expectedHash}, received {actualHash}).");
                    }
                }

                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                output.Flush(true);
                succeeded = true;
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"request timed out after {OperationTimeout.TotalSeconds:0} seconds");
            }
            catch (RetryableDownloadException exception)
            {
                lastError = exception;
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
            }
            catch (IOException exception)
            {
                lastError = exception;
            }
            finally
            {
                if (output is not null)
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                }

                if (!succeeded)
                {
                    TryDelete(destinationFull);
                }
            }

            if (attempt < 3)
            {
                await Task.Delay(BaseRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw PkgException.Network(
            $"{context}: failed to download {SanitizeUrl(uri)} after 3 attempts: " +
            $"{SecureText.RedactSecrets(lastError?.Message ?? "unknown error")}",
            lastError);
    }

    public void Dispose() => _client.Dispose();

    private static void ValidateUri(Uri uri, string context)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw PkgException.Integrity($"{context}: download URL is not absolute.");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw PkgException.Integrity($"{context}: download URL must use HTTP or HTTPS.");
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static async Task DelayForRetryAsync(HttpResponseMessage response, int attempt, CancellationToken token)
    {
        var delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        if (delay is null || delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(30))
        {
            delay = BaseRetryDelay * attempt;
        }

        await Task.Delay(delay.Value, token).ConfigureAwait(false);
    }

    private static string SanitizeUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RetryableDownloadException : Exception
    {
        public RetryableDownloadException(string message) : base(message)
        {
        }
    }
}
