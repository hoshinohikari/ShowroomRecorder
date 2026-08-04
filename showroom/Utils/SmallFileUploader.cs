using System.Net;
using Serilog;

namespace showroom.Utils;

/// <summary>
/// 小文件上传策略（< UploadConstants.LargeFileThresholdBytes）。
/// 整块 PUT 上传 + 全量完整性校验，简单直接。
/// </summary>
internal static class SmallFileUploader
{
    public static async Task ProcessAsync(string filePath, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
            {
                Log.Warning("WebDAV small upload skipped, file not found: {FilePath}", filePath);
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var targetUrl = UploadHelpers.BuildTargetUrl(baseUrl, fileInfo.Name);
            Log.Debug("WebDAV small uploading file: {FileName}, bytes={Length}", fileInfo.Name, fileInfo.Length);

            // 先 HEAD 看看远端情况
            var (headStatus, remoteSize, headError) = await WebDavHttp.HeadContentLength(targetUrl, 30000);
            var remoteExists = headStatus == HttpStatusCode.OK && remoteSize.HasValue;

            // 远端已存在且大小一致：先试试能不能直接校验过
            if (remoteExists && remoteSize!.Value == fileInfo.Length)
            {
                Log.Debug("WebDAV remote file size already matches local, verify first: {FileName}", fileInfo.Name);
                if (await VerifyAndDeleteLocalAsync(targetUrl, fileInfo))
                    return;
                Log.Warning("WebDAV remote size matches but verify failed, re-upload: {FileName}", fileInfo.Name);
            }
            // 远端已有部分数据（小于本地）：尝试 PATCH 续传
            else if (remoteExists && remoteSize!.Value > 0 && remoteSize.Value < fileInfo.Length)
            {
                Log.Information(
                    "WebDAV small resume upload by PATCH append: {FileName}, remoteSize={RemoteSize}, localSize={LocalSize}",
                    fileInfo.Name, remoteSize.Value, fileInfo.Length);
                var patchStatus = await WebDavHttp.PatchAppendFile(
                    targetUrl, filePath, remoteSize.Value, "application/octet-stream");
                if ((int)patchStatus is >= 200 and < 300)
                {
                    Log.Information("WebDAV small resumed upload: {FileName}", fileInfo.Name);
                    await UploadHelpers.TryApplyRemoteTimestampsAsync(targetUrl, fileInfo);
                    _ = await VerifyAndDeleteLocalAsync(targetUrl, fileInfo);
                    return;
                }

                Log.Warning("WebDAV small resume upload failed for {FileName}: {StatusCode}", fileInfo.Name,
                    patchStatus);
            }

            // 远端不存在、远端更大、或续传失败：整块 PUT
            var putStatus = await WebDavHttp.PutFile(targetUrl, filePath, "application/octet-stream");
            if ((int)putStatus is >= 200 and < 300)
            {
                Log.Information("WebDAV small uploaded: {FileName}", fileInfo.Name);
                await UploadHelpers.TryApplyRemoteTimestampsAsync(targetUrl, fileInfo);
                _ = await VerifyAndDeleteLocalAsync(targetUrl, fileInfo);
            }
            else
            {
                Log.Error("WebDAV small upload failed for {FileName}: {StatusCode}", fileInfo.Name, putStatus);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("WebDAV small upload canceled: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebDAV small upload failed for {FilePath}", filePath);
        }
    }

    /// <summary>
    /// 小文件全量校验：优先用服务端 hash，失败则流式全量比对。
    /// 校验通过则删除本地文件。
    /// </summary>
    private static async Task<bool> VerifyAndDeleteLocalAsync(string targetUrl, FileInfo localFileInfo)
    {
        try
        {
            if (!localFileInfo.Exists)
            {
                Log.Warning("Skip verify, local file missing: {FilePath}", localFileInfo.FullName);
                return false;
            }

            // 快速路径：服务端 hash
            var remoteSha256 = await UploadHelpers.TryGetRemoteSha256Async(targetUrl, 180000);
            if (!string.IsNullOrWhiteSpace(remoteSha256))
            {
                var localSha256 = await UploadHelpers.ComputeSha256HexAsync(localFileInfo.FullName);
                if (string.Equals(localSha256, remoteSha256, StringComparison.OrdinalIgnoreCase))
                {
                    localFileInfo.Delete();
                    Log.Information("WebDAV verify success by server hash, deleted local file: {FilePath}",
                        localFileInfo.FullName);
                    return true;
                }

                Log.Warning(
                    "WebDAV verify failed for {FileName}: server hash mismatch, keep local file (local={LocalHash}, remote={RemoteHash})",
                    localFileInfo.Name, localSha256, remoteSha256);
                return false;
            }

            // 降级：流式全量比对
            Log.Debug("WebDAV server hash unavailable, fallback to full stream compare for {FileName}",
                localFileInfo.Name);
            await using var remoteStream = await WebDavHttp.DownloadStream(targetUrl, [], 30000);
            if (remoteStream == null)
            {
                Log.Warning("WebDAV verify failed for {FileName}: remote stream unavailable", localFileInfo.Name);
                return false;
            }

            await using var localStream = File.OpenRead(localFileInfo.FullName);
            var isSame = await UploadHelpers.StreamsEqualAsync(localStream, remoteStream);
            if (!isSame)
            {
                Log.Warning("WebDAV verify failed for {FileName}: content mismatch, keep local file",
                    localFileInfo.Name);
                return false;
            }

            localFileInfo.Delete();
            Log.Information("WebDAV verify success, deleted local file: {FilePath}", localFileInfo.FullName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebDAV verify/delete failed for {FilePath}, keep local file", localFileInfo.FullName);
            return false;
        }
    }
}
