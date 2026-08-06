using System.Collections.Concurrent;
using System.Threading.Channels;
using Serilog;
using SimpleM3U8Parser.Media;

namespace showroom.Download;

/// <summary>
/// HLS 录制管线共享协调状态（StopCoordinator 的雏形）：三个模块（Fetcher/Downloader/Merger）
/// 通过它协调，不再各自持有漂移的 volatile 标志与事件。
/// </summary>
public sealed class PipelineState : IDisposable
{
    private const int MaxQueuedSegments = 128;

    /// <summary>段信道：Fetcher 写，Downloader 读。</summary>
    public Channel<M3u8Media> Segments { get; } =
        Channel.CreateBounded<M3u8Media>(new BoundedChannelOptions(MaxQueuedSegments)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public CancellationTokenSource Cts { get; } = new();

    public ManualResetEventSlim DownloadCompleted { get; } = new(false);
    public ManualResetEventSlim FetchCompleted { get; } = new(false);
    public ManualResetEventSlim FirstBlockDownloaded { get; } = new(false);

    /// <summary>已下载段路径（防重复入队）。Fetcher 检查，Downloader 标记。</summary>
    public ConcurrentDictionary<string, byte> DownloadedSegments { get; } = new();

    /// <summary>已入队/在途段路径。</summary>
    public ConcurrentDictionary<string, byte> QueuedSegments { get; } = new();

    /// <summary>已下载待合并段（Downloader 写，Merger 读/删）。</summary>
    public ConcurrentDictionary<long, byte[]> DownloadedSegmentsCache { get; } = new();

    public volatile bool StopFetching;
    public volatile bool StopDownloading;
    public volatile bool MergeFailed;
    public volatile bool UploadInterrupted;

    /// <summary>中止整条录制管线：置标志、完成信道、取消 cts。</summary>
    public async Task AbortRecordingAsync(string name, string reason, Exception? exception = null)
    {
        if (exception == null)
            Log.Error("{Name} aborting showroom download: {Reason}", name, reason);
        else
            Log.Error(exception, "{Name} aborting showroom download: {Reason}", name, reason);

        MergeFailed = true;
        StopFetching = true;
        StopDownloading = true;
        Segments.Writer.TryComplete();
        await Cts.CancelAsync();
    }

    public void Dispose()
    {
        Cts.Dispose();
        DownloadCompleted.Dispose();
        FetchCompleted.Dispose();
        FirstBlockDownloaded.Dispose();
    }
}