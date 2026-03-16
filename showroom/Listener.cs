using System.Text.Json;
using Serilog;
using showroom.Utils;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace showroom;

public class Listener
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly string _name;
    private readonly Task _task;
    private TimeSpan _apiCheckInterval = TimeSpan.Zero;
    private long _id;
    private DateTime _lastApiCheck = DateTime.UtcNow;
    private Recorder? _recorder;

    public Listener(string name)
    {
        _name = name;
        Log.Information("start listen to {Name}", name);
        _task = Start();
    }

    private async Task<(bool Success, string Error)> TryGetId()
    {
        Log.Verbose("{Name} resolving room id", _name);
        var res = await ShowroomHttp.Get("api/room/status", [("room_url_key", $"{_name}")]);

        if (res.Item1 == HttpStatusCode.NotFound)
            return (false, "user not found");

        if (res.Item1 != HttpStatusCode.OK || string.IsNullOrWhiteSpace(res.Item2))
            return (false, $"status: {res.Item1}");

        try
        {
            using var document = JsonDocument.Parse(res.Item2);
            var root = document.RootElement;

            if (!root.TryGetProperty("room_id", out var roomIdProp) || roomIdProp.ValueKind != JsonValueKind.Number)
                return (false, "room_id missing in response");

            _id = roomIdProp.GetInt64();
            Log.Verbose("{Name} resolved room id={RoomId}", _name, _id);
            return _id == 0 ? (false, "room_id is 0") : (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"parse error: {ex.Message}");
        }
    }

    private async Task<bool> IsLiving()
    {
        Log.Verbose("{Name} querying live_info by API for room {RoomId}", _name, _id);
        var res = await ShowroomHttp.Get("api/live/live_info", [("room_id", $"{_id}")]);

        if (res.Item1 != HttpStatusCode.OK || string.IsNullOrWhiteSpace(res.Item2))
        {
            Log.Warning("IsLiving failed: {Status}", res.Item1);
            return false;
        }

        using var document = JsonDocument.Parse(res.Item2);
        var root = document.RootElement;

        if (!root.TryGetProperty("live_status", out var statusProp) || statusProp.ValueKind != JsonValueKind.Number)
        {
            Log.Warning("{Name} live_info missing live_status", _name);
            return false;
        }

        var status = statusProp.GetInt64();

        var living = status == 2;
        Log.Verbose("{Name} live_info status={Status}, living={Living}", _name, status, living);
        return living;
    }

    // 通过全局 Online 单例的 _roomIds 判断是否在线（异步）
    private async Task<bool> IsLiving2()
    {
        var result = await Online.Instance.ContainsRoomAsync(_id);
        Log.Verbose("{Name} cache living={Living}", _name, result);
        return result;
    }

    private async Task Start()
    {
        var (success, error) = await TryGetId();

        if (!success)
        {
            Log.Error("{Name} listener initialization failed, listen not started: {Error}", _name, error);
            return;
        }

        Log.Debug("get {Name} user id is {Id}", _name, _id);

        // 初始化随机接口检查间隔（20~40 倍配置间隔）
        _apiCheckInterval = GetRandomApiInterval();
        Log.Debug("{Name} api check interval initialized: {Seconds:F0}s", _name, _apiCheckInterval.TotalSeconds);

        await Listen();
    }

    private async Task Listen()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
            try
            {
                var living = await IsLiving2();
                Log.Verbose("{Name} living cache result={Living}", _name, living);

                // 到达随机接口检查时间时，进行一次接口校验，避免同时触发
                if (!living && ShouldApiCheck())
                {
                    Log.Debug("{Name} cache miss, trigger API living check", _name);
                    living = await IsLiving();
                }

                if (living)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                        break;

                    Log.Information("{Name} begin living", _name);
                    _recorder = new Recorder(_name, _id);
                    Log.Debug("{Name} recorder instance created", _name);
                    await _recorder.Start();

                    if (_cancellationTokenSource.IsCancellationRequested)
                        break;

                    continue;
                }

                if (await DelayWithCancel(ConfigUtils.Interval))
                    break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Name} Listen error", _name);

                // 在发生异常时等待一段时间后再重新访问
                if (await DelayWithCancel(ConfigUtils.Interval))
                    break;
            }

        Log.Warning("{Name} stop", _name);
    }

    private bool ShouldApiCheck()
    {
        var now = DateTime.UtcNow;
        if (now - _lastApiCheck >= _apiCheckInterval)
        {
            _lastApiCheck = now;
            _apiCheckInterval = GetRandomApiInterval();
            Log.Verbose("{Name} trigger api check, next after {Seconds:F0}s", _name,
                _apiCheckInterval.TotalSeconds);
            return true;
        }

        return false;
    }

    private static TimeSpan GetRandomApiInterval()
    {
        try
        {
            var baseSec = Math.Max(1, ConfigUtils.Config.Interval);
            var multiplier = Random.Shared.Next(20, 41); // 20~40（含40）
            return TimeSpan.FromSeconds(baseSec * multiplier);
        }
        catch
        {
            // 兜底：默认 30 倍 1 秒
            return TimeSpan.FromSeconds(30);
        }
    }

    public async Task Stop()
    {
        try
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                Log.Debug("{Name} listener stop requested", _name);
                await _cancellationTokenSource.CancelAsync();
            }
            else
            {
                Log.Verbose("{Name} listener stop called repeatedly", _name);
            }

            if (_recorder != null)
                await _recorder.Stop();

            await _task;
            Log.Debug("{Name} listener stopped", _name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Name} Listen stop error", _name);
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