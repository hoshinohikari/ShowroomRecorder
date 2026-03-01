using System.Collections.Concurrent;
using System.Net;
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

public class HttpUtils
{
    private readonly RestClient _client;
    private readonly Dictionary<string, string> _defaultHeaders;

    public HttpUtils(string ip)
    {
        // 设置系统环境变量以优先使用 IPv4
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2UNENCRYPTEDSUPPORT",
            "false");
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_DISABLEIPV6", "true");

        var options = new RestClientOptions(ip)
        {
            ThrowOnAnyError = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            //AutomaticDecompression = DecompressionMethods.GZip
        };

        var proxy = BuildProxyFromConfig();
        if (proxy != null)
        {
            options.Proxy = proxy;
            Log.Information("HTTP proxy enabled: {Proxy}", ConfigUtils.Config.Proxy);
        }

        _client = new RestClient(options);

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
    }

    private static IWebProxy? BuildProxyFromConfig()
    {
        var proxyText = ConfigUtils.Config.Proxy?.Trim();
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
            // .NET uses socks5 URI scheme; map socks5h for compatibility.
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

    private void AddDefaultHeaders(RestRequest request, bool includeCookie)
    {
        request.AddHeaders(_defaultHeaders);
        if (includeCookie && !string.IsNullOrWhiteSpace(ConfigUtils.Config.SrId))
            request.AddHeader("cookie", $"sr_id={ConfigUtils.Config.SrId}");
    }

    private RestRequest BuildRequest(string resource, List<(string, string)> param, double? timeoutMs,
        bool includeCookie,
        Version httpVersion)
    {
        var request = new RestRequest(resource)
        {
            Timeout = GetTimeout(timeoutMs),
            Version = httpVersion
        };

        AddDefaultHeaders(request, includeCookie);

        foreach (var p in param) request.AddParameter(p.Item1, p.Item2);

        return request;
    }

    public async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param,
        double? timeoutMs = null, bool includeCookie = false)
    {
        try
        {
            var h2Request = BuildRequest(resource, param, timeoutMs, includeCookie, HttpVersion.Version20);
            var response = await _client.GetAsync(h2Request);

            if (response.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(response.Content))
                return (response.StatusCode, response.Content);

            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
                Log.Error(response.ErrorMessage);

            return (response.StatusCode, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HTTP/2 GET failed, fallback to HTTP/1.1");
        }

        try
        {
            var h1Request = BuildRequest(resource, param, timeoutMs, includeCookie, HttpVersion.Version11);
            var response = await _client.GetAsync(h1Request);

            if (response.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(response.Content))
                return (response.StatusCode, response.Content);

            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
                Log.Error(response.ErrorMessage);

            return (response.StatusCode, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP GET failed (HTTP/2 and HTTP/1.1)");
            return (HttpStatusCode.ServiceUnavailable, string.Empty);
        }
    }

    public async Task<byte[]> DownloadFile(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        try
        {
            var h2Request = BuildRequest(resource, param, timeoutMs, false, HttpVersion.Version20);
            var response = await _client.DownloadDataAsync(h2Request);
            return response ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HTTP/2 download failed, fallback to HTTP/1.1");
        }

        try
        {
            var h1Request = BuildRequest(resource, param, timeoutMs, false, HttpVersion.Version11);
            var response = await _client.DownloadDataAsync(h1Request);
            return response ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Download file failed (HTTP/2 and HTTP/1.1)");
            return Array.Empty<byte>();
        }
    }
}