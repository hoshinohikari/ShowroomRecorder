using showroom.WebRtc;
using Xunit;

namespace showroom.Tests;

public class FfmpegLibraryPathComparerTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Uses_platform_specific_case_sensitivity(bool isWindows, bool expectedEqual)
    {
        var comparer = FfmpegLibraryPathComparer.ForPlatform(isWindows);

        Assert.Equal(expectedEqual, comparer.Equals("ffmpeg/Lib", "FFMPEG/lib"));
    }

    [Fact]
    public void Current_platform_comparer_matches_the_host_os_policy()
    {
        var pathsDifferByCase = FfmpegLibraryPathComparer.ForCurrentPlatform.Equals("ffmpeg/Lib", "FFMPEG/lib");

        Assert.Equal(OperatingSystem.IsWindows(), pathsDifferByCase);
    }
}
