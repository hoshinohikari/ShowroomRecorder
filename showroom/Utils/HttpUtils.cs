using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using RestSharp;
using Serilog;

namespace showroom.Utils;

public static class ShowroomHttp
{
    private static readonly HttpUtils HttpUtils;

    static ShowroomHttp()
    {
        HttpUtils = new HttpUtils("https://www.showroom-live.com");
    }

    public static async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        return await HttpUtils.Get(resource, param, timeoutMs, true);
    }
}

public static class ShowroomDownloadHttp
{
    private static readonly ConcurrentDictionary<string, HttpUtils> DynamicClients = new();

    private static (HttpUtils Client, string Resource) ResolveClientAndResource(string resource)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            throw new ArgumentException(
                "ShowroomDownloadHttp requires an absolute URL. Relative paths are not allowed.",
                nameof(resource));

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var client = DynamicClients.GetOrAdd(origin, static o => new HttpUtils(o));
        return (client, uri.PathAndQuery);
    }

    public static async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.Get(resolvedResource, param, timeoutMs);
    }

    public static async Task<byte[]> DownloadFile(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.DownloadFile(resolvedResource, param, timeoutMs);
    }
}

public static class WebDavHttp
{
    private static readonly Lock ConfigLock = new();
    private static readonly ConcurrentDictionary<string, HttpUtils> DynamicClients = new();
    private static Dictionary<string, string> _authHeaders = [];
    private static bool _allowInsecureCertificate;

    public static void Configure(string username, string password, bool allowInsecureCertificate = false)
    {
        lock (ConfigLock)
        {
            _authHeaders = [];
            _authHeaders["Accept-Encoding"] = "gzip, deflate, br";
            _allowInsecureCertificate = allowInsecureCertificate;
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                _authHeaders["Authorization"] = $"Basic {token}";
            }

            DynamicClients.Clear();
        }
    }

    private static (HttpUtils Client, string Resource) ResolveClientAndResource(string resource)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            throw new ArgumentException(
                "WebDavHttp requires an absolute URL. Relative paths are not allowed.",
                nameof(resource));

        var origin = uri.GetLeftPart(UriPartial.Authority);
        Dictionary<string, string>? headers = null;
        lock (ConfigLock)
        {
            if (_authHeaders.Count > 0)
                headers = new Dictionary<string, string>(_authHeaders);
        }

        bool allowInsecureCertificate;
        lock (ConfigLock)
        {
            allowInsecureCertificate = _allowInsecureCertificate;
        }

        var client = DynamicClients.GetOrAdd(origin, _ => new HttpUtils(origin, headers, allowInsecureCertificate));
        return (client, uri.PathAndQuery);
    }

    public static async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.Get(resolvedResource, param, timeoutMs);
    }

    public static async Task<byte[]> DownloadFile(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.DownloadFile(resolvedResource, param, timeoutMs);
    }

    public static async Task<Stream?> DownloadStream(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.DownloadStream(resolvedResource, param, timeoutMs);
    }

    public static async Task<HttpStatusCode> PutBytes(string resource, byte[] payload, string contentType,
        double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.PutBytes(resolvedResource, payload, contentType, timeoutMs);
    }

    public static async Task<HttpStatusCode> PutFile(string resource, string filePath, string contentType,
        double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.PutFile(resolvedResource, filePath, contentType, timeoutMs);
    }

    public static async Task<HttpStatusCode> Delete(string resource, double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.Delete(resolvedResource, timeoutMs);
    }

    public static async Task<HttpStatusCode> ProppatchTimestamps(string resource, DateTime creationTimeUtc,
        DateTime modifiedTimeUtc, double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.ProppatchTimestamps(resolvedResource, creationTimeUtc, modifiedTimeUtc, timeoutMs);
    }

    public static async Task<(HttpStatusCode StatusCode, long? ContentLength, string Error)> HeadContentLength(
        string resource, double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.HeadContentLength(resolvedResource, timeoutMs);
    }

    public static async Task<HttpStatusCode> PatchAppendFile(string resource, string filePath, long offset,
        string contentType, double? timeoutMs = null)
    {
        var (client, resolvedResource) = ResolveClientAndResource(resource);
        return await client.PatchAppendFile(resolvedResource, filePath, offset, contentType, timeoutMs);
    }
}

public class HttpUtils
{
    private readonly RestClient _client;
    private readonly Dictionary<string, string> _defaultHeaders;
    private readonly Version _preferredHttpVersion;
    private readonly HttpClient _streamingClient;

    public HttpUtils(string ip, Dictionary<string, string>? customHeaders = null, bool allowInsecureCertificate = false)
    {
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2UNENCRYPTEDSUPPORT",
            "false");

