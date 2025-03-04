using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Serilog;
using showroom.Utils;
using SimpleM3U8Parser;
using SimpleM3U8Parser.Media;

namespace showroom.Download;

public class ShowroomUtils : DownloadUtils
{
    private static readonly Regex
        SequenceNumberRegex = new(@"-(\d+)\.ts", RegexOptions.Compiled); // Extract sequence number from path

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ManualResetEventSlim _downloadCompletedEvent = new(false);
    private readonly HashSet<string> _downloadedSegments = []; // Track downloaded segment paths

    private readonly ConcurrentDictionary<long, byte[]>
        _downloadedSegmentsCache = new(); // Store downloaded but not yet merged segments

    private readonly SemaphoreSlim _downloadSemaphore = new(8); // Limit max concurrent downloads to 8
    private readonly ManualResetEventSlim _fetchCompletedEvent = new(false);
    private readonly SemaphoreSlim _fileLock = new(1); // Ensure thread-safe file writing
    private readonly ManualResetEventSlim _firstBlockDownloadedEvent = new(false);
    private readonly string _outputDirectory;
    private readonly ConcurrentQueue<M3u8Media> _segmentsQueue = new();
    private string _combinedFilePath = null!; // Merged file path
    private Task? _downloadTask;
    private Task? _fetchTask;
    private bool _isDownloading;
    private long _lastProcessedSequence = -1; // Track the maximum sequence number processed
    private long _lastSequenceNumber = -1; // Track the sequence number of last merged segment
    private Task? _mergeTask; // New: merge thread
    private int _nonContinuousRetryCount;
    private volatile bool _stopDownloading;
    private volatile bool _stopFetchingM3u8;
    private long _waitingForSequence = -1;

    public ShowroomUtils(string name, string url) : base(name, url)
    {
        // Set output directory
        _outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
        if (!Directory.Exists(_outputDirectory)) Directory.CreateDirectory(_outputDirectory);

        // Create new output file
        CreateNewOutputFile();
    }

    // Create new output file
    private void CreateNewOutputFile()
    {
        var timestamp = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss_fff");
        _combinedFilePath = Path.Combine(_outputDirectory, $"{Name}_{timestamp}.ts");
        Log.Information($"Creating new output file: {_combinedFilePath}");
    }

