using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace showroom.WebRtc;

internal interface IWebRtcConnectionProviderFactory
{
    IWebRtcConnectionProvider Create(string proxy);
}

internal interface IWebRtcConnectionProvider : IAsyncDisposable
{
    Task<bool?> GetLiveStatusAsync(long roomId, CancellationToken cancellationToken);
    Task<IWebRtcMediaSession> ConnectAsync(long roomId, CancellationToken cancellationToken);
}

internal interface IWebRtcMediaSession : IAsyncDisposable
{
    RTCPeerConnectionState ConnectionState { get; }
    bool TryReadPacket(out WebRtcMediaPacket packet);
    void SendNack(bool isVideo, uint mediaSsrc, ushort packetId, ushort bitmask);
    void RequestKeyframe(uint mediaSsrc);
}

internal sealed class ShowroomWebRtcConnectionProviderFactory : IWebRtcConnectionProviderFactory
{
    public IWebRtcConnectionProvider Create(string proxy) => new ShowroomWebRtcConnectionProvider(proxy);
}

/// <summary>Owns the HTTP proxy handler and the SHOWROOM signaling flow for one downloader.</summary>
internal sealed class ShowroomWebRtcConnectionProvider : IWebRtcConnectionProvider
{
    private readonly HttpClient _http;
    private bool _disposed;

    public ShowroomWebRtcConnectionProvider(string proxy) : this(CreateHttpClient(proxy))
    {
    }

    internal ShowroomWebRtcConnectionProvider(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SHOWROOM-WebRTC-Recorder/1.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://www.showroom-live.com/");
        _http.DefaultRequestHeaders.Add("Origin", "https://www.showroom-live.com");
    }

