using Serilog;

namespace showroom.Download;

public enum MergeEventKind
{
    Merged,
    Rotated,
    Completed,
    Aborted
}

/// <summary>merger 对外事件。上传副作用由编排者据此处理，merger 不碰 WebDAV。</summary>
public readonly record struct MergeEvent(
    MergeEventKind Kind,
    string FilePath,
    string? Reason = null,
    bool IsDiskFull = false,
    long Sequence = -1);

/// <summary>连续性/滚动状态机：从共享缓存按序号合并，经 FileSink 写文件，输出各阶段事件。</summary>
public sealed class SegmentMerger(string name, IFileSinkFactory sinkFactory, PipelineState state)
{
    private const int NonContinuousRetryLimit = 3;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly string _name = name;
    private readonly IFileSinkFactory _sinkFactory = sinkFactory;
    private readonly PipelineState _state = state;
    private readonly TimeSpan _pollInterval;

    private IFileSink _currentSink = null!;
    private long _lastSequenceNumber = -1;
    private long _waitingForSequence = -1;
    private int _nonContinuousRetryCount;

    public SegmentMerger(string name, IFileSinkFactory sinkFactory, PipelineState state,
        TimeSpan? pollInterval = null)
        : this(name, sinkFactory, state)
    {
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public event Action<MergeEvent>? MergeEvent;

    public async Task RunAsync(CancellationToken ct)
    {
        _currentSink = await _sinkFactory.CreateNewAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested &&
                   (!_state.DownloadCompleted.IsSet || _state.DownloadedSegmentsCache.Count > 0))
            {
                if (_state.DownloadedSegmentsCache.Count > 0)
                    await MergePendingAsync(ct);

                await Task.Delay(_pollInterval, ct);
            }

            Log.Information("All segments merged successfully");
            Emit(MergeEventKind.Completed, _currentSink.FilePath);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Merge processing cancelled");
            Emit(MergeEventKind.Aborted, _currentSink.FilePath, "cancelled", false);
        }
        catch (Exception ex)
        {
            var isDiskFull = IsDiskFull(ex);
            _state.UploadInterrupted = isDiskFull;
            _state.MergeFailed = true;
            await _state.AbortRecordingAsync(_name, "merge process failed", ex);
            Emit(MergeEventKind.Aborted, _currentSink.FilePath, ex.Message, isDiskFull);
        }
        finally
        {
            await CloseSinkAsync();
        }
    }

    private async Task MergePendingAsync(CancellationToken ct)
    {
        var cache = _state.DownloadedSegmentsCache;
        var availableSequences = cache.Keys.OrderBy(k => k).ToList();

        if (availableSequences.Count > 0)
            Log.Verbose(
                $"Pending segments: {availableSequences.Count}, sequence range: {availableSequences.First()}-{availableSequences.Last()}");

        foreach (var seqNumber in availableSequences)
        {
            // 跳过比已处理更小的序号（迟到的段）
            if (_lastSequenceNumber > 0 && seqNumber < _lastSequenceNumber)
            {
                if (cache.TryRemove(seqNumber, out _))
                    Log.Warning(
                        $"Discarding expired segment: sequence {seqNumber} < already processed {_lastSequenceNumber}");
                continue;
            }

            // 首段或连续序号
            if (_lastSequenceNumber == -1 || seqNumber == _lastSequenceNumber + 1)
            {
                if (_waitingForSequence > 0 && seqNumber == _waitingForSequence)
                {
                    Log.Verbose($"Found waiting sequence {seqNumber}, resetting retry counter");
                    _nonContinuousRetryCount = 0;
                    _waitingForSequence = -1;
                }

                if (cache.TryRemove(seqNumber, out var segmentData))
                {
                    await _currentSink.AppendAsync(segmentData, ct);
                    _lastSequenceNumber = seqNumber;
                    Log.Verbose($"Merged segment: sequence {seqNumber}, {segmentData.Length} bytes");
                }
            }
            else if (seqNumber > _lastSequenceNumber + 1)
            {
                // 检测到非连续序号
                _nonContinuousRetryCount++;

                if (_waitingForSequence == -1)
                {
                    _waitingForSequence = _lastSequenceNumber + 1;
                    Log.Debug(
                        $"Detected non-continuous sequence: last {_lastSequenceNumber}, current {seqNumber}, waiting for {_waitingForSequence}, retry {_nonContinuousRetryCount}/3");
                }

                // 三次重试后仍未连续，滚动到新文件
                if (_nonContinuousRetryCount >= NonContinuousRetryLimit)
                {
                    Log.Warning(
                        $"After 3 retries, still non-continuous sequence: last {_lastSequenceNumber}, current {seqNumber}, gap {seqNumber - _lastSequenceNumber - 1}, creating new file");

                    var completedFilePath = _currentSink.FilePath;
                    await _currentSink.FlushAsync(ct);
                    await _currentSink.DisposeAsync();
                    Emit(MergeEventKind.Rotated, completedFilePath);

                    _currentSink = await _sinkFactory.CreateNewAsync(ct);
                    Log.Debug($"New file stream opened for: {_currentSink.FilePath}");

                    if (cache.TryRemove(seqNumber, out var segmentData))
                    {
                        await _currentSink.AppendAsync(segmentData, ct);
                        _lastSequenceNumber = seqNumber;
                        Log.Debug(
                            $"Merged segment to new file: sequence {seqNumber}, {segmentData.Length} bytes");
                    }

                    _nonContinuousRetryCount = 0;
                    _waitingForSequence = -1;
                }

                break;
            }
        }
    }

    private async Task CloseSinkAsync()
    {
        try
        {
            await _currentSink.FlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} error flushing file stream", _name);
        }

        try
        {
            await _currentSink.DisposeAsync();
            Log.Debug($"File stream closed for: {_currentSink.FilePath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} error closing file stream", _name);
        }
    }

    private static bool IsDiskFull(Exception exception)
    {
        return exception is IOException &&
               exception.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase);
    }

    private void Emit(MergeEventKind kind, string filePath, string? reason = null, bool isDiskFull = false,
        long sequence = -1)
        => MergeEvent?.Invoke(new MergeEvent(kind, filePath, reason, isDiskFull, sequence));
}