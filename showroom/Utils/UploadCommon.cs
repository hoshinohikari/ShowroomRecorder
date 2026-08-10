using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Serilog;

namespace showroom.Utils;

/// <summary>
///     WebDAV 上传的内部常量。全部为硬编码内部值，不暴露给用户配置。
/// </summary>
internal static class UploadConstants
{
    /// <summary>大文件阈值，超过此大小的文件走大文件上传策略（1GB）。</summary>
    public const long LargeFileThresholdBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>大文件分块上传每块大小（256MB）。</summary>
    public const long ChunkSizeBytes = 256L * 1024 * 1024;

    /// <summary>抽检时每个采样点读取的字节数（1MB）。</summary>
    public const int SampleCheckByteCount = 1 * 1024 * 1024;

    /// <summary>抽检采样点数量。</summary>
    public const int SampleCheckPointCount = 5;

    /// <summary>上传失败最大重试次数（不含首次）。</summary>
    public const int MaxUploadRetries = 3;

    /// <summary>进度文件后缀。</summary>
    public const string ProgressSuffix = ".upload-progress";

    /// <summary>失败文件子目录名。</summary>
    public const string FailedSubdirectory = "failed";

    /// <summary>服务端 hash 请求短超时（快速路径探测用）。</summary>
    public const double ServerHashQuickTimeoutMs = 30000;

    /// <summary>服务端 hash 最大重试次数（后台校验用）。</summary>
    public const int ServerHashMaxRetries = 12; // 12 * 5min = 1h

    /// <summary>单 chunk 上传无进度僵死超时。</summary>
    public static readonly TimeSpan StaleChunkTimeout = TimeSpan.FromMinutes(30);

    /// <summary>重试指数退避基数。</summary>
    public static readonly TimeSpan RetryBackoffBase = TimeSpan.FromSeconds(10);

    /// <summary>后台校验队列轮询间隔。</summary>
    public static readonly TimeSpan VerificationPollInterval = TimeSpan.FromMinutes(5);
}

/// <summary>
///     WebDAV 上传相关的公共工具方法，供各上传策略共享。
/// </summary>
internal static class UploadHelpers
{
    public static string BuildTargetUrl(string baseUrl, string fileName)
    {
        return $"{baseUrl}{Uri.EscapeDataString(fileName)}";
    }

    public static async Task<string> ComputeSha256HexAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var bytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static async Task TryApplyRemoteTimestampsAsync(string targetUrl, FileInfo localFileInfo)
    {
        try
        {
            var status = await WebDavHttp.ProppatchTimestamps(
                targetUrl,
                localFileInfo.CreationTimeUtc,
                localFileInfo.LastWriteTimeUtc,
                30000);

            if ((int)status is >= 200 and < 300 || status == HttpStatusCode.MultiStatus)
                Log.Debug(
                    "WebDAV timestamps synced: {FileName}, createdUtc={CreatedUtc:o}, modifiedUtc={ModifiedUtc:o}",
                    localFileInfo.Name, localFileInfo.CreationTimeUtc, localFileInfo.LastWriteTimeUtc);
            else
                Log.Warning("WebDAV timestamp sync unsupported or failed: {FileName}, status={StatusCode}",
                    localFileInfo.Name, status);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebDAV timestamp sync failed: {FileName}", localFileInfo.Name);
        }
    }

