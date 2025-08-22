using System.Net;
using System.Text.Json;
using Serilog;
using showroom.Utils;

namespace showroom;

public class Online
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _task;
    private bool _isStarting;
    private readonly HashSet<long> _roomIds = new();

    // 全局单例
    private static readonly Lazy<Online> _instance = new(() => new Online());
    public static Online Instance => _instance.Value;

    private Online()
    {
        Log.Information("start online check");
        _task = Start();
    }

    private async Task Start()
    {
        _isStarting = true;
        await Listen();
    }

    private async Task Listen()
    {
        while (_isStarting)
        {
            try
            {
                Log.Debug("test all online users");

                var res = await ShowroomHttp.Get("api/live/onlives", []);

                if (res.Item1 != HttpStatusCode.OK)
                {
                    Log.Warning("get onlines failed: {Status} {Body}", res.Item1, res.Item2);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ConfigUtils.Config.Interval), _cancellationTokenSource.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        Log.Debug("Task.Delay 被取消");
                        break;
                    }
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(res.Item2);
                    var root = document.RootElement;

                    if (root.TryGetProperty("onlives", out var onlives) && onlives.ValueKind == JsonValueKind.Array)
                    {
                        // 用临时集合构建，然后一次性替换，避免读写竞争
                        var tmp = new HashSet<long>();

                        foreach (var genre in onlives.EnumerateArray())
                        {
                            if (!genre.TryGetProperty("lives", out var lives) || lives.ValueKind != JsonValueKind.Array)
                                continue;

                            foreach (var live in lives.EnumerateArray())
                            {
                                if (live.TryGetProperty("room_id", out var roomIdProp) && roomIdProp.ValueKind == JsonValueKind.Number)
                                {
                                    try
                                    {
                                        var id = roomIdProp.GetInt64();
                                        tmp.Add(id);
                                    }
                                    catch
                                    {
                                        // 忽略非 long 的异常
                                    }
                                }
                            }
                        }

                        // 原子地更新内部集合
                        lock (_roomIds)
                        {
                            _roomIds.Clear();
                            foreach (var id in tmp)
                                _roomIds.Add(id);
                        }

                        Log.Debug("parsed {Count} online room_ids", _roomIds.Count);
                    }
                    else
                    {
                        Log.Warning("onlives field missing or not array");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "parse onlines json error");
                }

                // 正常情况下，等待一段时间再轮询
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ConfigUtils.Config.Interval), _cancellationTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    Log.Debug("Task.Delay 被取消");
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "online Listen error");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ConfigUtils.Config.Interval), _cancellationTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    Log.Debug("Retry Task.Delay 被取消");
                    break;
                }
            }
        }
    }

    // 提供线程安全的房间在线查询（异步包装，避免调用方阻塞）
    public Task<bool> ContainsRoomAsync(long roomId)
    {
        lock (_roomIds)
        {
            return Task.FromResult(_roomIds.Contains(roomId));
        }
    }

    // 可选：获取只读快照的异步方法
    public Task<IReadOnlyCollection<long>> GetRoomIdsSnapshotAsync()
    {
        lock (_roomIds)
        {
            IReadOnlyCollection<long> snapshot = _roomIds.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    public IReadOnlyCollection<long> GetRoomIdsSnapshot()
    {
        lock (_roomIds)
        {
            return _roomIds.ToArray();
        }
    }

    public async Task Stop()
    {
        try
        {
            _isStarting = false;
            await _cancellationTokenSource.CancelAsync();
            await _task;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "online stop error");
        }
    }
}