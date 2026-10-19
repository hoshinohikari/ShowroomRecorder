using showroom.WebRtc;
using Xunit;

namespace showroom.Tests;

public class WebRtcPacketProcessingTests
{
    [Fact]
    public void Reorders_packets_across_the_rtp_sequence_wrap()
    {
        var ready = new List<WebRtcMediaPacket>();
        var buffer = new RtpReorderBuffer((_, _, _) => { });

        buffer.Add(Packet(ushort.MaxValue, [3]), 0, ready);
        buffer.Add(Packet(1, [5]), 10, ready);
        buffer.Add(Packet(0, [4]), 20, ready);

        Assert.Equal([ushort.MaxValue, (ushort)0, (ushort)1], ready.Select(packet => packet.SequenceNumber));
        Assert.Empty(buffer.Flush(20));
    }

    [Fact]
    public void Requests_missing_packets_then_releases_later_packets_after_the_bounded_wait()
    {
        var requests = new List<(uint Ssrc, ushort First, ushort Mask)>();
        var buffer = new RtpReorderBuffer((ssrc, first, mask) => requests.Add((ssrc, first, mask)));

        buffer.Add(Packet(10, [1]), 0, []);
        buffer.Add(Packet(12, [3]), 20, []);
        var ready = buffer.Flush(169);
        var released = buffer.Flush(171);

        Assert.Equal((42u, (ushort)11, (ushort)0), Assert.Single(requests));
        Assert.Empty(ready);
        Assert.Equal([12], released.Select(packet => packet.SequenceNumber));
    }

    [Fact]
    public void Retry_delay_uses_capped_exponential_backoff_with_bounded_jitter()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.9), WebRtcRetryPolicy.GetDelay(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(2.2), WebRtcRetryPolicy.GetDelay(2, 1));
        Assert.Equal(TimeSpan.FromSeconds(30), WebRtcRetryPolicy.GetDelay(20, 1));
    }

    [Fact]
    public void Reassembles_an_h264_access_unit_as_length_prefixed_nals_without_changing_payloads()
    {
        var assembler = new H264AccessUnitAssembler();
        Assert.Null(assembler.Push(100, [0x67, 0x42, 0x00, 0x1e], marker: false));
        var frame = assembler.Push(100, [0x65, 0x88, 0x99], marker: true);

        Assert.NotNull(frame);
        Assert.True(frame.Value.IsKeyFrame);
        Assert.Equal(
            [0, 0, 0, 4, 0x67, 0x42, 0x00, 0x1e, 0, 0, 0, 3, 0x65, 0x88, 0x99],
            frame.Value.AvcPayload);
        Assert.Equal([0x67, 0x42, 0x00, 0x1e], assembler.Sps);
    }

    [Fact]
    public void Drops_the_entire_damaged_access_unit_and_accepts_a_later_complete_keyframe()
    {
        var assembler = new H264AccessUnitAssembler();
        Assert.Null(assembler.Push(100, [0x61, 0x01], marker: false));
        Assert.Null(assembler.Push(100, [0x65, 0x02], marker: true, lostBefore: true));

        var recovered = assembler.Push(200, [0x65, 0x03], marker: true);

        Assert.NotNull(recovered);
        Assert.True(recovered.Value.IsKeyFrame);
        Assert.Equal([0, 0, 0, 2, 0x65, 0x03], recovered.Value.AvcPayload);
    }

    private static WebRtcMediaPacket Packet(ushort sequenceNumber, byte[] payload) =>
        new(true, 90_000, sequenceNumber, 42, true, payload);
}
