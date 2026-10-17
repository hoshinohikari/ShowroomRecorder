using System.Net;
using showroom.Download;
using SimpleM3U8Parser.Media;
using Xunit;

namespace showroom.Tests;

public class StreamResetTests
{
    [Theory]
    [InlineData("room_main_ss", "old", "room_main_ss", "new")]
    [InlineData("old_main_ss", "same", "new_main_ss", "same")]
    public async Task PreservesBothStreams_WhenSequenceRestartsWithNewStreamId(
        string oldPath, string oldStream, string newPath, string newStream)
    {
        using var state = new PipelineState();
        using var cts = CreateCancellation(state);
        var factory = new MemorySinkFactory();
        var events = new List<MergeEvent>();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        merger.MergeEvent += events.Add;
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: url => Task.FromResult(new[] { Payload(url) }));

        foreach (var sequence in new[] { 2811, 2812 })
            await state.Segments.Writer.WriteAsync(Segment(sequence, oldStream, oldPath), cts.Token);
        foreach (var sequence in new[] { 1, 2, 3 })
            await state.Segments.Writer.WriteAsync(Segment(sequence, newStream, newPath), cts.Token);
        state.Segments.Writer.TryComplete();

        await Task.WhenAll(merger.RunAsync(cts.Token), downloader.RunAsync(cts.Token)).WaitAsync(cts.Token);

