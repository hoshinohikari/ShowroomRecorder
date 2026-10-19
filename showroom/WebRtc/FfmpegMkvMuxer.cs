extern alias FFmpegAutoGen;
using System.Runtime.InteropServices;
using FFmpegAutoGen::FFmpeg.AutoGen;
using FFmpegNative = FFmpegAutoGen::FFmpeg.AutoGen.ffmpeg;

namespace showroom.WebRtc;

internal interface IWebRtcMuxer : IDisposable
{
    void Write(bool isVideo, byte[] payload, long pts, bool keyFrame);
}

internal interface IWebRtcMuxerFactory
{
    IWebRtcMuxer Create(string path, byte[] sps, byte[] pps, int channels, int width, int height);
}

internal sealed class FfmpegMkvMuxerFactory : IWebRtcMuxerFactory
{
    public IWebRtcMuxer Create(string path, byte[] sps, byte[] pps, int channels, int width, int height) =>
        new FfmpegMkvMuxer(path, sps, pps, channels, width, height);
}

/// <summary>Muxes original H.264 and Opus payloads into Matroska; no decoding or encoding.</summary>
internal sealed unsafe class FfmpegMkvMuxer : IWebRtcMuxer
{
    private AVFormatContext* _context;
    private AVStream* _video;
    private AVStream* _audio;
    private bool _headerWritten;
    private bool _disposed;

    public FfmpegMkvMuxer(string path, byte[] sps, byte[] pps, int channels, int width, int height)
    {
        AVFormatContext* context = null;
        Check(FFmpegNative.avformat_alloc_output_context2(&context, null, "matroska", path));
        if (context == null) throw new IOException("FFmpeg could not allocate a Matroska output context.");
        _context = context;
        try
        {
            _video = FFmpegNative.avformat_new_stream(context, null);
            _audio = FFmpegNative.avformat_new_stream(context, null);
            if (_video == null || _audio == null) throw new OutOfMemoryException("FFmpeg could not allocate media streams.");

            _video->time_base = new AVRational { num = 1, den = 90_000 };
            _video->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _video->codecpar->codec_id = AVCodecID.AV_CODEC_ID_H264;
            _video->codecpar->width = width;
            _video->codecpar->height = height;
            AddExtradata(_video->codecpar, MakeAvcConfiguration(sps, pps));

            _audio->time_base = new AVRational { num = 1, den = 48_000 };
            _audio->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
            _audio->codecpar->codec_id = AVCodecID.AV_CODEC_ID_OPUS;
            _audio->codecpar->sample_rate = 48_000;
            FFmpegNative.av_channel_layout_default(&_audio->codecpar->ch_layout, channels);
            var opusHead = new byte[19];
            "OpusHead"u8.CopyTo(opusHead);
            opusHead[8] = 1;
            opusHead[9] = (byte)channels;
            BitConverter.GetBytes(48_000).CopyTo(opusHead, 12);
            AddExtradata(_audio->codecpar, opusHead);

            Check(FFmpegNative.avio_open(&context->pb, path, FFmpegNative.AVIO_FLAG_WRITE));
            Check(FFmpegNative.avformat_write_header(context, null));
            _headerWritten = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Write(bool isVideo, byte[] payload, long pts, bool keyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = isVideo ? _video : _audio;
        AVPacket* packet = FFmpegNative.av_packet_alloc();
        if (packet == null) throw new OutOfMemoryException("FFmpeg could not allocate a packet.");
        try
        {
            Check(FFmpegNative.av_new_packet(packet, payload.Length));
            Marshal.Copy(payload, 0, (IntPtr)packet->data, payload.Length);
            packet->stream_index = stream->index;
            packet->pts = packet->dts = pts;
            if (keyFrame) packet->flags |= FFmpegNative.AV_PKT_FLAG_KEY;
            FFmpegNative.av_packet_rescale_ts(packet,
                new AVRational { num = 1, den = isVideo ? 90_000 : 48_000 }, stream->time_base);
            Check(FFmpegNative.av_interleaved_write_frame(_context, packet));
        }
        finally
        {
            FFmpegNative.av_packet_free(&packet);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context == null) return;

        Exception? failure = null;
        try
        {
            if (_headerWritten)
                Check(FFmpegNative.av_write_trailer(_context));
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (_context->pb != null)
            {
                try { Check(FFmpegNative.avio_closep(&_context->pb)); }
                catch (Exception ex) { failure ??= ex; }
            }
            FFmpegNative.avformat_free_context(_context);
            _context = null;
            _video = null;
            _audio = null;
        }

        if (failure is not null) throw failure;
    }

    private static byte[] MakeAvcConfiguration(byte[] sps, byte[] pps)
    {
        if (sps.Length < 4 || pps.Length == 0 || sps.Length > ushort.MaxValue || pps.Length > ushort.MaxValue)
            throw new InvalidDataException("H.264 SPS/PPS cannot be represented in AVC configuration data.");

        var bytes = new List<byte> { 1, sps[1], sps[2], sps[3], 0xff, 0xe1,
            (byte)(sps.Length >> 8), (byte)sps.Length };
        bytes.AddRange(sps);
        bytes.Add(1);
        bytes.Add((byte)(pps.Length >> 8));
        bytes.Add((byte)pps.Length);
        bytes.AddRange(pps);
        return bytes.ToArray();
    }

    private static void AddExtradata(AVCodecParameters* parameters, byte[] data)
    {
        parameters->extradata = (byte*)FFmpegNative.av_mallocz((ulong)(data.Length + FFmpegNative.AV_INPUT_BUFFER_PADDING_SIZE));
        if (parameters->extradata == null) throw new OutOfMemoryException("FFmpeg could not allocate codec extradata.");
        parameters->extradata_size = data.Length;
        Marshal.Copy(data, 0, (IntPtr)parameters->extradata, data.Length);
    }

    private static void Check(int result)
    {
        if (result >= 0) return;
        byte* message = stackalloc byte[1024];
        FFmpegNative.av_strerror(result, message, 1024);
        throw new IOException(Marshal.PtrToStringUTF8((IntPtr)message) ?? $"FFmpeg error {result}.");
    }
}
