using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using showroom;
using showroom.Utils;

// Bootstrap logger to capture early initialization logs
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(theme: AnsiConsoleTheme.Sixteen, restrictedToMinimumLevel: LogEventLevel.Information)
    .CreateLogger();

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Debug();

// 添加控制台日志输出
loggerConfig = ConfigUtils.Config.DebugLog
    ? loggerConfig.WriteTo.Async(a =>
        a.Console(theme: AnsiConsoleTheme.Sixteen, restrictedToMinimumLevel: LogEventLevel.Debug))
    : loggerConfig.WriteTo.Async(a =>
        a.Console(theme: AnsiConsoleTheme.Sixteen, restrictedToMinimumLevel: LogEventLevel.Information));

// 如果配置文件中设置了FileLog为true，则添加文件日志输出，并设置文件日志级别为Debug
if (ConfigUtils.Config.FileLog)
    loggerConfig = loggerConfig.WriteTo.Async(a =>
        a.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "log.log"),
            rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: LogEventLevel.Debug));

Log.Logger = loggerConfig.CreateLogger();

/*Log.Verbose("hello");
Log.Debug("hello");
Log.Information("hello");
Log.Warning("hello");
Log.Error("hello");
Log.Fatal("hello");*/

// 初始化在线检测（单例会在首次访问时启动）
var online = Online.Instance;
// 初始化监听器列表
var listeners = ConfigUtils.Config.Users.Select(n => new Listener(n)).ToList();

// 捕获 Ctrl+C 事件
Console.CancelKeyPress += async (_, e) =>
{
    Log.Warning("检测到 Ctrl+C，程序即将终止...");
    e.Cancel = true; // 取消默认的终止行为

    // 执行关闭时的代码
    await Cleanup();

    // 退出程序
    Environment.Exit(0);
};

// 无限等待，直到触发 Ctrl+C
await Task.Delay(Timeout.InfiniteTimeSpan);
return;

async Task Cleanup()
{
    try
    {
        List<Task> tasks = [];
        tasks.AddRange(listeners.Select(l => l.Stop()));
        tasks.Add(online.Stop());

        await Task.WhenAll(tasks);

        //await NowConversionTask;
    }
    catch (OperationCanceledException e)
    {
        Log.Error(e, e.Message);
    }
    finally
    {
        Log.Debug("cancel");
        Log.CloseAndFlush();
    }
}