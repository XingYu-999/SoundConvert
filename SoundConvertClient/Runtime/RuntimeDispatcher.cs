using System.Collections.Concurrent;
using System.Diagnostics;

namespace SoundConvertClient;

/// <summary>Serializes engine state without a window, UI message pump, or Avalonia.</summary>
public sealed class RuntimeDispatcher : SynchronizationContext, IDisposable
{
    readonly record struct Work(SendOrPostCallback Callback,object? State,string Label,long Enqueued,RuntimeExecutionSample EnqueuedSample);
    readonly BlockingCollection<Work> queue = new();
    readonly RuntimeCallbackTrace?[] recent=new RuntimeCallbackTrace[128],slow=new RuntimeCallbackTrace[32];
    readonly bool traceEnabled;
    RuntimeCallbackTrace? active;long completed,slowCount,maximumQueueTicks,maximumCallbackTicks;
    readonly Thread thread;
    public RuntimeDispatcher(bool? diagnostics=null)
    {
        traceEnabled=diagnostics??Environment.GetEnvironmentVariable("UAB_ENGINE_DIAGNOSTICS")=="1";
        thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach(var item in queue.GetConsumingEnumerable())
            {
                if(!traceEnabled){item.Callback(item.State);continue;}
                long started=Stopwatch.GetTimestamp();var sample=RuntimeExecutionSample.Capture();
                var trace=new RuntimeCallbackTrace(item.Label,item.Enqueued,started,0,item.EnqueuedSample,sample,default);
                Volatile.Write(ref active,trace);
                try{item.Callback(item.State);}
                finally
                {
                    long ended=Stopwatch.GetTimestamp();trace=trace with{CompletedTimestamp=ended,CompletedSample=RuntimeExecutionSample.Capture()};
                    long sequence=Interlocked.Increment(ref completed)-1;Volatile.Write(ref recent[(int)(sequence%recent.Length)],trace);
                    maximumQueueTicks=Math.Max(maximumQueueTicks,started-item.Enqueued);maximumCallbackTicks=Math.Max(maximumCallbackTicks,ended-started);
                    if(Stopwatch.GetElapsedTime(started,ended).TotalMilliseconds>=10){long slot=Interlocked.Increment(ref slowCount)-1;Volatile.Write(ref slow[(int)(slot%slow.Length)],trace);}
                    Volatile.Write(ref active,null);
                }
            }
        })
            { IsBackground = true, Name = "UAB engine state" };
        thread.Start();
    }
    public override void Post(SendOrPostCallback callback, object? state)
        =>PostNamed(traceEnabled?callback.Method.DeclaringType?.FullName+"."+callback.Method.Name:"",callback,state);
    public void PostNamed(string label,SendOrPostCallback callback,object? state)
    { if (!queue.IsAddingCompleted) try { queue.Add(new(callback,state,label,traceEnabled?Stopwatch.GetTimestamp():0,traceEnabled?RuntimeExecutionSample.Capture():default)); } catch (InvalidOperationException) { } }
    public object Diagnostics=>new{enabled=traceEnabled,clock_frequency=Stopwatch.Frequency,thread_id=thread.ManagedThreadId,thread_alive=thread.IsAlive,queue_depth=queue.Count,completed,slow_callbacks=slowCount,maximum_queue_ms=maximumQueueTicks*1000d/Stopwatch.Frequency,maximum_callback_ms=maximumCallbackTicks*1000d/Stopwatch.Frequency,active=Volatile.Read(ref active),recent=Enumerable.Range(0,recent.Length).Select(i=>Volatile.Read(ref recent[i])).Where(x=>x is not null).OrderBy(x=>x!.StartedTimestamp).ToArray(),slow=Enumerable.Range(0,slow.Length).Select(i=>Volatile.Read(ref slow[i])).Where(x=>x is not null).OrderBy(x=>x!.StartedTimestamp).ToArray(),sample=RuntimeExecutionSample.Capture()};
    public Task<T> InvokeAsync<T>(Func<Task<T>> action,string? label=null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostNamed(label??"InvokeAsync",async _ => { try { completion.SetResult(await action()); } catch (Exception error) { completion.SetException(error); } }, null);
        return completion.Task;
    }
    public Task InvokeAsync(Func<Task> action) => InvokeAsync(async () => { await action(); return true; });
    public void Dispose() { queue.CompleteAdding(); thread.Join(3000); }
}

public readonly record struct RuntimeExecutionSample(int Gen0,int Gen1,int Gen2,long GcPauseTicks,int ThreadPoolThreads,long PendingWorkItems,int AvailableWorkers,int AvailableIo)
{
    public static RuntimeExecutionSample Capture(){ThreadPool.GetAvailableThreads(out int workers,out int io);return new(GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),GC.GetTotalPauseDuration().Ticks,ThreadPool.ThreadCount,ThreadPool.PendingWorkItemCount,workers,io);}
}
public sealed record RuntimeCallbackTrace(string Label,long EnqueuedTimestamp,long StartedTimestamp,long CompletedTimestamp,RuntimeExecutionSample EnqueuedSample,RuntimeExecutionSample StartedSample,RuntimeExecutionSample CompletedSample);

public sealed class RuntimeTimer
{
    readonly SynchronizationContext context = SynchronizationContext.Current ?? throw new InvalidOperationException("Runtime timers require an engine context");
    Timer? timer;
    int posted;
    public TimeSpan Interval { get; set; }
    public event EventHandler? Tick;
    public void Start() => timer ??= new Timer(_ =>
    {
        if (Interlocked.Exchange(ref posted, 1) != 0) return;
        context.Post(_ => { Interlocked.Exchange(ref posted, 0); if (timer is not null) Tick?.Invoke(this, EventArgs.Empty); }, null);
    }, null, Interval, Interval);
    public void Stop() { var active = timer; timer = null; active?.Dispose(); }
}