        var baseUri = new Uri(ip);
        var proxy = BuildProxyFromConfig();
        _preferredHttpVersion = ResolvePreferredHttpVersion(baseUri, proxy != null, allowInsecureCertificate);
        Log.Information("HTTP version locked by ALPN: host={Host}, version={Version}", baseUri.Host,
            _preferredHttpVersion);

        var options = new RestClientOptions(ip)
        {
            ThrowOnAnyError = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        if (allowInsecureCertificate)
            options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        if (proxy != null)
        {
            options.Proxy = proxy;
            Log.Information("HTTP proxy enabled: {Proxy}", ConfigUtils.Config.Proxy);
        }

        _client = new RestClient(options);

        var streamingHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            EnableMultipleHttp2Connections = _preferredHttpVersion == HttpVersion.Version20,
            UseProxy = proxy != null
        };
        if (proxy != null)
            streamingHandler.Proxy = proxy;
        if (allowInsecureCertificate)
            streamingHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        _streamingClient = new HttpClient(streamingHandler)
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = _preferredHttpVersion,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        _defaultHeaders = new Dictionary<string, string>
        {
            { "Accept", "*/*" },
            { "Accept-Encoding", "gzip, deflate, br, zstd" },
            { "Accept-Language", "en-US,en;q=0.9,ja;q=0.8,ja-JP;q=0.7,zh-CN;q=0.6,zh;q=0.5" },
            { "Connection", "keep-alive" },
            {
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36"
            }
        };

