using Xunit;
using showroom.Download;
using showroom.Utils;
using showroom.WebRtc;

namespace showroom.Tests;

public class RecorderTests
{
    [Fact]
    public async Task WebRtcRecorder_RegistersBeforeWriting_AndQueuesOnlyFinalizedSegments()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "test_user_2026_10_18_12_00_00_000.mkv");
        var uploads = new List<string>();
        var downloader = new FakeWebRtcDownloader(path, finalizeWhenStarted: true);
        var factory = new FakeRecorderBackendFactory(webRtcDownloader: downloader);
        var recorder = new Recorder("test_user", 12345, new Configs { Protocol = "webrtc" }, factory,
            new WebRtcRecordingLifecycle(uploads.Add));

        await recorder.Start();

        Assert.True(downloader.RecordingMarkerWasPresentBeforeWriting);
        Assert.False(RecordingRegistry.IsRecording(path));
        Assert.True(File.Exists(path));
        Assert.Equal([path], uploads);
    }

    [Fact]
    public async Task HlsRecorder_UsesOnlyHlsBackend()
    {
        var path = Path.Combine(CreateTempDirectory(), "hls.ts");
        var downloader = new FakeDownload(path, onDownload: null);
        var factory = new FakeRecorderBackendFactory(hlsDownloader: downloader, webRtcDownloader: null);
        var recorder = new Recorder("test_user", 12345, new Configs { Protocol = "hls" }, factory,
            new WebRtcRecordingLifecycle(_ => { }));

        await recorder.Start();

        Assert.Equal(1, factory.HlsCreationCount);
        Assert.Equal(0, factory.WebRtcCreationCount);
    }

    [Fact]
    public async Task WebRtcRecorder_UsesConfigurationSnapshotForBackend()
    {
        var config = new Configs
        {
            Protocol = "webrtc",
            Proxy = "http://proxy-at-start",
            WebRtc = new WebRtcConfigs { NativeLibraryPath = "C:\\ffmpeg\\start" },
            Users = ["start-user"]
        };
        var downloader = new FakeWebRtcDownloader(Path.Combine(CreateTempDirectory(), "segment.mkv"), true);
        var factory = new FakeRecorderBackendFactory(webRtcDownloader: downloader);
        var recorder = new Recorder("test_user", 12345, config, factory,
            new WebRtcRecordingLifecycle(_ => { }));
        config.Protocol = "hls";
        config.Proxy = "http://proxy-after-reload";
        config.WebRtc.NativeLibraryPath = "C:\\ffmpeg\\after-reload";
        config.Users[0] = "after-reload-user";

        await recorder.Start();

        Assert.Equal("webrtc", factory.LastConfig?.Protocol);
        Assert.Equal("http://proxy-at-start", factory.LastConfig?.Proxy);
        Assert.Equal("C:\\ffmpeg\\start", factory.LastConfig?.WebRtc.NativeLibraryPath);
        Assert.Equal("start-user", Assert.Single(factory.LastConfig!.Users));
        Assert.Equal(0, factory.HlsCreationCount);
    }

    [Fact]
    public async Task RecorderStoppedBeforeStart_DoesNotStartBackendLater()
    {
        var downloader = new FakeWebRtcDownloader(Path.Combine(CreateTempDirectory(), "segment.mkv"), true);
        var factory = new FakeRecorderBackendFactory(webRtcDownloader: downloader);
        var recorder = new Recorder("test_user", 12345, new Configs { Protocol = "webrtc" }, factory,
            new WebRtcRecordingLifecycle(_ => { }));

        await recorder.Stop();
        await recorder.Start();

        Assert.Equal(0, downloader.DownloadCallCount);
        Assert.Equal(0, factory.HlsCreationCount);
    }

    [Fact]
    public async Task AbortedWebRtcSegment_IsDeletedAndNeverQueuedForUpload()
    {
        var path = Path.Combine(CreateTempDirectory(), "aborted.mkv");
        var uploads = new List<string>();
        var downloader = new FakeWebRtcDownloader(path, finalizeWhenStarted: false, abortWhenStarted: true);
        var recorder = new Recorder("test_user", 12345, new Configs { Protocol = "webrtc" },
            new FakeRecorderBackendFactory(webRtcDownloader: downloader), new WebRtcRecordingLifecycle(uploads.Add));

        await recorder.Start();

        Assert.False(File.Exists(path));
        Assert.False(RecordingRegistry.IsRecording(path));
        Assert.Empty(uploads);
    }

    [Fact]
    public async Task RecorderStop_WaitsUntilWebRtcSegmentIsFinalized()
    {
        var path = Path.Combine(CreateTempDirectory(), "segment.mkv");
        var uploads = new List<string>();
        var downloader = new FakeWebRtcDownloader(path, finalizeWhenStarted: false);
        var recorder = new Recorder("test_user", 12345, new Configs { Protocol = "webrtc" },
            new FakeRecorderBackendFactory(webRtcDownloader: downloader), new WebRtcRecordingLifecycle(uploads.Add));

        var start = recorder.Start();
        await downloader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stop = recorder.Stop();
        await downloader.StopCalled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(stop.IsCompleted);
        Assert.Empty(uploads);

        downloader.AllowStopToFinalize();
        await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([path], uploads);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AbortedSegment_RemainsProtectedWhenPartialFileCannotBeDeleted()
    {
        var path = Path.Combine(CreateTempDirectory(), "aborted.mkv");
        File.WriteAllText(path, "partial");
        var lifecycle = new WebRtcRecordingLifecycle(_ => throw new InvalidOperationException("must not enqueue"),
            _ => throw new UnauthorizedAccessException("test delete failure"));
        var args = new WebRtcSegmentEventArgs(12345, path, DateTimeOffset.UtcNow);

        lifecycle.OnSegmentStarted(args);
        lifecycle.OnSegmentAborted(args);

        Assert.True(File.Exists(path));
        Assert.True(RecordingRegistry.IsRecording(path));
        RecordingRegistry.MarkCompleted(path);
        File.Delete(path);
    }

    [Fact]
    public async Task Recorder_Stop_WhenDownloaderIsNull_DoesNotThrow()
    {
        var recorder = new Recorder("test_user", 12345);

        var exception = await Record.ExceptionAsync(recorder.Stop);

        Assert.Null(exception);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ShowroomRecorderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRecorderBackendFactory(
        DownloadUtils? hlsDownloader = null,
        FakeWebRtcDownloader? webRtcDownloader = null) : IRecorderBackendFactory
    {
        public int HlsCreationCount { get; private set; }
        public int WebRtcCreationCount { get; private set; }
        public Configs? LastConfig { get; private set; }

        public Task<DownloadUtils?> CreateHlsAsync(string name, long roomId, Configs configSnapshot)
        {
            HlsCreationCount++;
            LastConfig = configSnapshot;
            return Task.FromResult(hlsDownloader);
        }

        public DownloadUtils CreateWebRtc(string name, long roomId, Configs configSnapshot)
        {
            WebRtcCreationCount++;
            LastConfig = configSnapshot;
            return webRtcDownloader ?? throw new InvalidOperationException("WebRTC backend was not configured.");
        }
    }

    private sealed class FakeDownload(string path, Action? onDownload) : DownloadUtils("fake", path)
    {
        public override Task DownloadAsync()
        {
            onDownload?.Invoke();
            return Task.CompletedTask;
        }

        public override Task Stop() => Task.CompletedTask;
    }

    private sealed class FakeWebRtcDownloader(string path, bool finalizeWhenStarted, bool abortWhenStarted = false)
        : DownloadUtils("fake", "12345"), IWebRtcSegmentEventSource
    {
        private readonly TaskCompletionSource _downloadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<WebRtcSegmentEventArgs>? SegmentStarted;
        public event EventHandler<WebRtcSegmentEventArgs>? SegmentFinalized;
        public event EventHandler<WebRtcSegmentEventArgs>? SegmentAborted;
        public TaskCompletionSource Started => _started;
        public TaskCompletionSource StopCalled => _stopCalled;
        public bool RecordingMarkerWasPresentBeforeWriting { get; private set; }
        public int DownloadCallCount { get; private set; }

        public override Task DownloadAsync()
        {
            DownloadCallCount++;
            var args = new WebRtcSegmentEventArgs(12345, path, DateTimeOffset.UtcNow);
            SegmentStarted?.Invoke(this, args);
            _started.TrySetResult();
            RecordingMarkerWasPresentBeforeWriting = RecordingRegistry.IsRecording(path);
            File.WriteAllText(path, "mkv data");

            if (abortWhenStarted)
            {
                SegmentAborted?.Invoke(this, args with { Error = "simulated failure" });
                _downloadCompletion.TrySetResult();
            }
            else if (finalizeWhenStarted)
            {
                SegmentFinalized?.Invoke(this, args with { FinalizedAt = DateTimeOffset.UtcNow });
                _downloadCompletion.TrySetResult();
            }

            return _downloadCompletion.Task;
        }

        public override async Task Stop()
        {
            _stopCalled.TrySetResult();
            await _stopGate.Task;
            SegmentFinalized?.Invoke(this,
                new WebRtcSegmentEventArgs(12345, path, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            _downloadCompletion.TrySetResult();
        }

        public void AllowStopToFinalize() => _stopGate.TrySetResult();
    }
}
