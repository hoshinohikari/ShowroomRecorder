using System.Globalization;
using SIPSorcery.Net;
using Serilog;
using showroom.Download;
using showroom.Utils;

namespace showroom.WebRtc;

public sealed record WebRtcSegmentEventArgs(long RoomId, string Path, DateTimeOffset StartedAt, DateTimeOffset? FinalizedAt = null, string? Error = null);

/// <summary>Receives SHOWROOM WebRTC media and writes each connected session to an independent MKV.</summary>
public sealed class WebRtcDownloader : DownloadUtils, IWebRtcSegmentEventSource
{
    /// <summary>Maximum monotonic time allowed for a peer to finish ICE/DTLS connection establishment.</summary>
    private static readonly TimeSpan ConnectionEstablishmentTimeout = TimeSpan.FromSeconds(30);

    private readonly long _roomId;
    private readonly string _proxy;
    private readonly IWebRtcConnectionProviderFactory _providerFactory;
    private readonly IWebRtcMuxerFactory _muxerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _random01;
    private readonly string _outputDirectory;
    private CancellationTokenSource? _stop;
    private Task? _downloadTask;

    public WebRtcDownloader(string name, long roomId, Configs configSnapshot)
        : this(name, roomId, configSnapshot, new ShowroomWebRtcConnectionProviderFactory(), new FfmpegMkvMuxerFactory(), TimeProvider.System, Random.Shared.NextDouble, null)
    {
    }

    internal WebRtcDownloader(
        string name,
        long roomId,
        Configs configSnapshot,
        IWebRtcConnectionProviderFactory providerFactory,
        IWebRtcMuxerFactory muxerFactory,
        TimeProvider timeProvider,
        Func<double> random01,
        string? outputDirectory)
        : base(name, roomId.ToString(CultureInfo.InvariantCulture))
    {
        _roomId = roomId;
        _proxy = configSnapshot.Proxy;
        _providerFactory = providerFactory;
        _muxerFactory = muxerFactory;
        _timeProvider = timeProvider;
        _random01 = random01;
        _outputDirectory = outputDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
    }

    public event EventHandler<WebRtcSegmentEventArgs>? SegmentStarted;
    public event EventHandler<WebRtcSegmentEventArgs>? SegmentFinalized;
    public event EventHandler<WebRtcSegmentEventArgs>? SegmentAborted;

    public override Task DownloadAsync()
    {
        if (_downloadTask is not null)
            return _downloadTask;

        _stop = new CancellationTokenSource();
        _downloadTask = RunAsync(_stop.Token);
        return _downloadTask;
    }