        if (customHeaders != null)
            foreach (var header in customHeaders)
                _defaultHeaders[header.Key] = header.Value;
    }

    private static Version ResolvePreferredHttpVersion(Uri baseUri, bool usingProxy, bool allowInsecureCertificate)
    {
        if (usingProxy)
        {
            Log.Warning("ALPN probe skipped due proxy for {Host}, fallback to HTTP/1.1 lock", baseUri.Host);
            return HttpVersion.Version11;
        }

        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return HttpVersion.Version11;

        try
        {
            var port = baseUri.IsDefaultPort ? 443 : baseUri.Port;
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(baseUri.Host, port);
            if (!connectTask.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("ALPN TCP connect timeout");

            using var sslStream = new SslStream(tcpClient.GetStream(), false);
            var authOptions = new SslClientAuthenticationOptions
            {
                TargetHost = baseUri.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols =
                [
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                ]
            };
            if (allowInsecureCertificate)
                authOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            sslStream.AuthenticateAsClientAsync(authOptions).GetAwaiter().GetResult();
            var negotiated = sslStream.NegotiatedApplicationProtocol;

            return negotiated == SslApplicationProtocol.Http2 ? HttpVersion.Version20 : HttpVersion.Version11;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ALPN probe failed for {Host}, fallback to HTTP/1.1 lock", baseUri.Host);
            return HttpVersion.Version11;
        }
    }

    private static IWebProxy? BuildProxyFromConfig()
    {
        var proxyText = ConfigUtils.Config.Proxy.Trim();
        if (string.IsNullOrWhiteSpace(proxyText))
            return null;

        if (!Uri.TryCreate(proxyText, UriKind.Absolute, out var proxyUri))
        {
            Log.Warning("Invalid proxy format \"{Proxy}\", fallback to direct connection", proxyText);
            return null;
        }

        var scheme = proxyUri.Scheme.ToLowerInvariant();
        if (scheme == "socks5h")
        {
            proxyUri = new UriBuilder(proxyUri) { Scheme = "socks5", Port = proxyUri.Port }.Uri;
            scheme = "socks5";
        }

        if (scheme is not ("http" or "https" or "socks5"))
        {
            Log.Warning("Unsupported proxy scheme \"{Scheme}\", fallback to direct connection", proxyUri.Scheme);
            return null;
        }

        return new WebProxy(proxyUri);
    }

    private static TimeSpan GetTimeout(double? timeoutMs)
    {
        return timeoutMs.HasValue
            ? TimeSpan.FromMilliseconds(timeoutMs.Value)
            : ConfigUtils.Interval;
    }

    private static TimeSpan GetPatchTimeout(double? timeoutMs, long remainingBytes)
    {
        if (timeoutMs.HasValue)
            return TimeSpan.FromMilliseconds(timeoutMs.Value);

        // Keep a practical default for large append uploads:
        // assume at least ~256 KB/s, plus 20s base overhead.
        const double bytesPerSecondFloor = 256 * 1024;
        var seconds = 20 + remainingBytes / bytesPerSecondFloor;
        seconds = Math.Clamp(seconds, 30, 18000); // 30s ~ 5h
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan GetPutTimeout(double? timeoutMs, long? contentLength)
    {
        if (timeoutMs.HasValue)
            return TimeSpan.FromMilliseconds(timeoutMs.Value);

        if (!contentLength.HasValue || contentLength.Value <= 0)
            return TimeSpan.FromSeconds(60);

        // Conservative default for upload throughput, with startup overhead.
        const double bytesPerSecondFloor = 256 * 1024;
        var seconds = 20 + contentLength.Value / bytesPerSecondFloor;
        seconds = Math.Clamp(seconds, 30, 18000);
        return TimeSpan.FromSeconds(seconds);
    }

    private void AddDefaultHeaders(RestRequest request, bool includeCookie)
    {
        request.AddHeaders(_defaultHeaders);
        if (includeCookie && !string.IsNullOrWhiteSpace(ConfigUtils.Config.SrId))
            request.AddHeader("cookie", $"sr_id={ConfigUtils.Config.SrId}");
    }

    private void AddDefaultHeaders(HttpRequestMessage request, bool includeCookie)
    {
        foreach (var header in _defaultHeaders)
            if (!string.Equals(header.Key, "Connection", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (includeCookie && !string.IsNullOrWhiteSpace(ConfigUtils.Config.SrId))
            request.Headers.TryAddWithoutValidation("cookie", $"sr_id={ConfigUtils.Config.SrId}");
    }

    private void AddUploadHeaders(HttpRequestMessage request)
    {
        if (_defaultHeaders.TryGetValue("Authorization", out var authorization) &&
            !string.IsNullOrWhiteSpace(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

        if (_defaultHeaders.TryGetValue("User-Agent", out var userAgent) &&
            !string.IsNullOrWhiteSpace(userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
    }

    private RestRequest BuildRequest(string resource, List<(string, string)> param, double? timeoutMs,
        bool includeCookie)
    {
        Log.Verbose(
            "HTTP request build: resource={Resource}, params={ParamCount}, timeoutMs={TimeoutMs}, includeCookie={IncludeCookie}, http={HttpVersion}",
            resource, param.Count, timeoutMs, includeCookie, _preferredHttpVersion);
        var request = new RestRequest(resource)
        {
            Timeout = GetTimeout(timeoutMs),
            Version = _preferredHttpVersion
        };

        AddDefaultHeaders(request, includeCookie);

        foreach (var p in param) request.AddParameter(p.Item1, p.Item2);

        return request;
    }

    private async Task<(HttpStatusCode StatusCode, string Error)> ExecutePutWithStreamingHttpClient(
        string resource,
        HttpContent content,
        double? timeoutMs)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, resource);
            // PUT is forced to HTTP/1.1 to avoid unstable HTTP/2 upload behavior.
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Content = content;
            request.Headers.ExpectContinue = false;
            AddUploadHeaders(request);

            using var cts = new CancellationTokenSource(GetPutTimeout(timeoutMs, content.Headers.ContentLength));
            using var response = await _streamingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            return (response.StatusCode, string.Empty);
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    public async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param,
        double? timeoutMs = null, bool includeCookie = false)
    {
        try
        {
            var request = BuildRequest(resource, param, timeoutMs, includeCookie);
            var response = await _client.GetAsync(request);

            if (response.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(response.Content))
            {
                Log.Verbose("HTTP GET success: resource={Resource}, length={Length}", resource,
                    response.Content.Length);
                return (response.StatusCode, response.Content);
            }

            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
                Log.Error(response.ErrorMessage);
            else
                Log.Warning("HTTP GET non-OK: resource={Resource}, status={Status}", resource, response.StatusCode);

            return (response.StatusCode, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP GET failed");
            return (HttpStatusCode.ServiceUnavailable, string.Empty);
        }
    }

    public async Task<byte[]> DownloadFile(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        try
        {
            var request = BuildRequest(resource, param, timeoutMs, false);
            var response = await _client.DownloadDataAsync(request);
            Log.Verbose("HTTP download success: resource={Resource}, bytes={Bytes}", resource, response?.Length ?? 0);
            return response ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Download file failed");
            return Array.Empty<byte>();
        }
    }

    public async Task<Stream?> DownloadStream(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        try
        {
            var request = BuildRequest(resource, param, timeoutMs, false);
            var response = await _client.DownloadStreamAsync(request);
            if (response != null)
            {
                Log.Verbose("HTTP stream download success: resource={Resource}", resource);
                return response;
            }

            Log.Warning("HTTP stream download empty: resource={Resource}", resource);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP stream download failed");
            return null;
        }
    }

    public async Task<HttpStatusCode> PutBytes(string resource, byte[] payload,
        string contentType = "application/octet-stream",
        double? timeoutMs = null)
    {
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = payload.LongLength;
        var (status, error) = await ExecutePutWithStreamingHttpClient(resource, content, timeoutMs);
        if ((int)status is >= 200 and < 300)
            return status;

        Log.Warning("HTTP PUT non-OK: resource={Resource}, status={Status}, error={Error}", resource, status, error);
        return status;
    }

    public async Task<HttpStatusCode> PutFile(string resource, string filePath,
        string contentType = "application/octet-stream",
        double? timeoutMs = null)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new StreamContent(stream, 128 * 1024);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Headers.ContentLength = stream.Length;

            var (status, error) = await ExecutePutWithStreamingHttpClient(resource, content, timeoutMs);
            if ((int)status is >= 200 and < 300)
                return status;

            Log.Warning("HTTP PUT file non-OK: resource={Resource}, status={Status}, error={Error}", resource, status,
                error);
            return status;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP PUT file failed: {FilePath}", filePath);
            return HttpStatusCode.ServiceUnavailable;
        }
    }

    public async Task<HttpStatusCode> Delete(string resource, double? timeoutMs = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, resource);
            request.Version = _preferredHttpVersion;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            AddDefaultHeaders(request, false);

            using var cts = new CancellationTokenSource(GetTimeout(timeoutMs));
            using var response =
                await _streamingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode is >= 200 and < 300)
                return response.StatusCode;

            Log.Warning("HTTP DELETE non-OK: resource={Resource}, status={Status}", resource, response.StatusCode);
            return response.StatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP DELETE failed: resource={Resource}", resource);
            return HttpStatusCode.ServiceUnavailable;
        }
    }

    public async Task<(HttpStatusCode StatusCode, long? ContentLength, string Error)> HeadContentLength(
        string resource, double? timeoutMs = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, resource);
            request.Version = _preferredHttpVersion;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            AddDefaultHeaders(request, false);

            using var cts = new CancellationTokenSource(GetTimeout(timeoutMs));
            using var response =
                await _streamingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (response.StatusCode, response.Content.Headers.ContentLength, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HTTP HEAD failed: resource={Resource}", resource);
            return (HttpStatusCode.ServiceUnavailable, null, ex.Message);
        }
    }

    public async Task<HttpStatusCode> PatchAppendFile(string resource, string filePath, long offset,
        string contentType = "application/octet-stream", double? timeoutMs = null)
    {
        try
        {
            if (!File.Exists(filePath))
                return HttpStatusCode.NotFound;

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (offset < 0 || offset > stream.Length)
                return HttpStatusCode.RequestedRangeNotSatisfiable;

            stream.Seek(offset, SeekOrigin.Begin);
            using var content = new StreamContent(stream, 128 * 1024);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Headers.ContentLength = stream.Length - offset;

            using var request = new HttpRequestMessage(new HttpMethod("PATCH"), resource);
            // PATCH append is forced to HTTP/1.1 to avoid unstable HTTP/2 upload behavior.
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Content = content;
            request.Headers.ExpectContinue = false;
            request.Headers.TryAddWithoutValidation("X-Update-Range", "append");
            AddUploadHeaders(request);

            var patchTimeout = GetPatchTimeout(timeoutMs, stream.Length - offset);
            using var cts = new CancellationTokenSource(patchTimeout);
            using var response =
                await _streamingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode is >= 200 and < 300)
                return response.StatusCode;

            Log.Warning("HTTP PATCH append non-OK: resource={Resource}, status={Status}, offset={Offset}",
                resource, response.StatusCode, offset);
            return response.StatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP PATCH append failed: resource={Resource}, filePath={FilePath}, offset={Offset}",
                resource, filePath, offset);
            return HttpStatusCode.ServiceUnavailable;
        }
    }

    public async Task<HttpStatusCode> ProppatchTimestamps(string resource, DateTime creationTimeUtc,
        DateTime modifiedTimeUtc, double? timeoutMs = null)
    {
        try
        {
            var creationIso = creationTimeUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            var modifiedRfc1123 = modifiedTimeUtc.ToUniversalTime().ToString("R");
            var body =
                $"""
                 <?xml version="1.0" encoding="utf-8"?>
                 <d:propertyupdate xmlns:d="DAV:">
                   <d:set>
                     <d:prop>
                       <d:creationdate>{creationIso}</d:creationdate>
                       <d:getlastmodified>{modifiedRfc1123}</d:getlastmodified>
                     </d:prop>
                   </d:set>
                 </d:propertyupdate>
                 """;

            using var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), resource);
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
            AddUploadHeaders(request);

            using var cts = new CancellationTokenSource(GetTimeout(timeoutMs));
            using var response =
                await _streamingClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if ((int)response.StatusCode is >= 200 and < 300 || response.StatusCode == HttpStatusCode.MultiStatus)
                return response.StatusCode;

            Log.Warning("HTTP PROPPATCH non-OK: resource={Resource}, status={Status}", resource, response.StatusCode);
            return response.StatusCode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HTTP PROPPATCH failed: resource={Resource}", resource);
            return HttpStatusCode.ServiceUnavailable;
        }
    }
}