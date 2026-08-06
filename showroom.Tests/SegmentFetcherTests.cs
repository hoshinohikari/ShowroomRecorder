using System.Net;
using showroom.Download;
using Xunit;

namespace showroom.Tests;

public class SegmentFetcherTests
{
    private const string Playlist =
        "#EXTM3U\n#EXTINF:2.0,\nhttps://example.com/seg-1.ts\n#EXTINF:2.0,\nhttps://example.com/seg-2.ts\n";

    [Fact]
    public async Task Parses_Playlist_AndWritesSegments_ToChannel()
    {
        using var state = new PipelineState();
        var fetcher = new SegmentFetcher("test", "https://example.com/playlist.m3u8", state,
            get: _ => Task.FromResult((HttpStatusCode.OK, Playlist)));

        var run = fetcher.RunAsync(state.Cts.Token);

        var seg1 = await state.Segments.Reader.ReadAsync();
        var seg2 = await state.Segments.Reader.ReadAsync();

        state.Cts.Cancel();
        await run;

        Assert.Equal("https://example.com/seg-1.ts", seg1.Path);
        Assert.Equal("https://example.com/seg-2.ts", seg2.Path);
        Assert.Equal(2, state.QueuedSegments.Count);
        Assert.True(state.FetchCompleted.IsSet);
    }

    [Fact]
    public async Task DoesNotReEnqueue_AlreadyQueuedSegments_OnSubsequentPoll()
    {
        using var state = new PipelineState();
        var fetcher = new SegmentFetcher("test", "https://example.com/playlist.m3u8", state,
            get: _ => Task.FromResult((HttpStatusCode.OK, Playlist)));

        var run = fetcher.RunAsync(state.Cts.Token);

        // consume first poll's two segments
        _ = await state.Segments.Reader.ReadAsync();
        _ = await state.Segments.Reader.ReadAsync();

        // wait past the dynamic poll interval (~1s) so a second fetch runs
        await Task.Delay(1200);

        // second poll finds both segments already queued → writes nothing
        using var timeoutCts = new CancellationTokenSource(200);
        var hasMore = true;
        try
        {
            hasMore = await state.Segments.Reader.WaitToReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            hasMore = false; // nothing arrived within the timeout
        }

        state.Cts.Cancel();
        await run;

        Assert.False(hasMore);
        Assert.Equal(2, state.QueuedSegments.Count);
    }
}