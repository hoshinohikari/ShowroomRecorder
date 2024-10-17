using System.Diagnostics;
using Serilog;

namespace showroom.Download;

public class Minyami(string name, string url) : DownloadUtils(name, url)
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
                .Any(p => File.Exists(Path.Combine(p, "minyami")) || File.Exists(Path.Combine(p, "minyami.exe")));

            if (!minyamiExists)
            {
                Log.Error("Minyami 不存在，请安装Minyami后再试。");
                return;
            }

            // 设置输出文件路径
            var timestamp = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
            var outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
            if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            outputFilePath = Path.Combine(outputDirectory, $"{Name}_{timestamp}.ts");

            // 使用Minyami下载m3u8流并保存到本地
            var startInfo = new ProcessStartInfo
            {
                FileName = "minyami",
                Arguments = $"-d {Url} -o {outputFilePath} --live",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _process = new Process { StartInfo = startInfo };
            _process.OutputDataReceived += (sender, args) => Log.Verbose(args.Data!);
            _process.ErrorDataReceived += (sender, args) => Log.Verbose(args.Data!);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _downloadTask = Task.Run(() => _process.WaitForExit(), _cancellationTokenSource.Token);
            await _downloadTask;

            switch (_process.ExitCode)
            {
                case 0:
                    Log.Information($"下载完成: {outputFilePath}");
                    break;
                case 137:
                    Log.Warning($"下载已取消，存于{outputFilePath}");
                    break;
                default:
                    Log.Error($"下载失败，退出代码: {_process.ExitCode}");
                    break;
            }
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

    public override void Stop()
    {
        if (_cancellationTokenSource.IsCancellationRequested) return;
        _cancellationTokenSource.Cancel();
        if (!_process.HasExited) _process.Kill();
    }
}