using showroom.Utils;
using Xunit;

namespace showroom.Tests;

public class ConfigManagerTests
{
    [Fact]
    public void Missing_protocol_defaults_to_hls_without_touching_webrtc_dependencies()
    {
        var preflight = new RecordingDependencyPreflight();
        var manager = new ConfigManager(preflight);

        var config = manager.Parse("Users: []");

        Assert.Equal("hls", config.Protocol);
        Assert.Empty(preflight.Paths);
    }

    [Fact]
    public void Explicit_hls_does_not_touch_webrtc_dependencies()
    {
        var preflight = new RecordingDependencyPreflight();
        var manager = new ConfigManager(preflight);

        var config = manager.Parse("Protocol: hls\nUsers: []");

        Assert.Equal("hls", config.Protocol);
        Assert.Empty(preflight.Paths);
    }

    [Fact]
    public void Webrtc_uses_the_configured_native_library_path_for_preflight()
    {
        var preflight = new RecordingDependencyPreflight();
        var manager = new ConfigManager(preflight);

        var config = manager.Parse("Protocol: webrtc\nWebRtc:\n  NativeLibraryPath: /opt/ffmpeg\nUsers: []");

        Assert.Equal("webrtc", config.Protocol);
        Assert.Equal(["/opt/ffmpeg"], preflight.Paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_native_library_path_uses_system_search_path(string nativeLibraryPath)
    {
        var preflight = new RecordingDependencyPreflight();
        var manager = new ConfigManager(preflight);

        manager.Parse($"Protocol: webrtc\nWebRtc:\n  NativeLibraryPath: '{nativeLibraryPath}'\nUsers: []");

        Assert.Equal([""], preflight.Paths);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("rtmp")]
    [InlineData("null")]
    public void Invalid_protocol_is_rejected_with_supported_values(string protocol)
    {
        var preflight = new RecordingDependencyPreflight();
        var manager = new ConfigManager(preflight);

        var error = Assert.Throws<ConfigurationException>(() => manager.Parse($"Protocol: {protocol}\nUsers: []"));

        Assert.Contains("hls", error.Message);
        Assert.Contains("webrtc", error.Message);
        Assert.Empty(preflight.Paths);
    }

    [Fact]
    public void Failed_webrtc_reload_keeps_the_previous_configuration()
    {
        var preflight = new RecordingDependencyPreflight();
        var manager = new ConfigManager(preflight);
        var original = manager.Load("Protocol: hls\nUsers: []");
        preflight.Fail = true;

        var succeeded = manager.TryReload(
            "Protocol: webrtc\nWebRtc:\n  NativeLibraryPath: /opt/ffmpeg\nUsers: []",
            out var error);

        Assert.False(succeeded);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Same(original, manager.Current);
        Assert.Equal(["/opt/ffmpeg"], preflight.Paths);
    }

    [Fact]
    public void Production_preflight_fails_closed_until_abi_validator_is_registered()
    {
        var manager = new ConfigManager(new MissingWebRtcDependencyPreflight());

        var error = Assert.Throws<InvalidOperationException>(() => manager.Load("Protocol: webrtc\nUsers: []"));

        Assert.Contains("no FFmpeg native dependency validator", error.Message);
        Assert.Contains("system library search paths", error.Message);
        Assert.Equal("hls", manager.Current.Protocol);
    }

    private sealed class RecordingDependencyPreflight : IWebRtcDependencyPreflight
    {
        public List<string> Paths { get; } = [];
        public bool Fail { get; set; }

        public void Validate(string nativeLibraryPath)
        {
            Paths.Add(nativeLibraryPath);
            if (Fail)
                throw new InvalidOperationException("Test FFmpeg dependency is unavailable.");
        }
    }
}
