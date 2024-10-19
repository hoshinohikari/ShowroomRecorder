using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace showroom.Download;

public class Minyami(string name, string url) : DownloadUtils(name, url)
{
    private const int SIGINT = 2;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task _downloadTask = null!;
    private Timer _outputTimer = null!;
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
                Arguments = $"-d {Url} -o {outputFilePath} --retries 2 --live",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _process = new Process { StartInfo = startInfo };
            _process.OutputDataReceived += (_, args) =>
            {
                Log.Verbose(args.Data!);
                ResetOutputTimer();
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                Log.Verbose(args.Data!);
                ResetOutputTimer();
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // 初始化计时器
            _outputTimer = new Timer(OutputTimerCallback, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

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

    private void ResetOutputTimer()
    {
        _outputTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private void OutputTimerCallback(object? state)
    {
        if (!_process.HasExited)
        {
            Log.Warning("Minyami 在10秒内没有任何输出，发送Ctrl+C信号终止进程。");
            SendCtrlC(_process);
        }
    }

    private void SendCtrlC(Process process)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            // Windows 平台发送 Ctrl+C 信号
            GenerateConsoleCtrlEvent(ConsoleCtrlEvent.CTRL_C, (uint)process.SessionId);
        else
            // Unix 平台发送 SIGINT 信号
            kill(process.Id, SIGINT);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(ConsoleCtrlEvent sigevent, uint dwProcessGroupId);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    public override void Stop()
    {
        if (_cancellationTokenSource.IsCancellationRequested) return;
        _cancellationTokenSource.Cancel();
        if (!_process.HasExited) _process.Kill();
    }

    private enum ConsoleCtrlEvent
    {
        CTRL_C = 0,
        CTRL_BREAK = 1
    }
}