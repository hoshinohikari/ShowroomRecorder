using System.Threading.Channels;
using Serilog;
using showroom.Utils;
using SimpleM3U8Parser;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace showroom.Download;

/// <summary>轮询 m3u8 播放清单，把未下载的段描述写入信道。不碰任何下载状态。</summary>
public sealed class SegmentFetcher
{
    private const int MaxRetries = 5;
    private static readonly TimeSpan DefaultWaitTime = TimeSpan.FromSeconds(4);

    private readonly string _name;
    private readonly string _url;
    private readonly PipelineState _state;
    private readonly Func<string, Task<(HttpStatusCode, string)>> _get;

    public SegmentFetcher(string name, string url, PipelineState state,
        Func<string, Task<(HttpStatusCode, string)>>? get = null)
    {
        _name = name;
        _url = url;
        _state = state;
        _get = get ?? (r => ShowroomDownloadHttp.Get(r, [], 2000));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var retryCount = 0;

            while (!ct.IsCancellationRequested && !_state.StopFetching)
                try
                {
                    var m3U8Content = await _get(_url);

                    if (m3U8Content.Item1 != HttpStatusCode.OK)
                    {
                        Log.Error($"Unable to fetch m3u8 content, status code: {m3U8Content.Item1}");
                        retryCount++;
                        Log.Warning($"M3u8 fetch failed, retry {retryCount}/5");

                        if (retryCount >= MaxRetries)
                        {
                            Log.Information("Failed to fetch 5 consecutive times, stream may have ended");
                            _state.StopFetching = true;
                            _state.StopDownloading = true;
                            break;
                        }

                        await Task.Delay(1000, ct);
                        continue;
                    }

                    retryCount = 0;
                    Log.Verbose("Get m3u8 content length: {Length}", m3U8Content.Item2.Length);
                    var m3U8 = M3u8Parser.Parse(m3U8Content.Item2);
                    var newSegments = 0;
                    var totalMediaDuration = 0.0;

                    foreach (var segment in m3U8.Medias)
                    {
                        if (segment.Duration > 0) totalMediaDuration += segment.Duration;

                        // 已下载或已入队则跳过
                        if (_state.DownloadedSegments.ContainsKey(segment.Path) ||
                            !_state.QueuedSegments.TryAdd(segment.Path, 0))
                            continue;

                        try
                        {
                            await _state.Segments.Writer.WriteAsync(segment, ct);
                        }
                        catch (ChannelClosedException)
                        {
                            _state.QueuedSegments.TryRemove(segment.Path, out _);
                            Log.Verbose("Segment channel closed, stop adding new segments");
                            _state.StopFetching = true;
                            break;
                        }

                        newSegments++;
                    }

                    if (newSegments > 0)
                        Log.Debug($"Added {newSegments} new segments to download queue");

                    // 动态等待：媒体总时长的一半，1s~10s
                    TimeSpan waitTime;
                    if (totalMediaDuration > 0)
                    {
                        waitTime = TimeSpan.FromSeconds(Math.Min(Math.Max(totalMediaDuration / 2.0 - 1.5, 1.0), 10.0));
                        Log.Verbose(
                            $"Calculated wait time based on media duration: {waitTime.TotalSeconds:F2}s (total media duration: {totalMediaDuration:F2}s)");
                    }
                    else
                    {
                        waitTime = DefaultWaitTime;
                        Log.Verbose($"Using default wait time: {waitTime.TotalSeconds:F1}s");
                    }

                    await Task.Delay(waitTime, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "{Name} error fetching m3u8 content", _name);
                    retryCount++;
                    Log.Warning($"M3u8 fetch exception, retry {retryCount}/5");

                    if (retryCount >= MaxRetries)
                    {
                        Log.Information("Failed to fetch 5 consecutive times, stream may have ended");
                        _state.StopFetching = true;
                        _state.StopDownloading = true;
                        break;
                    }

                    await Task.Delay(1000, ct);
                }
        }
        catch (OperationCanceledException)
        {
            Log.Information("M3u8 fetch task cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} unhandled exception in m3u8 fetch task", _name);
        }
        finally
        {
            if (!_state.StopDownloading)
            {
                _state.StopDownloading = true;
                Log.Information("M3u8 fetching ended, marking download queue to complete remaining items");
            }

            _state.Segments.Writer.TryComplete();
            _state.FetchCompleted.Set();
            Log.Information("M3u8 fetching stopped");
        }
    }
}