        Assert.Equal(2, factory.Sinks.Count);
        Assert.Equal(new byte[] { 11, 12 }, factory.Sinks[0].ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, factory.Sinks[1].ToArray());
        Assert.Single(events, e => e.Kind == MergeEventKind.Rotated);
        Assert.Contains(events, e => e.Kind == MergeEventKind.Completed);
        Assert.DoesNotContain(events, e => e.Kind == MergeEventKind.Aborted);
    }

    [Fact]
    public async Task DrainsOldInFlightDownload_BeforeDownloadingNewStream()
    {
        using var state = new PipelineState();
        using var cts = CreateCancellation(state);
        var factory = new MemorySinkFactory();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prematureNewDownload = false;
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: async url =>
            {
                if (url.Contains("-2812.ts"))
                {
                    oldStarted.TrySetResult();
                    await releaseOld.Task.WaitAsync(cts.Token);
                    oldFinished.TrySetResult();
                }
                else if (url.Contains("txspiseq=new") && !oldFinished.Task.IsCompleted)
                {
                    prematureNewDownload = true;
                }

                return new[] { Payload(url) };
            });
        foreach (var (sequence, stream) in new[] { (2811, "old"), (2812, "old"), (1, "new"), (2, "new") })
            await state.Segments.Writer.WriteAsync(Segment(sequence, stream), cts.Token);
        state.Segments.Writer.TryComplete();

        var run = Task.WhenAll(merger.RunAsync(cts.Token), downloader.RunAsync(cts.Token));
        try
        {
            await oldStarted.Task.WaitAsync(cts.Token);
            await Task.Delay(100, cts.Token);
        }
        finally
        {
            releaseOld.TrySetResult();
        }

        await run.WaitAsync(cts.Token);

        Assert.False(prematureNewDownload);
        Assert.Equal(2, factory.Sinks.Count);
        Assert.Equal(new byte[] { 11, 12 }, factory.Sinks[0].ToArray());
        Assert.Equal(new byte[] { 1, 2 }, factory.Sinks[1].ToArray());
    }

    [Fact]
    public async Task IgnoresRetiredStream_WhenOldPlaylistReappearsAfterReset()
    {
        using var state = new PipelineState();
        using var cts = CreateCancellation(state);
        var factory = new MemorySinkFactory();
        var downloaded = new System.Collections.Concurrent.ConcurrentBag<string>();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: url =>
            {
                downloaded.Add(url);
                return Task.FromResult(new[] { Payload(url) });
            });
        foreach (var (sequence, stream) in new[]
                 { (2811, "old"), (2812, "old"), (1, "new"), (2, "new"), (2813, "old"), (3, "new") })
            await state.Segments.Writer.WriteAsync(Segment(sequence, stream), cts.Token);
        state.Segments.Writer.TryComplete();

        await Task.WhenAll(merger.RunAsync(cts.Token), downloader.RunAsync(cts.Token)).WaitAsync(cts.Token);

        Assert.Equal(2, factory.Sinks.Count);
        Assert.Equal(new byte[] { 11, 12 }, factory.Sinks[0].ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, factory.Sinks[1].ToArray());
        Assert.DoesNotContain(downloaded, url => url.Contains("-2813.ts"));
    }

    [Fact]
    public async Task DoesNotRotate_WhenStreamIdChangesWithoutSequenceRollback()
    {
        using var state = new PipelineState();
        using var cts = CreateCancellation(state);
        var factory = new MemorySinkFactory();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: url => Task.FromResult(new[] { Payload(url) }));
        foreach (var (sequence, stream) in new[] { (1, "old"), (2, "new"), (3, "old"), (4, "new") })
            await state.Segments.Writer.WriteAsync(Segment(sequence, stream), cts.Token);
        state.Segments.Writer.TryComplete();

        await Task.WhenAll(merger.RunAsync(cts.Token), downloader.RunAsync(cts.Token)).WaitAsync(cts.Token);

        Assert.Single(factory.Sinks);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, factory.Sinks[0].ToArray());
    }

    [Fact]
    public async Task PreservesAllStreams_AcrossConsecutiveResets()
    {
        using var state = new PipelineState();
        using var cts = CreateCancellation(state);
        var factory = new MemorySinkFactory();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: url => Task.FromResult(new[] { Payload(url) }));
        foreach (var (sequence, stream) in new[]
                 { (2811, "old"), (2812, "old"), (1, "new"), (2, "new"), (1, "newer"), (2, "newer") })
            await state.Segments.Writer.WriteAsync(Segment(sequence, stream), cts.Token);
        state.Segments.Writer.TryComplete();

        await Task.WhenAll(merger.RunAsync(cts.Token), downloader.RunAsync(cts.Token)).WaitAsync(cts.Token);

        Assert.Equal(3, factory.Sinks.Count);
        Assert.Equal(new byte[] { 11, 12 }, factory.Sinks[0].ToArray());
        Assert.Equal(new byte[] { 1, 2 }, factory.Sinks[1].ToArray());
        Assert.Equal(new byte[] { 1, 2 }, factory.Sinks[2].ToArray());
    }

    [Fact]
    public async Task CancellationUnblocksDownloader_WhileWaitingForStreamReset()
    {
        using var state = new PipelineState();
        using var cts = CreateCancellation(state);
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: url => Task.FromResult(new[] { Payload(url) }));
        await state.Segments.Writer.WriteAsync(Segment(2812, "old"), cts.Token);
        await state.Segments.Writer.WriteAsync(Segment(1, "new"), cts.Token);
        state.Segments.Writer.TryComplete();

        // No Merger: cancellation must release the downloader's pending reset handshake.
        var run = downloader.RunAsync(cts.Token);
        while (!state.IsStreamResetRequested)
            await Task.Delay(10, cts.Token);
        await state.Cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(state.DownloadCompleted.IsSet);
        Assert.False(state.IsStreamResetRequested);
    }

    [Fact]
    public async Task ReplaysPlaylistReset_ThroughFetcherDownloaderAndMerger()
    {
        using var state = new PipelineState();
        using var cts = CreateCancellation(state);
        var factory = new MemorySinkFactory();
        var polls = 0;
        var fetcher = new SegmentFetcher("test", "https://example.com/playlist.m3u8", state,
            get: _ =>
            {
                var first = ++polls == 1;
                if (!first) state.StopFetching = true;
                var segments = first
                    ? new[] { Segment(2811, "old"), Segment(2812, "old") }
                    : new[] { Segment(1, "new"), Segment(2, "new"), Segment(3, "new") };
                var playlist = "#EXTM3U\n" + string.Join("", segments.Select(s => $"#EXTINF:1.0,\n{s.Path}\n"));
                return Task.FromResult((HttpStatusCode.OK, playlist));
            });
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: url => Task.FromResult(new[] { Payload(url) }));
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));

        await Task.WhenAll(fetcher.RunAsync(cts.Token), downloader.RunAsync(cts.Token), merger.RunAsync(cts.Token))
            .WaitAsync(cts.Token);

        Assert.Equal(2, factory.Sinks.Count);
        Assert.Equal(new byte[] { 11, 12 }, factory.Sinks[0].ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, factory.Sinks[1].ToArray());
        Assert.False(state.MergeFailed);
    }

    private static CancellationTokenSource CreateCancellation(PipelineState state)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(state.Cts.Token, TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        return cts;
    }

    private static M3u8Media Segment(int sequence, string stream, string path = "room_main_ss") => new()
    {
        Path = $"https://example.com/live/{path}-{sequence}.ts?txspiseq={stream}"
    };

    private static byte Payload(string url)
    {
        var sequence = int.Parse(new Uri(url).AbsolutePath.Split('-')[^1].Split('.')[0]);
        return (byte)(sequence >= 2800 ? sequence - 2800 : sequence);
    }
}
