using Xunit;
using showroom.Utils;

namespace showroom.Tests;

public class UploadHelpersTests
{
    [Fact]
    public void EstimateServerHashTimeoutMs_NonPositive_ReturnsQuickTimeout()
    {
        Assert.Equal(UploadConstants.ServerHashQuickTimeoutMs,
            UploadHelpers.EstimateServerHashTimeoutMs(0));
        Assert.Equal(UploadConstants.ServerHashQuickTimeoutMs,
            UploadHelpers.EstimateServerHashTimeoutMs(-1));
    }

    [Fact]
    public void EstimateServerHashTimeoutMs_SmallFile_UsesBaseTimeout()
    {
        // 1MB 文件：算 hash 时间可忽略，应接近基础开销 30s
        var timeout = UploadHelpers.EstimateServerHashTimeoutMs(1 * 1024 * 1024);
        Assert.True(timeout <= UploadConstants.ServerHashBaseTimeoutMs + 1000);
        Assert.True(timeout >= UploadConstants.ServerHashBaseTimeoutMs);
    }

    [Fact]
    public void EstimateServerHashTimeoutMs_10GB_ExceedsQuickTimeoutAndScales()
    {
        var tenGb = 10L * 1024 * 1024 * 1024;
        var timeout = UploadHelpers.EstimateServerHashTimeoutMs(tenGb);

        // 10GB 在 128MB/s 下限下约需 80s，加基础 30s ≈ 110s，必然远超原来的 30s 快速超时
        Assert.True(timeout > UploadConstants.ServerHashQuickTimeoutMs,
            $"10GB timeout {timeout} should exceed quick timeout {UploadConstants.ServerHashQuickTimeoutMs}");

        // 更大文件应得到更长超时（线性增长，未封顶前）
        var twentyGb = 20L * 1024 * 1024 * 1024;
        var timeout20 = UploadHelpers.EstimateServerHashTimeoutMs(twentyGb);
        Assert.True(timeout20 > timeout);
    }

    [Fact]
    public void EstimateServerHashTimeoutMs_CappedAtMaxTimeout()
    {
        // 超大文件（如 1TB）不应超过上限，避免 worker 被长期占用
        var oneTb = 1024L * 1024 * 1024 * 1024;
        var timeout = UploadHelpers.EstimateServerHashTimeoutMs(oneTb);
        Assert.True(timeout <= UploadConstants.ServerHashMaxTimeoutMs);
    }
}