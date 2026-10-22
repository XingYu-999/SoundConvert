using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace SoundConvertClient;

/// <summary>Bounded control proxy. Owned engines stop when their client exits.</summary>
public sealed class EngineClient : IAsyncDisposable
{
    readonly Func<ProcessStartInfo, Process?>? launchEngine;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim wake = new(0, 1);
    readonly object pendingGate = new(), disposeGate = new();
    readonly Dictionary<string, (JsonElement Value, long Serial)> pending = [];
    readonly Dictionary<string, TaskCompletionSource<EngineResponse>> operations = [], requestWaiters = [];
    readonly Channel<(EngineRequest Request, TaskCompletionSource<EngineResponse> Completion)> requests = Channel.CreateBounded<(EngineRequest, TaskCompletionSource<EngineResponse>)>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    readonly EngineLifetime lifetime = new();
    bool exiting;
    Process? engine; Task? worker, disposal; long serial; int disposed;
    string expectedExecutable = Path.Combine(AppContext.BaseDirectory, "Engine", "SoundConvertEngine.exe");
    public EngineClient(Func<ProcessStartInfo, Process?>? launchEngine = null, string? engineExecutable = null)
    { this.launchEngine = launchEngine; if (engineExecutable is not null) expectedExecutable = Path.GetFullPath(engineExecutable); }
    public event Action<EngineSnapshot>? SnapshotReceived;
    public event Action<string>? ConnectionChanged;
    public event Action<OperationTicket>? OperationChanged;
    public int ProcessId { get; private set; }
    public string? InstanceId { get; private set; }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        worker ??= RunAsync();
    }
    void Wake() { if (wake.CurrentCount == 0) try { wake.Release(); } catch (SemaphoreFullException) { } }
    public void Set(string name, JsonElement value)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        lock (pendingGate)
        {
            if (pending.Count >= 64 && !pending.ContainsKey(name)) throw new InvalidOperationException("待提交设置已满，请等待后台处理。");
            pending[name] = (value, ++serial);
        }
        Wake();
    }
    public bool HasPending(string name) { lock (pendingGate) return pending.ContainsKey(name); }
    public Task<EngineResponse> SubmitAsync(EngineRequest request)
    {
        var completion = new TaskCompletionSource<EngineResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        request = request with { OperationId = request.OperationId ?? Guid.NewGuid().ToString("N"), CreatedTimestamp = request.CreatedTimestamp == 0 ? Stopwatch.GetTimestamp() : request.CreatedTimestamp };
        if (disposed != 0) completion.TrySetException(new ObjectDisposedException(nameof(EngineClient)));
        else if (!requests.Writer.TryWrite((request, completion))) completion.TrySetResult(new(false, "操作队列已满，请等待后重试。"));
        else Wake();
        return completion.Task;
    }
    public async Task<EngineResponse> RequestAsync(EngineRequest request)
    {
        if(request.Operation is not ("command" or "changeIp" or "resolveDevice" or "listen" or "stopListening" or "connect"))
            return await SubmitAsync(request).ConfigureAwait(false);
        request=request with { OperationId=request.OperationId??Guid.NewGuid().ToString("N") };
        var waiter=new TaskCompletionSource<EngineResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(pendingGate) requestWaiters.Add(request.OperationId,waiter);
        try
        {
            var reply=await SubmitAsync(request).ConfigureAwait(false);
            if(reply.Ticket is not { IsTerminal:false })return reply;
            return await waiter.Task.ConfigureAwait(false);
        }
        finally { lock(pendingGate) { requestWaiters.Remove(request.OperationId);operations.Remove(request.OperationId); } }
    }
    public Task<EngineResponse> CancelOperationAsync(string id) => SubmitAsync(new("cancel", id));
    void Observe(OperationTicket ticket)
    {
        OperationChanged?.Invoke(ticket);
        if (!ticket.IsTerminal) return;
        TaskCompletionSource<EngineResponse>? completion;
        lock (pendingGate) { operations.Remove(ticket.OperationId, out completion); }
        completion?.TrySetResult(new(ticket.State == OperationState.Completed, ticket.Error, Result: ticket.Result, Ticket: ticket));
    }
    void ClearPending(Exception error)
    {
        while (requests.Reader.TryRead(out var queued)) queued.Completion.TrySetException(error);
        lock (pendingGate)
        {
            pending.Clear();
            foreach (var completion in operations.Values.Concat(requestWaiters.Values).Distinct()) completion.TrySetResult(new(false,error.Message));
            operations.Clear();requestWaiters.Clear();
        }
    }
    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = await ConnectAsync(stop.Token).ConfigureAwait(false);
                var hello = await Exchange(pipe, new("snapshot"), stop.Token).ConfigureAwait(false);
                if (hello.Snapshot is not { } snapshot) throw new InvalidDataException(hello.Error ?? "后台未返回身份。");
                if (snapshot.Protocol != EngineProtocol.Version || snapshot.Identity is not { } identity || identity.ProtocolMin > EngineProtocol.Version || identity.ProtocolMax < EngineProtocol.Version || snapshot.Version != EngineProtocol.Release)
                    throw new InvalidDataException($"前台 {EngineProtocol.Release} 与后台 {snapshot.Version} 不兼容，请通过旧版停止后台后重新启动；已有后台保持运行。");
                var verified = EngineLifetime.ValidatePeer(pipe, snapshot, expectedExecutable);
                if (InstanceId is not null && InstanceId != identity.InstanceId) ClearPending(new IOException("后台实例已改变，旧操作和草稿已清除，请读取实际状态后重试。"));
                engine?.Dispose(); engine = verified; InstanceId = identity.InstanceId; ProcessId = snapshot.ProcessId;
                SnapshotReceived?.Invoke(snapshot); ConnectionChanged?.Invoke("");
                long nextSnapshot = Environment.TickCount64 + 200, nextOperations = 0;
                while (!stop.IsCancellationRequested)
                {
                    Dictionary<string, (JsonElement Value, long Serial)> batch;
                    lock (pendingGate) batch = new(pending);
                    if (batch.Count != 0)
                    {
                        var reply = await Exchange(pipe, new("set", Changes: batch.ToDictionary(x => x.Key, x => x.Value.Value), EngineInstanceId: InstanceId), stop.Token).ConfigureAwait(false);
                        lock (pendingGate) foreach (var change in batch) if (pending.TryGetValue(change.Key, out var current) && current.Serial == change.Value.Serial) pending.Remove(change.Key);
                        if (!reply.Ok) ConnectionChanged?.Invoke(reply.Error ?? "后台拒绝修改");
                    }
                    for (int i = 0; i < 8 && requests.Reader.TryRead(out var request); i++)
                    {
                        if (BridgeRuntime.KeyForCommand(request.Request.Name) is not null && Stopwatch.GetElapsedTime(request.Request.CreatedTimestamp).TotalMilliseconds > 200)
                        { request.Completion.TrySetResult(new(false, "媒体键已过期，未发送。")); continue; }
                        try
                        {
                            var result = await Exchange(pipe, request.Request with { EngineInstanceId = InstanceId }, stop.Token).ConfigureAwait(false);
                            if(result.Ticket is { IsTerminal:false } accepted)
                            {
                                lock(pendingGate) if(requestWaiters.TryGetValue(accepted.OperationId,out var waiter)) operations[accepted.OperationId]=waiter;
                            }
                            request.Completion.TrySetResult(result);
                            if (request.Request.Operation == "shutdown" && result.Ok) return;
                        }
                        catch (Exception error) { request.Completion.TrySetException(new IOException("操作结果未知；不会自动重发。", error)); throw; }
                    }
                    long now = Environment.TickCount64; bool haveOperations; string[] operationIds;
                    lock (pendingGate) { haveOperations = operations.Count != 0; operationIds = operations.Keys.ToArray(); }
                    if (haveOperations && now >= nextOperations)
                    {
                        var reply = await Exchange(pipe, new("operations", Value: EngineProtocol.Json(operationIds), EngineInstanceId: InstanceId), stop.Token).ConfigureAwait(false);
                        foreach (var ticket in reply.Tickets ?? []) Observe(ticket);
                        nextOperations = now + 25;
                    }
                    if (now >= nextSnapshot)
                    {
                        var reply = await Exchange(pipe, new("snapshot"), stop.Token).ConfigureAwait(false);
                        if (!reply.Ok || reply.Snapshot is null) throw new IOException(reply.Error ?? "后台状态不可用");
                        SnapshotReceived?.Invoke(reply.Snapshot); nextSnapshot = now + 200;
                    }
                    int delay = (int)Math.Clamp(Math.Min(nextSnapshot, haveOperations ? nextOperations : nextSnapshot) - Environment.TickCount64, 1, 200);
                    await wake.WaitAsync(TimeSpan.FromMilliseconds(delay), stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                ProcessId = 0; LocalStore.Log("音频后台连接异常："+error);ConnectionChanged?.Invoke(ConnectionFeedback.BackendFailure(error));
                if(exiting) break;
                ClearPending(new IOException("后台连接中断，操作结果未知；未发送操作已取消，不会重放。", error));
                try { await Task.Delay(1000, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        ClearPending(new OperationCanceledException("界面已断开后台。"));
    }
    async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        NamedPipeClientStream NewPipe() => new(".", EngineProtocol.Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var pipe = NewPipe();
        try { await pipe.ConnectAsync(300, ct).ConfigureAwait(false); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); }
        catch { pipe.Dispose(); throw; }
        if (launchEngine is null && !File.Exists(expectedExecutable)) throw new FileNotFoundException("独立后台程序缺失，请完整解压发布目录。", expectedExecutable);
        if(exiting)throw new OperationCanceledException("应用正在退出，不再启动后台。");
        var start = new ProcessStartInfo(expectedExecutable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(expectedExecutable)! };
        lifetime.Configure(start);
        engine?.Dispose(); engine = (launchEngine is not null ? launchEngine(start) : Process.Start(start)) ?? throw new IOException("后台启动失败。");
        lifetime.Track(engine);
        pipe = NewPipe();
        try
        {
            await pipe.ConnectAsync(10000, ct).ConfigureAwait(false);
            if(launchEngine is not null)
            {
                // A newly created process can have no main module until its loader is ready.
                // Resolve injected launchers only after their managed pipe server is listening.
                engine.Refresh();
                expectedExecutable=engine.MainModule?.FileName??throw new IOException("后台管道已就绪，但进程路径尚不可用。");
            }
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    static async Task<EngineResponse> Exchange(Stream pipe, EngineRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await EngineProtocol.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        return await EngineProtocol.ReadAsync<EngineResponse>(pipe, timeout.Token).ConfigureAwait(false);
    }
    public async Task ShutdownEngineAsync()
    {
        if (ProcessId == 0 || engine is null) throw new IOException("后台身份尚未验证，无法确认停止；请先恢复连接。");
        var reply = await RequestAsync(new("shutdown")).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        if (!reply.Ok) throw new IOException(reply.Error ?? "后台拒绝停止。");
        await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        ProcessId = 0;
        if(engine.ExitCode!=0)
        {
            string error=$"后台异常退出（退出码 {engine.ExitCode}），未确认正常停止，请查看后台日志。";
            ConnectionChanged?.Invoke(error);throw new IOException(error);
        }
    }
    public async Task StopForExitAsync()
    {
        // A verified existing peer may belong to another living owner. Ask it to stop,
        // but never force-terminate a peer outside this client's job.
        if(engine is not null&&ProcessId!=0&&!engine.HasExited&&!lifetime.Owns(ProcessId))await ShutdownEngineAsync();
        await DisposeAsync();
    }
    public ValueTask DisposeAsync() { lock (disposeGate) return new(disposal ??= DisposeCoreAsync()); }
    async Task DisposeCoreAsync()
    {
        exiting=true;
        try
        {
            if(engine is not null&&ProcessId!=0&&!engine.HasExited&&lifetime.Owns(ProcessId))
                await ShutdownEngineAsync();
        }
        catch(Exception error) { string message="后台未完成正常清理，将回收本应用所属后台："+error.Message; LocalStore.Log(message); ConnectionChanged?.Invoke(message); }
        Interlocked.Exchange(ref disposed, 1); requests.Writer.TryComplete(); stop.Cancel(); Wake();
        if (worker is not null) await worker.ConfigureAwait(false);
        ClearPending(new OperationCanceledException("界面已断开后台。"));
        lifetime.Dispose();
        engine?.Dispose(); engine = null; ProcessId = 0; stop.Dispose(); wake.Dispose();
    }
}
