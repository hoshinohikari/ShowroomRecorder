using showroom.Utils;
using SimpleM3U8Parser;

namespace showroom.Download;

public class ShowroomUtils(string name, string url) : DownloadUtils(name, url)
{
    public override async Task DownloadAsync()
    {
        var m3u8Content = await ShowroomDownloadHttp.Get(Url, new List<(string, string)>());

        var m3u8 = M3u8Parser.Parse(m3u8Content.Item2);

        
    }

    public override async Task Stop()
    {

    }
}

public struct ShowroomSegment
{
    public string Path { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Downloaded { get; set; }
}
