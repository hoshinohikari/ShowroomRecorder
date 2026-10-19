using showroom.WebRtc;
using Xunit;

namespace showroom.Tests;

public class WebRtcDependencyPreflightTests
{
    [Fact]
    public void Accepts_native_libraries_when_all_abi_majors_match_the_build()
    {
        var nativeLibraryPath = Path.Combine(Path.GetTempPath(), "showroom-ffmpeg");
        var runtime = new RecordingNativeRuntime(FfmpegAbi.Expected);
        var preflight = new FfmpegWebRtcDependencyPreflight(runtime);

        preflight.Validate(nativeLibraryPath);

        Assert.Equal([nativeLibraryPath], runtime.Paths);
    }

    [Fact]
    public void Rejects_native_libraries_when_any_abi_major_does_not_match_and_reports_the_platform_native_path()
    {
        var nativeLibraryPath = Path.Combine(Path.GetTempPath(), "showroom-ffmpeg");
        var runtime = new RecordingNativeRuntime(new FfmpegAbi(60, 62, 58, "FFmpeg 6"));
        var preflight = new FfmpegWebRtcDependencyPreflight(runtime);

        var error = Assert.Throws<InvalidOperationException>(() => preflight.Validate(nativeLibraryPath));

        Assert.Contains("libavformat", error.Message);
        Assert.Contains("libavcodec", error.Message);
        Assert.Contains("libavutil", error.Message);
        Assert.Contains(Path.GetFullPath(nativeLibraryPath), error.Message);
        Assert.Contains("FFmpeg 6", error.Message);
        Assert.Equal([nativeLibraryPath], runtime.Paths);
    }

    [Fact]
    public void Reports_native_load_failures_with_the_selected_search_path()
    {
        var nativeLibraryPath = Path.Combine(Path.GetTempPath(), "missing-showroom-ffmpeg");
        var runtime = new RecordingNativeRuntime(null) { Failure = new DllNotFoundException("avformat missing") };
        var preflight = new FfmpegWebRtcDependencyPreflight(runtime);

        var error = Assert.Throws<InvalidOperationException>(() => preflight.Validate(nativeLibraryPath));

        Assert.Contains(Path.GetFullPath(nativeLibraryPath), error.Message);
        Assert.Contains("FFmpeg", error.Message);
        Assert.IsType<DllNotFoundException>(error.InnerException);
        Assert.Equal([nativeLibraryPath], runtime.Paths);
    }

    private sealed class RecordingNativeRuntime(FfmpegAbi? abi) : IFfmpegNativeRuntime
    {
        public List<string> Paths { get; } = [];
        public Exception? Failure { get; init; }

        public FfmpegAbi Load(string libraryPath)
        {
            Paths.Add(libraryPath);
            if (Failure is not null)
                throw Failure;
            return abi!;
        }
    }
}
