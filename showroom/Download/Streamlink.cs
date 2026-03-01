using System.Diagnostics;
using Serilog;
using showroom.Utils;

namespace showroom.Download;

public class StreamlinkUtils(string name, string url) : DownloadUtils(name, url)
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task _downloadTask = null!;
    private Process? _process;

    public override async Task DownloadAsync()
    {
        string outputFilePath = null!;
        try
        {
            // 检查系统环境变量中是否存在Minyami
            var streamlinkExists = Environment.GetEnvironmentVariable("PATH")!
                .Split(Path.PathSeparator)
                .Any(p => File.Exists(Path.Combine(p, "streamlink")) || File.Exists(Path.Combine(p, "streamlink.exe")));

            if (!streamlinkExists)
            {
                Log.Error("streamlink 不存在，请安装streamlink后再试。");
                return;
            }

            // 设置输出文件路径
            var timestamp = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss_fff");
            var outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
            if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            outputFilePath = Path.Combine(outputDirectory, $"{Name}_{timestamp}.ts");

            string? logFilePath = null;
            if (ConfigUtils.Config.FileLog)
            {
                var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
                logFilePath = Path.Combine(logDirectory, $"{Name}_{timestamp}.log");
            }

            // 使用Minyami下载m3u8流并保存到本地
            var startInfo = CreateStartInfo(Url, outputFilePath, ConfigUtils.Config.FileLog, logFilePath);

            _process = new Process { StartInfo = startInfo };
            _process.OutputDataReceived += (_, dataReceivedEventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(dataReceivedEventArgs.Data))
                    Log.Verbose(dataReceivedEventArgs.Data);
            };
            _process.ErrorDataReceived += (_, dataReceivedEventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(dataReceivedEventArgs.Data))
                    Log.Warning(dataReceivedEventArgs.Data);
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
        finally
        {
            _process?.Dispose();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string url, string outputFilePath, bool fileLog, string? logFilePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "streamlink",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-4");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputFilePath);
        startInfo.ArgumentList.Add("--retry-streams");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("--stream-segment-threads");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("--stream-segment-timeout");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--retry-open");
        startInfo.ArgumentList.Add("6");
        startInfo.ArgumentList.Add("--stream-timeout");
        startInfo.ArgumentList.Add("5");

        if (fileLog)
        {
            if (string.IsNullOrWhiteSpace(logFilePath))
                throw new ArgumentException("logFilePath is required when fileLog is enabled", nameof(logFilePath));

            startInfo.ArgumentList.Add("--loglevel");
            startInfo.ArgumentList.Add("all");
            startInfo.ArgumentList.Add("--logfile");
            startInfo.ArgumentList.Add(logFilePath);
        }

        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add("best");

        return startInfo;
    }

    public override async Task Stop()
    {
        if (_cancellationTokenSource.IsCancellationRequested) return;
        await _cancellationTokenSource.CancelAsync();

        if (_process == null || _process.HasExited) return;
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
