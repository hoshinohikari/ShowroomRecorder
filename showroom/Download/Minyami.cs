using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace showroom.Download;

public class Minyami(string name, string url) : DownloadUtils(name, url)
{
    private const int SIGINT = 2;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task _downloadTask = null!;
    private Timer? _outputTimer;
    private Process? _process;

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
            var startInfo = CreateStartInfo(Url, outputFilePath, outputDirectory);

            _process = new Process { StartInfo = startInfo };
            _process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    Log.Verbose(args.Data);
                ResetOutputTimer();
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    Log.Warning(args.Data);
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
            _outputTimer?.Dispose();
            _process?.Dispose();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string url, string outputFilePath, string outputDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "minyami",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputFilePath);
        startInfo.ArgumentList.Add("--temp-dir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("--retries");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("--live");

        return startInfo;
    }

    private void ResetOutputTimer()
    {
        _outputTimer?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private void OutputTimerCallback(object? state)
    {
        if (_process is { HasExited: false })
        {
            Log.Warning("Minyami 在10秒内没有任何输出，发送Ctrl+C信号终止进程。");
            if (!SendCtrlC(_process))
            {
                Log.Warning("发送Ctrl+C失败，尝试强制终止进程。");
                try
                {
                    _process.Kill();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "强制终止 Minyami 进程失败");
                }
            }
        }
    }

    private bool SendCtrlC(Process process)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            // Windows 平台发送 Ctrl+C 信号
            return SendCtrlCWindows(process.Id);
        else
            // Unix 平台发送 SIGINT 信号
            return kill(process.Id, SIGINT) == 0;
    }

    private static bool SendCtrlCWindows(int pid)
    {
        try
        {
            if (!AttachConsole((uint)pid))
                return false;

            // Disable Ctrl+C handling for current process while sending
            SetConsoleCtrlHandler(null, true);

            // Send Ctrl+C to all processes attached to the console
            var result = GenerateConsoleCtrlEvent(ConsoleCtrlEvent.CTRL_C, 0);

            // Allow some time for the signal to be delivered
            Thread.Sleep(200);

            FreeConsole();
            SetConsoleCtrlHandler(null, false);

            return result;
        }
        catch
        {
            try
            {
                FreeConsole();
            }
            catch
            {
                // ignore
            }

            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(ConsoleCtrlEvent sigevent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handlerRoutine, bool add);

    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

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

    private enum ConsoleCtrlEvent
    {
        CTRL_C = 0,
        CTRL_BREAK = 1
    }
}