    private static HttpClient CreateHttpClient(string proxy)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri))
                throw new InvalidOperationException($"Invalid WebRTC HTTP proxy URI: '{proxy}'.");
            if (proxyUri.Scheme.Equals("socks5h", StringComparison.OrdinalIgnoreCase))
                proxyUri = new UriBuilder(proxyUri) { Scheme = "socks5", Port = proxyUri.Port }.Uri;
            if (proxyUri.Scheme is not ("http" or "https" or "socks5"))
                throw new InvalidOperationException($"Unsupported WebRTC HTTP proxy scheme '{proxyUri.Scheme}'. Use http, https, or socks5.");
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<bool?> GetLiveStatusAsync(long roomId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"https://www.showroom-live.com/api/live/live_info?room_id={roomId}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("live_status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var value))
            return null;
        // SHOWROOM uses 2 for broadcasting; the observed offline response is 1, not only 0.
        // Unrecognized values remain unknown so they cannot prematurely terminate a recording.
        return value switch
        {
            2 => true,
            0 or 1 => false,
            _ => null
        };
    }

    public async Task<IWebRtcMediaSession> ConnectAsync(long roomId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"https://www.showroom-live.com/api/live/streaming_url?room_id={roomId}&abr_available=1&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var streams = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!streams.RootElement.TryGetProperty("streaming_url_list", out var streamList) || streamList.ValueKind != JsonValueKind.Array)
            throw new IOException("SHOWROOM response did not include streaming_url_list.");

        if (streamList.GetArrayLength() == 0)
            throw new IOException("SHOWROOM streaming_url_list is empty; original-quality WebRTC is unavailable. No protocol or quality fallback is allowed.");

        // Only protocol, numeric quality and scheme are safe to log: URLs carry authentication parameters.
        var summary = string.Join(", ", streamList.EnumerateArray().Take(16).Select(DescribeStream));
        Log.Debug("WebRTC stream availability for room {RoomId}: count={Count}, streams={Streams}",
            roomId, streamList.GetArrayLength(), summary);

        var stream = streamList.EnumerateArray().FirstOrDefault(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "webrtc" &&
            item.TryGetProperty("quality", out var quality) && quality.ValueKind == JsonValueKind.Number && quality.TryGetInt32(out var value) && value == 1000);
        if (stream.ValueKind == JsonValueKind.Undefined)
            throw new IOException($"SHOWROOM did not provide an original-quality WebRTC stream; no protocol or quality fallback is allowed. Available streams: {summary}.");

        var streamUrl = stream.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString() : null;
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri) || streamUri.Scheme != "webrtc" ||
            Uri.CheckHostName(streamUri.Host) == UriHostNameType.Unknown || !string.IsNullOrEmpty(streamUri.UserInfo))
            throw new IOException($"SHOWROOM returned an invalid WebRTC stream URL; expected webrtc:// with a valid host and no embedded credentials. Selected stream: {DescribeStream(stream)}.");

        using var schedule = await PostJsonAsTextAsync("https://webrtc-signal-scheduler.tlivesource.com/signal_query", new
        {
            domain = streamUri.Host,
            requestid = Guid.NewGuid().ToString("N")[..16],
            client_type = "Web",
            client_info = "ShowroomRecorder; native C#"
        }, cancellationToken).ConfigureAwait(false);
        if (schedule.RootElement.GetProperty("errcode").GetInt32() != 0)
            throw new IOException("SHOWROOM WebRTC signal scheduling failed.");
        var signalDomain = schedule.RootElement.GetProperty("data").GetProperty("signal_domain").GetString();
        if (string.IsNullOrWhiteSpace(signalDomain) || signalDomain.Contains('/') || Uri.CheckHostName(signalDomain) == UriHostNameType.Unknown)
            throw new IOException("Signal scheduler returned an invalid signal domain.");

        return await ShowroomPeerSession.ConnectAsync(_http, roomId, streamUrl!, signalDomain, cancellationToken).ConfigureAwait(false);
    }

    private static string DescribeStream(JsonElement stream)
    {
        if (stream.ValueKind != JsonValueKind.Object) return "malformed entry";
        var type = stream.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString() : null;
        type = type is "webrtc" or "hls" or "rtmp" ? type : "other";
        var quality = stream.TryGetProperty("quality", out var qualityValue) && qualityValue.ValueKind == JsonValueKind.Number &&
            qualityValue.TryGetInt32(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
        var url = stream.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String
            ? urlValue.GetString() : null;
        var scheme = string.IsNullOrEmpty(url) ? "missing" :
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Scheme : "invalid";
        scheme = scheme is "webrtc" or "https" or "http" or "rtmp" or "missing" or "invalid" ? scheme : "other";
        return $"{type}/quality={quality}/scheme={scheme}";
    }

    private async Task<JsonDocument> PostJsonAsTextAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _http.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private sealed class ShowroomPeerSession : IWebRtcMediaSession
    {
        private readonly RTCPeerConnection _peer;
        private readonly Channel<WebRtcMediaPacket> _packets;
        private int _overflow;
        private bool _disposed;

        private ShowroomPeerSession(RTCPeerConnection peer, Channel<WebRtcMediaPacket> packets)
        {
            _peer = peer;
            _packets = packets;
        }

        public RTCPeerConnectionState ConnectionState => _peer.connectionState;

        public static async Task<ShowroomPeerSession> ConnectAsync(
            HttpClient http, long roomId, string streamUrl, string signalDomain, CancellationToken cancellationToken)
        {
            var peer = new RTCPeerConnection(new RTCConfiguration
            {
                X_UseRsaForDtlsCertificate = true,
                X_UseRtpFeedbackProfile = true
            });
            var packets = Channel.CreateBounded<WebRtcMediaPacket>(new BoundedChannelOptions(4096)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            var session = new ShowroomPeerSession(peer, packets);
            try
            {
                peer.addTrack(new MediaStreamTrack(
                    new AudioFormat(AudioCodecsEnum.OPUS, 111, 48_000, 2, "minptime=10;useinbandfec=1"),
                    MediaStreamStatusEnum.RecvOnly) { NoDtmfSupport = true });
                peer.addTrack(new MediaStreamTrack(
                    new VideoFormat(VideoCodecsEnum.H264, 102, 90_000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"),
                    MediaStreamStatusEnum.RecvOnly));

                var videoChannel = peer.VideoStream.GetRTPChannel();
                videoChannel.RtpSocket.ReceiveBufferSize = Math.Max(videoChannel.RtpSocket.ReceiveBufferSize, 4 * 1024 * 1024);

                peer.OnRtpPacketReceived += (_, kind, packet) =>
                {
                    var media = new WebRtcMediaPacket(
                        kind == SDPMediaTypesEnum.video,
                        packet.Header.Timestamp,
                        packet.Header.SequenceNumber,
                        packet.Header.SyncSource,
                        packet.Header.MarkerBit != 0,
                        packet.Payload.ToArray());
                    if (!packets.Writer.TryWrite(media)) Interlocked.Exchange(ref session._overflow, 1);
                };
                peer.OnReceiveReport += (_, kind, report) =>
                {
                    if (report.SenderReport is not { } senderReport) return;
                    var media = new WebRtcMediaPacket(kind == SDPMediaTypesEnum.video,
                        senderReport.RtpTimestamp, 0, senderReport.SSRC, false, [], true, senderReport.NtpTimestamp);
                    if (!packets.Writer.TryWrite(media)) Interlocked.Exchange(ref session._overflow, 1);
                };

                var offer = peer.createOffer();
                offer.sdp = offer.sdp.Replace("OPUS/48000", "opus/48000")
                    .Replace("a=rtpmap:102 H264/90000", "a=rtpmap:102 H264/90000\r\na=rtcp-fb:102 nack\r\na=rtcp-fb:102 nack pli");
                await peer.setLocalDescription(offer).ConfigureAwait(false);

                using var requestContent = new StringContent(JsonSerializer.Serialize(new
                {
                    streamurl = streamUrl,
                    sessionid = Guid.NewGuid().ToString("N"),
                    clientinfo = "ShowroomRecorder; native C#",
                    localsdp = new { type = "offer", sdp = offer.sdp }
                }), Encoding.UTF8, "text/plain");
                using var response = await http.PostAsync(
                    $"https://{signalDomain}/webrtc/v1/pullstream", requestContent, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (answer.RootElement.GetProperty("errcode").GetInt32() != 0)
                    throw new IOException($"SHOWROOM pullstream failed: {answer.RootElement.GetProperty("errmsg").GetString()}");
                var remoteSdp = answer.RootElement.GetProperty("remotesdp").GetProperty("sdp").GetString();
                var result = peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = remoteSdp });
                if (result != SetDescriptionResultEnum.OK)
                    throw new IOException($"SHOWROOM WebRTC answer was rejected: {result}.");
                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public bool TryReadPacket(out WebRtcMediaPacket packet)
        {
            if (Volatile.Read(ref _overflow) != 0)
                throw new IOException("WebRTC media queue exceeded its 4096 packet bound; refusing silent media loss.");
            return _packets.Reader.TryRead(out packet!);
        }

        public void SendNack(bool isVideo, uint mediaSsrc, ushort packetId, ushort bitmask)
        {
            _peer.SendRtcpFeedback(isVideo ? SDPMediaTypesEnum.video : SDPMediaTypesEnum.audio,
                new RTCPFeedback(isVideo ? _peer.VideoLocalTrack.Ssrc : _peer.AudioLocalTrack.Ssrc,
                    mediaSsrc, RTCPFeedbackTypesEnum.NACK, packetId, bitmask));
        }

        public void RequestKeyframe(uint mediaSsrc) =>
            _peer.SendRtcpFeedback(SDPMediaTypesEnum.video,
                new RTCPFeedback(_peer.VideoLocalTrack.Ssrc, mediaSsrc, PSFBFeedbackTypesEnum.PLI));

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _packets.Writer.TryComplete();
            try { _peer.Close("WebRTC recorder session ended"); }
            catch { }
            try { _peer.Dispose(); }
            catch { }
            return ValueTask.CompletedTask;
        }
    }
}
