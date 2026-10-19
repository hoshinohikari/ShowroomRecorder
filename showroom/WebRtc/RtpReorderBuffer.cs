using System.Buffers.Binary;

namespace showroom.WebRtc;

internal sealed record WebRtcMediaPacket(
    bool IsVideo,
    uint Timestamp,
    ushort SequenceNumber,
    uint Ssrc,
    bool Marker,
    byte[] Payload,
    bool IsSenderReport = false,
    ulong NtpTimestamp = 0);

/// <summary>Bounded RTP reorder window. Sequence comparisons are modulo 16 bits.</summary>
internal sealed class RtpReorderBuffer(Action<uint, ushort, ushort> nack)
{
    private const int MaxPackets = 1024;
    private const long ReorderWaitMs = 150;
    private readonly Dictionary<ushort, (WebRtcMediaPacket Packet, long ReceivedAt)> _packets = [];
    private ushort? _next;
    private long _lastNackMs = -1000;

    public long LatePackets { get; private set; }

    public void Add(WebRtcMediaPacket packet, long nowMs, ICollection<WebRtcMediaPacket> ready)
    {
        _next ??= packet.SequenceNumber;
        var delta = (short)(packet.SequenceNumber - _next.Value);
        if (delta < 0 || _packets.ContainsKey(packet.SequenceNumber))
        {
            LatePackets++;
            return;
        }

        if (_packets.Count >= MaxPackets)
            throw new IOException("RTP reorder window exceeded its 1024 packet bound.");

        _packets.Add(packet.SequenceNumber, (packet, nowMs));
        if (delta > 0 && nowMs - _lastNackMs >= 50)
        {
            var missing = Math.Min((int)delta, 17);
            var bitmask = (ushort)((1 << (missing - 1)) - 1);
            nack(packet.Ssrc, _next.Value, bitmask);
            _lastNackMs = nowMs;
        }

        FlushInto(nowMs, ready);
    }

    public IReadOnlyList<WebRtcMediaPacket> Flush(long nowMs)
    {
        var ready = new List<WebRtcMediaPacket>();
        FlushInto(nowMs, ready);
        return ready;
    }

    private void FlushInto(long nowMs, ICollection<WebRtcMediaPacket> ready)
    {
        while (_next.HasValue && _packets.Count > 0)
        {
            if (_packets.Remove(_next.Value, out var current))
            {
                ready.Add(current.Packet);
                _next = unchecked((ushort)(_next.Value + 1));
                continue;
            }

            var nearest = _packets.Values.MinBy(item => (ushort)(item.Packet.SequenceNumber - _next.Value));
            if (nowMs - nearest.ReceivedAt < ReorderWaitMs)
                break;

            _next = nearest.Packet.SequenceNumber;
        }
    }
}

internal readonly record struct H264AccessUnit(byte[] AvcPayload, bool IsKeyFrame, uint Timestamp);

