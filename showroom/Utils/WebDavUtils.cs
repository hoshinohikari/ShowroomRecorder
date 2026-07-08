using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Serilog;

namespace showroom.Utils;

public static class RecordingRegistry
{
    private static readonly ConcurrentDictionary<string, byte> ActiveFiles = new(StringComparer.OrdinalIgnoreCase);

    public static void MarkRecording(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        var fullPath = Path.GetFullPath(filePath);
        ActiveFiles[fullPath] = 0;
        Log.Verbose("Recording file tracked: {FilePath}", fullPath);
    }

    public static void MarkCompleted(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        var fullPath = Path.GetFullPath(filePath);
        ActiveFiles.TryRemove(fullPath, out _);
        Log.Verbose("Recording file untracked: {FilePath}", fullPath);
    }

    public static bool IsRecording(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;
        var fullPath = Path.GetFullPath(filePath);
        return ActiveFiles.ContainsKey(fullPath);
    }
}

public static class WebDavUploader
{
    private const int MaxConcurrentUploads = 5;
    private const double HashRequestTimeoutMs = 180000;
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static readonly Lock StateLock = new();

    private static readonly Channel<string> UploadQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private static readonly ConcurrentDictionary<string, byte> PendingUploads = new(StringComparer.OrdinalIgnoreCase);
    private static string _baseUrl = string.Empty;
    private static bool _enabled;
    private static bool _initialized;
    private static CancellationTokenSource? _backgroundCts;
    private static Task? _scanTask;
    private static Task[]? _uploadWorkerTasks;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    public static async Task InitializeAsync()
    {
        if (_initialized) return;

        await InitLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await InitializeInternalAsync();
            _initialized = true;
        }
        finally
        {
            InitLock.Release();
        }
    }

    private static async Task InitializeInternalAsync()
    {
        var webDavUrl = ConfigUtils.Config.WebDavUrl.Trim();
        var username = ConfigUtils.Config.WebDavUsername.Trim();
        var password = ConfigUtils.Config.WebDavPassword;
        var allowInsecureCertificate = ConfigUtils.Config.WebDavAllowInsecureCertificate;
        Log.Debug(
            "WebDAV init: urlConfigured={UrlConfigured}, usernameConfigured={UserConfigured}, passwordConfigured={PassConfigured}, allowInsecureCertificate={AllowInsecureCertificate}",
            !string.IsNullOrWhiteSpace(webDavUrl),
            !string.IsNullOrWhiteSpace(username),
            !string.IsNullOrWhiteSpace(password),
            allowInsecureCertificate);

        if (string.IsNullOrWhiteSpace(webDavUrl))
        {
            _enabled = false;
            return;
        }

        if (!Uri.TryCreate(webDavUrl, UriKind.Absolute, out var uri))
        {
            Log.Warning("Invalid WebDAV URL \"{WebDavUrl}\", upload disabled", webDavUrl);
            _enabled = false;
            return;
        }

        WebDavHttp.Configure(username, password, allowInsecureCertificate);

        lock (StateLock)
        {
            _baseUrl = uri.ToString().TrimEnd('/') + "/";
        }

        Log.Information("WebDAV auth mode: {Mode}",
            string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password) ? "anonymous" : "basic");
        Log.Debug("WebDAV base URL: {BaseUrl}", _baseUrl);

        var testFileName = $".showroomrecorder_upload_test_{Guid.NewGuid():N}.txt";
        var testUrl = BuildTargetUrl(_baseUrl, testFileName);
        try
        {
            var testPayload = "showroomrecorder webdav test"u8.ToArray();
            var putStatus = await WebDavHttp.PutBytes(testUrl, testPayload, "text/plain", 30000);
            if ((int)putStatus is < 200 or >= 300)
            {
                Log.Warning("WebDAV upload test failed: {StatusCode}, upload disabled", putStatus);
                _enabled = false;
                return;
            }

            _ = await WebDavHttp.Delete(testUrl, 30000);

            _enabled = true;
            Log.Information("WebDAV upload enabled");
            Log.Debug("WebDAV upload test file created and deleted successfully");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebDAV upload test failed, upload disabled");
            _enabled = false;
        }
    }

    public static Task StartBackgroundUploaderAsync()
    {
        if (_scanTask is { IsCompleted: false })
            return Task.CompletedTask;

        _backgroundCts = new CancellationTokenSource();
        _scanTask = Task.Run(() => RunScannerAsync(_backgroundCts.Token));
        _uploadWorkerTasks = Enumerable.Range(0, MaxConcurrentUploads)
            .Select(i => Task.Run(() => RunUploadWorkerAsync(i + 1, _backgroundCts.Token)))
            .ToArray();
        Log.Information("WebDAV background uploader started with {WorkerCount} workers", MaxConcurrentUploads);
        return Task.CompletedTask;
    }

    public static async Task StopBackgroundUploaderAsync()
    {
        if (_backgroundCts == null || _scanTask == null || _uploadWorkerTasks == null)
            return;

        try
        {
            await _backgroundCts.CancelAsync();
            await _scanTask;
            await Task.WhenAll(_uploadWorkerTasks);
            Log.Information("WebDAV background uploader stopped");
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebDAV background uploader stop failed");
        }
        finally
        {
            _backgroundCts.Dispose();
            _backgroundCts = null;
            _scanTask = null;
            _uploadWorkerTasks = null;
        }
    }

    public static void EnqueueUpload(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        var fullPath = Path.GetFullPath(filePath);
        if (!PendingUploads.TryAdd(fullPath, 0))
            return;

        if (!UploadQueue.Writer.TryWrite(fullPath))
        {
            PendingUploads.TryRemove(fullPath, out _);
            Log.Warning("WebDAV enqueue upload failed: {FilePath}", fullPath);
            return;
        }

        Log.Debug("WebDAV upload enqueued: {FilePath}", fullPath);
    }

    private static async Task RunUploadWorkerAsync(int workerId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string filePath;
            try
            {
                filePath = await UploadQueue.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ProcessFileAsync(filePath, cancellationToken);
            }
            finally
            {
                PendingUploads.TryRemove(filePath, out _);
            }
        }

        Log.Debug("WebDAV upload worker stopped: id={WorkerId}", workerId);
    }

    private static async Task RunScannerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
            try
            {
                await ScanAndEnqueueVideoFolderAsync(cancellationToken);
                await Task.Delay(ScanInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WebDAV background scanner loop error");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }

        Log.Debug("WebDAV background scanner stopped");
    }

    private static Task ScanAndEnqueueVideoFolderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var videoFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
            if (!Directory.Exists(videoFolder))
                return Task.CompletedTask;

            foreach (var filePath in Directory.EnumerateFiles(videoFolder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (RecordingRegistry.IsRecording(filePath))
                    continue;
                EnqueueUpload(filePath);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private static async Task ProcessFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync();

        string baseUrl;
        var enabled = _enabled;

        lock (StateLock)
        {
            baseUrl = _baseUrl;
        }

        if (!enabled || string.IsNullOrWhiteSpace(baseUrl))
        {
            Log.Verbose("WebDAV upload skipped: enabled={Enabled}, baseUrlEmpty={BaseUrlEmpty}",
                enabled, string.IsNullOrWhiteSpace(baseUrl));
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
            {
                Log.Warning("WebDAV upload skipped, file not found: {FilePath}", filePath);
                return;
            }

            var fileInfo = new FileInfo(filePath);

            var targetUrl = BuildTargetUrl(baseUrl, fileInfo.Name);
            Log.Debug("WebDAV uploading file: {FileName}, bytes={Length}", fileInfo.Name, fileInfo.Length);

            var (headStatus, remoteSize, headError) = await WebDavHttp.HeadContentLength(targetUrl, 30000);
            var shouldPreVerify = false;
            if (headStatus == HttpStatusCode.OK && remoteSize.HasValue)
            {
                shouldPreVerify = true;
                if (remoteSize.Value == fileInfo.Length)
                {
                    Log.Debug("WebDAV remote file size already matches local, start verify: {FileName}", fileInfo.Name);
                    if (await VerifyRemoteAndDeleteLocalAsync(targetUrl, fileInfo))
                        return;
                    Log.Warning("WebDAV remote size matches but content verify failed, re-upload: {FileName}",
                        fileInfo.Name);
                }
                else if (remoteSize.Value > 0 && remoteSize.Value < fileInfo.Length)
                {
                    Log.Information(
                        "WebDAV resume upload by PATCH append: {FileName}, remoteSize={RemoteSize}, localSize={LocalSize}",
                        fileInfo.Name, remoteSize.Value, fileInfo.Length);
                    var patchStatus = await WebDavHttp.PatchAppendFile(
                        targetUrl,
                        filePath,
                        remoteSize.Value,
                        "application/octet-stream");
                    if ((int)patchStatus is >= 200 and < 300)
                    {
                        Log.Information("WebDAV resumed upload: {FileName}", fileInfo.Name);
                        await TryApplyRemoteTimestampsAsync(targetUrl, fileInfo);
                        _ = await VerifyRemoteAndDeleteLocalAsync(targetUrl, fileInfo);
                        return;
                    }

                    Log.Warning("WebDAV resume upload failed for {FileName}: {StatusCode}", fileInfo.Name, patchStatus);
                }
                else if (remoteSize.Value > fileInfo.Length)
                {
                    Log.Warning(
                        "WebDAV remote file larger than local, force overwrite upload: {FileName}, remote={RemoteSize}, local={LocalSize}",
                        fileInfo.Name, remoteSize.Value, fileInfo.Length);
                }
            }
            else if (headStatus is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed))
            {
                Log.Verbose("WebDAV HEAD unavailable before upload: status={Status}, error={Error}", headStatus,
                    headError);
            }

            // Only pre-verify when remote object is known to exist.
            if (shouldPreVerify)
                if (await VerifyRemoteAndDeleteLocalAsync(targetUrl, fileInfo))
                    return;

            var putStatus = await WebDavHttp.PutFile(targetUrl, filePath, "application/octet-stream");
            if ((int)putStatus is >= 200 and < 300)
            {
                Log.Information("WebDAV uploaded: {FileName}", fileInfo.Name);
                await TryApplyRemoteTimestampsAsync(targetUrl, fileInfo);
                _ = await VerifyRemoteAndDeleteLocalAsync(targetUrl, fileInfo);
            }
            else
            {
                Log.Error("WebDAV upload failed for {FileName}: {StatusCode}", fileInfo.Name, putStatus);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebDAV upload failed for {FilePath}", filePath);
        }
    }

    private static string BuildTargetUrl(string baseUrl, string fileName)
    {
        return $"{baseUrl}{Uri.EscapeDataString(fileName)}";
    }

    private static async Task TryApplyRemoteTimestampsAsync(string targetUrl, FileInfo localFileInfo)
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

    private static async Task<bool> VerifyRemoteAndDeleteLocalAsync(string targetUrl, FileInfo localFileInfo)
    {
        try
        {
            if (!localFileInfo.Exists)
            {
                Log.Warning("Skip verify, local file missing: {FilePath}", localFileInfo.FullName);
                return false;
            }

            // Preferred fast-path: ask server to compute hash via "?hash".
            var remoteSha256 = await TryGetRemoteSha256Async(targetUrl);
            if (!string.IsNullOrWhiteSpace(remoteSha256))
            {
                var localSha256 = await ComputeSha256HexAsync(localFileInfo.FullName);
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

            Log.Debug("WebDAV server hash unavailable, fallback to stream comparison for {FileName}",
                localFileInfo.Name);
            await using var remoteStream = await WebDavHttp.DownloadStream(targetUrl, [], 30000);
            if (remoteStream == null)
            {
                Log.Warning("WebDAV verify failed for {FileName}: remote stream unavailable", localFileInfo.Name);
                return false;
            }

            await using var localStream = File.OpenRead(localFileInfo.FullName);
            var isSame = await StreamsEqualAsync(localStream, remoteStream);
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

    private static async Task<string?> TryGetRemoteSha256Async(string targetUrl)
    {
        try
        {
            var hashUrl = targetUrl.Contains('?')
                ? $"{targetUrl}&hash"
                : $"{targetUrl}?hash";

            var (statusCode, content) = await WebDavHttp.Get(hashUrl, [], HashRequestTimeoutMs);
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

        // Keep visible ASCII for hash extraction and strip common control characters.
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

    private static async Task<string> ComputeSha256HexAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var bytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<bool> StreamsEqualAsync(Stream localStream, Stream remoteStream)
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
}
