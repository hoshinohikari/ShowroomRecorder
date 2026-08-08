using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace showroom.Utils;

public class Configs
{
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
    public string[] Users { get; set; } = [];
    public string SrId { get; set; } = "";
}

public static class ConfigUtils
{
    private const string ConfigFileName = "configs.yml";
    private static readonly CancellationTokenSource _cts = new();
    private static readonly object _configLock = new();
    private static DateTime _lastWriteUtc;

    static ConfigUtils()
    {
        Load();
        _lastWriteUtc = GetLastWriteUtc();
        _ = ReloadLoopAsync();
    }

    public static Configs Config { get; private set; } = new();

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
                Config = defaultConfig;
                Log.Information("默认配置文件已创建");
            }
            else
            {
                Config = ParseConfig(configFilePath);
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

    private static Configs ParseConfig(string configFilePath)
    {
        var configStr = File.ReadAllText(configFilePath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();
        var cfg = deserializer.Deserialize<Configs>(configStr);

        // 去重 Users，忽略空值
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<string>(cfg.Users.Length);
        foreach (var user in cfg.Users)
        {
            if (string.IsNullOrWhiteSpace(user))
                continue;
            if (seen.Add(user))
                deduped.Add(user);
            else
                Log.Warning("重复用户已忽略: {User}", user);
        }
        cfg.Users = deduped.ToArray();
        return cfg;
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
            var newConfig = ParseConfig(ConfigFilePath);
            lock (_configLock)
            {
                Config = newConfig;
                _lastWriteUtc = lastWrite;
            }
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