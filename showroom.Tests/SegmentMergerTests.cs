using showroom.Download;
using Xunit;

namespace showroom.Tests;

public class SegmentMergerTests
{
    [Fact]
    public async Task Merges_ConsecutiveSegments_InOrder_AndCompletes()
    {
        using var state = new PipelineState();
        var factory = new MemorySinkFactory();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        var events = new List<MergeEvent>();
        merger.MergeEvent += events.Add;

        state.DownloadedSegmentsCache[1] = new byte[] { 1 };
        state.DownloadedSegmentsCache[2] = new byte[] { 2 };
        state.DownloadedSegmentsCache[3] = new byte[] { 3 };
        state.DownloadCompleted.Set();

        await merger.RunAsync(state.Cts.Token);

        Assert.Single(factory.Sinks);
        Assert.Equal(new byte[] { 1, 2, 3 }, factory.Sinks[0].ToArray());
        Assert.Contains(events, e => e.Kind == MergeEventKind.Completed);
        Assert.DoesNotContain(events, e => e.Kind == MergeEventKind.Rotated);
    }

    [Fact]
    public async Task Rolls_Over_AfterThreeRetries_OnContinuityGap()
    {
        using var state = new PipelineState();
        var factory = new MemorySinkFactory();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        var events = new List<MergeEvent>();
        merger.MergeEvent += events.Add;

        // 1 processed, then a gap to 5
        state.DownloadedSegmentsCache[1] = new byte[] { 1 };
        state.DownloadedSegmentsCache[5] = new byte[] { 5 };
        state.DownloadCompleted.Set();

        await merger.RunAsync(state.Cts.Token);

        Assert.Equal(2, factory.Sinks.Count);
        Assert.Equal(new byte[] { 1 }, factory.Sinks[0].ToArray());
        Assert.Equal(new byte[] { 5 }, factory.Sinks[1].ToArray());
        Assert.Contains(events, e => e.Kind == MergeEventKind.Rotated);
        Assert.Contains(events, e => e.Kind == MergeEventKind.Completed);
    }

    [Fact]
    public async Task Discards_StaleDelayedSegment_BelowLastProcessed()
    {
        using var state = new PipelineState();
        var factory = new MemorySinkFactory();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));

        state.DownloadedSegmentsCache[1] = new byte[] { 1 };
        state.DownloadedSegmentsCache[2] = new byte[] { 2 };
        state.DownloadedSegmentsCache[3] = new byte[] { 3 };

        var run = merger.RunAsync(state.Cts.Token); // DownloadCompleted not set → keeps looping
        await Task.Delay(100); // merger processes 1,2,3

        state.DownloadedSegmentsCache[1] = new byte[] { 9 }; // stale: seq 1 < last 3
        state.DownloadCompleted.Set();
        await run;

        Assert.Equal(new byte[] { 1, 2, 3 }, factory.Sinks[0].ToArray()); // stale 9 not appended
    }

    [Fact]
    public async Task Aborts_WithDiskFullFlag_WhenAppendThrowsNoSpace()
    {
        using var state = new PipelineState();
        var factory = new MemorySinkFactory { ThrowOnAppend = true };
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(10));
        var events = new List<MergeEvent>();
        merger.MergeEvent += events.Add;

        state.DownloadedSegmentsCache[1] = new byte[] { 1 };
        state.DownloadCompleted.Set();

        await merger.RunAsync(state.Cts.Token);

        Assert.Contains(events, e => e.Kind == MergeEventKind.Aborted && e.IsDiskFull);
        Assert.True(state.UploadInterrupted);
    }

    [Fact]
    public async Task ResetsRetry_WhenWaitingSequenceArrives_NoRollover()
    {
        using var state = new PipelineState();
        var factory = new MemorySinkFactory();
        var merger = new SegmentMerger("test", factory, state, TimeSpan.FromMilliseconds(300));
        var events = new List<MergeEvent>();
        merger.MergeEvent += events.Add;

        state.DownloadedSegmentsCache[1] = new byte[] { 1 };
        state.DownloadedSegmentsCache[3] = new byte[] { 3 };

        var run = merger.RunAsync(state.Cts.Token); // gap detected, retry=1
        await Task.Delay(50);

        state.DownloadedSegmentsCache[2] = new byte[] { 2 }; // waiting sequence arrives
        state.DownloadCompleted.Set();
        await run;

        Assert.Single(factory.Sinks); // no rollover because retry counter was reset
        Assert.Equal(new byte[] { 1, 2, 3 }, factory.Sinks[0].ToArray());
        Assert.Contains(events, e => e.Kind == MergeEventKind.Completed);
        Assert.DoesNotContain(events, e => e.Kind == MergeEventKind.Rotated);
    }
}