using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace showroom.Utils;

public class Configs
{
    public bool DebugLog { get; set; } = false;
    public bool FileLog { get; set; } = false;
    public double Interval { get; set; } = 20.0;
    public string Downloader { get; set; } = "none";
    public string[] Users { get; set; } = [];
}

public static class ConfigUtils
{
    static ConfigUtils()
    {
        var configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs.yml");
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
                var configStr = File.ReadAllText(configFilePath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(NullNamingConvention.Instance)
                    .Build();
                Config = deserializer.Deserialize<Configs>(configStr);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"配置文件解析错误: {ex.Message}");
            Environment.Exit(1); // 退出程序
        }
    }

    public static Configs Config { get; set; } = new();
}