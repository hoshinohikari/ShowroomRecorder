using Xunit;

namespace showroom.Tests;

public class RecorderTests
{
    [Fact]
    public async Task Recorder_Stop_WhenDownloaderIsNull_DoesNotThrow()
    {
        var recorder = new Recorder("test_user", 12345);

        var exception = await Record.ExceptionAsync(recorder.Stop);

        Assert.Null(exception);
    }
}