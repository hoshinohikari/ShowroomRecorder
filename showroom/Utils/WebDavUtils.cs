using System.Collections.Concurrent;
using System.Threading.Channels;
using Serilog;

namespace showroom.Utils;

public static class RecordingRegistry
{
    private const string RecordingSuffix = ".recording";
    private static readonly ConcurrentDictionary<string, byte> ActiveFiles = new(StringComparer.OrdinalIgnoreCase);

    public static void MarkRecording(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        var fullPath = Path.GetFullPath(filePath);
        ActiveFiles[fullPath] = 0;

        try
        {
            var markerPath = GetMarkerPath(fullPath);
            var directory = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(markerPath,
                DateTimeOffset.UtcNow.ToString("O"));
            Log.Verbose("Recording marker written: {MarkerPath}", markerPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write recording marker for {FilePath}", fullPath);
            // 标记文件写入失败不影响录制流程
        }

        Log.Verbose("Recording file tracked: {FilePath}", fullPath);
    }

    public static void MarkCompleted(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        var fullPath = Path.GetFullPath(filePath);
        ActiveFiles.TryRemove(fullPath, out _);

        try
        {
            var markerPath = GetMarkerPath(fullPath);
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
                Log.Verbose("Recording marker removed: {MarkerPath}", markerPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove recording marker for {FilePath}", fullPath);
        }

        Log.Verbose("Recording file untracked: {FilePath}", fullPath);
    }

    public static bool IsRecording(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;
        var fullPath = Path.GetFullPath(filePath);
        if (ActiveFiles.ContainsKey(fullPath))
            return true;

        // 内存中没有记录时，检查磁盘上的 .recording 标记文件
        // 这用于处理程序重启后，遗留的正在录制中的文件
        try
        {
            var markerPath = GetMarkerPath(fullPath);
            if (File.Exists(markerPath))
            {
                // 如果标记文件存在且超过 2 小时没有更新，视为崩溃遗留的孤儿标记
                // 仍然返回 true（避免上传半截文件），但打个 warning
                var markerAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(markerPath);
                if (markerAge > TimeSpan.FromHours(2))
                    Log.Warning(
                        "Stale recording marker detected (age={Age:hh\\:mm}), file still treated as recording: {FilePath}",
                        markerAge, fullPath);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Failed to check recording marker for {FilePath}", fullPath);
        }

        return false;
    }

    private static string GetMarkerPath(string fullFilePath)
    {
        return fullFilePath + RecordingSuffix;
    }
}

public static class WebDavUploader
{
    private const int MaxConcurrentUploads = 5;
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static readonly Lock StateLock = new();
    private static BackgroundVerificationQueue? _verificationQueue;

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

    /// <summary>
    ///     扫描时忽略的文件后缀（标记文件、进度文件等）。
    /// </summary>
    private static readonly HashSet<string> SkipSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".recording",
        UploadConstants.ProgressSuffix,
        ".log"
    };

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
        var testUrl = UploadHelpers.BuildTargetUrl(_baseUrl, testFileName);
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

        // 初始化后台校验队列（大文件上传后做延迟全量校验）
        string baseUrl;
        lock (StateLock)
        {
            baseUrl = _baseUrl;
        }

        _verificationQueue = new BackgroundVerificationQueue(baseUrl);
        _verificationQueue.Start(_backgroundCts.Token);

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
            if (_verificationQueue != null)
                await _verificationQueue.StopAsync();
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

                // 跳过标记文件、进度文件、日志等非视频文件
                var ext = Path.GetExtension(filePath);
                if (SkipSuffixes.Contains(ext))
                    continue;

                // 跳过 failed 子目录里的文件
                var dirName = Path.GetFileName(Path.GetDirectoryName(filePath));
                if (string.Equals(dirName, UploadConstants.FailedSubdirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

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
        var verificationQueue = _verificationQueue;

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

            // 按文件大小分发到不同策略
            if (fileInfo.Length >= UploadConstants.LargeFileThresholdBytes)
            {
                if (verificationQueue == null)
                {
                    Log.Warning(
                        "Verification queue not available, falling back to small file uploader for large file: {FileName}",
                        fileInfo.Name);
                    await SmallFileUploader.ProcessAsync(filePath, baseUrl, cancellationToken);
                }
                else
                {
                    await LargeFileUploader.ProcessAsync(filePath, baseUrl, verificationQueue, cancellationToken);
                }
            }
            else
            {
                await SmallFileUploader.ProcessAsync(filePath, baseUrl, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebDAV upload failed for {FilePath}", filePath);
        }
    }
}