    public override async Task Stop()
    {
        if (_stop is null) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_downloadTask is not null)
        {
            try { await _downloadTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _stop = null;
        _downloadTask = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_outputDirectory);
        await using var provider = _providerFactory.Create(_proxy);
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var online = await provider.GetLiveStatusAsync(_roomId, cancellationToken).ConfigureAwait(false);
                if (online == false)
                {
                    Log.Information("WebRTC room {RoomId} is confirmed offline; recording ended.", _roomId);
                    break;
                }

                await using var session = await provider.ConnectAsync(_roomId, cancellationToken).ConfigureAwait(false);
                await RecordSessionAsync(session, cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                Log.Warning(exception, "WebRTC session for room {RoomId} failed; reconnecting with configured WebRTC only.", _roomId);
            }

            if (cancellationToken.IsCancellationRequested) break;
            var delay = WebRtcRetryPolicy.GetDelay(consecutiveFailures, _random01());
            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RecordSessionAsync(IWebRtcMediaSession session, CancellationToken cancellationToken)
    {
        var sessionStartedAt = _timeProvider.GetTimestamp();
        var anchors = new Dictionary<uint, (uint Rtp, ulong Ntp)>();
        var rtpPositions = new Dictionary<uint, (uint Timestamp, long ElapsedTicks)>();
        var reportedAnchorUpdates = new HashSet<uint>();
        var reorder = new Dictionary<bool, RtpReorderBuffer>();
        var ready = new Queue<WebRtcMediaPacket>();
        var ssrcByTrack = new Dictionary<bool, uint>();
        var previousSequence = new Dictionary<bool, ushort>();
        var lastPts = new Dictionary<bool, long>();
        var assembler = new H264AccessUnitAssembler();
        IWebRtcMuxer? muxer = null;
        string? outputPath = null;
        DateTimeOffset? segmentStartedAt = null;
        double? epochMilliseconds = null;
        var waitForKeyFrame = true;
        var lastVideoPacketMs = 0d;
        var connectedAtMs = (double?)null;
        var lastVideoWriteMs = (double?)null;
        var disconnectedAtMs = (double?)null;
        var lastPliMs = -1000d;
        var nextProgressMs = 5_000d;
        long videoRtp = 0, audioRtp = 0, videoWrites = 0, audioWrites = 0;
        long videoPtsRejected = 0, audioPtsRejected = 0, missingAnchors = 0;
        long incompleteVideoFrames = 0, keyframeWaitFrames = 0, invalidSenderReports = 0, repeatedSenderReports = 0;
        var maxSenderReportShiftMs = 0d;

        void LogMediaStatistics(bool final)
        {
            Log.Write(final ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Debug,
                "WebRTC media counters for room {RoomId}: final={Final}, elapsedMs={ElapsedMs}, " +
                "videoRtp={VideoRtp}, audioRtp={AudioRtp}, videoWrites={VideoWrites}, audioWrites={AudioWrites}, " +
                "videoPtsRejected={VideoPtsRejected}, audioPtsRejected={AudioPtsRejected}, missingAnchors={MissingAnchors}, " +
                "incompleteVideoFrames={IncompleteVideoFrames}, keyframeWaitFrames={KeyframeWaitFrames}, " +
                "invalidSenderReports={InvalidSenderReports}, repeatedSenderReports={RepeatedSenderReports}, maxSenderReportShiftMs={MaxSenderReportShiftMs}",
                _roomId, final, _timeProvider.GetElapsedTime(sessionStartedAt).TotalMilliseconds,
                videoRtp, audioRtp, videoWrites, audioWrites, videoPtsRejected, audioPtsRejected, missingAnchors,
                incompleteVideoFrames, keyframeWaitFrames, invalidSenderReports, repeatedSenderReports, maxSenderReportShiftMs);
        }

        foreach (var isVideo in new[] { true, false })
        {
            reorder[isVideo] = new RtpReorderBuffer((ssrc, packetId, bitmask) =>
            {
                try { session.SendNack(isVideo, ssrc, packetId, bitmask); }
                catch (Exception exception) { Log.Debug(exception, "Unable to send WebRTC NACK for room {RoomId}.", _roomId); }
            });
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsedMs = _timeProvider.GetElapsedTime(sessionStartedAt).TotalMilliseconds;
                if (elapsedMs >= nextProgressMs)
                {
                    LogMediaStatistics(final: false);
                    nextProgressMs = elapsedMs + 5_000;
                }
                var connectionState = session.ConnectionState;
                if (connectionState == RTCPeerConnectionState.failed)
                    throw new IOException("WebRTC peer connection failed.");
                if (connectionState == RTCPeerConnectionState.closed)
                    throw new IOException("WebRTC peer connection closed before or during recording.");

                if (connectedAtMs is null && connectionState != RTCPeerConnectionState.connected &&
                    elapsedMs >= ConnectionEstablishmentTimeout.TotalMilliseconds)
                    throw new IOException("WebRTC peer did not establish a connection within 30 seconds.");

                if (connectionState == RTCPeerConnectionState.disconnected)
                {
                    disconnectedAtMs ??= elapsedMs;
                    if (elapsedMs - disconnectedAtMs >= 5_000)
                        throw new IOException("WebRTC peer remained disconnected for 5 seconds.");
                }
                else
                {
                    disconnectedAtMs = null;
                }

                if (connectionState == RTCPeerConnectionState.connected)
                {
                    if (connectedAtMs is null)
                    {
                        connectedAtMs = elapsedMs;
                        lastVideoPacketMs = elapsedMs;
                    }
                    lastVideoWriteMs ??= connectedAtMs;
                    if (elapsedMs - lastVideoPacketMs >= 10_000)
                        throw new IOException("No video RTP packet arrived for 10 seconds.");
                    if (lastVideoWriteMs.HasValue && elapsedMs - lastVideoWriteMs.Value >= 30_000)
                        throw new IOException("No complete video frame was written for 30 seconds after connection.");
                }

                while (session.TryReadPacket(out var packet))
                {
                    elapsedMs = _timeProvider.GetElapsedTime(sessionStartedAt).TotalMilliseconds;
                    if (packet.IsSenderReport)
                    {
                        if (packet.NtpTimestamp == 0)
                        {
                            if (++invalidSenderReports == 1)
                                Log.Debug("WebRTC ignored a sender report with zero NTP for room {RoomId}, SSRC={Ssrc}.", _roomId, packet.Ssrc);
                            continue;
                        }

                        // Keep one RTP/NTP origin per SSRC for this connection, as in the validated probe.
                        // Rebasing an open segment can make PTS go backwards and silently discard both tracks.
                        if (anchors.TryAdd(packet.Ssrc, (packet.Timestamp, packet.NtpTimestamp)))
                        {
                            Log.Debug("WebRTC synchronization anchor for room {RoomId}: video={Video}, SSRC={Ssrc}, RTP={Rtp}, NTP={Ntp}",
                                _roomId, packet.IsVideo, packet.Ssrc, packet.Timestamp, packet.NtpTimestamp);
                        }
                        else
                        {
                            repeatedSenderReports++;
                            var initial = anchors[packet.Ssrc];
                            var position = rtpPositions.GetValueOrDefault(packet.Ssrc, (Timestamp: initial.Rtp, ElapsedTicks: 0L));
                            var reportElapsedTicks = position.ElapsedTicks + unchecked((int)(packet.Timestamp - position.Timestamp));
                            var reportClockRate = packet.IsVideo ? 90_000d : 48_000d;
                            var ntpDeltaMs = ((double)(packet.NtpTimestamp >> 32) - (initial.Ntp >> 32)) * 1000d +
                                ((double)(packet.NtpTimestamp & uint.MaxValue) - (initial.Ntp & uint.MaxValue)) * (1000d / 4_294_967_296d);
                            var mappingShiftMs = ntpDeltaMs - reportElapsedTicks * (1000d / reportClockRate);
                            maxSenderReportShiftMs = Math.Max(maxSenderReportShiftMs, Math.Abs(mappingShiftMs));
                            if (reportedAnchorUpdates.Add(packet.Ssrc))
                            {
                                Log.Debug("WebRTC retained initial synchronization anchor for room {RoomId}: video={Video}, SSRC={Ssrc}, laterReportMappingShiftMs={MappingShiftMs}",
                                    _roomId, packet.IsVideo, packet.Ssrc, mappingShiftMs);
                            }
                        }
                        continue;
                    }

                    if (packet.IsVideo)
                    {
                        videoRtp++;
                        lastVideoPacketMs = elapsedMs;
                    }
                    else audioRtp++;
                    var orderedNow = new List<WebRtcMediaPacket>();
                    reorder[packet.IsVideo].Add(packet, (long)elapsedMs, orderedNow);
                    foreach (var ordered in orderedNow) ready.Enqueue(ordered);
                    foreach (var ordered in reorder[packet.IsVideo].Flush((long)elapsedMs)) ready.Enqueue(ordered);
                }

                foreach (var buffer in reorder.Values)
                    foreach (var ordered in buffer.Flush((long)elapsedMs)) ready.Enqueue(ordered);

                if (ready.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var mediaPacket = ready.Dequeue();
                if (ssrcByTrack.TryGetValue(mediaPacket.IsVideo, out var previousSsrc) && previousSsrc != mediaPacket.Ssrc)
                    throw new IOException("WebRTC media SSRC changed; starting a new independent segment.");
                ssrcByTrack[mediaPacket.IsVideo] = mediaPacket.Ssrc;

                var gapDetected = previousSequence.TryGetValue(mediaPacket.IsVideo, out var previousSeq) &&
                    unchecked((ushort)(previousSeq + 1)) != mediaPacket.SequenceNumber;
                if (mediaPacket.IsVideo && gapDetected)
                    waitForKeyFrame = true;
                previousSequence[mediaPacket.IsVideo] = mediaPacket.SequenceNumber;

                if (mediaPacket.IsVideo && waitForKeyFrame && elapsedMs - lastPliMs >= 1_000)
                {
                    try { session.RequestKeyframe(mediaPacket.Ssrc); }
                    catch (Exception exception) { Log.Debug(exception, "Unable to send WebRTC PLI for room {RoomId}.", _roomId); }
                    lastPliMs = elapsedMs;
                }

                if (!anchors.TryGetValue(mediaPacket.Ssrc, out var anchor))
                {
                    missingAnchors++;
                    if (mediaPacket.IsVideo) assembler.Lose();
                    continue;
                }

                var rate = mediaPacket.IsVideo ? 90_000d : 48_000d;
                var ntpMs = (anchor.Ntp >> 32) * 1000d + (anchor.Ntp & uint.MaxValue) * (1000d / 4_294_967_296d);
                // Unwrap consecutive RTP timestamps instead of taking a signed difference from the
                // initial anchor forever; the latter turns negative after about 6.6 hours at 90 kHz.
                var previousPosition = rtpPositions.GetValueOrDefault(mediaPacket.Ssrc, (Timestamp: anchor.Rtp, ElapsedTicks: 0L));
                var elapsedRtpTicks = previousPosition.ElapsedTicks + unchecked((int)(mediaPacket.Timestamp - previousPosition.Timestamp));
                rtpPositions[mediaPacket.Ssrc] = (mediaPacket.Timestamp, elapsedRtpTicks);
                var mediaMs = ntpMs + elapsedRtpTicks * (1000d / rate);
                byte[] payload;
                var keyFrame = false;
                if (mediaPacket.IsVideo)
                {
                    var frame = assembler.Push(mediaPacket.Timestamp, mediaPacket.Payload, mediaPacket.Marker, gapDetected);
                    if (frame is null)
                    {
                        if (mediaPacket.Marker)
                        {
                            incompleteVideoFrames++;
                            waitForKeyFrame = true;
                        }
                        continue;
                    }
                    payload = frame.Value.AvcPayload;
                    keyFrame = frame.Value.IsKeyFrame;
                    if (waitForKeyFrame && !keyFrame)
                    {
                        keyframeWaitFrames++;
                        continue;
                    }
                    if (keyFrame && assembler.Sps is not null && assembler.Pps is not null && anchors.Count >= 2)
                    {
                        if (muxer is null)
                        {
                            var (width, height) = H264AccessUnitAssembler.GetDimensions(assembler.Sps);
                            outputPath = MakeOutputPath();
                            segmentStartedAt = _timeProvider.GetLocalNow();
                            RaiseSafely(SegmentStarted, new WebRtcSegmentEventArgs(_roomId, outputPath, segmentStartedAt.Value));
                            try
                            {
                                muxer = _muxerFactory.Create(outputPath, assembler.Sps, assembler.Pps, 2, width, height);
                                epochMilliseconds = mediaMs;
                            }
                            catch (Exception exception)
                            {
                                RaiseSafely(SegmentAborted, new WebRtcSegmentEventArgs(_roomId, outputPath, segmentStartedAt.Value, Error: exception.Message));
                                outputPath = null;
                                segmentStartedAt = null;
                                throw;
                            }
                        }
                        waitForKeyFrame = false;
                    }
                }
                else
                {
                    payload = mediaPacket.Payload;
                }

                if (muxer is null || !epochMilliseconds.HasValue) continue;
                var pts = (long)Math.Round((mediaMs - epochMilliseconds.Value) * (mediaPacket.IsVideo ? 90d : 48d));
                if (pts < 0 || (lastPts.TryGetValue(mediaPacket.IsVideo, out var last) && pts <= last))
                {
                    if (mediaPacket.IsVideo) videoPtsRejected++;
                    else audioPtsRejected++;
                    continue;
                }
                muxer.Write(mediaPacket.IsVideo, payload, pts, keyFrame);
                lastPts[mediaPacket.IsVideo] = pts;
                if (mediaPacket.IsVideo)
                {
                    videoWrites++;
                    lastVideoWriteMs = elapsedMs;
                }
                else audioWrites++;
            }
        }
        finally
        {
            LogMediaStatistics(final: true);
            if (muxer is not null)
            {
                try
                {
                    muxer.Dispose();
                    if (outputPath is not null && segmentStartedAt.HasValue)
                        RaiseSafely(SegmentFinalized, new WebRtcSegmentEventArgs(_roomId, outputPath, segmentStartedAt.Value, _timeProvider.GetLocalNow()));
                }
                catch (Exception exception)
                {
                    if (outputPath is not null && segmentStartedAt.HasValue)
                        RaiseSafely(SegmentAborted, new WebRtcSegmentEventArgs(_roomId, outputPath, segmentStartedAt.Value, Error: exception.Message));
                    throw;
                }
            }
            else if (outputPath is not null && segmentStartedAt.HasValue)
            {
                RaiseSafely(SegmentAborted, new WebRtcSegmentEventArgs(_roomId, outputPath, segmentStartedAt.Value, Error: "Session ended before a muxer could be finalized."));
            }
        }
    }

    private string MakeOutputPath()
    {
        var stem = $"{Name}_{_timeProvider.GetLocalNow():yyyy_MM_dd_HH_mm_ss_fff}";
        var path = Path.Combine(_outputDirectory, stem + ".mkv");
        var suffix = 1;
        while (File.Exists(path))
            path = Path.Combine(_outputDirectory, $"{stem}_{suffix++:D2}.mkv");
        return path;
    }

    private static void RaiseSafely(EventHandler<WebRtcSegmentEventArgs>? handlers, WebRtcSegmentEventArgs args)
    {
        if (handlers is null) return;
        foreach (EventHandler<WebRtcSegmentEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(null, args); }
            catch (Exception exception) { Log.Warning(exception, "WebRTC segment lifecycle subscriber failed for {Path}.", args.Path); }
        }
    }
}

internal static class WebRtcRetryPolicy
{
    public static TimeSpan GetDelay(int retryNumber, double random01)
    {
        var exponent = Math.Clamp(retryNumber - 1, 0, 5);
        var seconds = Math.Min(30, Math.Pow(2, exponent));
        var jitter = 0.9 + (Math.Clamp(random01, 0, 1) * 0.2);
        return TimeSpan.FromSeconds(Math.Min(30, seconds * jitter));
    }
}
