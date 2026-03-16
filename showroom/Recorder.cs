using System.Net;
using System.Text.Json;
using Serilog;
using showroom.Download;
using showroom.Utils;

namespace showroom;

public class Recorder(string name, long id)
{
    private DownloadUtils? _download;

    //private string _viewUrl = null!;
    private string _recordUrl = string.Empty;

    private async Task GetUrls()
    {
        Log.Verbose("{Name} requesting streaming_url for room {RoomId}", name, id);
        var res = await ShowroomHttp.Get("api/live/streaming_url",
        [
            ("room_id", $"{id}"),
            ("_",
                $"{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() * 1000 + DateTimeOffset.UtcNow.Millisecond}"),

            ("abr_available", "1")
        ]);

        if (res.Item1 != HttpStatusCode.OK || string.IsNullOrWhiteSpace(res.Item2))
        {
            Log.Warning("{Name} GetUrls failed: {Status}", name, res.Item1);
            _recordUrl = string.Empty;
            return;
        }

        Log.Verbose("{Name} GetUrls response length: {Length}", name, res.Item2.Length);

        if (res.Item2 == "{}")
        {
            Log.Warning("{Name} streaming_url response is empty object", name);
            _recordUrl = string.Empty;
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(res.Item2);
            var root = document.RootElement;

            if (!root.TryGetProperty("streaming_url_list", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                Log.Warning("{Name} streaming_url_list missing or invalid", name);
                _recordUrl = string.Empty;
                return;
            }

            foreach (var stream in streams.EnumerateArray().Where(stream =>
                         stream.GetProperty("type").GetString() == "hls" &&
                         stream.GetProperty("label").GetString() == "original quality"))
            {
                _recordUrl = stream.GetProperty("url").GetString()!;
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} failed to parse streaming_url response", name);
            _recordUrl = string.Empty;
        }
    }

    public async Task Start()
    {
        Log.Debug("{Name} recorder start", name);
        await GetUrls();

        if (_recordUrl == string.Empty)
        {
            Log.Warning("{Name} record URL not found, skip this round", name);
            await Task.Delay(ConfigUtils.Interval);
            return;
        }

        Log.Information("{Name} record URL resolved", name);
        //Log.Information($"{_name} view url is {_viewUrl}");

        switch (ConfigUtils.Config.Downloader)
        {
            case "minyami":
                _download = new Minyami(name, _recordUrl);
                Log.Debug("{Name} using downloader: minyami", name);
                break;
            case "streamlink":
                _download = new StreamlinkUtils(name, _recordUrl);
                Log.Debug("{Name} using downloader: streamlink", name);
                break;
            case "ffmpeg":
                _download = new FFmpegUtils(name, _recordUrl);
                Log.Debug("{Name} using downloader: ffmpeg", name);
                break;
            default:
                _download = new ShowroomUtils(name, _recordUrl);
                Log.Debug("{Name} using downloader: showroom", name);
                break;
        }

        Log.Verbose("{Name} downloader instance type={DownloaderType}", name, _download.GetType().Name);
        await _download.DownloadAsync();
        Log.Debug("{Name} recorder end", name);
    }

    public async Task Stop()
    {
        if (_download == null)
        {
            Log.Verbose("{Name} recorder stop skipped: downloader not started", name);
            return;
        }

        Log.Debug("{Name} recorder stop requested", name);
        try
        {
            await _download.Stop();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} recorder stop failed", name);
        }

        Log.Debug("{Name} recorder stopped", name);
    }
}