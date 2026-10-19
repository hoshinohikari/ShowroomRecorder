using Serilog;
using showroom.Download;
using showroom.Utils;
using showroom.WebRtc;

namespace showroom;

public class Recorder
{
    private readonly string _name;
    private readonly long _id;
    private readonly Configs _configSnapshot;
    private readonly IRecorderBackendFactory _backendFactory;
    private readonly IWebRtcRecordingLifecycle _webRtcLifecycle;
    private readonly object _lifecycleLock = new();
    private DownloadUtils? _download;
    private Task? _downloadTask;
    private IWebRtcSegmentEventSource? _segmentEventSource;
    private bool _stopRequested;

    public Recorder(string name, long id)
        : this(name, id, ConfigUtils.Config, new RecorderBackendFactory(), new WebRtcRecordingLifecycle())
    {
    }

    internal Recorder(
        string name,
        long id,
        Configs configSnapshot,
        IRecorderBackendFactory backendFactory,
        IWebRtcRecordingLifecycle webRtcLifecycle)
    {
        _name = name;
        _id = id;
        _configSnapshot = CopyConfig(configSnapshot);
        _backendFactory = backendFactory;
        _webRtcLifecycle = webRtcLifecycle;
    }

    public async Task Start()
    {
        Log.Debug("{Name} recorder start using protocol {Protocol}", _name, _configSnapshot.Protocol);

        DownloadUtils? downloader;
        switch (_configSnapshot.Protocol)
        {
            case "hls":
                downloader = await _backendFactory.CreateHlsAsync(_name, _id, _configSnapshot).ConfigureAwait(false);
                if (downloader is null)
                {
                    Log.Warning("{Name} original-quality HLS URL not found, skip this round", _name);
                    if (!IsStopRequested())
                        await Task.Delay(TimeSpan.FromSeconds(_configSnapshot.Interval)).ConfigureAwait(false);
                    return;
                }
                break;
            case "webrtc":
                // Deliberately bypass HLS URL resolution and downloader selection completely.
                downloader = _backendFactory.CreateWebRtc(_name, _id, _configSnapshot);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported recording protocol '{_configSnapshot.Protocol}'. Expected 'hls' or 'webrtc'.");
        }

        Task downloadTask;
        lock (_lifecycleLock)
        {
            if (_stopRequested)
            {
                Log.Debug("{Name} recorder stopped before backend start", _name);
                return;
            }

            _download = downloader;
            if (downloader is IWebRtcSegmentEventSource eventSource)
            {
                _segmentEventSource = eventSource;
                eventSource.SegmentStarted += OnWebRtcSegmentStarted;
                eventSource.SegmentFinalized += OnWebRtcSegmentFinalized;
                eventSource.SegmentAborted += OnWebRtcSegmentAborted;
            }

            // Start under the same lock used by Stop so a stop request cannot race before the
            // backend has created its cancellation state (notably during WebRTC negotiation).
            _downloadTask = downloader.DownloadAsync();
            downloadTask = _downloadTask;
        }

        try
        {
            Log.Verbose("{Name} downloader instance type={DownloaderType}", _name, downloader.GetType().Name);
            await downloadTask.ConfigureAwait(false);
            Log.Debug("{Name} recorder end", _name);
        }
        finally
        {
            DetachSegmentEvents();
        }
    }

    public async Task Stop()
    {
        DownloadUtils? downloader;
        Task? downloadTask;
        lock (_lifecycleLock)
        {
            _stopRequested = true;
            downloader = _download;
            downloadTask = _downloadTask;
        }

        if (downloader is null)
        {
            Log.Verbose("{Name} recorder stop skipped: downloader not started", _name);
            return;
        }

        Log.Debug("{Name} recorder stop requested", _name);
        try
        {
            await downloader.Stop().ConfigureAwait(false);
            if (downloadTask is not null)
                await downloadTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} recorder stop failed", _name);
        }

        Log.Debug("{Name} recorder stopped", _name);
    }

    private bool IsStopRequested()
    {
        lock (_lifecycleLock)
            return _stopRequested;
    }

    private void OnWebRtcSegmentStarted(object? sender, WebRtcSegmentEventArgs args) =>
        _webRtcLifecycle.OnSegmentStarted(args);

    private void OnWebRtcSegmentFinalized(object? sender, WebRtcSegmentEventArgs args) =>
        _webRtcLifecycle.OnSegmentFinalized(args);

    private void OnWebRtcSegmentAborted(object? sender, WebRtcSegmentEventArgs args) =>
        _webRtcLifecycle.OnSegmentAborted(args);

    private void DetachSegmentEvents()
    {
        lock (_lifecycleLock)
        {
            if (_segmentEventSource is null)
                return;

            _segmentEventSource.SegmentStarted -= OnWebRtcSegmentStarted;
            _segmentEventSource.SegmentFinalized -= OnWebRtcSegmentFinalized;
            _segmentEventSource.SegmentAborted -= OnWebRtcSegmentAborted;
            _segmentEventSource = null;
        }
    }

    private static Configs CopyConfig(Configs config) => new()
    {
        Protocol = config.Protocol,
        DebugLog = config.DebugLog,
        TraceLog = config.TraceLog,
        FileLog = config.FileLog,
        Interval = config.Interval,
        Proxy = config.Proxy,
        WebDavUrl = config.WebDavUrl,
        WebDavUsername = config.WebDavUsername,
        WebDavPassword = config.WebDavPassword,
        WebDavAllowInsecureCertificate = config.WebDavAllowInsecureCertificate,
        Downloader = config.Downloader,
        WebRtc = new WebRtcConfigs { NativeLibraryPath = config.WebRtc?.NativeLibraryPath ?? string.Empty },
        Users = config.Users?.ToArray() ?? [],
        SrId = config.SrId
    };
}
