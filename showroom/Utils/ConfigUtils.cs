using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace showroom.Utils;

public class Configs
{
    public string Protocol { get; set; } = "hls";
    public bool DebugLog { get; set; } = false;
    public bool TraceLog { get; set; } = false;
    public bool FileLog { get; set; } = false;
    public double Interval { get; set; } = 20.0;
    public string Proxy { get; set; } = "";
    public string WebDavUrl { get; set; } = "";
    public string WebDavUsername { get; set; } = "";
    public string WebDavPassword { get; set; } = "";
    public bool WebDavAllowInsecureCertificate { get; set; } = false;
    public string Downloader { get; set; } = "none";
    public WebRtcConfigs WebRtc { get; set; } = new();
    public string[] Users { get; set; } = [];
    public string SrId { get; set; } = "";
}

public class WebRtcConfigs
{
    // Optional native shared-library directory. Empty uses the system library search path.
    public string NativeLibraryPath { get; set; } = "";
}

public static class ConfigUtils
{
    private const string ConfigFileName = "configs.yml";
    private static readonly ConfigManager _configManager = new(new showroom.WebRtc.FfmpegWebRtcDependencyPreflight());
    private static readonly CancellationTokenSource _cts = new();
    private static readonly object _configLock = new();
    private static DateTime _lastWriteUtc;

    static ConfigUtils()
    {
        Load();
        _lastWriteUtc = GetLastWriteUtc();
        _ = ReloadLoopAsync();
    }

    public static Configs Config => _configManager.Current;

    public static TimeSpan Interval => TimeSpan.FromSeconds(Config.Interval);

    private static string ConfigFilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

    private static DateTime GetLastWriteUtc() =>
        File.Exists(ConfigFilePath) ? File.GetLastWriteTimeUtc(ConfigFilePath) : DateTime.UtcNow;

    private static void Load()
    {
        var configFilePath = ConfigFilePath;
        try
        {
            if (!File.Exists(configFilePath))
            {
                // 创建默认配置文件
                var defaultConfig = new Configs();
                var serializer = new SerializerBuilder()
                    .WithNamingConvention(NullNamingConvention.Instance)
                    .Build();
                var yaml = serializer.Serialize(defaultConfig);
                File.WriteAllText(configFilePath, yaml);
                _configManager.SetCurrent(defaultConfig);
                Log.Information("默认配置文件已创建");
            }
            else
            {
                _configManager.Load(File.ReadAllText(configFilePath));
                Log.Debug(
                    "配置文件加载成功: path={ConfigPath}, users={UserCount}, debug={DebugLog}, trace={TraceLog}, fileLog={FileLog}",
                    configFilePath, Config.Users.Length, Config.DebugLog, Config.TraceLog, Config.FileLog);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"配置文件解析错误: {ex.Message}");
            Environment.Exit(1); // 退出程序
        }
    }

    private static async Task ReloadLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, Config.Interval));
                await Task.Delay(delay, _cts.Token);
                ReloadIfChanged();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "配置重载循环异常");
            }
        }
    }

    private static void ReloadIfChanged()
    {
        if (!File.Exists(ConfigFilePath))
            return;

        var lastWrite = File.GetLastWriteTimeUtc(ConfigFilePath);
        if (lastWrite == _lastWriteUtc)
            return;

        try
        {
            var configYaml = File.ReadAllText(ConfigFilePath);
            Exception? reloadError;
            bool reloaded;
            lock (_configLock)
            {
                reloaded = _configManager.TryReload(configYaml, out reloadError);
                if (reloaded)
                    _lastWriteUtc = lastWrite;
            }

            if (!reloaded)
                throw reloadError!;

            var newConfig = _configManager.Current;
            Log.Information("配置已热重载: users={UserCount}, interval={Interval}s",
                newConfig.Users.Length, newConfig.Interval);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "配置热重载失败，保留旧配置: {Message}", ex.Message);
        }
    }

    public static void Stop()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
