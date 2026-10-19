using Serilog;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace showroom.Utils;

internal interface IWebRtcDependencyPreflight
{
    void Validate(string nativeLibraryPath);
}

internal sealed class MissingWebRtcDependencyPreflight : IWebRtcDependencyPreflight
{
    public void Validate(string nativeLibraryPath)
    {
        var searchPath = string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? "the system library search paths"
            : $"'{nativeLibraryPath}'";

        throw new InvalidOperationException(
            $"Protocol is configured as 'webrtc', but this build has no FFmpeg native dependency validator. " +
            $"ABI validation cannot be performed for {searchPath}. Install the matching FFmpeg shared libraries " +
            "and use a build with WebRTC dependency support.");
    }
}

internal sealed class ConfigManager
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .Build();

    private readonly IWebRtcDependencyPreflight _webRtcDependencyPreflight;
    private Configs _current = new();

    public ConfigManager(IWebRtcDependencyPreflight webRtcDependencyPreflight)
    {
        _webRtcDependencyPreflight = webRtcDependencyPreflight;
    }

    public Configs Current => Volatile.Read(ref _current);

    public Configs Parse(string yaml)
    {
        Configs config;
        try
        {
            config = Deserializer.Deserialize<Configs>(yaml)
                     ?? throw new ConfigurationException("The configuration document is empty.");
        }
        catch (YamlException ex)
        {
            throw new ConfigurationException($"The configuration YAML is invalid: {ex.Message}", ex);
        }

        config.Protocol = NormalizeProtocol(config.Protocol);
        config.WebRtc ??= new WebRtcConfigs();
        config.Users ??= [];
        NormalizeUsers(config);

        if (config.Protocol == "webrtc")
        {
            if (string.IsNullOrWhiteSpace(config.WebRtc.NativeLibraryPath))
                config.WebRtc.NativeLibraryPath = "";

            _webRtcDependencyPreflight.Validate(config.WebRtc.NativeLibraryPath);
        }

        return config;
    }

    public Configs Load(string yaml)
    {
        var candidate = Parse(yaml);
        SetCurrent(candidate);
        return candidate;
    }

    public bool TryReload(string yaml, out Exception? error)
    {
        try
        {
            Load(yaml);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    public void SetCurrent(Configs config) => Volatile.Write(ref _current, config);

    private static string NormalizeProtocol(string? protocol)
    {
        var normalizedProtocol = protocol?.Trim().ToLowerInvariant();
        if (normalizedProtocol is "hls" or "webrtc")
            return normalizedProtocol;

        throw new ConfigurationException(
            $"Protocol must be 'hls' or 'webrtc'; received '{protocol ?? "<null>"}'.");
    }

    private static void NormalizeUsers(Configs config)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = new List<string>(config.Users.Length);
        foreach (var user in config.Users)
        {
            if (string.IsNullOrWhiteSpace(user))
                continue;

            if (seen.Add(user))
                deduplicated.Add(user);
            else
                Log.Warning("重复用户已忽略: {User}", user);
        }

        config.Users = deduplicated.ToArray();
    }
}

internal sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message)
    {
    }

    public ConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
