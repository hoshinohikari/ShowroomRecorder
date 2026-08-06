using Serilog;
using showroom.Utils;

namespace showroom.Download;

/// <summary>
/// HLS 录制编排者（owner）：持有共享协调状态，接线 Fetcher/Downloader/Merger 三个模块，
/// 负责停止编排与上传副作用。具体数据流在 SegmentFetcher/SegmentDownloader/SegmentMerger。
/// </summary>
public class ShowroomUtils : DownloadUtils, IFileSinkFactory
{
    private readonly PipelineState _state = new();
    private readonly SegmentFetcher _fetcher;
    private readonly SegmentDownloader _downloader;
    private readonly SegmentMerger _merger;
    private readonly string _outputDirectory;

    private Task? _downloadTask;
    private Task? _fetchTask;
    private Task? _mergeTask;
    private volatile bool _isDownloading;

    public ShowroomUtils(string name, string url) : base(name, url)
    {
        _outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
        if (!Directory.Exists(_outputDirectory)) Directory.CreateDirectory(_outputDirectory);

        _fetcher = new SegmentFetcher(name, url, _state);
        _downloader = new SegmentDownloader(name, url, _state);
        _merger = new SegmentMerger(name, this, _state);
        _merger.MergeEvent += OnMergeEvent;
    }

    // ---- 上传副作用：由 merger 事件驱动 ----

    private void OnMergeEvent(MergeEvent e)
    {
        switch (e.Kind)
        {
            case MergeEventKind.Completed:
            case MergeEventKind.Rotated:
                UploadCompletedFile(e.FilePath);
                break;
            case MergeEventKind.Aborted:
                if (e.IsDiskFull)
                    UploadInterruptedFile(e.FilePath);
                else
                    Log.Warning("{Name} recording file left incomplete, upload not marked complete: {FilePath}",
                        Name, e.FilePath);
                break;
        }
    }

    private void UploadCompletedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Log.Warning("{Name} upload skipped: file path is empty", Name);
            return;
        }

        RecordingRegistry.MarkCompleted(filePath);
        WebDavUploader.EnqueueUpload(filePath);
        Log.Debug("{Name} completed recording file, upload queued: {FilePath}", Name, filePath);
    }

    private void UploadInterruptedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            Log.Warning("{Name} interrupted upload skipped: file path is empty", Name);
            return;
        }

        RecordingRegistry.MarkCompleted(filePath);
        WebDavUploader.EnqueueUpload(filePath);
        Log.Warning("{Name} interrupted recording enqueued for WebDAV upload to free local disk: {FilePath}",
            Name, filePath);
    }

    // ---- IFileSinkFactory：命名 + 注册 + 打开 sink ----

    public async Task<IFileSink> CreateNewAsync(CancellationToken ct)
    {
        var path = CreateNewOutputFile();
        await Task.CompletedTask;
        return new FileSink(path);
    }

    private string CreateNewOutputFile()
    {
        var timestamp = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss_fff");
        var path = Path.Combine(_outputDirectory, $"{Name}_{timestamp}.ts");
        RecordingRegistry.MarkRecording(path);
        Log.Information($"Creating new output file: {path}");
        return path;
    }

    // ---- 生命周期 ----

    public override async Task DownloadAsync()
    {
        try
        {
            _isDownloading = true;
            Log.Debug("{Name} showroom downloader start: playlist={PlaylistUrl}", Name, Url);

            _downloadTask = Task.Run(() => _downloader.RunAsync(_state.Cts.Token));
            _fetchTask = Task.Run(() => _fetcher.RunAsync(_state.Cts.Token));

            // 延迟启动 merge 任务：等首块下载信号，超时 20 秒
            _mergeTask = Task.Run(async () =>
            {
                var firstBlockDownloaded = await Task.Run(() => _state.FirstBlockDownloaded.Wait(20000));

                if (firstBlockDownloaded)
                {
                    Log.Information("First segment downloaded, waiting 2 seconds before starting merge process...");
                    await Task.Delay(2000, _state.Cts.Token);
                }
                else
                {
                    Log.Warning("Timeout waiting for first segment download, starting merge process anyway");
                }

                await _merger.RunAsync(_state.Cts.Token);
            });

            await Task.WhenAll(_fetchTask, _downloadTask, _mergeTask);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} initialize showroom download process failed", Name);
        }
        finally
        {
            _isDownloading = false;
            Log.Debug("{Name} showroom downloader end", Name);
        }
    }

    public override async Task Stop()
    {
        if (_isDownloading)
            try
            {
                Log.Information("Starting to stop download for {Name}...", Name);
                Log.Debug(
                    "{Name} stop state before: fetchingStopped={StopFetching}, downloadingStopped={StopDownloading}, cacheCount={CacheCount}",
                    Name, _state.StopFetching, _state.StopDownloading, _state.DownloadedSegmentsCache.Count);

                // Phase 1: 停止抓取新 m3u8
                _state.StopFetching = true;
                Log.Information("Stopping new M3u8 segment fetching...");

                _state.Segments.Writer.TryComplete();

                // 等 m3u8 抓取完成（最多 5 秒）
                await Task.Run(() => _state.FetchCompleted.Wait(5000));

                // Phase 2: 停止新增下载任务，让在途任务完成
                _state.StopDownloading = true;
                Log.Information("Waiting for existing download tasks to complete...");

                await Task.Run(() => _state.DownloadCompleted.Wait(30000));

                // Phase 3: 等所有段合并完成（最多 10 秒）
                if (_mergeTask != null)
                {
                    var mergeTimeout = Task.Delay(10000);
                    var completed = await Task.WhenAny(_mergeTask, mergeTimeout);
                    if (completed == mergeTimeout)
                        Log.Warning("Merge task did not complete within 10 seconds");
                }

                // Final: 取消所有剩余任务
                await _state.Cts.CancelAsync();

                var tasks = new List<Task?> { _downloadTask, _fetchTask, _mergeTask };
                foreach (var task in tasks.Where(t => t != null))
                    try
                    {
                        var timeoutTask = Task.Delay(2000);
                        await Task.WhenAny(task!, timeoutTask);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error stopping task: {ex}");
                    }

                // 清空信道与缓存
                while (_state.Segments.Reader.TryRead(out _))
                {
                }

                _state.DownloadedSegmentsCache.Clear();

                // 重置事件
                _state.FetchCompleted.Reset();
                _state.DownloadCompleted.Reset();
                _state.FirstBlockDownloaded.Reset();

                _isDownloading = false;
                Log.Information("Download for {Name} has completely stopped", Name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Name} error during stop process", Name);
                _isDownloading = false;
            }
    }
}