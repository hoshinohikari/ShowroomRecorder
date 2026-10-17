using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Serilog;
using showroom.Utils;
using SimpleM3U8Parser.Media;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace showroom.Download;

/// <summary>消费信道，独占段下载：去重、缓存、并发信号量、重试。下载字节写入共享缓存供 merger 合并。</summary>
public sealed class SegmentDownloader
{
    private const int MaxCachedSegments = 256;
    private const int MaxActiveTasks = 20;
    private const int MaxDownloadRetries = 3;
    private const int MaxConcurrentDownloads = 8;

    private static readonly Regex SequenceNumberRegex = new(@"-(\d+)\.ts", RegexOptions.Compiled);

    private readonly string _name;
    private readonly PipelineState _state;
    private readonly SemaphoreSlim _downloadSemaphore = new(MaxConcurrentDownloads);
    private readonly Func<string, Task<byte[]>> _downloadFile;

    private readonly Uri? _playlistUri;

    public SegmentDownloader(string name, string playlistUrl, PipelineState state,
        Func<string, Task<byte[]>>? downloadFile = null)
    {
        _name = name;
        _state = state;
        _playlistUri = Uri.TryCreate(playlistUrl, UriKind.Absolute, out var uri) ? uri : null;
        _downloadFile = downloadFile ?? (r => ShowroomDownloadHttp.DownloadFile(r, [], 2000));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var activeTasks = new List<Task>();
            var retiredStreams = new HashSet<string>(StringComparer.Ordinal);
            var currentStreams = new HashSet<string>(StringComparer.Ordinal);
            long highestSequence = -1;

            while (!ct.IsCancellationRequested)
            {
                activeTasks.RemoveAll(t => t.IsCompleted);

                if (!await _state.Segments.Reader.WaitToReadAsync(ct))
                {
                    Log.Information("M3u8 fetch has stopped and download channel is completed, ending download task");
                    break;
                }

                while (_state.Segments.Reader.TryRead(out var segment))
                {
                    var sequence = ExtractSequenceNumber(segment.Path);
                    var identity = GetStreamIdentity(segment.Path);
                    if (retiredStreams.Contains(identity))
                    {
                        _state.QueuedSegments.TryRemove(segment.Path, out _);
                        continue;
                    }

                    if (sequence >= 0)
                    {
                        if (currentStreams.Count > 0 && !currentStreams.Contains(identity) && sequence < highestSequence)
                        {
                            // No old request may publish into the cache after Merger resets its sequence.
                            await Task.WhenAll(activeTasks).WaitAsync(ct);
                            activeTasks.Clear();
                            Log.Information(
                                "{Name} stream identity changed with sequence reset: last={LastSequence}, current={Sequence}",
                                _name, highestSequence, sequence);
                            await _state.RequestStreamResetAsync(ct);
                            _state.DownloadedSegments.Clear();
                            retiredStreams.UnionWith(currentStreams);
                            currentStreams.Clear();
                            highestSequence = -1;
                        }

                        currentStreams.Add(identity);
                        highestSequence = Math.Max(highestSequence, sequence);
                    }

                    await _downloadSemaphore.WaitAsync(ct);

                    var downloadTask = Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadSegmentAsync(segment, ct);
                        }
                        finally
                        {
                            _downloadSemaphore.Release();
                        }
                    }, ct);

