using System.Net;
using System.Text.Json;
using Serilog;
using showroom.Utils;

namespace showroom;

public class Listener
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly string _name;
    private readonly Task _task;
    private long _id;
    private bool _isStarting;
    private Recorder? _recorder;
    private DateTime _lastApiCheck = DateTime.UtcNow;
    private TimeSpan _apiCheckInterval = TimeSpan.Zero;

    public Listener(string name)
    {
        _name = name;
        Log.Information($"start listen to {name}");
        _task = Start();
    }

    private async Task GetId()
    {
        var res = await ShowroomHttp.Get("api/room/status", [("room_url_key", $"{_name}")]);

        if (res.Item1 == HttpStatusCode.NotFound)
        {
            Log.Warning($"user {_name} not found");
            return;
        }

        Log.Debug($"GetId \"{res.Item2}\"");

        using var document = JsonDocument.Parse(res.Item2);
        var root = document.RootElement;

        _id = root.GetProperty("room_id").GetInt64();
    }

    private async Task<bool> IsLiving()
    {
        var res = await ShowroomHttp.Get("api/live/live_info", [("room_id", $"{_id}")]);

        Log.Debug($"IsLiving \"{res.Item2}\"");

        using var document = JsonDocument.Parse(res.Item2);
        var root = document.RootElement;

        var status = root.GetProperty("live_status").GetInt64();

        return status == 2;
    }

    // 通过全局 Online 单例的 _roomIds 判断是否在线（异步）
    private async Task<bool> IsLiving2()
    {
        return await Online.Instance.ContainsRoomAsync(_id);
    }

    private async Task Start()
    {
        await GetId();

        if (_id == 0) return;

        Log.Debug($"get {_name} user id is {_id}");

        // 初始化随机接口检查间隔（20~40 倍配置间隔）
        _apiCheckInterval = GetRandomApiInterval();

        _isStarting = true;
        await Listen();
    }

    private async Task Listen()
    {
        while (_isStarting)
        {
            try
            {
                Log.Debug($"{_name} test living (cache first)");

                var living = await IsLiving2();

                // 到达随机接口检查时间时，进行一次接口校验，避免同时触发
                if (!living && ShouldApiCheck())
                    living = await IsLiving();

                if (living)
                {
                    Log.Information($"{_name} begin living");
                    _recorder = new Recorder(_name, _id);
                    await _recorder.Start();
                    continue;
                }

                Log.Debug($"{_name} not living");

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
                Log.Error($"{_name} Listen error: {ex}");

                // 在发生异常时等待一段时间后再重新访问
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

        Log.Warning($"{_name} stop");
    }

    private bool ShouldApiCheck()
    {
        var now = DateTime.UtcNow;
        if (now - _lastApiCheck >= _apiCheckInterval)
        {
            _lastApiCheck = now;
            _apiCheckInterval = GetRandomApiInterval();
            Log.Debug($"{_name} trigger api check, next after {_apiCheckInterval.TotalSeconds:F0}s");
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
            _isStarting = false;
            if (_recorder != null)
                await _recorder?.Stop()!;
            await _cancellationTokenSource.CancelAsync();
            await _task;
        }
        catch (Exception ex)
        {
            Log.Error($"{_name} Listen stop error: {ex}");
        }
    }
}