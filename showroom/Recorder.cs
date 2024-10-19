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
        var res = await ShowroomHttp.Get("api/live/streaming_url",
        [
            ("room_id", $"{id}"),
            ("_",
                $"{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() * 1000 + DateTimeOffset.UtcNow.Millisecond}"),

            ("abr_available", "1")
        ]);

        Log.Debug($"GetUrls \"{res.Item2}\"");

        if (res.Item2 == "{}")
        {
            _recordUrl = string.Empty;
            return;
        }

        using var document = JsonDocument.Parse(res.Item2);
        var root = document.RootElement;

        var streams = root.GetProperty("streaming_url_list");

        foreach (var stream in streams.EnumerateArray().Where(stream =>
                     stream.GetProperty("type").GetString() == "hls" &&
                     stream.GetProperty("label").GetString() == "original quality"))
        {
            _recordUrl = stream.GetProperty("url").GetString()!;
            break;
        }
    }

    public async Task Start()
    {
        await GetUrls();

        if (_recordUrl == string.Empty)
        {
            await Task.Delay(TimeSpan.FromSeconds(ConfigUtils.Config.Interval));
            return;
        }

        Log.Information($"{name} Record url is {_recordUrl}");
        //Log.Information($"{_name} view url is {_viewUrl}");

        if (ConfigUtils.Config.Downloader == "ffmpeg")
            _download = new FFmpegUtils(name, _recordUrl);
        else
            _download = new Minyami(name, _recordUrl);
        await _download.DownloadAsync();
    }

    public void Stop()
    {
        _download?.Stop();
    }
}