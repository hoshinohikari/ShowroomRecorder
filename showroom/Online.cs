using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Serilog;
using showroom.Utils;

namespace showroom;

public class Online
{
    // 全局单例
    private static readonly Lazy<Online> _instance = new(() => new Online());
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ConcurrentDictionary<long, byte> _roomIds = new(); // 使用 ConcurrentDictionary
    private readonly Task _task;

    private Online()
    {
        Log.Information("start online check");
        _task = Start();
    }

    public static Online Instance => _instance.Value;

    private async Task Start()
    {
        await Listen();
    }

    private async Task Listen()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
            try
            {
                var res = await ShowroomHttp.Get("api/live/onlives", []);

                if (res.Item1 != HttpStatusCode.OK || string.IsNullOrWhiteSpace(res.Item2))
                {
                    Log.Warning("get onlines failed: {Status}", res.Item1);
                    if (await DelayWithCancel(ConfigUtils.Interval))
                        break;

                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(res.Item2);
                    var root = document.RootElement;

                    if (root.TryGetProperty("onlives", out var onlives) && onlives.ValueKind == JsonValueKind.Array)
                    {
                        // 收集新的房间ID
                        var newRoomIds = new HashSet<long>();

                        foreach (var genre in onlives.EnumerateArray())
                        {
                            if (!genre.TryGetProperty("lives", out var lives) || lives.ValueKind != JsonValueKind.Array)
                                continue;

                            foreach (var live in lives.EnumerateArray())
                                if (live.TryGetProperty("room_id", out var roomIdProp) &&
                                    roomIdProp.ValueKind == JsonValueKind.Number)
                                    try
                                    {
                                        var id = roomIdProp.GetInt64();
                                        newRoomIds.Add(id);
                                    }
                                    catch
                                    {
                                        // 忽略非 long 的异常
                                    }
                        }

                        // 移除不在新列表中的房间
                        foreach (var existingId in _roomIds.Keys)
                            if (!newRoomIds.Contains(existingId))
                                _roomIds.TryRemove(existingId, out _);

                        // 添加新房间
                        foreach (var id in newRoomIds)
                            _roomIds.TryAdd(id, 0);

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

                if (ConfigUtils.Config.SrId != "")
                {
                    // 如果登记了SrId，则继续获取关注列表以保持活跃
                    res = await ShowroomHttp.Get("api/follow/rooms", []);

                    if (res.Item1 != HttpStatusCode.OK || string.IsNullOrWhiteSpace(res.Item2))
                    {
                        Log.Warning("get follow failed: {Status}", res.Item1);
                        if (await DelayWithCancel(ConfigUtils.Interval))
                            break;

                        continue;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse(res.Item2);
                        var root = document.RootElement;

                        if (root.TryGetProperty("rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array)
                        {
                            var followCount = 0;
                            foreach (var genre in rooms.EnumerateArray())
                            {
                                if (!genre.TryGetProperty("room_id", out var roomIdProp) ||
                                    roomIdProp.ValueKind != JsonValueKind.String) continue;
                                try
                                {
                                    var isOnline = false;
                                    var id = long.Parse(roomIdProp.GetString()!);
                                    followCount++;

                                    if (genre.TryGetProperty("is_online", out var isOnlineProp) &&
                                        isOnlineProp.ValueKind == JsonValueKind.Number)
                                        isOnline = isOnlineProp.GetInt64() != 0;

                                    if (isOnline)
                                        _roomIds.TryAdd(id, 0);
                                    else
                                        _roomIds.TryRemove(id, out _);
                                }
                                catch
                                {
                                    // 忽略非 long 的异常
                                }
                            }

                            Log.Debug("fetched follow rooms: {FollowCount}, online room_ids: {OnlineCount}",
                                followCount, _roomIds.Count);
                        }
                        else
                        {
                            Log.Warning("rooms field missing or not array");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "parse follow json error");
                    }
                }

                // 正常情况下，等待一段时间再轮询
                if (await DelayWithCancel(ConfigUtils.Interval))
                    break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "online Listen error");
                if (await DelayWithCancel(ConfigUtils.Interval))
                    break;
            }
    }

    // 提供线程安全的房间在线查询
    public Task<bool> ContainsRoomAsync(long roomId)
    {
        return Task.FromResult(_roomIds.ContainsKey(roomId));
    }

    // 获取只读快照
    public Task<IReadOnlyCollection<long>> GetRoomIdsSnapshotAsync()
    {
        IReadOnlyCollection<long> snapshot = _roomIds.Keys.ToArray();
        return Task.FromResult(snapshot);
    }

    public IReadOnlyCollection<long> GetRoomIdsSnapshot()
    {
        return _roomIds.Keys.ToArray();
    }

    public async Task Stop()
    {
        try
        {
            await _cancellationTokenSource.CancelAsync();
            await _task;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "online stop error");
        }
    }

    private async Task<bool> DelayWithCancel(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _cancellationTokenSource.Token);
            return false;
        }
        catch (TaskCanceledException)
        {
            Log.Verbose("Task.Delay 被取消");
            return true;
        }
    }
}