    /// <summary>
    ///     请求服务端 hash（?hash 扩展）。返回 null 表示服务端不支持或请求失败。
    /// </summary>
    public static async Task<string?> TryGetRemoteSha256Async(string targetUrl, double timeoutMs)
    {
        try
        {
            var hashUrl = targetUrl.Contains('?')
                ? $"{targetUrl}&hash"
                : $"{targetUrl}?hash";

            var (statusCode, content) = await WebDavHttp.Get(hashUrl, [], timeoutMs);
            if (statusCode != HttpStatusCode.OK || string.IsNullOrWhiteSpace(content))
            {
                Log.Verbose("WebDAV hash endpoint unavailable: status={StatusCode}", statusCode);
                return null;
            }

            var raw = NormalizeHashPayload(content);
            Log.Debug("WebDAV hash payload received: length={Length}, preview={Preview}",
                raw.Length, raw.Length > 120 ? raw[..120] : raw);
            if (TryExtractSha256(raw, out var hash))
            {
                Log.Verbose("WebDAV hash endpoint returned valid sha256");
                return hash;
            }

            Log.Warning("WebDAV hash endpoint returned non-sha256 payload: length={Length}", raw.Length);
            return null;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "WebDAV hash endpoint request failed");
            return null;
        }
    }

    private static string NormalizeHashPayload(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var cleaned = raw
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Replace("\t", string.Empty)
            .Trim();
        return cleaned;
    }

    private static bool TryExtractSha256(string raw, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var match = Regex.Match(raw, "(?i)\\b[a-f0-9]{64}\\b");
        if (!match.Success)
            return false;

        hash = match.Value;
        return true;
    }

    /// <summary>
    ///     流式比对两个流的内容是否完全一致。
    /// </summary>
    public static async Task<bool> StreamsEqualAsync(Stream localStream, Stream remoteStream)
    {
        var localBuffer = new byte[64 * 1024];
        var remoteBuffer = new byte[64 * 1024];
        var comparedBytes = 0L;

        while (true)
        {
            var localRead = await localStream.ReadAsync(localBuffer.AsMemory(0, localBuffer.Length));
            var remoteRead = await remoteStream.ReadAsync(remoteBuffer.AsMemory(0, remoteBuffer.Length));

            if (localRead != remoteRead)
                return false;

            if (localRead == 0)
            {
                Log.Verbose("WebDAV stream compare finished, bytes={ComparedBytes}", comparedBytes);
                return true;
            }

            if (!localBuffer.AsSpan(0, localRead).SequenceEqual(remoteBuffer.AsSpan(0, remoteRead)))
                return false;

            comparedBytes += localRead;
        }
    }

    /// <summary>
    ///     抽检式校验：在文件的若干位置各读取一小块与远端比对。
    ///     对于视频文件等大文件，抽检在极低流量成本下提供很高的可信度。
    /// </summary>
    public static async Task<bool> SampleVerifyAsync(string targetUrl, FileInfo localFileInfo,
        int sampleCount, int sampleByteCount)
    {
        if (localFileInfo.Length <= 0)
            return false;

        // 生成采样偏移量：开头一块、结尾一块、中间均匀分布 sampleCount-2 块
        var offsets = GenerateSampleOffsets(localFileInfo.Length, sampleCount, sampleByteCount);

        await using var localStream = File.OpenRead(localFileInfo.FullName);

        foreach (var offset in offsets)
        {
            // 读本地
            localStream.Seek(offset, SeekOrigin.Begin);
            var localBuffer = new byte[sampleByteCount];
            var localRead = 0;
            while (localRead < sampleByteCount)
            {
                var n = await localStream.ReadAsync(localBuffer.AsMemory(localRead, sampleByteCount - localRead));
                if (n == 0) break;
                localRead += n;
            }

            if (localRead == 0) continue;
            var localSlice = localBuffer.AsMemory(0, localRead);

            // 读远端（HTTP Range），用流式读取而非 DownloadFile。
            // DownloadFile 会把整个响应读进内存：若服务端忽略 Range 头返回整个文件，
            // 大文件会直接 OOM（ReadAsBytes -> MemoryStream.EnsureCapacity）。流式只读所需字节即可。
            var remoteBytes = await ReadRemoteRangeAsync(targetUrl, offset, localRead);
            if (remoteBytes.Length != localRead)
            {
                Log.Warning(
                    "WebDAV sample verify failed: range length mismatch at offset={Offset}, local={LocalBytes}, remote={RemoteBytes}",
                    offset, localRead, remoteBytes.Length);
                return false;
            }

            if (!localSlice.Span.SequenceEqual(remoteBytes))
            {
                Log.Warning("WebDAV sample verify failed: content mismatch at offset={Offset}", offset);
                return false;
            }

            Log.Verbose("WebDAV sample verify passed: offset={Offset}, bytes={ByteCount}", offset, localRead);
        }

        Log.Debug("WebDAV sample verify passed: {FileName}, samples={SampleCount}, bytesPerSample={SampleBytes}",
            localFileInfo.Name, sampleCount, sampleByteCount);
        return true;
    }

    /// <summary>
    ///     流式读取远端指定范围 [offset, offset+count) 的字节。
    ///     相比 DownloadFile（整响应读入内存），当服务端忽略 Range 头返回整个文件时，
    ///     这里只读取前 count 字节就停止，避免大文件 OOM。服务端不支持 Range 时会退化为读取文件开头。
    /// </summary>
    private static async Task<byte[]> ReadRemoteRangeAsync(string targetUrl, long offset, int count)
    {
        var rangeEnd = offset + count - 1;
        var rangeHeader = new List<(string, string)>
        {
            ("Range", $"bytes={offset}-{rangeEnd}")
        };

        var stream = await WebDavHttp.DownloadStream(targetUrl, rangeHeader, 30000);
        if (stream == null)
            return Array.Empty<byte>();

        try
        {
            var buffer = new byte[count];
            var totalRead = 0;
            while (totalRead < count)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(totalRead));
                if (n == 0) break;
                totalRead += n;
            }

            // 只保留实际读到的字节
            if (totalRead == count)
                return buffer;
            return buffer.AsMemory(0, totalRead).ToArray();
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static List<long> GenerateSampleOffsets(long fileSize, int sampleCount, int sampleByteCount)
    {
        var offsets = new List<long>();
        if (fileSize <= 0 || sampleCount <= 0)
            return offsets;

        // 文件太小，不足以采样多个点：只采开头
        if (fileSize <= sampleByteCount)
        {
            offsets.Add(0);
            return offsets;
        }

        // 第一个点：文件开头
        offsets.Add(0);

        // 最后一个点：文件末尾倒数 sampleByteCount 处
        var lastOffset = Math.Max(0, fileSize - sampleByteCount);
        if (sampleCount == 1)
            return offsets;

        if (sampleCount == 2)
        {
            offsets.Add(lastOffset);
            return offsets;
        }

        // 中间点：均匀分布在 (0, lastOffset) 区间内
        var middleCount = sampleCount - 2;
        var step = lastOffset / (middleCount + 1);
        for (var i = 1; i <= middleCount; i++)
        {
            var offset = step * i;
            // 避免和首尾点太近
            if (offset > sampleByteCount && offset < lastOffset - sampleByteCount)
                offsets.Add(offset);
        }

        offsets.Add(lastOffset);
        return offsets.Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>
    ///     计算指数退避延迟。retryAttempt 从 1 开始（第一次重试）。
    /// </summary>
    public static TimeSpan GetRetryDelay(int retryAttempt)
    {
        var ms = UploadConstants.RetryBackoffBase.TotalMilliseconds * Math.Pow(2, retryAttempt - 1);
        // 加一点抖动避免同步重试
        var jitter = Random.Shared.Next(0, (int)(ms * 0.1));
        return TimeSpan.FromMilliseconds(Math.Min(ms + jitter, TimeSpan.FromMinutes(5).TotalMilliseconds));
    }

    /// <summary>
    ///     获取失败文件的存放路径（video/failed/）。如目录不存在则创建。
    ///     写失败时返回 null，由调用方决定怎么处理。
    /// </summary>
    public static string? GetFailedDirectory(string videoFolder)
    {
        try
        {
            var dir = Path.Combine(videoFolder, UploadConstants.FailedSubdirectory);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create failed upload directory");
            return null;
        }
    }
}