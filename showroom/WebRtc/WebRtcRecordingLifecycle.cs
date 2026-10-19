using Serilog;
using showroom.Utils;

namespace showroom.WebRtc;

internal interface IWebRtcSegmentEventSource
{
    event EventHandler<WebRtcSegmentEventArgs>? SegmentStarted;
    event EventHandler<WebRtcSegmentEventArgs>? SegmentFinalized;
    event EventHandler<WebRtcSegmentEventArgs>? SegmentAborted;
}

internal interface IWebRtcRecordingLifecycle
{
    void OnSegmentStarted(WebRtcSegmentEventArgs args);
    void OnSegmentFinalized(WebRtcSegmentEventArgs args);
    void OnSegmentAborted(WebRtcSegmentEventArgs args);
}

internal sealed class WebRtcRecordingLifecycle : IWebRtcRecordingLifecycle
{
    private readonly Action<string> _enqueueUpload;
    private readonly Action<string> _deletePartialFile;

    public WebRtcRecordingLifecycle(Action<string>? enqueueUpload = null, Action<string>? deletePartialFile = null)
    {
        _enqueueUpload = enqueueUpload ?? WebDavUploader.EnqueueUpload;
        _deletePartialFile = deletePartialFile ?? File.Delete;
    }

    public void OnSegmentStarted(WebRtcSegmentEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
        {
            Log.Warning("WebRTC segment start ignored because its output path is empty.");
            return;
        }

        RecordingRegistry.MarkRecording(args.Path);
        Log.Information("WebRTC recording segment started: {FilePath}", args.Path);
    }

    public void OnSegmentFinalized(WebRtcSegmentEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
        {
            Log.Warning("WebRTC segment completion ignored because its output path is empty.");
            return;
        }

        RecordingRegistry.MarkCompleted(args.Path);
        _enqueueUpload(args.Path);
        Log.Information("WebRTC recording segment finalized and queued for upload: {FilePath}", args.Path);
    }

    public void OnSegmentAborted(WebRtcSegmentEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
        {
            Log.Warning("WebRTC aborted segment has no output path; there is nothing to clean up.");
            return;
        }

        try
        {
            if (File.Exists(args.Path))
                _deletePartialFile(args.Path);

            if (File.Exists(args.Path))
                throw new IOException("The incomplete WebRTC segment still exists after deletion was attempted.");

            RecordingRegistry.MarkCompleted(args.Path);
            Log.Warning("WebRTC incomplete recording segment deleted: {FilePath}; error={Error}", args.Path,
                args.Error);
        }
        catch (Exception ex)
        {
            // Keep the registry marker in place so the background scanner cannot upload the partial MKV.
            Log.Warning(ex, "WebRTC incomplete recording segment remains protected from upload: {FilePath}",
                args.Path);
        }
    }
}
