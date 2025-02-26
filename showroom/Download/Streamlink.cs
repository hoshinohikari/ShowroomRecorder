using Serilog;
using System.Diagnostics;

namespace showroom.Download;

public class StreamlinkUtils(string name, string url) : DownloadUtils(name, url)
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task _downloadTask = null!;
    private Process _process = null!;

    public override async Task DownloadAsync()
    {
        string outputFilePath = null!;
        try
        {
            // 检查系统环境变量中是否存在Minyami
            var minyamiExists = Environment.GetEnvironmentVariable("PATH")!
                .Split(Path.PathSeparator)
                .Any(p => File.Exists(Path.Combine(p, "streamlink")) || File.Exists(Path.Combine(p, "streamlink.exe")));

            if (!minyamiExists)
            {
                Log.Error("streamlink 不存在，请安装streamlink后再试。");
                return;
            }

            // 设置输出文件路径
            var timestamp = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss_fff");
            var outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
            if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            outputFilePath = Path.Combine(outputDirectory, $"{Name}_{timestamp}.ts");

            // 使用Minyami下载m3u8流并保存到本地
            var startInfo = new ProcessStartInfo
            {
                FileName = "streamlink",
                Arguments = $"-4 -o {outputFilePath} --retry-streams 1 --retry-max 3 --stream-segment-threads 5 --stream-segment-timeout 5 --stream-timeout 5 {Url} best",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _process = new Process { StartInfo = startInfo };
            _process.OutputDataReceived += (_, args) =>
            {
                Log.Verbose(args.Data!);
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                Log.Warning(args.Data!);
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _downloadTask = Task.Run(() => _process.WaitForExit(), _cancellationTokenSource.Token);
            await _downloadTask;

            switch (_process.ExitCode)
            {
                case 0:
                    Log.Information($"{Name} 下载完成: {outputFilePath}");
                    break;
                case 137:
                    Log.Warning($"{Name} 下载已取消，存于{outputFilePath}");
                    break;
                default:
                    Log.Error($"{Name} 下载失败，退出代码: {_process.ExitCode}");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning($"{Name} 下载已取消，存于{outputFilePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"{Name} 下载失败: {ex}");
        }
    }

    public override async Task Stop()
    {
        if (_cancellationTokenSource.IsCancellationRequested) return;
        await _cancellationTokenSource.CancelAsync();

        if (_process.HasExited) return;
        var delayTask = Task.Delay(TimeSpan.FromSeconds(60));
        var processExitTask = Task.Run(() => _process.WaitForExit());

        var completedTask = await Task.WhenAny(delayTask, processExitTask);

        Log.Information($"{Name} 进程停止");

        if (completedTask == delayTask)
        {
            Log.Warning($"{Name} 进程在60秒内未结束，强制终止进程。");
            _process.Kill();
        }
        else
        {
            Log.Information($"{Name} 进程在60秒内正常结束。");
        }
    }
}