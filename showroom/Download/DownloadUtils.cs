namespace showroom.Download;

public abstract class DownloadUtils(string name, string url)
{
    protected readonly string Name = name;
    protected readonly string Url = url;

    // 定义一个抽象的异步download方法
    public abstract Task DownloadAsync();

    public abstract void Stop();
}