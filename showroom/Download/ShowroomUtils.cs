using System.Collections.Concurrent;
using System.Net;
using Serilog;
using showroom.Utils;
using SimpleM3U8Parser;

namespace showroom.Download;

public class ShowroomUtils : DownloadUtils
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly HashSet<string> _downloadedSegments = []; // 用于跟踪已下载的片段，避免重复
    private readonly SemaphoreSlim _downloadSemaphore = new(5); // 限制同时处理的最大片段数为5
    private readonly string _outputDirectory;
    private readonly ConcurrentQueue<ShowroomSegment> _segmentsQueue = new();
    private Task? _downloadTask;
    private Task? _fetchTask;
    private bool _isDownloading;

    public ShowroomUtils(string name, string url) : base(name, url)
    {
        // 设置输出目录
        _outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
        if (!Directory.Exists(_outputDirectory)) Directory.CreateDirectory(_outputDirectory);
    }

    /// <summary>
    ///     从URL中提取路径部分
    /// </summary>
    /// <param name="url">完整URL</param>
    /// <param name="includeQuery">是否包含查询参数</param>
    /// <returns>URL的路径部分</returns>
    private static string ExtractPathFromUrl(string url, bool includeQuery = false)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return includeQuery ? uri.PathAndQuery : uri.AbsolutePath;

        // 如果URL格式无效，返回原始字符串
        return url;
    }

    public override async Task DownloadAsync()
    {
        try
        {
            _isDownloading = true;

            // 初始化下载任务
            _downloadTask = Task.Run(ProcessDownloadQueueAsync);

            // 初始化m3u8获取任务
            _fetchTask = Task.Run(FetchM3u8ContentAsync);

            // 任务已在后台启动，可以立即返回
            await _fetchTask;
            await _downloadTask;
        }
        catch (Exception ex)
        {
            Log.Error($"初始化下载过程时出错: {ex.Message}");
        }
    }

    private async Task FetchM3u8ContentAsync()
    {
        try
        {
            // 继续获取m3u8内容，直到取消
            while (!_cancellationTokenSource.IsCancellationRequested)
                try
                {
                    var m3u8Content =
                        await ShowroomDownloadHttp.Get(ExtractPathFromUrl(Url), new List<(string, string)>());

                    if (m3u8Content.Item1 != HttpStatusCode.OK)
                    {
                        Log.Error($"无法获取m3u8内容，状态码: {m3u8Content.Item1}");

                        // 短暂等待后重试
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                        continue;
                    }

                    var m3u8 = M3u8Parser.Parse(m3u8Content.Item2);
                    var newSegments = 0;

                    // 将新的片段添加到队列
                    foreach (var segment in m3u8.Medias)
                    {
                        // 检查是否已经下载过该片段（通过Path匹配）
                        if (_downloadedSegments.Contains(segment.Path)) continue;
                        _segmentsQueue.Enqueue(new ShowroomSegment
                        {
                            Path = segment.Path,
                            Duration = segment.Duration,
                            Downloaded = false
                        });

                        newSegments++;
                    }

                    if (newSegments > 0) Log.Debug($"添加了 {newSegments} 个新片段到下载队列，当前队列长度: {_segmentsQueue.Count}");

                    // 根据配置的间隔时间等待
                    await Task.Delay(TimeSpan.FromMilliseconds(4000), _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // 取消操作，直接退出循环
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"获取m3u8内容出错: {ex.Message}");

                    // 出错后短暂等待，避免快速重试导致资源浪费
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
        }
        catch (OperationCanceledException)
        {
            // 任务被取消
            Log.Information("m3u8获取任务已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"m3u8获取任务发生未处理异常: {ex}");
        }
    }

    private async Task ProcessDownloadQueueAsync()
    {
        try
        {
            // 创建任务列表跟踪所有下载任务
            List<Task> activeTasks = [];

            // 从队列中获取片段并下载
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                // 先清理已完成的任务
                activeTasks.RemoveAll(t => t.IsCompleted);

                // 如果队列为空，等待一段时间后再检查
                if (_segmentsQueue.IsEmpty)
                {
                    await Task.Delay(500, _cancellationTokenSource.Token);
                    continue;
                }

                // 尝试从队列中取出片段
                if (_segmentsQueue.TryDequeue(out var segment))
                {
                    // 等待获取信号量，限制并发数
                    await _downloadSemaphore.WaitAsync(_cancellationTokenSource.Token);

                    // 启动新任务下载片段
                    var downloadTask = Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadSegmentAsync(segment);
                        }
                        finally
                        {
                            // 释放信号量，允许新的下载任务启动
                            _downloadSemaphore.Release();
                        }
                    }, _cancellationTokenSource.Token);

                    activeTasks.Add(downloadTask);
                }

                // 如果活动任务太多，等待一些任务完成
                if (activeTasks.Count > 20) await Task.WhenAny(activeTasks);
            }

            // 等待所有剩余任务完成
            if (activeTasks.Count > 0) await Task.WhenAll(activeTasks);

            Log.Information("所有片段下载已完成或取消");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("下载处理已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"下载处理过程中发生错误: {ex}");
        }
    }

    private async Task DownloadSegmentAsync(ShowroomSegment segment)
    {
        try
        {
            // 从URL中获取文件名或使用唯一标识符
            var fileName = Path.GetFileName(segment.Path);
            if (string.IsNullOrEmpty(fileName)) fileName = $"segment_{Guid.NewGuid()}.ts";

            var outputPath = Path.Combine(_outputDirectory, fileName);

            // 下载m3u8片段
            var response = await ShowroomDownloadHttp.Get(
                ExtractPathFromUrl(segment.Path, true),
                new List<(string, string)>(),
                5000); // 设置5秒超时

            if (response.Item1 == HttpStatusCode.OK && !string.IsNullOrEmpty(response.Item2))
            {
                // 将内容写入文件
                await File.WriteAllTextAsync(outputPath, response.Item2, _cancellationTokenSource.Token);

                // 记录此片段已下载，避免重复
                _downloadedSegments.Add(segment.Path);

                Log.Debug($"成功下载片段: {fileName}");
            }
            else
            {
                Log.Warning($"下载片段失败: {fileName}, 状态码: {response.Item1}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"下载片段时发生错误: {segment.Path}, 错误: {ex.Message}");
        }
    }

    public override async Task Stop()
    {
        if (_isDownloading)
        {
            // 取消所有任务
            await _cancellationTokenSource.CancelAsync();

            // 等待任务结束
            if (_downloadTask != null)
                try
                {
                    // 设置超时，避免无限等待
                    var timeoutTask = Task.Delay(5000);
                    await Task.WhenAny(_downloadTask, timeoutTask);
                }
                catch (Exception ex)
                {
                    Log.Error($"停止下载任务时发生错误: {ex.Message}");
                }

            if (_fetchTask != null)
                try
                {
                    // 设置超时，避免无限等待
                    var timeoutTask = Task.Delay(5000);
                    await Task.WhenAny(_fetchTask, timeoutTask);
                }
                catch (Exception ex)
                {
                    Log.Error($"停止m3u8获取任务时发生错误: {ex.Message}");
                }

            // 清空队列
            while (_segmentsQueue.TryDequeue(out _))
            {
            }

            _isDownloading = false;

            Log.Information($"{Name} 的下载已停止");
        }
    }
}

public struct ShowroomSegment
{
    public string Path { get; set; }
    public double Duration { get; set; }
    public bool Downloaded { get; set; }
}