                    activeTasks.Add(downloadTask);
                }

                if (activeTasks.Count > MaxActiveTasks) await Task.WhenAny(activeTasks);
            }

            if (activeTasks.Count > 0) await Task.WhenAll(activeTasks);

            Log.Information("All segment downloads completed or cancelled");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Download processing cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} error during download processing", _name);
        }
        finally
        {
            _state.DownloadCompleted.Set();
            Log.Information("All downloads completed");
        }
    }

    private long ExtractSequenceNumber(string path)
    {
        var match = SequenceNumberRegex.Match(path);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var sequenceNumber)) return sequenceNumber;
        return -1;
    }

    private string ResolveSegmentUrl(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            throw new InvalidOperationException("Segment path is empty.");

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.ToString();

        if (_playlistUri != null && Uri.TryCreate(_playlistUri, pathOrUrl, out var resolved))
            return resolved.ToString();

        throw new InvalidOperationException(
            $"Cannot resolve segment URL from path '{pathOrUrl}'. Playlist URL: '{_playlistUri}'.");
    }

    private string GetStreamIdentity(string path)
    {
        var uri = new Uri(ResolveSegmentUrl(path));
        var streamSequence = uri.Query.TrimStart('?').Split('&')
            .FirstOrDefault(part => part.StartsWith("txspiseq=", StringComparison.Ordinal));

        // Showroom can reuse the same path after a restart, but changes txspiseq.
        // Ignore CDN host and authentication query changes, which do not identify a new stream.
        return SequenceNumberRegex.Replace(uri.AbsolutePath, "-{sequence}.ts") +
               (streamSequence == null ? string.Empty : "?" + streamSequence);
    }

    private async Task DownloadSegmentAsync(M3u8Media segment, CancellationToken ct)
    {
        var retryCount = 0;

        var fileName = Path.GetFileName(segment.Path);
        if (string.IsNullOrEmpty(fileName)) fileName = $"segment_{Guid.NewGuid()}.ts";

        var currentSequence = ExtractSequenceNumber(segment.Path);

        while (true)
            try
            {
                if (retryCount > 0)
                    Log.Warning(
                        $"Retrying download for segment: {fileName} (sequence: {currentSequence}), attempt {retryCount}/{MaxDownloadRetries}");

                var segmentBytes = await _downloadFile(ResolveSegmentUrl(segment.Path));

                if (segmentBytes is { Length: > 0 })
                {
                    if (currentSequence > 0)
                    {
                        if (_state.DownloadedSegmentsCache.Count >= MaxCachedSegments)
                        {
                            await _state.AbortRecordingAsync(_name,
                                $"downloaded segment cache exceeded limit {MaxCachedSegments}");
                            return;
                        }

                        _state.DownloadedSegmentsCache.TryAdd(currentSequence, segmentBytes);

                        if (!_state.FirstBlockDownloaded.IsSet)
                        {
                            _state.FirstBlockDownloaded.Set();
                            Log.Verbose("First segment downloaded signal set");
                        }
                    }

                    _state.DownloadedSegments.TryAdd(segment.Path, 0);
                    _state.QueuedSegments.TryRemove(segment.Path, out _);

                    if (retryCount > 0)
                        Log.Information(
                            $"Successfully downloaded segment after {retryCount} retries: {fileName} (sequence: {currentSequence}, {segmentBytes.Length} bytes)");
                    else
                        Log.Verbose(
                            $"Successfully downloaded segment: {fileName} (sequence: {currentSequence}, {segmentBytes.Length} bytes)");

                    return;
                }

                retryCount++;

                if (retryCount > MaxDownloadRetries)
                {
                    Log.Warning(
                        $"Failed to download segment after {MaxDownloadRetries} attempts: {fileName}, no data received");
                    _state.QueuedSegments.TryRemove(segment.Path, out _);
                    break;
                }

                await Task.Delay(500 * retryCount, ct);
            }
            catch (Exception ex)
            {
                retryCount++;

                if (retryCount > MaxDownloadRetries)
                {
                    Log.Error(ex,
                        "{Name} error downloading segment after retries: retries={MaxRetries}, segmentPath={SegmentPath}",
                        _name, MaxDownloadRetries, segment.Path);
                    _state.QueuedSegments.TryRemove(segment.Path, out _);
                    break;
                }

                Log.Warning(
                    $"Error downloading segment (attempt {retryCount}/{MaxDownloadRetries}): {segment.Path}, error: {ex}");

                await Task.Delay(500 * retryCount, ct);
            }
    }
}
