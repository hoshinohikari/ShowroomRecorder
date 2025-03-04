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
        return await HttpUtils.Get(resource, param, timeoutMs);
    }
}

public static class ShowroomDownloadHttp
{
    private static readonly HttpUtils HttpUtils;

    static ShowroomDownloadHttp()
    {
        HttpUtils = new HttpUtils("https://hls-css.live.showroom-live.com");
    }

    public static async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        return await HttpUtils.Get(resource, param, timeoutMs);
    }

    public static async Task<byte[]> DownloadFile(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        return await HttpUtils.DownloadFile(resource, param, timeoutMs);
    }
}

public class HttpUtils
{
    private readonly RestClient _client;

    public HttpUtils(string ip)
    {
        // 设置系统环境变量以优先使用 IPv4
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2UNENCRYPTEDSUPPORT", "false");
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_DISABLEIPV6", "true");
        
        var options = new RestClientOptions(ip)
        {
            ThrowOnAnyError = true,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            //AutomaticDecompression = DecompressionMethods.GZip
        };

        _client = new RestClient(options);
    }

    public async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        var request = new RestRequest(resource)
        {
            // 如果提供了超时参数，则设置请求级别的超时
            Timeout = timeoutMs.HasValue
                ? TimeSpan.FromMilliseconds(timeoutMs.Value)
                :
                // 否则使用默认超时
                TimeSpan.FromSeconds(ConfigUtils.Config.Interval)
        };

        request.AddHeaders(new Dictionary<string, string>
        {
            { "Accept", "*/*" },
            { "Accept-Encoding", "gzip, deflate, br, zstd" },
            { "Accept-Language", "en-US,en;q=0.9,ja;q=0.8,ja-JP;q=0.7,zh-CN;q=0.6,zh;q=0.5" },
            { "Connection", "keep-alive" },
            {
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            }
        });

        foreach (var p in param) request.AddParameter(p.Item1, p.Item2);

        var response = await _client.GetAsync(request);

        if (response.StatusCode == HttpStatusCode.OK) return (response.StatusCode, response.Content)!;

        Log.Error(response.ErrorMessage!);
        return (response.StatusCode, null)!;
    }

    public async Task<byte[]> DownloadFile(string resource, List<(string, string)> param,
        double? timeoutMs = null)
    {
        var request = new RestRequest(resource)
        {
            Timeout = timeoutMs.HasValue
                ? TimeSpan.FromMilliseconds(timeoutMs.Value)
                : TimeSpan.FromSeconds(ConfigUtils.Config.Interval)
        };

        request.AddHeaders(new Dictionary<string, string>
        {
            { "Accept", "*/*" },
            { "Accept-Encoding", "gzip, deflate, br, zstd" },
            { "Accept-Language", "en-US,en;q=0.9,ja;q=0.8,ja-JP;q=0.7,zh-CN;q=0.6,zh;q=0.5" },
            { "Connection", "keep-alive" },
            {
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            }
        });

        foreach (var p in param) request.AddParameter(p.Item1, p.Item2);

        var response = await _client.DownloadDataAsync(request);

        return response!;
    }
}