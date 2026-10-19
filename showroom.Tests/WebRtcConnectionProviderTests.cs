using System.Net;
using System.Text;
using System.Text.Json;
using showroom.WebRtc;
using showroom.Utils;
using Xunit;

namespace showroom.Tests;

public class WebRtcConnectionProviderTests
{
    [Theory]
    [InlineData("{\"live_status\":0}", false)]
    [InlineData("{\"live_status\":1,\"live_id\":0}", false)]
    [InlineData("{\"live_status\":2}", true)]
    [InlineData("{\"live_status\":3}", null)]
    [InlineData("{\"live_status\":-1}", null)]
    [InlineData("{}", null)]
    [InlineData("{\"live_status\":null}", null)]
    [InlineData("{\"live_status\":\"2\"}", null)]
    [InlineData("{\"live_status\":2147483648}", null)]
    [InlineData("null", null)]
    [InlineData("[]", null)]
    public async Task Live_status_distinguishes_broadcasting_offline_and_unknown(string response, bool? expected)
    {
        var handler = new LiveStatusHandler(response);
        await using var provider = new ShowroomWebRtcConnectionProvider(new HttpClient(handler));

        var actual = await provider.GetLiveStatusAsync(270186, TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
        Assert.Equal(["/api/live/live_info"], handler.RequestPaths);
    }

    [Fact]
    public async Task Failed_live_status_query_is_not_reported_as_confirmed_offline()
    {
        var handler = new LiveStatusHandler("{}", HttpStatusCode.ServiceUnavailable);
        await using var provider = new ShowroomWebRtcConnectionProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetLiveStatusAsync(270186, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Offline_status_one_ends_the_downloader_without_requesting_a_stream_or_retrying()
    {
        var handler = new LiveStatusHandler("{\"live_status\":1,\"live_id\":0}");
        var provider = new ShowroomWebRtcConnectionProvider(new HttpClient(handler));
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-offline-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 270186, new Configs(),
            new ProviderFactory(provider), new FfmpegMkvMuxerFactory(), TimeProvider.System, () => 0, output);

        try
        {
            await downloader.DownloadAsync().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            Assert.Equal(["/api/live/live_info"], handler.RequestPaths);
            Assert.Empty(Directory.GetFiles(output));
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Original_webrtc_url_reaches_https_signaling_without_changing_its_authentication_parameters()
    {
        // Match the HAR's type/quality/scheme; use synthetic authentication values.
        const string originalUrl = "webrtc://media.example.test/live/stream?txSecret=test%2Fsignature+value&txTime=abcdef";
        var handler = new SignalingHandler(Response(
            Stream("hls", 1000, "https://media.example.test/live.m3u8"),
            Stream("webrtc", 0, "webrtc://media.example.test/auto"),
            Stream("webrtc", 1000, originalUrl)));
        await using var provider = new ShowroomWebRtcConnectionProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<IOException>(() => provider.ConnectAsync(270161, TestContext.Current.CancellationToken));

        Assert.Contains("test-stop-after-signaling", error.Message);
        Assert.Equal("media.example.test", handler.ScheduledDomain);
        Assert.Equal(originalUrl, handler.SubmittedStreamUrl);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Equal("https", uri.Scheme));
    }

    [Theory]
    [InlineData("{\"streaming_url_list\":[]}", "empty")]
    [InlineData("{}", "streaming_url_list")]
    [InlineData("{\"streaming_url_list\":null}", "streaming_url_list")]
    public async Task Empty_or_missing_stream_lists_fail_before_signaling(string response, string expectedReason)
    {
        var handler = new SignalingHandler(response);
        await using var provider = new ShowroomWebRtcConnectionProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<IOException>(() => provider.ConnectAsync(270161, TestContext.Current.CancellationToken));

        Assert.Contains(expectedReason, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_original_quality_does_not_select_auto_lower_quality_or_hls_and_reports_safe_metadata()
    {
        var handler = new SignalingHandler(Response(
            Stream("hls", 1000, "https://media.example.test/live?secret=hidden"),
            Stream("webrtc", 0, "webrtc://media.example.test/auto?secret=hidden"),
            Stream("webrtc", 200, "webrtc://media.example.test/low?secret=hidden")));
        await using var provider = new ShowroomWebRtcConnectionProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<IOException>(() => provider.ConnectAsync(270161, TestContext.Current.CancellationToken));

        Assert.Contains("original-quality", error.Message);
        Assert.Contains("quality=0", error.Message);
        Assert.Contains("quality=200", error.Message);
        Assert.DoesNotContain("hidden", error.Message);
        Assert.DoesNotContain("media.example.test", error.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://media.example.test/live?secret=hidden")]
    [InlineData("file:///tmp/live?secret=hidden")]
    [InlineData("webrtc:///live?secret=hidden")]
    [InlineData("webrtc://user:hidden@media.example.test/live")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Invalid_original_urls_are_rejected_without_logging_the_url(string? url)
    {
        var handler = new SignalingHandler(Response(Stream("webrtc", 1000, url)));
        await using var provider = new ShowroomWebRtcConnectionProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<IOException>(() => provider.ConnectAsync(270161, TestContext.Current.CancellationToken));

        Assert.Contains("invalid WebRTC stream URL", error.Message);
        Assert.DoesNotContain("hidden", error.Message);
        Assert.DoesNotContain("media.example.test", error.Message);
        Assert.Single(handler.Requests);
    }

    private static object Stream(string type, int quality, string? url) => new { type, quality, url };
    private static string Response(params object[] streams) => JsonSerializer.Serialize(new { streaming_url_list = streams });

    private sealed class ProviderFactory(IWebRtcConnectionProvider provider) : IWebRtcConnectionProviderFactory
    {
        public IWebRtcConnectionProvider Create(string proxy) => provider;
    }

    private sealed class LiveStatusHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);
            if (path != "/api/live/live_info")
                throw new InvalidOperationException("An offline room must not request streaming URLs or signaling.");
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SignalingHandler(string streamsResponse) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string? ScheduledDomain { get; private set; }
        public string? SubmittedStreamUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            string response;
            if (uri.AbsolutePath == "/api/live/streaming_url")
            {
                response = streamsResponse;
            }
            else if (uri.AbsolutePath == "/signal_query")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                ScheduledDomain = body.RootElement.GetProperty("domain").GetString();
                response = "{\"errcode\":0,\"data\":{\"signal_domain\":\"signal.example.test\"}}";
            }
            else if (uri.AbsolutePath == "/webrtc/v1/pullstream")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                SubmittedStreamUrl = body.RootElement.GetProperty("streamurl").GetString();
                // Stop after inspecting the real provider's offer request; no remote peer is needed.
                response = "{\"errcode\":1,\"errmsg\":\"test-stop-after-signaling\"}";
            }
            else
            {
                throw new InvalidOperationException("Unexpected signaling request.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
