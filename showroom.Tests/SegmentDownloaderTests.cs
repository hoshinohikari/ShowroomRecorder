using System.Net;
using showroom.Download;
using SimpleM3U8Parser.Media;
using Xunit;

namespace showroom.Tests;

public class SegmentDownloaderTests
{
    [Fact]
    public async Task Downloads_Segments_IntoCache_AndSignalsCompletion()
    {
        using var state = new PipelineState();
        var payload = new byte[] { 0xAA, 0xBB };
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: _ => Task.FromResult(payload));

        await state.Segments.Writer.WriteAsync(new M3u8Media { Path = "https://example.com/seg-1.ts" });
        await state.Segments.Writer.WriteAsync(new M3u8Media { Path = "https://example.com/seg-2.ts" });
        state.Segments.Writer.TryComplete();

        await downloader.RunAsync(state.Cts.Token);

        Assert.Equal(2, state.DownloadedSegmentsCache.Count);
        Assert.Contains(1L, state.DownloadedSegmentsCache.Keys);
        Assert.Contains(2L, state.DownloadedSegmentsCache.Keys);
        Assert.Contains("https://example.com/seg-1.ts", state.DownloadedSegments.Keys);
        Assert.True(state.DownloadCompleted.IsSet);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_WhenDownloadFails()
    {
        using var state = new PipelineState();
        var downloader = new SegmentDownloader("test", "https://example.com/playlist.m3u8", state,
            downloadFile: _ => Task.FromException<byte[]>(new HttpRequestException("connection reset")));

        await state.Segments.Writer.WriteAsync(new M3u8Media { Path = "https://example.com/seg-1.ts" });
        state.Segments.Writer.TryComplete();

        await downloader.RunAsync(state.Cts.Token);

        // segment 1 never lands in cache after retries exhausted
        Assert.Empty(state.DownloadedSegmentsCache);
        Assert.DoesNotContain("https://example.com/seg-1.ts", state.DownloadedSegments.Keys);
        Assert.True(state.DownloadCompleted.IsSet);
    }
}