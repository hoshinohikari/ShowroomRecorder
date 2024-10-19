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

    public static async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param)
    {
        return await HttpUtils.Get(resource, param);
    }
}

public class HttpUtils
{
    private readonly RestClient _client;

    public HttpUtils(string ip)
    {
        var options = new RestClientOptions(ip)
        {
            ThrowOnAnyError = true,
            Timeout = TimeSpan.FromSeconds(ConfigUtils.Config.Interval), // 15 second
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            //AutomaticDecompression = DecompressionMethods.GZip
        };
        _client = new RestClient(options);
    }

    public async Task<(HttpStatusCode, string)> Get(string resource, List<(string, string)> param)
    {
        var request = new RestRequest(resource);

        request.AddHeaders(new Dictionary<string, string>
        {
            {
                "Accept", "*/*"
            },
            {
                "Accept-Encoding", "gzip, deflate, br, zstd"
            },
            {
                "Accept-Language", "en-US,en;q=0.9,ja;q=0.8,ja-JP;q=0.7,zh-CN;q=0.6,zh;q=0.5"
            },
            {
                "Connection", "keep-alive"
            },
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
}