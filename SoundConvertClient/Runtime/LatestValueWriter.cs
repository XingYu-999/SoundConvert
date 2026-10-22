using System.Threading.Channels;

namespace SoundConvertClient;

/// <summary>One in-flight write and one latest pending value. Superseding a value never cancels an active transport.</summary>
public sealed class LatestValueWriter<T>(Func<T, Task> write, Action<Exception> failed) : IAsyncDisposable
{
    readonly Channel<T> pending = Channel.CreateBounded<T>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    Task? worker;
    public void Submit(T value)
    {
        pending.Writer.TryWrite(value);
        worker ??= RunAsync();
    }
    async Task RunAsync()
    {
        while (await pending.Reader.WaitToReadAsync())
        {
            await Task.Delay(35);
            if (!pending.Reader.TryRead(out var latest)) continue;
            try { await write(latest); } catch (Exception error) { failed(error); }
        }
    }
    public async ValueTask DisposeAsync() { pending.Writer.TryComplete(); if (worker is not null) await worker; }
}
