using System.Diagnostics;
using System.Text.Json;

namespace UsbAudioBridge;

/// <summary>One bounded queue for every caller sharing the board's single TCP stream.</summary>
public sealed class ControlRequestScheduler : IAsyncDisposable
{
    sealed record Work(string Op, int Priority, long Enqueued, double QueueBudgetMs,
        Func<CancellationToken, Task<JsonElement>> Execute, CancellationToken Cancellation,
        TaskCompletionSource<JsonElement> Completion);
    readonly object gate = new();
    readonly List<Work> queue = [];
    readonly Dictionary<string, long> lastKeepalive = [];
    readonly SemaphoreSlim wake = new(0, 1);
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    long queued, completed, expired, rejected, coalesced, keepaliveWarnings, maximumQueueTicks, lastHostKey;
    long maximumKeepaliveGapTicks;
    int disposed;
    public ControlRequestScheduler() => worker = Task.Run(RunAsync);
    public object Statistics { get { lock (gate) return new { queue_depth = queue.Count, queued, completed, expired, rejected, coalesced, keepalive_queue_warnings = keepaliveWarnings, maximum_queue_ms = maximumQueueTicks * 1000d / Stopwatch.Frequency, maximum_keepalive_send_gap_ms = maximumKeepaliveGapTicks * 1000d / Stopwatch.Frequency }; } }
    public Task<JsonElement> ScheduleAsync(string op, Func<CancellationToken, Task<JsonElement>> execute, CancellationToken cancellation = default, long deadlineTimestamp = 0)
    {
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<JsonElement>(cancellation);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if ((op is "GET_STATUS" or "GET_STATS") && queue.FirstOrDefault(x => x.Op == op) is { } existing)
            { coalesced++; return existing.Completion.Task.WaitAsync(cancellation); }
            if (queue.Count >= 128) { rejected++; return Task.FromException<JsonElement>(new InvalidOperationException("开发板控制队列已满，未发送操作。")); }
            bool critical = op.Contains("KEEPALIVE", StringComparison.Ordinal) || op.StartsWith("STREAM_", StringComparison.Ordinal) || op.StartsWith("CAPTURE_", StringComparison.Ordinal);
            int priority = critical ? 0 : op == "GET_STATS" ? 3 : op == "GET_STATUS" ? 2 : 1;
            double budget = op == "USB_HOST_KEY" ? 200 : op == "GET_STATS" ? 200 : op == "GET_STATUS" ? 500 : critical ? 1800 : 3000;
            if (deadlineTimestamp != 0) budget = Math.Min(budget, (deadlineTimestamp - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add(new(op, priority, Stopwatch.GetTimestamp(), budget, execute, cancellation, completion)); queued++;
            if (wake.CurrentCount == 0) try { wake.Release(); } catch (SemaphoreFullException) { }
            return completion.Task;
        }
    }
    async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await wake.WaitAsync(stop.Token).ConfigureAwait(false);
                while (true)
                {
                    Work work;
                    lock (gate)
                    {
                        if (queue.Count == 0) break;
                        work = queue.OrderBy(x => x.Priority).ThenBy(x => x.Enqueued).First(); queue.Remove(work);
                    }
                    try
                    {
                        work.Cancellation.ThrowIfCancellationRequested();
                        if (work.Op == "USB_HOST_KEY" && lastHostKey != 0)
                        {
                            double remaining = 30 - Stopwatch.GetElapsedTime(lastHostKey).TotalMilliseconds;
                            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), stop.Token).ConfigureAwait(false);
                        }
                        long waited = Stopwatch.GetTimestamp() - work.Enqueued;
                        lock (gate)
                        {
                            maximumQueueTicks = Math.Max(maximumQueueTicks, waited);
                            if (work.Op.Contains("KEEPALIVE", StringComparison.Ordinal) && waited * 1000d / Stopwatch.Frequency >= 500) keepaliveWarnings++;
                        }
                        if (waited * 1000d / Stopwatch.Frequency > work.QueueBudgetMs)
                        { Interlocked.Increment(ref expired); throw new ControlRequestExpiredException(work.Op); }
                        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, work.Cancellation);
                        if (work.Op == "USB_HOST_KEY") lastHostKey = Stopwatch.GetTimestamp();
                        if (work.Op.Contains("KEEPALIVE", StringComparison.Ordinal))
                        {
                            long sent = Stopwatch.GetTimestamp();
                            lock (gate) { if (lastKeepalive.TryGetValue(work.Op, out long previous)) maximumKeepaliveGapTicks = Math.Max(maximumKeepaliveGapTicks, sent - previous); lastKeepalive[work.Op] = sent; }
                        }
                        var response = await work.Execute(cancellation.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref completed); work.Completion.TrySetResult(response);
                    }
                    catch (OperationCanceledException) { work.Completion.TrySetCanceled(work.Cancellation.IsCancellationRequested ? work.Cancellation : stop.Token); }
                    catch (Exception error) { work.Completion.TrySetException(error); }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { lock (gate) { foreach (var work in queue) work.Completion.TrySetCanceled(); queue.Clear(); } }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); await worker.ConfigureAwait(false); stop.Dispose(); wake.Dispose();
    }
}
public sealed class ControlRequestExpiredException(string op) : InvalidOperationException($"{op} 排队已过期，未发送；可读取实际状态后重试。")
{
    public string Operation { get; } = op;
}
