using System.Collections.Concurrent;
using System.Net;
using Serilog;
using showroom.Utils;
using SimpleM3U8Parser;

namespace showroom.Download;

public class ShowroomUtils : DownloadUtils
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly HashSet<string> _downloadedSegments = []; // ���ڸ��������ص�Ƭ�Σ������ظ�
    private readonly SemaphoreSlim _downloadSemaphore = new(5); // ����ͬʱ��������Ƭ����Ϊ5
    private readonly string _outputDirectory;
    private readonly ConcurrentQueue<ShowroomSegment> _segmentsQueue = new();
    private Task? _downloadTask;
    private Task? _fetchTask;
    private bool _isDownloading;

    public ShowroomUtils(string name, string url) : base(name, url)
    {
        // �������Ŀ¼
        _outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "video");
        if (!Directory.Exists(_outputDirectory)) Directory.CreateDirectory(_outputDirectory);
    }

    /// <summary>
    ///     ��URL����ȡ·������
    /// </summary>
    /// <param name="url">����URL</param>
    /// <param name="includeQuery">�Ƿ������ѯ����</param>
    /// <returns>URL��·������</returns>
    private static string ExtractPathFromUrl(string url, bool includeQuery = false)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return includeQuery ? uri.PathAndQuery : uri.AbsolutePath;

        // ���URL��ʽ��Ч������ԭʼ�ַ���
        return url;
    }

    public override async Task DownloadAsync()
    {
        try
        {
            _isDownloading = true;

            // ��ʼ����������
            _downloadTask = Task.Run(ProcessDownloadQueueAsync);

            // ��ʼ��m3u8��ȡ����
            _fetchTask = Task.Run(FetchM3u8ContentAsync);

            // �������ں�̨������������������
            await _fetchTask;
            await _downloadTask;
        }
        catch (Exception ex)
        {
            Log.Error($"��ʼ�����ع���ʱ����: {ex.Message}");
        }
    }

    private async Task FetchM3u8ContentAsync()
    {
        try
        {
            // ������ȡm3u8���ݣ�ֱ��ȡ��
            while (!_cancellationTokenSource.IsCancellationRequested)
                try
                {
                    var m3u8Content =
                        await ShowroomDownloadHttp.Get(ExtractPathFromUrl(Url), new List<(string, string)>());

                    if (m3u8Content.Item1 != HttpStatusCode.OK)
                    {
                        Log.Error($"�޷���ȡm3u8���ݣ�״̬��: {m3u8Content.Item1}");

                        // ���ݵȴ�������
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                        continue;
                    }

                    var m3u8 = M3u8Parser.Parse(m3u8Content.Item2);
                    var newSegments = 0;

                    // ���µ�Ƭ����ӵ�����
                    foreach (var segment in m3u8.Medias)
                    {
                        // ����Ƿ��Ѿ����ع���Ƭ�Σ�ͨ��Pathƥ�䣩
                        if (_downloadedSegments.Contains(segment.Path)) continue;
                        _segmentsQueue.Enqueue(new ShowroomSegment
                        {
                            Path = segment.Path,
                            Duration = segment.Duration,
                            Downloaded = false
                        });

                        newSegments++;
                    }

                    if (newSegments > 0) Log.Debug($"����� {newSegments} ����Ƭ�ε����ض��У���ǰ���г���: {_segmentsQueue.Count}");

                    // �������õļ��ʱ��ȴ�
                    await Task.Delay(TimeSpan.FromMilliseconds(4000), _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // ȡ��������ֱ���˳�ѭ��
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"��ȡm3u8���ݳ���: {ex.Message}");

                    // �������ݵȴ�������������Ե�����Դ�˷�
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
        }
        catch (OperationCanceledException)
        {
            // ����ȡ��
            Log.Information("m3u8��ȡ������ȡ��");
        }
        catch (Exception ex)
        {
            Log.Error($"m3u8��ȡ������δ�����쳣: {ex}");
        }
    }

    private async Task ProcessDownloadQueueAsync()
    {
        try
        {
            // ���������б����������������
            List<Task> activeTasks = [];

            // �Ӷ����л�ȡƬ�β�����
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                // ����������ɵ�����
                activeTasks.RemoveAll(t => t.IsCompleted);

                // �������Ϊ�գ��ȴ�һ��ʱ����ټ��
                if (_segmentsQueue.IsEmpty)
                {
                    await Task.Delay(500, _cancellationTokenSource.Token);
                    continue;
                }

                // ���ԴӶ�����ȡ��Ƭ��
                if (_segmentsQueue.TryDequeue(out var segment))
                {
                    // �ȴ���ȡ�ź��������Ʋ�����
                    await _downloadSemaphore.WaitAsync(_cancellationTokenSource.Token);

                    // ��������������Ƭ��
                    var downloadTask = Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadSegmentAsync(segment);
                        }
                        finally
                        {
                            // �ͷ��ź����������µ�������������
                            _downloadSemaphore.Release();
                        }
                    }, _cancellationTokenSource.Token);

                    activeTasks.Add(downloadTask);
                }

                // ��������̫�࣬�ȴ�һЩ�������
                if (activeTasks.Count > 20) await Task.WhenAny(activeTasks);
            }

            // �ȴ�����ʣ���������
            if (activeTasks.Count > 0) await Task.WhenAll(activeTasks);

            Log.Information("����Ƭ����������ɻ�ȡ��");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("���ش�����ȡ��");
        }
        catch (Exception ex)
        {
            Log.Error($"���ش�������з�������: {ex}");
        }
    }

    private async Task DownloadSegmentAsync(ShowroomSegment segment)
    {
        try
        {
            // ��URL�л�ȡ�ļ�����ʹ��Ψһ��ʶ��
            var fileName = Path.GetFileName(segment.Path);
            if (string.IsNullOrEmpty(fileName)) fileName = $"segment_{Guid.NewGuid()}.ts";

            var outputPath = Path.Combine(_outputDirectory, fileName);

            // ����m3u8Ƭ��
            var response = await ShowroomDownloadHttp.Get(
                ExtractPathFromUrl(segment.Path, true),
                new List<(string, string)>(),
                5000); // ����5�볬ʱ

            if (response.Item1 == HttpStatusCode.OK && !string.IsNullOrEmpty(response.Item2))
            {
                // ������д���ļ�
                await File.WriteAllTextAsync(outputPath, response.Item2, _cancellationTokenSource.Token);

                // ��¼��Ƭ�������أ������ظ�
                _downloadedSegments.Add(segment.Path);

                Log.Debug($"�ɹ�����Ƭ��: {fileName}");
            }
            else
            {
                Log.Warning($"����Ƭ��ʧ��: {fileName}, ״̬��: {response.Item1}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"����Ƭ��ʱ��������: {segment.Path}, ����: {ex.Message}");
        }
    }

    public override async Task Stop()
    {
        if (_isDownloading)
        {
            // ȡ����������
            await _cancellationTokenSource.CancelAsync();

            // �ȴ��������
            if (_downloadTask != null)
                try
                {
                    // ���ó�ʱ���������޵ȴ�
                    var timeoutTask = Task.Delay(5000);
                    await Task.WhenAny(_downloadTask, timeoutTask);
                }
                catch (Exception ex)
                {
                    Log.Error($"ֹͣ��������ʱ��������: {ex.Message}");
                }

            if (_fetchTask != null)
                try
                {
                    // ���ó�ʱ���������޵ȴ�
                    var timeoutTask = Task.Delay(5000);
                    await Task.WhenAny(_fetchTask, timeoutTask);
                }
                catch (Exception ex)
                {
                    Log.Error($"ֹͣm3u8��ȡ����ʱ��������: {ex.Message}");
                }

            // ��ն���
            while (_segmentsQueue.TryDequeue(out _))
            {
            }

            _isDownloading = false;

            Log.Information($"{Name} ��������ֹͣ");
        }
    }
}

public struct ShowroomSegment
{
    public string Path { get; set; }
    public double Duration { get; set; }
    public bool Downloaded { get; set; }
}