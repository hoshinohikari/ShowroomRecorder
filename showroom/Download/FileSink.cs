namespace showroom.Download;

/// <summary>录制文件写入接缝：append/flush/close。测试用内存实现替换。</summary>
public interface IFileSink : IAsyncDisposable
{
    string FilePath { get; }
    Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    Task FlushAsync(CancellationToken ct = default);
}

/// <summary>文件 sink。磁盘满（IOException + "No space left on device"）向上抛，由 merger 判为 Aborted。</summary>
public sealed class FileSink(string path) : IFileSink
{
    private readonly FileStream _stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    public string FilePath { get; } = path;

    public Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _stream.WriteAsync(data, ct).AsTask();

    public Task FlushAsync(CancellationToken ct = default) => _stream.FlushAsync(ct);

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

/// <summary>按需创建新录制文件 sink（滚动时由 merger 调用）。命名与注册由实现方负责。</summary>
public interface IFileSinkFactory
{
    Task<IFileSink> CreateNewAsync(CancellationToken ct);
}