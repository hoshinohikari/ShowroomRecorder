using System.Collections.Concurrent;
using System.Buffers.Binary;
using SIPSorcery.Net;
using showroom.Utils;
using showroom.WebRtc;
using Xunit;

namespace showroom.Tests;

public class WebRtcDownloaderAcceptanceTests
{
    [Fact]
    public async Task Keeps_the_connection_when_disconnection_recovers_before_five_seconds()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        var provider = new FakeConnectionProvider(session);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));

            session.ConnectionState = RTCPeerConnectionState.disconnected;
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));

            clock.Advance(TimeSpan.FromMilliseconds(4_900));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));
            var readsBeforeReconnect = session.ConnectionStateReads;
            session.ConnectionState = RTCPeerConnectionState.connected;
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => session.ConnectionStateReads > readsBeforeReconnect, TimeSpan.FromMilliseconds(500));

            Assert.False(session.IsDisposed);
            Assert.Equal(1, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Reconnects_after_peer_stays_disconnected_for_five_seconds_of_monotonic_time()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.disconnected);
        var provider = new FakeConnectionProvider(session);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();

            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => session.IsDisposed, TimeSpan.FromMilliseconds(500));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => provider.ConnectCount == 2, TimeSpan.FromMilliseconds(500));

            Assert.True(session.IsDisposed);
            Assert.Equal(2, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Reconnects_when_connection_establishment_stays_connecting_for_thirty_seconds()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connecting);
        var provider = new FakeConnectionProvider(session);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));

            var readsBeforeBoundary = session.ConnectionStateReads;
            clock.Advance(TimeSpan.FromSeconds(29) + TimeSpan.FromMilliseconds(975));
            await WaitUntilAsync(() => session.ConnectionStateReads > readsBeforeBoundary, TimeSpan.FromMilliseconds(500));
            Assert.False(session.IsDisposed);

            clock.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => session.IsDisposed, TimeSpan.FromMilliseconds(500));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => provider.ConnectCount == 2, TimeSpan.FromMilliseconds(500));

            Assert.Equal(2, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Closed_peer_is_failed_immediately_and_reconnected()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.closed);
        var provider = new FakeConnectionProvider(session);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();
            await WaitUntilAsync(() => session.IsDisposed, TimeSpan.FromMilliseconds(500));

            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => provider.ConnectCount == 2, TimeSpan.FromMilliseconds(500));

            Assert.Equal(2, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_cancels_the_wait_for_connection_establishment()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connecting);
        var provider = new FakeConnectionProvider(session);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            var recording = downloader.DownloadAsync();
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));

            await downloader.Stop().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            await recording.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.True(session.IsDisposed);
            Assert.Equal(1, provider.ConnectCount);
            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Reconnects_when_video_rtp_is_silent_for_ten_seconds_of_monotonic_time()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        var provider = new FakeConnectionProvider(session);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();
            Assert.Equal(1, provider.ConnectCount);

            clock.Advance(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => session.IsDisposed, TimeSpan.FromMilliseconds(500));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => provider.ConnectCount == 2, TimeSpan.FromMilliseconds(500));

            Assert.True(session.IsDisposed);
            Assert.Equal(2, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Reconnects_after_thirty_seconds_without_a_complete_video_write_even_when_video_rtp_arrives()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        var provider = new FakeConnectionProvider(session);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();
            clock.Advance(TimeSpan.FromMilliseconds(25));

            for (ushort sequence = 1; sequence <= 5; sequence++)
            {
                session.Enqueue(new WebRtcMediaPacket(true, (uint)sequence * 450_000, sequence, 42, true, [0x61, 0x01]));
                clock.Advance(TimeSpan.FromSeconds(5));
                var expectedPackets = sequence;
                await WaitUntilAsync(() => session.PacketsRead >= expectedPackets, TimeSpan.FromMilliseconds(500));
            }

            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => session.IsDisposed, TimeSpan.FromMilliseconds(500));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => provider.ConnectCount == 2, TimeSpan.FromMilliseconds(500));

            Assert.Equal(5, session.PacketsRead);
            Assert.True(session.IsDisposed);
            Assert.Equal(2, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_status_is_retryable_but_confirmed_offline_ends_the_recording()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.failed);
        var provider = new FakeConnectionProvider(session, null, false);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            var recording = downloader.DownloadAsync();
            Assert.Equal(1, provider.StatusCallCount);
            Assert.Equal(1, provider.ConnectCount);

            clock.Advance(TimeSpan.FromSeconds(1));
            await recording.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.Equal(2, provider.StatusCallCount);
            Assert.Equal(1, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Confirmed_offline_status_ends_without_connecting()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        var provider = new FakeConnectionProvider(session, false);
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            await downloader.DownloadAsync().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.Equal(1, provider.StatusCallCount);
            Assert.Equal(0, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_cancels_a_pending_retry_delay_without_waiting_for_the_backoff()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        var provider = new FakeConnectionProvider(session) { ConnectFailure = new IOException("temporary connect failure") };
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            var recording = downloader.DownloadAsync();
            Assert.False(recording.IsCompleted);

            await downloader.Stop().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            await recording.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.Equal(1, provider.ConnectCount);
            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_cancels_a_pending_connection_attempt()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        var provider = new FakeConnectionProvider(session) { BlockConnectUntilCancelled = true };
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), new NoSegmentMuxerFactory(), clock, () => 0, output);

        try
        {
            var recording = downloader.DownloadAsync();
            await provider.ConnectStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            await downloader.Stop().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            await recording.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.Equal(1, provider.ConnectCount);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Preserves_audio_while_damaged_video_waits_for_a_complete_keyframe()
    {
        var sps = Convert.FromHexString("674d401edb0280bfe584000003000400000300a23c58bb80");
        var pps = Convert.FromHexString("68ef3c80");
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        session.Enqueue(SenderReport(isVideo: true, 42));
        session.Enqueue(SenderReport(isVideo: false, 43));
        session.Enqueue(new WebRtcMediaPacket(true, 0, 100, 42, true,
            MakeStapA(sps, pps, [0x65, 0x88, 0x99])));
        session.Enqueue(new WebRtcMediaPacket(false, 48_000, 1, 43, true, [0xf8, 0xff, 0xfe]));

        var provider = new FakeConnectionProvider(session);
        var muxers = new RecordingMuxerFactory();
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), muxers, clock, () => 0, output);

        try
        {
            var recording = downloader.DownloadAsync();
            await WaitUntilAsync(() => muxers.Muxer?.Writes.Length >= 2, TimeSpan.FromMilliseconds(500));

            session.Enqueue(new WebRtcMediaPacket(true, 90_000, 101, 42, true, [0x00]));
            session.Enqueue(new WebRtcMediaPacket(false, 96_000, 2, 43, true, [0xf8, 0xff, 0xfd]));
            session.Enqueue(new WebRtcMediaPacket(true, 180_000, 102, 42, true, [0x61, 0x05]));
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => session.PacketsRead >= 7, TimeSpan.FromMilliseconds(500));
            await WaitUntilAsync(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(500));

            var writesDuringRecovery = muxers.Muxer!.Writes.ToArray();
            Assert.Single(writesDuringRecovery, write => write.IsVideo);
            Assert.Equal(2, writesDuringRecovery.Count(write => !write.IsVideo));

            session.Enqueue(new WebRtcMediaPacket(true, 270_000, 103, 42, true, [0x65, 0x06]));
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => muxers.Muxer.Writes.Length >= 4, TimeSpan.FromMilliseconds(500));
            await downloader.Stop().WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            await recording.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.Equal(2, muxers.Muxer.Writes.Count(write => write.IsVideo));
            Assert.Equal(2, muxers.Muxer.Writes.Count(write => !write.IsVideo));
            Assert.True(muxers.Muxer.IsDisposed);
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("backward")]
    [InlineData("forward")]
    [InlineData("consistent")]
    [InlineData("rtp-wrap")]
    public async Task Later_sender_reports_do_not_rebase_an_open_segment_or_stall_either_track(string reportKind)
    {
        var sps = Convert.FromHexString("674d401edb0280bfe584000003000400000300a23c58bb80");
        var pps = Convert.FromHexString("68ef3c80");
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        session.Enqueue(SenderReport(true, 42));
        session.Enqueue(SenderReport(false, 43));
        session.Enqueue(new WebRtcMediaPacket(true, 0, 100, 42, true, MakeStapA(sps, pps, [0x65, 0x88])));
        session.Enqueue(new WebRtcMediaPacket(false, 0, 1, 43, true, [0xf8, 0xff, 0xfe]));
        var provider = new FakeConnectionProvider(session);
        var muxers = new RecordingMuxerFactory();
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(provider), muxers, clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();
            await WaitUntilAsync(() => muxers.Muxer?.Writes.Length == 2 && clock.PendingTimerCount > 0,
                TimeSpan.FromMilliseconds(500));

            // Continue past the 30-second stall guard, with a new SR before every pair of media packets.
            for (ushort second = 1; second <= 35; second++)
            {
                ulong reportSeconds = reportKind switch
                {
                    "zero" => 0,
                    "backward" => 999,
                    "forward" => 1_060,
                    _ => (ulong)(1_000 + second)
                };
                // Large but unambiguous per-packet increments also exercise multiple 32-bit RTP wraps.
                var videoTicks = (long)second * (reportKind == "rtp-wrap" ? 300_000_000 : 90_000);
                var audioTicks = (long)second * (reportKind == "rtp-wrap" ? 160_000_000 : 48_000);
                var videoTimestamp = unchecked((uint)videoTicks);
                var audioTimestamp = unchecked((uint)audioTicks);
                session.Enqueue(SenderReport(true, 42, videoTimestamp, reportSeconds << 32));
                session.Enqueue(SenderReport(false, 43, audioTimestamp, reportSeconds << 32));
                session.Enqueue(new WebRtcMediaPacket(true, videoTimestamp, (ushort)(100 + second), 42, true, [0x61, 0x01]));
                session.Enqueue(new WebRtcMediaPacket(false, audioTimestamp, (ushort)(1 + second), 43, true, [0xf8, 0xff, 0xfe]));
                clock.Advance(TimeSpan.FromSeconds(1));
                var expectedWrites = (second + 1) * 2;
                await WaitUntilAsync(() => muxers.Muxer!.Writes.Length == expectedWrites && clock.PendingTimerCount > 0,
                    TimeSpan.FromMilliseconds(500));
                Assert.Equal(videoTicks, muxers.Muxer!.Writes.Last(write => write.IsVideo).Pts);
                Assert.Equal(audioTicks, muxers.Muxer.Writes.Last(write => !write.IsVideo).Pts);
            }

            Assert.Equal(1, provider.ConnectCount);
            Assert.False(session.IsDisposed);
            Assert.Equal(36, muxers.Muxer!.Writes.Count(write => write.IsVideo));
            Assert.Equal(36, muxers.Muxer.Writes.Count(write => !write.IsVideo));
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Zero_ntp_sender_report_cannot_open_a_segment_before_both_tracks_have_valid_anchors()
    {
        var sps = Convert.FromHexString("674d401edb0280bfe584000003000400000300a23c58bb80");
        var pps = Convert.FromHexString("68ef3c80");
        var clock = new ManualTimeProvider();
        var session = new FakeMediaSession(RTCPeerConnectionState.connected);
        session.Enqueue(SenderReport(true, 42, ntpTimestamp: 0));
        session.Enqueue(SenderReport(false, 43));
        session.Enqueue(new WebRtcMediaPacket(true, 0, 100, 42, true, MakeStapA(sps, pps, [0x65, 0x88])));
        session.Enqueue(new WebRtcMediaPacket(false, 0, 1, 43, true, [0xf8, 0xff, 0xfe]));
        var muxers = new RecordingMuxerFactory();
        var output = Path.Combine(Path.GetTempPath(), $"webrtc-acceptance-{Guid.NewGuid():N}");
        var downloader = new WebRtcDownloader("test_user", 123, new Configs(),
            new FakeProviderFactory(new FakeConnectionProvider(session)), muxers, clock, () => 0, output);

        try
        {
            _ = downloader.DownloadAsync();
            await WaitUntilAsync(() => session.PacketsRead == 4 && clock.PendingTimerCount > 0,
                TimeSpan.FromMilliseconds(500));
            Assert.Null(muxers.Muxer);

            session.Enqueue(SenderReport(true, 42, 90_000, 1_001UL << 32));
            session.Enqueue(new WebRtcMediaPacket(true, 90_000, 101, 42, true, MakeStapA(sps, pps, [0x65, 0x88])));
            session.Enqueue(new WebRtcMediaPacket(false, 48_000, 2, 43, true, [0xf8, 0xff, 0xfe]));
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => muxers.Muxer?.Writes.Length == 2, TimeSpan.FromMilliseconds(500));
            Assert.All(muxers.Muxer!.Writes, write => Assert.Equal(0, write.Pts));
        }
        finally
        {
            await downloader.Stop();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static WebRtcMediaPacket SenderReport(bool isVideo, uint ssrc, uint timestamp = 0, ulong ntpTimestamp = 1_000UL << 32) =>
        new(isVideo, timestamp, 0, ssrc, false, [], true, ntpTimestamp);

    private static byte[] MakeStapA(params byte[][] nals)
    {
        var payload = new byte[1 + nals.Sum(nal => 2 + nal.Length)];
        payload[0] = 24;
        var offset = 1;
        foreach (var nal in nals)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), checked((ushort)nal.Length));
            offset += 2;
            nal.CopyTo(payload, offset);
            offset += nal.Length;
        }

        return payload;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The controlled-time operation did not reach its expected state.");
    }

    private sealed class FakeProviderFactory(FakeConnectionProvider provider) : IWebRtcConnectionProviderFactory
    {
        public IWebRtcConnectionProvider Create(string proxy) => provider;
    }

    private sealed class FakeConnectionProvider(IWebRtcMediaSession session, params bool?[] statuses) : IWebRtcConnectionProvider
    {
        private int _connectCount;
        private int _statusCallCount;
        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int StatusCallCount => Volatile.Read(ref _statusCallCount);
        public Exception? ConnectFailure { get; init; }
        public bool BlockConnectUntilCancelled { get; init; }
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool?> GetLiveStatusAsync(long roomId, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _statusCallCount) - 1;
            var status = index < statuses.Length ? statuses[index] : null;
            return Task.FromResult(status);
        }

        public async Task<IWebRtcMediaSession> ConnectAsync(long roomId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectStarted.TrySetResult();
            if (ConnectFailure is not null) throw ConnectFailure;
            if (BlockConnectUntilCancelled)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return session;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMediaSession(RTCPeerConnectionState state) : IWebRtcMediaSession
    {
        private readonly ConcurrentQueue<WebRtcMediaPacket> _packets = new();
        private int _state = (int)state;
        private int _disposed;
        private int _packetsRead;
        private int _connectionStateReads;
        public RTCPeerConnectionState ConnectionState
        {
            get
            {
                Interlocked.Increment(ref _connectionStateReads);
                return (RTCPeerConnectionState)Volatile.Read(ref _state);
            }
            set => Volatile.Write(ref _state, (int)value);
        }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public int PacketsRead => Volatile.Read(ref _packetsRead);
        public int ConnectionStateReads => Volatile.Read(ref _connectionStateReads);

        public void Enqueue(WebRtcMediaPacket packet) => _packets.Enqueue(packet);

        public bool TryReadPacket(out WebRtcMediaPacket packet)
        {
            if (!_packets.TryDequeue(out packet!)) return false;
            Interlocked.Increment(ref _packetsRead);
            return true;
        }

        public void SendNack(bool isVideo, uint mediaSsrc, ushort packetId, ushort bitmask) { }
        public void RequestKeyframe(uint mediaSsrc) { }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoSegmentMuxerFactory : IWebRtcMuxerFactory
    {
        public IWebRtcMuxer Create(string path, byte[] sps, byte[] pps, int channels, int width, int height) =>
            throw new InvalidOperationException("This test does not create an output segment.");
    }

    private sealed class RecordingMuxerFactory : IWebRtcMuxerFactory
    {
        public RecordingMuxer? Muxer { get; private set; }

        public IWebRtcMuxer Create(string path, byte[] sps, byte[] pps, int channels, int width, int height) =>
            Muxer = new RecordingMuxer();
    }

    private sealed class RecordingMuxer : IWebRtcMuxer
    {
        private readonly ConcurrentQueue<WebRtcMuxedPacket> _writes = new();
        private int _disposed;

        public WebRtcMuxedPacket[] Writes => _writes.ToArray();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Write(bool isVideo, byte[] payload, long pts, bool keyFrame) =>
            _writes.Enqueue(new WebRtcMuxedPacket(isVideo, payload, pts, keyFrame));

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed record WebRtcMuxedPacket(bool IsVideo, byte[] Payload, long Pts, bool KeyFrame);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public int PendingTimerCount
        {
            get
            {
                lock (_gate) return _timers.Count(timer => timer.DueAtTicks.HasValue);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.ChangeUnderLock(dueTime, period, _timestamp);
            }

            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            long target;
            lock (_gate) target = checked(_timestamp + amount.Ticks);

            while (true)
            {
                ManualTimer? next;
                TimerCallback? callback;
                object? state;
                lock (_gate)
                {
                    next = _timers
                        .Where(timer => timer.DueAtTicks.HasValue && timer.DueAtTicks.Value <= target)
                        .MinBy(timer => timer.DueAtTicks);
                    if (next is null)
                    {
                        Interlocked.Exchange(ref _timestamp, target);
                        return;
                    }

                    Interlocked.Exchange(ref _timestamp, next.DueAtTicks!.Value);
                    (callback, state) = next.FireUnderLock(_timestamp);
                }

                callback?.Invoke(state);
            }
        }

        private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) timer.ChangeUnderLock(dueTime, period, _timestamp);
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                timer.ChangeUnderLock(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, _timestamp);
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public long? DueAtTicks { get; private set; }
            private TimeSpan _period;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Change(this, dueTime, period);
                return true;
            }

            public void Dispose() => owner.Remove(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void ChangeUnderLock(TimeSpan dueTime, TimeSpan period, long now)
            {
                _period = period;
                DueAtTicks = dueTime == Timeout.InfiniteTimeSpan ? null : checked(now + Math.Max(0, dueTime.Ticks));
            }

            public (TimerCallback? Callback, object? State) FireUnderLock(long now)
            {
                if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
                    DueAtTicks = null;
                else
                    DueAtTicks = checked(now + _period.Ticks);
                return (callback, state);
            }
        }
    }
}
