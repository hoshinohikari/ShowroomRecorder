using System.Collections.Concurrent;
using System.Threading.Channels;
using Serilog;

namespace showroom.Utils;

/// <summary>
///     后台全量校验队列。
///     大文件上传完成并通过抽检后，加入此队列进行最终的全量 SHA256 校验。
///     校验通过才删除本地文件。校验通过 / 失败 / 超过重试次数都会从队列中移除。
/// </summary>
internal sealed class BackgroundVerificationQueue : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<VerificationItem> _queue = Channel.CreateUnbounded<VerificationItem>();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public void Dispose()
    {
        _cts?.Dispose();
    }

    public void Start(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = Task.Run(() => RunWorkerAsync(_cts.Token), _cts.Token);
        Log.Debug("Background verification queue started");
    }

    public async Task StopAsync()
    {
        if (_cts == null || _workerTask == null)
            return;

        try
        {
            _queue.Writer.Complete();
            await _workerTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background verification queue stop failed");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _workerTask = null;
        }
    }

    public void Enqueue(string targetUrl, string localFilePath, string localHash)
    {
        var key = localFilePath;
        if (!_pending.TryAdd(key, 0))
            return; // 已经在队列里了

        var item = new VerificationItem
        {
            TargetUrl = targetUrl,
            LocalFilePath = localFilePath,
            LocalHash = localHash,
            RetryCount = 0
        };

        if (!_queue.Writer.TryWrite(item))
        {
            _pending.TryRemove(key, out _);
            Log.Warning("Failed to enqueue background verification: {FilePath}", localFilePath);
        }
        else
        {
            Log.Debug("Background verification enqueued: {FileName}", Path.GetFileName(localFilePath));
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            VerificationItem? item;
            try
            {
                item = await _queue.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            var requeue = false;
            try
            {
                requeue = await ProcessItemAsync(item, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Background verification worker error for {FilePath}", item.LocalFilePath);
            }
            finally
            {
                _pending.TryRemove(item.LocalFilePath, out _);
            }

            if (requeue)
            {
                // 重试：先等一会儿，再重新入队
                // 等待放在移除 pending 之后，避免长时间占用 pending 标记
                try
                {
                    await Task.Delay(UploadConstants.VerificationPollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (_pending.TryAdd(item.LocalFilePath, 0))
                    if (!_queue.Writer.TryWrite(item))
                        _pending.TryRemove(item.LocalFilePath, out _);
            }
        }

        Log.Debug("Background verification worker stopped");
    }

    /// <returns>true 表示需要重试（重新入队），false 表示已完成（无论通过还是失败）</returns>
    private async Task<bool> ProcessItemAsync(VerificationItem item, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(item.LocalFilePath);

        // 先检查本地文件还在不在
        if (!File.Exists(item.LocalFilePath))
        {
            Log.Debug("Background verification skipped, local file gone: {FileName}", fileName);
            return false;
        }

        // 请求服务端 hash。超时按本地文件大小动态估算（服务端需读文件算 hash，超大文件固定短超时会误判超时）
        var fileSize = new FileInfo(item.LocalFilePath).Length;
        var hashTimeoutMs = UploadHelpers.EstimateServerHashTimeoutMs(fileSize);
        var remoteHash = await UploadHelpers.TryGetRemoteSha256Async(item.TargetUrl, hashTimeoutMs);

        if (!string.IsNullOrWhiteSpace(remoteHash))
        {
            // 拿到了 hash，直接比对
            if (string.Equals(remoteHash, item.LocalHash, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(item.LocalFilePath);
                    Log.Information(
                        "Background verification passed, deleted local file: {FileName}", fileName);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete local file after verification: {FileName}", fileName);
                }

                return false;
            }

            Log.Error(
                "Background verification FAILED: hash mismatch for {FileName}, local={LocalHash}, remote={RemoteHash}",
                fileName, item.LocalHash, remoteHash);
            // hash 不匹配：文件留在本地，不上传了（抽检过了但全量不一致，概率极低，需要人工介入）
            return false;
        }

        // 服务端 hash 不可用（不支持或超时）
        item.RetryCount++;
        if (item.RetryCount >= UploadConstants.ServerHashMaxRetries)
        {
            Log.Warning(
                "Background verification gave up after {MaxRetries} retries (server hash unavailable): {FileName}",
                UploadConstants.ServerHashMaxRetries, fileName);
            // 服务端一直不支持 hash，文件留在本地（抽检已经通过了，安全起见不删）
            return false;
        }

        Log.Debug(
            "Background verification: server hash unavailable for {FileName}, retry {Retry}/{MaxRetry}",
            fileName, item.RetryCount, UploadConstants.ServerHashMaxRetries);

        // 需要重试
        return true;
    }
}

internal class VerificationItem
{
    public string TargetUrl { get; set; } = string.Empty;
    public string LocalFilePath { get; set; } = string.Empty;
    public string LocalHash { get; set; } = string.Empty;
    public int RetryCount { get; set; }
}