    // Extract sequence number from segment path
    private long ExtractSequenceNumber(string path)
    {
        var match = SequenceNumberRegex.Match(path);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var sequenceNumber)) return sequenceNumber;
        return -1; // Unable to extract sequence number
    }

    /// <summary>
    ///     Extract path portion from URL
    /// </summary>
    /// <param name="url">Complete URL</param>
    /// <param name="includeQuery">Whether to include query parameters</param>
    /// <returns>Path portion of the URL</returns>
    private static string ExtractPathFromUrl(string url, bool includeQuery = false)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return includeQuery ? uri.PathAndQuery : uri.AbsolutePath;

        // If URL format is invalid, return the original string
        return url;
    }

    public override async Task DownloadAsync()
    {
        try
        {
            _isDownloading = true;

            // Initialize download task
            _downloadTask = Task.Run(ProcessDownloadQueueAsync);

            // Initialize m3u8 fetch task
            _fetchTask = Task.Run(FetchM3u8ContentAsync);

            // Delayed initialization of merge task - wait for first block download
            _mergeTask = Task.Run(async () =>
            {
                // Wait for first block download signal, timeout after 30 seconds
                var firstBlockDownloaded = await Task.Run(() => _firstBlockDownloadedEvent.Wait(30000));

                if (firstBlockDownloaded)
                {
                    // First block downloaded, wait additional 2 seconds
                    Log.Information("First segment downloaded, waiting 2 seconds before starting merge process...");
                    await Task.Delay(2000, _cancellationTokenSource.Token);

                    // Start merge process
                    await MergeSegmentsAsync();
                }
                else
                {
                    Log.Warning("Timeout waiting for first segment download, starting merge process anyway");
                    await MergeSegmentsAsync();
                }
            });

            await Task.WhenAll(_fetchTask, _downloadTask, _mergeTask);
        }
        catch (Exception ex)
        {
            Log.Error($"Error initializing download process: {ex}");
        }
    }

    private async Task MergeSegmentsAsync()
    {
        FileStream? currentFileStream = null;

        try
        {
            // 在开始合并线程时打开文件流
            currentFileStream = new FileStream(
                _combinedFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);

            Log.Debug($"File stream opened for: {_combinedFilePath}");

            while (!_cancellationTokenSource.IsCancellationRequested &&
                   (!_downloadCompletedEvent.IsSet || _downloadedSegmentsCache.Count > 0))
            {
                // Check if there are new segments to merge
                if (_downloadedSegmentsCache.Count > 0)
                {
                    // Get all available sequence numbers and sort them
                    var availableSequences = _downloadedSegmentsCache.Keys.OrderBy(k => k).ToList();

                    if (availableSequences.Count > 0)
                        Log.Debug(
                            $"Pending segments: {availableSequences.Count}, sequence range: {availableSequences.First()}-{availableSequences.Last()}");

                    // Start merging from the smallest sequence number
                    foreach (var seqNumber in availableSequences)
                    {
                        // Skip segments with sequence numbers smaller than already processed ones (likely delayed segments)
                        if (_lastSequenceNumber > 0 && seqNumber < _lastSequenceNumber)
                        {
                            if (_downloadedSegmentsCache.TryRemove(seqNumber, out _))
                                Log.Warning(
                                    $"Discarding expired segment: sequence {seqNumber} < already processed {_lastSequenceNumber}");
                            continue;
                        }

                        // If this is the first segment, or a consecutive sequence number
                        if (_lastSequenceNumber == -1 || seqNumber == _lastSequenceNumber + 1)
                        {
                            // If we found the sequence number we were waiting for, reset retry counter
                            if (_waitingForSequence > 0 && seqNumber == _waitingForSequence)
                            {
                                Log.Debug($"Found waiting sequence {seqNumber}, resetting retry counter");
                                _nonContinuousRetryCount = 0;
                                _waitingForSequence = -1;
                            }

                            // Try to get segment from cache
                            if (!_downloadedSegmentsCache.TryRemove(seqNumber, out var segmentData)) continue;
                            await _fileLock.WaitAsync(_cancellationTokenSource.Token);
                            try
                            {
                                // 使用已打开的文件流写入
                                await currentFileStream.WriteAsync(segmentData, _cancellationTokenSource.Token);

                                // Update last processed sequence number
                                _lastSequenceNumber = seqNumber;
                                Log.Debug($"Merged segment: sequence {seqNumber}, {segmentData.Length} bytes");
                            }
                            finally
                            {
                                _fileLock.Release();
                            }
                        }
                        else if (seqNumber > _lastSequenceNumber + 1)
                        {
                            // Detected non-continuous sequence
                            _nonContinuousRetryCount++;

                            // Set the sequence number we're waiting for
                            if (_waitingForSequence == -1)
                            {
                                _waitingForSequence = _lastSequenceNumber + 1;
                                Log.Debug(
                                    $"Detected non-continuous sequence: last {_lastSequenceNumber}, current {seqNumber}, waiting for {_waitingForSequence}, retry {_nonContinuousRetryCount}/3");
                            }

                            // Only create a new file after three retries
                            if (_nonContinuousRetryCount >= 3)
                            {
                                // Detected non-continuous sequence, create new file
                                Log.Warning(
                                    $"After 3 retries, still non-continuous sequence: last {_lastSequenceNumber}, current {seqNumber}, gap {seqNumber - _lastSequenceNumber - 1}, creating new file");
                                await _fileLock.WaitAsync(_cancellationTokenSource.Token);
                                try
                                {
                                    // 关闭当前文件流
                                    await currentFileStream.FlushAsync(_cancellationTokenSource.Token);
                                    await currentFileStream.DisposeAsync();

                                    // Create new file
                                    CreateNewOutputFile();

                                    // 打开新文件流
                                    currentFileStream = new FileStream(
                                        _combinedFilePath,
                                        FileMode.Append,
                                        FileAccess.Write,
                                        FileShare.Read);

                                    Log.Debug($"New file stream opened for: {_combinedFilePath}");

                                    // Write current segment
                                    if (_downloadedSegmentsCache.TryRemove(seqNumber, out var segmentData))
                                    {
                                        await currentFileStream.WriteAsync(segmentData, _cancellationTokenSource.Token);

                                        // Update last processed sequence number
                                        _lastSequenceNumber = seqNumber;
                                        Log.Debug(
                                            $"Merged segment to new file: sequence {seqNumber}, {segmentData.Length} bytes");
                                    }

                                    // Reset retry counter and waiting sequence number
                                    _nonContinuousRetryCount = 0;
                                    _waitingForSequence = -1;
                                }
                                finally
                                {
                                    _fileLock.Release();
                                }

                                // Current sequence has been processed, break loop to continue with other segments
                            }

                            // Not reached maximum retry count, exit this loop and wait for next check
                            break;
                        }
                    }
                }

                // Wait before checking again
                await Task.Delay(2000, _cancellationTokenSource.Token);
            }

            Log.Information("All segments merged successfully");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Merge processing cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"Error during merge process: {ex}");
        }
        finally
        {
            // 确保在任务结束时关闭文件流
            if (currentFileStream != null)
            {
                await _fileLock.WaitAsync();
                try
                {
                    await currentFileStream.FlushAsync();
                    await currentFileStream.DisposeAsync();
                    Log.Debug($"File stream closed for: {_combinedFilePath}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Error closing file stream: {ex.Message}");
                }
                finally
                {
                    _fileLock.Release();
                }
            }
        }
    }

    private async Task FetchM3u8ContentAsync()
    {
        try
        {
            var retryCount = 0; // Initialize retry counter
            var defaultWaitTime = TimeSpan.FromSeconds(4); // Default wait time of 4 seconds

            // Continue fetching m3u8 content until cancelled or max retries reached
            while (!_cancellationTokenSource.IsCancellationRequested && !_stopFetchingM3u8)
                try
                {
                    var m3u8Content =
                        await ShowroomDownloadHttp.Get(ExtractPathFromUrl(Url), new List<(string, string)>(), 2000);

                    if (m3u8Content.Item1 != HttpStatusCode.OK)
                    {
                        Log.Error($"Unable to fetch m3u8 content, status code: {m3u8Content.Item1}");

                        // Increment retry counter
                        retryCount++;
                        Log.Warning($"M3u8 fetch failed, retry {retryCount}/5");

                        // If max retry count reached, the stream has likely ended
                        if (retryCount >= 5)
                        {
                            Log.Information("Failed to fetch 5 consecutive times, stream may have ended");

                            // Set stop flags to begin shutdown process
                            _stopFetchingM3u8 = true;

                            // When stream ends, signal to stop downloading
                            // Note: This will let the download queue complete existing content before stopping
                            _stopDownloading = true;

                            break;
                        }

                        // Short delay before retry
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                        continue;
                    }

                    // Successfully fetched m3u8 content, reset retry counter
                    retryCount = 0;

                    var m3u8 = M3u8Parser.Parse(m3u8Content.Item2);
                    var newSegments = 0;
                    double totalMediaDuration = 0; // Total duration of all media segments in seconds

                    // Add new segments to queue
                    foreach (var segment in m3u8.Medias)
                    {
                        // Accumulate segment duration (assuming segment has Duration property in seconds)
                        // Note: If M3u8Media doesn't directly provide Duration, adjust based on actual implementation
                        if (segment.Duration > 0) totalMediaDuration += segment.Duration;

                        // Check if segment has already been downloaded (match by Path)
                        if (_downloadedSegments.Contains(segment.Path)) continue;
                        _segmentsQueue.Enqueue(segment);

                        newSegments++;
                    }

                    if (newSegments > 0)
                        Log.Debug(
                            $"Added {newSegments} new segments to download queue, current queue length: {_segmentsQueue.Count}");

                    // Calculate dynamic wait time: half of total duration, but not less than 1s and not more than 10s
                    TimeSpan waitTime; // Initial wait time
                    if (totalMediaDuration > 0)
                    {
                        waitTime = TimeSpan.FromSeconds(Math.Min(Math.Max(totalMediaDuration / 2 - 1.0, 1), 10));
                        Log.Debug(
                            $"Calculated wait time based on media duration: {waitTime.TotalSeconds:F1}s (total media duration: {totalMediaDuration:F1}s)");
                    }
                    else
                    {
                        // Use default value if unable to calculate duration
                        waitTime = defaultWaitTime;
                        Log.Debug($"Using default wait time: {waitTime.TotalSeconds:F1}s");
                    }

                    // Wait according to calculated interval
                    await Task.Delay(waitTime, _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // Operation cancelled, exit loop
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Error fetching m3u8 content: {ex}");

                    // Increment retry counter (exceptions also count as retries)
                    retryCount++;
                    Log.Warning($"M3u8 fetch exception, retry {retryCount}/5");

                    // If max retry count reached, the stream has likely ended
                    if (retryCount >= 5)
                    {
                        Log.Information("Failed to fetch 5 consecutive times, stream may have ended");

                        // Set stop flags to begin shutdown process
                        _stopFetchingM3u8 = true;

                        // When stream ends, signal to stop downloading
                        _stopDownloading = true;

                        break;
                    }

                    // Short delay before retry
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
        }
        catch (OperationCanceledException)
        {
            Log.Information("M3u8 fetch task cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"Unhandled exception in m3u8 fetch task: {ex}");
        }
        finally
        {
            // Ensure download stop flag is set when m3u8 fetch completes
            if (!_stopDownloading)
            {
                _stopDownloading = true;
                Log.Information("M3u8 fetching ended, marking download queue to complete remaining items");
            }

            // Signal that m3u8 fetching is complete
            _fetchCompletedEvent.Set();
            Log.Information("M3u8 fetching stopped");
        }
    }

    private async Task ProcessDownloadQueueAsync()
    {
        try
        {
            // Create task list to track all download tasks
            List<Task> activeTasks = [];

            // Get segments and download until cancelled or stop flag is set and queue is empty
            while (!_cancellationTokenSource.IsCancellationRequested &&
                   (!_stopDownloading || !_segmentsQueue.IsEmpty))
            {
                // First clean up completed tasks
                activeTasks.RemoveAll(t => t.IsCompleted);

                // If queue is empty, wait for a while before checking again
                if (_segmentsQueue.IsEmpty)
                {
                    // If m3u8 fetch has stopped and queue is empty, end download task
                    if (_fetchCompletedEvent.IsSet)
                    {
                        Log.Information("M3u8 fetch has stopped and download queue is empty, ending download task");
                        break;
                    }

                    await Task.Delay(500, _cancellationTokenSource.Token);
                    continue;
                }

                // Try to dequeue a segment
                if (_segmentsQueue.TryDequeue(out var segment))
                {
                    // Wait for semaphore to limit concurrency
                    await _downloadSemaphore.WaitAsync(_cancellationTokenSource.Token);

                    // Start new task to download segment
                    var downloadTask = Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadSegmentAsync(segment);
                        }
                        finally
                        {
                            // Release semaphore to allow new download tasks
                            _downloadSemaphore.Release();
                        }
                    }, _cancellationTokenSource.Token);

                    activeTasks.Add(downloadTask);
                }

                // If too many active tasks, wait for some to complete
                if (activeTasks.Count > 20) await Task.WhenAny(activeTasks);
            }

            // Wait for all remaining tasks to complete
            if (activeTasks.Count > 0) await Task.WhenAll(activeTasks);

            Log.Information("All segment downloads completed or cancelled");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Download processing cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"Error during download processing: {ex}");
        }
        finally
        {
            // Signal that downloading is completed
            _downloadCompletedEvent.Set();
            Log.Information("All downloads completed");
        }
    }

    private async Task DownloadSegmentAsync(M3u8Media segment)
    {
        // Maximum retry attempts
        const int maxRetries = 3;
        var retryCount = 0;

        // Extract filename for logging
        var fileName = Path.GetFileName(segment.Path);
        if (string.IsNullOrEmpty(fileName)) fileName = $"segment_{Guid.NewGuid()}.ts";

        // Extract sequence number
        var currentSequence = ExtractSequenceNumber(segment.Path);

        while (true)
            try
            {
                // If this is not the first attempt, log retry information
                if (retryCount > 0)
                    Log.Warning(
                        $"Retrying download for segment: {fileName} (sequence: {currentSequence}), attempt {retryCount}/{maxRetries}");

                // Download m3u8 segment
                var segmentBytes = await ShowroomDownloadHttp.DownloadFile(
                    ExtractPathFromUrl(segment.Path, true),
                    new List<(string, string)>(),
                    2000); // 2 second timeout

                // Download successful
                if (segmentBytes is { Length: > 0 })
                {
                    // Only cache segments with recognizable sequence numbers
                    if (currentSequence > 0)
                    {
                        // Add segment to cache
                        _downloadedSegmentsCache.TryAdd(currentSequence, segmentBytes);

                        // Update maximum processed sequence number
                        if (currentSequence > _lastProcessedSequence) _lastProcessedSequence = currentSequence;

                        // Signal that first block has been downloaded (if not already set)
                        if (!_firstBlockDownloadedEvent.IsSet)
                        {
                            _firstBlockDownloadedEvent.Set();
                            Log.Debug("First segment downloaded signal set");
                        }
                    }

                    // Mark segment as downloaded
                    _downloadedSegments.Add(segment.Path);

                    // Special log if retry was successful
                    if (retryCount > 0)
                        Log.Information(
                            $"Successfully downloaded segment after {retryCount} retries: {fileName} (sequence: {currentSequence}, {segmentBytes.Length} bytes)");
                    else
                        Log.Debug(
                            $"Successfully downloaded segment: {fileName} (sequence: {currentSequence}, {segmentBytes.Length} bytes)");

                    // Download successful, exit loop
                    return;
                }

                // No data received, increase retry counter
                retryCount++;

                if (retryCount > maxRetries)
                {
                    Log.Warning(
                        $"Failed to download segment after {maxRetries} attempts: {fileName}, no data received");
                    break;
                }

                // Wait before retrying
                await Task.Delay(500 * retryCount, _cancellationTokenSource.Token); // Incremental wait time
            }
            catch (Exception ex)
            {
                // Increase retry counter
                retryCount++;

                if (retryCount > maxRetries)
                {
                    Log.Error($"Error downloading segment after {maxRetries} attempts: {segment.Path}, error: {ex}");
                    break;
                }

                Log.Warning(
                    $"Error downloading segment (attempt {retryCount}/{maxRetries}): {segment.Path}, error: {ex}");

                // Wait before retrying
                await Task.Delay(500 * retryCount, _cancellationTokenSource.Token); // Incremental wait time
            }
    }

    public override async Task Stop()
    {
        if (_isDownloading)
            try
            {
                Log.Information($"Starting to stop download for {Name}...");

                // Phase 1: Stop fetching new m3u8
                _stopFetchingM3u8 = true;
                Log.Information("Stopping new M3u8 segment fetching...");

                // Wait for m3u8 fetching to complete (max 5 seconds)
                await Task.Run(() => _fetchCompletedEvent.Wait(5000));

                // Phase 2: Stop adding new download tasks but let existing ones complete
                _stopDownloading = true;
                Log.Information("Waiting for existing download tasks to complete...");

                // Wait for downloads to complete (max 30 seconds)
                await Task.Run(() => _downloadCompletedEvent.Wait(30000));

                // Phase 3: Wait for all segments to be merged (max 10 seconds)
                if (_downloadedSegmentsCache.Count > 0)
                {
                    Log.Information($"Waiting for {_downloadedSegmentsCache.Count} segments to be merged...");
                    foreach (var seq in _downloadedSegmentsCache.Keys)
                        Log.Debug($"Waiting for sequence {seq} to be merged...");
                    await Task.Delay(10000); // Give merging process up to 10 seconds
                }

                // Final phase: Cancel all remaining tasks
                await _cancellationTokenSource.CancelAsync();

                // Wait for tasks to fully end
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

                // Clear queue and cache
                while (_segmentsQueue.TryDequeue(out _))
                {
                }

                _downloadedSegmentsCache.Clear();

                // Reset events
                _fetchCompletedEvent.Reset();
                _downloadCompletedEvent.Reset();
                _firstBlockDownloadedEvent.Reset();

                _isDownloading = false;
                Log.Information($"Download for {Name} has completely stopped");
            }
            catch (Exception ex)
            {
                Log.Error($"Error during stop process: {ex}");
                // Ensure download status is set to false
                _isDownloading = false;
            }
    }
}