using Serilog;
using showroom.Utils;

namespace showroom;

public class UserManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Listener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;

    public UserManager()
    {
        _task = ReconcileLoopAsync();
    }

    private async Task ReconcileLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, ConfigUtils.Config.Interval));
                await Task.Delay(delay, _cts.Token);
                Reconcile();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "用户列表 reconcile 循环异常");
            }
        }
    }

    private void Reconcile()
    {
        var desired = ConfigUtils.Config.Users;
        var toStop = new List<Listener>();

        lock (_lock)
        {
            // 新增 + 死 listener 自愈重建
            foreach (var name in desired)
            {
                if (_listeners.TryGetValue(name, out var existing))
                {
                    if (!existing.Started)
                    {
                        Log.Warning("监听用户未启动成功，重建: {User}", name);
                        _listeners.Remove(name);
                        _listeners[name] = new Listener(name);
                    }
                    continue;
                }

                Log.Information("新增监听用户: {User}", name);
                _listeners[name] = new Listener(name);
            }

            // 移除已从配置中删除的用户
            foreach (var pair in _listeners.ToList())
            {
                if (!desired.Contains(pair.Key))
                {
                    Log.Information("移除监听用户: {User}", pair.Key);
                    _listeners.Remove(pair.Key);
                    toStop.Add(pair.Value);
                }
            }
        }

        // 在锁外停止被移除的 listener
        foreach (var listener in toStop)
            _ = StopAsync(listener);
    }

    private static async Task StopAsync(Listener listener)
    {
        try
        {
            await listener.Stop();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止监听用户失败");
        }
    }

    public async Task StopAsync()
    {
        if (_cts.IsCancellationRequested)
            return;

        _cts.Cancel();

        List<Listener> all;
        lock (_lock)
        {
            all = _listeners.Values.ToList();
            _listeners.Clear();
        }

        foreach (var listener in all)
            await StopAsync(listener);

        await _task;
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}