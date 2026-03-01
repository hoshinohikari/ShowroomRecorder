using showroom.Download;
using Xunit;

namespace showroom.Tests;

public class ProcessStartInfoTests
{
    [Fact]
    public void Minyami_StartInfo_UsesArgumentList_AndPreservesSpacedPaths()
    {
        var url = "https://example.com/live path/index.m3u8?token=abc";
        var outputFilePath = @"C:\recordings with space\output file.ts";
        var outputDirectory = @"C:\recordings with space";

        var startInfo = Minyami.CreateStartInfo(url, outputFilePath, outputDirectory);
        var args = startInfo.ArgumentList.ToArray();

        Assert.Equal("minyami", startInfo.FileName);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(
        [
            "-d", url,
            "-o", outputFilePath,
            "--temp-dir", outputDirectory,
            "--timeout", "5",
            "--retries", "5",
            "--live"
        ], args);
    }

    [Fact]
    public void Streamlink_StartInfo_UsesArgumentList_AndPreservesSpacedPaths()
    {
        var url = "https://example.com/live path/index.m3u8?token=abc";
        var outputFilePath = @"C:\recordings with space\output file.ts";

        var startInfo = StreamlinkUtils.CreateStartInfo(url, outputFilePath, false, null);
        var args = startInfo.ArgumentList.ToArray();

        Assert.Equal("streamlink", startInfo.FileName);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(
        [
            "-4",
            "-o", outputFilePath,
            "--retry-streams", "2",
            "--stream-segment-threads", "5",
            "--stream-segment-timeout", "1",
            "--retry-open", "6",
            "--stream-timeout", "5",
            url, "best"
        ], args);
        Assert.DoesNotContain("--logfile", args);
    }

    [Fact]
    public void Streamlink_StartInfo_IncludesLogfileArgs_WhenFileLogEnabled()
    {
        var url = "https://example.com/live/index.m3u8";
        var outputFilePath = @"C:\recordings\output.ts";
        var logFilePath = @"C:\logs with space\streamlink.log";

        var startInfo = StreamlinkUtils.CreateStartInfo(url, outputFilePath, true, logFilePath);
        var args = startInfo.ArgumentList.ToArray();

        var logLevelIndex = Array.IndexOf(args, "--loglevel");
        var logFileIndex = Array.IndexOf(args, "--logfile");

        Assert.True(logLevelIndex >= 0);
        Assert.True(logFileIndex >= 0);
        Assert.Equal("all", args[logLevelIndex + 1]);
        Assert.Equal(logFilePath, args[logFileIndex + 1]);
        Assert.Equal(url, args[^2]);
        Assert.Equal("best", args[^1]);
    }
}