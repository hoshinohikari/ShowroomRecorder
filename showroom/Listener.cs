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

    private async Task Start()
    {
        await GetId();

        if (_id == 0) return;

        Log.Debug($"get {_name} user id is {_id}");

        _isStarting = true;
        await Listen();
    }

    private async Task Listen()
    {
        while (_isStarting)
        {
            if (await IsLiving())
            {
                Log.Information($"{_name} begin living");
                _recorder = new Recorder(_name, _id);
                await _recorder.Start();
            }
            else
            {
                Log.Debug($"{_name} not living");
            }

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
    }

    public async Task Stop()
    {
        _isStarting = false;
        _recorder?.Stop();
        await _cancellationTokenSource.CancelAsync();
        await _task;
    }
}