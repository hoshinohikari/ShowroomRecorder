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

var runtimeMinLevel = ConfigUtils.Config.TraceLog ? LogEventLevel.Verbose : LogEventLevel.Debug;
var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Is(runtimeMinLevel);

// 添加控制台日志输出
loggerConfig = ConfigUtils.Config.DebugLog
    ? loggerConfig.WriteTo.Async(a =>
        a.Console(theme: AnsiConsoleTheme.Sixteen, restrictedToMinimumLevel: runtimeMinLevel))
    : loggerConfig.WriteTo.Async(a =>
        a.Console(theme: AnsiConsoleTheme.Sixteen, restrictedToMinimumLevel: LogEventLevel.Information));

// 如果配置文件中设置了FileLog为true，则添加文件日志输出，并设置文件日志级别为Debug
if (ConfigUtils.Config.FileLog)
    loggerConfig = loggerConfig.WriteTo.Async(a =>
        a.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "log.log"),
            rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: runtimeMinLevel));

Log.Logger = loggerConfig.CreateLogger();

AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception ex)
        Log.Fatal(ex, "Unhandled exception, IsTerminating={IsTerminating}", args.IsTerminating);
    else
        Log.Fatal("Unhandled non-exception object, IsTerminating={IsTerminating}", args.IsTerminating);
};

TaskScheduler.UnobservedTaskException += (_, args) =>
{
    Log.Error(args.Exception, "Unobserved task exception");
    args.SetObserved();
};

Log.Information("ShowroomRecorder starting...");
Log.Information("Logger minimum level={MinimumLevel}, debugLog={DebugLog}, traceLog={TraceLog}, fileLog={FileLog}",
    runtimeMinLevel, ConfigUtils.Config.DebugLog, ConfigUtils.Config.TraceLog, ConfigUtils.Config.FileLog);
Log.Debug(
    "Config summary: Users={UserCount}, Interval={IntervalSeconds}s, Downloader={Downloader}, ProxyConfigured={ProxyConfigured}, WebDavConfigured={WebDavConfigured}, SrIdConfigured={SrIdConfigured}",
    ConfigUtils.Config.Users.Length,
    ConfigUtils.Config.Interval,
    ConfigUtils.Config.Downloader,
    !string.IsNullOrWhiteSpace(ConfigUtils.Config.Proxy),
    !string.IsNullOrWhiteSpace(ConfigUtils.Config.WebDavUrl),
    !string.IsNullOrWhiteSpace(ConfigUtils.Config.SrId));

if (ConfigUtils.Config.Users.Length == 0)
    Log.Warning("No users configured, application will idle until shutdown");

await WebDavUploader.InitializeAsync();
await WebDavUploader.StartBackgroundUploaderAsync();

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
Log.Information("Initialized {ListenerCount} listeners", listeners.Count);

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
        Log.Information("Cleanup started...");
        List<Task> tasks = [];
        tasks.AddRange(listeners.Select(l => l.Stop()));
        tasks.Add(online.Stop());
        tasks.Add(WebDavUploader.StopBackgroundUploaderAsync());

        await Task.WhenAll(tasks);
        Log.Information("Cleanup completed");

        //await NowConversionTask;
    }
    catch (OperationCanceledException e)
    {
        Log.Warning(e, "Cleanup canceled");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Cleanup failed");
    }
    finally
    {
        Log.Information("Application shutdown");
        Log.CloseAndFlush();
    }
}