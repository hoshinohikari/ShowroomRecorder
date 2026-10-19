using System.Net;
using System.Text.Json;
using Serilog;
using showroom.Download;
using showroom.Utils;
using showroom.WebRtc;

namespace showroom;

internal interface IRecorderBackendFactory
{
    Task<DownloadUtils?> CreateHlsAsync(string name, long roomId, Configs configSnapshot);
    DownloadUtils CreateWebRtc(string name, long roomId, Configs configSnapshot);
}

internal sealed class RecorderBackendFactory : IRecorderBackendFactory
{
    public async Task<DownloadUtils?> CreateHlsAsync(string name, long roomId, Configs configSnapshot)
    {
        var recordUrl = await GetOriginalQualityHlsUrlAsync(name, roomId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(recordUrl))
            return null;

        return configSnapshot.Downloader switch
        {
            "minyami" => new Minyami(name, recordUrl),
            "streamlink" => new StreamlinkUtils(name, recordUrl),
            "ffmpeg" => new FFmpegUtils(name, recordUrl),
            _ => new ShowroomUtils(name, recordUrl)
        };
    }

    public DownloadUtils CreateWebRtc(string name, long roomId, Configs configSnapshot) =>
        new WebRtcDownloader(name, roomId, configSnapshot);

    private static async Task<string?> GetOriginalQualityHlsUrlAsync(string name, long roomId)
    {
        Log.Verbose("{Name} requesting streaming_url for room {RoomId}", name, roomId);
        var res = await ShowroomHttp.Get("api/live/streaming_url",
        [
            ("room_id", $"{roomId}"),
            ("_",
                $"{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() * 1000 + DateTimeOffset.UtcNow.Millisecond}"),
            ("abr_available", "1")
        ]).ConfigureAwait(false);

        if (res.Item1 != HttpStatusCode.OK || string.IsNullOrWhiteSpace(res.Item2))
        {
            Log.Warning("{Name} GetUrls failed: {Status}", name, res.Item1);
            return null;
        }

        Log.Verbose("{Name} GetUrls response length: {Length}", name, res.Item2.Length);
        if (res.Item2 == "{}")
        {
            Log.Warning("{Name} streaming_url response is empty object", name);
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(res.Item2);
            var root = document.RootElement;
            if (!root.TryGetProperty("streaming_url_list", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                Log.Warning("{Name} streaming_url_list missing or invalid", name);
                return null;
            }

            foreach (var stream in streams.EnumerateArray().Where(stream =>
                         stream.GetProperty("type").GetString() == "hls" &&
                         stream.GetProperty("label").GetString() == "original quality"))
                return stream.GetProperty("url").GetString();

            Log.Warning("{Name} original-quality HLS stream URL was not found", name);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} failed to parse streaming_url response", name);
            return null;
        }
    }
}
