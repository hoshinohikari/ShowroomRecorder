using Serilog;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace showroom.Download;

public class FFmpegUtils(string name, string url) : DownloadUtils(name, url)
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private IMediaInfo _info = null!;
    private Task<IConversionResult> _nowConversionTask = null!;
    private IConversion conversion = null!;

    public override async Task DownloadAsync()
    {
        string outputFilePath = null!;
        try
        {
            // 检查系统环境变量中是否存在FFmpeg
            var ffmpegExists = Environment.GetEnvironmentVariable("PATH")!
                .Split(Path.PathSeparator)
                .Any(p => File.Exists(Path.Combine(p, "ffmpeg")) || File.Exists(Path.Combine(p, "ffmpeg.exe")));

            if (!ffmpegExists)
            {
                Log.Information("FFmpeg 不存在，正在下载...");
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
                Log.Information("FFmpeg 下载完成");
            }

            // 设置输出文件路径
            var timestamp = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
            var outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
            if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            outputFilePath = Path.Combine(outputDirectory, $"{Name}_{timestamp}.ts");

            // 使用FFmpeg下载m3u8流并保存到本地
            _info = await FFmpeg.GetMediaInfo(Url);
            //var stream = _info.Streams.FirstOrDefault();
            conversion = FFmpeg.Conversions.New();
            conversion.AddParameter("-rw_timeout 10000000");
            conversion.SetOverwriteOutput(true);
            foreach (var stream in _info.Streams) conversion.AddStream(stream);
            conversion.AddParameter("-c copy").SetOutput(outputFilePath);

            /*conversion.OnDataReceived += (sender, args) =>
            {
                Log.Information(args.Data!);
                if (args.Data!.Contains("m3u8 stream end")) // 假设这是流结束的标志
                {
                    _cancellationTokenSource.Cancel();
                }
            };*/

            _nowConversionTask = conversion.Start(_cancellationTokenSource.Token);
            await _nowConversionTask;

            Log.Information($"下载完成: {outputFilePath}");
        }
        catch (OperationCanceledException)
        {
            Log.Warning($"下载已取消，存于{outputFilePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"下载失败: {ex}");
        }
    }

    public override async Task Stop()
    {
        await _cancellationTokenSource.CancelAsync();
    }
}