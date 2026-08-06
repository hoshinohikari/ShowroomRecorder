using showroom.Download;

namespace showroom.Tests;

public sealed class MemoryFileSink(string path) : IFileSink
{
    public string FilePath { get; } = path;
    public readonly MemoryStream Data = new();
    public bool ThrowOnAppend;

    public Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (ThrowOnAppend) throw new IOException("No space left on device");
        Data.Write(data.Span);
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public byte[] ToArray() => Data.ToArray();
}

public sealed class MemorySinkFactory : IFileSinkFactory
{
    public List<MemoryFileSink> Sinks { get; } = new();
    public bool ThrowOnAppend { get; set; }
    private int _n;

    public Task<IFileSink> CreateNewAsync(CancellationToken ct)
    {
        var sink = new MemoryFileSink($"video_{_n++}.ts") { ThrowOnAppend = ThrowOnAppend };
        Sinks.Add(sink);
        return Task.FromResult<IFileSink>(sink);
    }
}