/// <summary>RFC 6184 mode-1 packet depacketizer. Emits AVCC length-prefixed NAL units.</summary>
internal sealed class H264AccessUnitAssembler
{
    private readonly List<byte[]> _nals = [];
    private MemoryStream? _fragment;
    private uint? _timestamp;
    private bool _damaged;

    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }

    public void Lose()
    {
        _damaged = true;
        _fragment?.Dispose();
        _fragment = null;
    }

    public H264AccessUnit? Push(uint timestamp, byte[] payload, bool marker, bool lostBefore = false)
    {
        if (_timestamp != timestamp)
        {
            _nals.Clear();
            _fragment?.Dispose();
            _fragment = null;
            _damaged = false;
            _timestamp = timestamp;
        }

        if (lostBefore)
            Lose();

        if (payload.Length == 0 || (payload[0] & 0x80) != 0)
            Lose();
        else
        {
            switch (payload[0] & 0x1f)
            {
                case >= 1 and <= 23:
                    _nals.Add(payload);
                    break;
                case 24:
                    AddStapA(payload);
                    break;
                case 28:
                    AddFuA(payload);
                    break;
                default:
                    Lose();
                    break;
            }
        }

        if (_nals.Count > 256 || _nals.Sum(nal => (long)nal.Length) > 16 * 1024 * 1024)
            Lose();

        if (!marker)
            return null;

        if (_fragment is not null)
            Lose();

        if (_damaged || _nals.Count == 0)
        {
            _nals.Clear();
            return null;
        }

        foreach (var nal in _nals)
        {
            if ((nal[0] & 0x1f) == 7) Sps = nal;
            if ((nal[0] & 0x1f) == 8) Pps = nal;
        }

        var isKeyFrame = _nals.Any(nal => (nal[0] & 0x1f) == 5);
        using var data = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var nal in _nals)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, nal.Length);
            data.Write(length);
            data.Write(nal);
        }

        _nals.Clear();
        return new H264AccessUnit(data.ToArray(), isKeyFrame, timestamp);
    }

    private void AddStapA(byte[] payload)
    {
        for (var offset = 1; offset < payload.Length;)
        {
            if (offset + 2 > payload.Length)
            {
                Lose();
                return;
            }

            var length = (payload[offset] << 8) | payload[offset + 1];
            offset += 2;
            if (length == 0 || offset + length > payload.Length || (payload[offset] & 0x80) != 0 || (payload[offset] & 0x1f) is 0 or >= 24)
            {
                Lose();
                return;
            }

            _nals.Add(payload.AsSpan(offset, length).ToArray());
            offset += length;
        }
    }

    private void AddFuA(byte[] payload)
    {
        if (payload.Length < 3 || (payload[1] & 0xc0) == 0xc0 || (payload[1] & 0x1f) is 0 or >= 24)
        {
            Lose();
            return;
        }

        if ((payload[1] & 0x80) != 0)
        {
            if (_fragment is not null)
                Lose();
            _fragment = new MemoryStream();
            _fragment.WriteByte((byte)((payload[0] & 0xe0) | (payload[1] & 0x1f)));
        }

        if (_fragment is null)
        {
            Lose();
            return;
        }

        _fragment.Write(payload, 2, payload.Length - 2);
        if (_fragment.Length > 8 * 1024 * 1024)
        {
            Lose();
            return;
        }

        if ((payload[1] & 0x40) != 0)
        {
            _nals.Add(_fragment.ToArray());
            _fragment.Dispose();
            _fragment = null;
        }
    }

    public static (int Width, int Height) GetDimensions(byte[] sps)
    {
        var rbsp = new List<byte>();
        var zeros = 0;
        foreach (var value in sps.Skip(1))
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }

            rbsp.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }

        var reader = new SpsBits(rbsp.ToArray());
        var profile = reader.Read(8);
        reader.Read(16);
        reader.ReadUnsignedExpGolomb();
        var chroma = 1;
        if (new[] { 100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135 }.Contains(profile))
        {
            chroma = reader.ReadUnsignedExpGolomb();
            if (chroma == 3 && reader.Read(1) != 0) chroma = 0;
            reader.ReadUnsignedExpGolomb();
            reader.ReadUnsignedExpGolomb();
            reader.Read(1);
            if (reader.Read(1) != 0)
            {
                for (var i = 0; i < (chroma == 3 ? 12 : 8); i++)
                {
                    if (reader.Read(1) == 0) continue;
                    var last = 8;
                    var next = 8;
                    for (var j = 0; j < (i < 6 ? 16 : 64); j++)
                    {
                        if (next != 0) next = (last + reader.ReadSignedExpGolomb() + 256) % 256;
                        if (next != 0) last = next;
                    }
                }
            }
        }

        reader.ReadUnsignedExpGolomb();
        var poc = reader.ReadUnsignedExpGolomb();
        if (poc == 0) reader.ReadUnsignedExpGolomb();
        else if (poc == 1)
        {
            reader.Read(1);
            reader.ReadSignedExpGolomb();
            reader.ReadSignedExpGolomb();
            var count = reader.ReadUnsignedExpGolomb();
            for (var i = 0; i < count; i++) reader.ReadSignedExpGolomb();
        }

        reader.ReadUnsignedExpGolomb();
        reader.Read(1);
        var width = (reader.ReadUnsignedExpGolomb() + 1) * 16;
        var height = (reader.ReadUnsignedExpGolomb() + 1) * 16;
        var frameOnly = reader.Read(1);
        height *= 2 - frameOnly;
        if (frameOnly == 0) reader.Read(1);
        reader.Read(1);
        if (reader.Read(1) != 0)
        {
            var left = reader.ReadUnsignedExpGolomb();
            var right = reader.ReadUnsignedExpGolomb();
            var top = reader.ReadUnsignedExpGolomb();
            var bottom = reader.ReadUnsignedExpGolomb();
            var cropX = chroma is 1 or 2 ? 2 : 1;
            var cropY = (chroma == 1 ? 2 : 1) * (2 - frameOnly);
            width -= (left + right) * cropX;
            height -= (top + bottom) * cropY;
        }

        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new InvalidDataException("Invalid H.264 SPS dimensions.");
        return (width, height);
    }

    private sealed class SpsBits(byte[] bytes)
    {
        private int _bit;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                if (_bit >= bytes.Length * 8) throw new InvalidDataException("Truncated H.264 SPS.");
                value = (value << 1) | ((bytes[_bit / 8] >> (7 - _bit++ % 8)) & 1);
            }
            return value;
        }

        public int ReadUnsignedExpGolomb()
        {
            var zeros = 0;
            while (Read(1) == 0)
                if (++zeros > 24) throw new InvalidDataException("Invalid H.264 SPS integer.");
            return (1 << zeros) - 1 + Read(zeros);
        }

        public int ReadSignedExpGolomb()
        {
            var value = ReadUnsignedExpGolomb();
            return (value & 1) != 0 ? (value + 1) / 2 : -value / 2;
        }
    }
}
