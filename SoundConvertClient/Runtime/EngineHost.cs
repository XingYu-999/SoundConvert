using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;
using System.Diagnostics;

namespace SoundConvertClient;

public sealed class EngineHost
{
    static readonly PropertyInfo[] scalars = typeof(BridgeRuntime).GetProperties().Where(p =>
        p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string) ||
        p.PropertyType == typeof(string[]) || p.PropertyType == typeof(IReadOnlyList<string>)).ToArray();
    static readonly Dictionary<string, PropertyInfo> setters = scalars.Where(p => p.SetMethod?.IsPublic == true).ToDictionary(p => p.Name);
    static readonly Dictionary<string, PropertyInfo> commands = typeof(BridgeRuntime).GetProperties()
        .Where(p => p.PropertyType == typeof(ICommand)).ToDictionary(p => p.Name);
    readonly RuntimeDispatcher dispatcher;
    readonly BridgeRuntime runtime;
    readonly CancellationTokenSource stopped = new();
    readonly List<Task> clients = [];
    readonly EngineIpcTrace?[] ipcTraces=new EngineIpcTrace[128];long ipcSequence;
    readonly bool traceEnabled=Environment.GetEnvironmentVariable("UAB_ENGINE_DIAGNOSTICS")=="1";
    readonly Dictionary<string, (OperationTicket Ticket, CancellationTokenSource Cancellation)> operations = [];
    readonly EngineIdentity identity = new(Guid.NewGuid().ToString("N"), EngineProtocol.Version, EngineProtocol.Version,
        Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Process.GetCurrentProcess().SessionId);
    public EngineHost(RuntimeDispatcher dispatcher, BridgeRuntime runtime) { this.dispatcher = dispatcher; this.runtime = runtime; }
    public async Task RunAsync()
    {
        while (!stopped.IsCancellationRequested)
        {
            clients.RemoveAll(t => t.IsCompleted);
            // Eight connected clients already consume every configured pipe instance.
            // Waiting for a slot also bounds clients whose response is not being read.
            if (clients.Count >= 8)
            {
                try { await Task.WhenAny(clients).WaitAsync(stopped.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            var pipe = new NamedPipeServerStream(EngineProtocol.Name, PipeDirection.InOut, 8,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(stopped.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            clients.RemoveAll(t => t.IsCompleted);
            clients.Add(ServeAsync(pipe));
        }
        await Task.WhenAll(clients).ConfigureAwait(false);
    }
    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        var ownedCancelableOperations=new HashSet<string>();
        using (pipe)
        try
        {
            while (!stopped.IsCancellationRequested && pipe.IsConnected)
            {
                EngineReadTiming? readTiming=null;
                EngineRequest request = await EngineProtocol.ReadAsync<EngineRequest>(pipe, stopped.Token,traceEnabled?t=>readTiming=t:null).ConfigureAwait(false);
                long dispatched=Stopwatch.GetTimestamp(),handlerStarted=0,handlerEnded=0;
                EngineResponse reply;
                try { reply = await dispatcher.InvokeAsync(async()=>
                {
                    handlerStarted=Stopwatch.GetTimestamp();
                    try{ownedCancelableOperations.RemoveWhere(id=>!operations.TryGetValue(id,out var entry)||entry.Ticket.IsTerminal);return await HandleAsync(request,ownedCancelableOperations);}
                    finally{handlerEnded=Stopwatch.GetTimestamp();}
                },"IPC "+request.Operation+" "+request.Name).ConfigureAwait(false); }
                catch (Exception error) { reply = new(false, error.GetBaseException().Message); }
                long replyStarted=Stopwatch.GetTimestamp();
                try{await EngineProtocol.WriteAsync(pipe, reply, stopped.Token).ConfigureAwait(false);}
                finally
                {
                    if(traceEnabled)
                    {
                        var trace=new EngineIpcTrace(request.OperationId,request.Operation,request.Name,request.CreatedTimestamp,readTiming,dispatched,handlerStarted,handlerEnded,replyStarted,Stopwatch.GetTimestamp(),RuntimeExecutionSample.Capture());
                        long slot=Interlocked.Increment(ref ipcSequence)-1;Volatile.Write(ref ipcTraces[(int)(slot%ipcTraces.Length)],trace);
                    }
                }
                if (request.Operation == "shutdown" && reply.Ok) { stopped.Cancel(); break; }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
        finally
        {
            await dispatcher.InvokeAsync(()=>
            {
                foreach(string id in ownedCancelableOperations)
                    if(operations.TryGetValue(id,out var entry) && !entry.Ticket.IsTerminal)entry.Cancellation.Cancel();
                return Task.CompletedTask;
            });
        }
    }
    public EngineSnapshot Snapshot(bool includeDiagnostics=false)
    {
        long started=Stopwatch.GetTimestamp(),scalarTicks=0,commandTicks=0,maxScalar=0,maxCommand=0;
        string? slowScalar=null,slowCommand=null;
        var values=new Dictionary<string,JsonElement>();var enabled=new Dictionary<string,bool>();
        foreach(var scalar in scalars)
        {
            long before=Stopwatch.GetTimestamp();values[scalar.Name]=JsonSerializer.SerializeToElement(scalar.GetValue(runtime),scalar.PropertyType);
            long elapsed=Stopwatch.GetTimestamp()-before;scalarTicks+=elapsed;if(elapsed>maxScalar){maxScalar=elapsed;slowScalar=scalar.Name;}
        }
        foreach(var command in commands)
        {
            long before=Stopwatch.GetTimestamp();enabled[command.Key]=((ICommand)command.Value.GetValue(runtime)!).CanExecute(null);
            long elapsed=Stopwatch.GetTimestamp()-before;commandTicks+=elapsed;if(elapsed>maxCommand){maxCommand=elapsed;slowCommand=command.Key;}
        }
        if(includeDiagnostics)
        {
            values["DispatcherDiagnostics"]=EngineProtocol.Json(JsonSerializer.Serialize(dispatcher.Diagnostics));
            values["IpcDiagnostics"]=EngineProtocol.Json(JsonSerializer.Serialize(new{enabled=traceEnabled,clock_frequency=Stopwatch.Frequency,requests=Enumerable.Range(0,ipcTraces.Length).Select(i=>Volatile.Read(ref ipcTraces[i])).Where(x=>x is not null).OrderBy(x=>x!.DispatchedTimestamp).ToArray()}));
            values["LogDiagnostics"]=EngineProtocol.Json(JsonSerializer.Serialize(LocalStore.LogDiagnostics));
            values["ClockObservationDiagnostics"]=EngineProtocol.Json(runtime.ReadClockEvidence());
            values["ManagedAssemblyIdentity"]=EngineProtocol.Json(JsonSerializer.Serialize(AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic&&a.Location.Length!=0).Select(a=>new{name=a.GetName().Name,version=a.GetName().Version?.ToString(),path=a.Location}).ToArray()));
        }
        var snapshot=new EngineSnapshot(EngineProtocol.Version, EngineProtocol.Release, Environment.ProcessId,values,enabled,
        runtime.Devices.ToArray(), runtime.Outputs.ToArray(), runtime.Inputs.ToArray(), runtime.SearchAdapters.ToArray(),
        runtime.RememberedBoards.Select(r => r.Board).ToArray(), runtime.LogEntries.ToArray(),
        runtime.FormatOptions.Select(f => new FormatState(f.Bit, f.Selected, f.CanSelect, f.Hint)).ToArray(),
        runtime.CaptureFormatOptions.Select(f => new FormatState(f.Bit, f.Selected, f.CanSelect, f.Hint)).ToArray(),
        runtime.SelectedOutput?.Id, runtime.SelectedInput?.Id,
        runtime.SelectedSearchAdapter is { } adapter ? EngineProtocol.AdapterKey(adapter) : null,
        runtime.SelectedDevice is { } device ? EngineProtocol.DeviceKey(device) : null, identity,
        operations.Values.Select(x => x.Ticket).ToArray());
        double milliseconds=1000d/Stopwatch.Frequency;
        return snapshot with{Timing=new((Stopwatch.GetTimestamp()-started)*milliseconds,scalarTicks*milliseconds,commandTicks*milliseconds,slowScalar,maxScalar*milliseconds,slowCommand,maxCommand*milliseconds)};
    }

    async Task<EngineResponse> HandleAsync(EngineRequest request, HashSet<string> owner)
    {
        if (request.Operation != "snapshot" && request.EngineInstanceId != identity.InstanceId)
            throw new InvalidOperationException("后台实例已改变或尚未握手，操作未发送。");
        if(request.Operation=="command" && request.Name is "CancelConnectionCommand" or "CancelSearchCommand")
        {
            bool connecting=request.Name=="CancelConnectionCommand";
            foreach(var entry in operations.Values.Where(x=>!x.Ticket.IsTerminal && (connecting ? x.Ticket.Kind=="connect" || x.Ticket.Target is "ConnectCommand" or "QuickConnectCommand" or "ConnectRememberedCommand" or "ToggleConnectionCommand" : x.Ticket.Target is "DiscoverCommand" or "QuickSearchCommand" or "DiscoverBroadcastCommand")).ToArray()) entry.Cancellation.Cancel();
            if(connecting)runtime.CancelConnection();else runtime.CancelSearch();
            return new(true,Result:"已请求取消，等待任务结束。");
        }
        if (request.Operation is "command" or "changeIp" or "resolveDevice" or "listen" or "stopListening" or "connect") return QueueOperation(request,owner);
        switch (request.Operation)
        {
            case "snapshot": return new(true, Snapshot: Snapshot(request.Name=="diagnostics"));
            case "operations":
                var ids = request.Value.ValueKind == JsonValueKind.Array ? request.Value.Deserialize<string[]>() : null;
                if (ids is null) return new(true, Tickets: operations.Values.Select(x => x.Ticket).ToArray());
                return new(true, Tickets: ids.Distinct().Take(128).Select(id => operations.TryGetValue(id, out var known) ? known.Ticket :
                    new OperationTicket(id, identity.InstanceId, "unknown", null, OperationState.Unknown, "操作记录已过期，结果未知；请读取当前状态，不会重发操作。", DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow, CompletedTimestamp: Stopwatch.GetTimestamp())).ToArray());
            case "operation": return operations.TryGetValue(request.Name ?? "", out var entry) ? new(true, Ticket: entry.Ticket) : new(false, "找不到操作，结果未知。请读取当前状态。");
            case "cancel":
                if (!operations.TryGetValue(request.Name ?? "", out var cancel)) return new(false, "操作不存在或记录已过期。");
                if (cancel.Ticket.IsTerminal) return new(true, Result: "操作已经结束，取消未改变当前连接。", Ticket: cancel.Ticket);
                if (cancel.Ticket.State != OperationState.Queued && cancel.Ticket.Kind != "connect" && cancel.Ticket.Target is not ("DiscoverCommand" or "QuickSearchCommand" or "DiscoverBroadcastCommand")) return new(false, "操作已发送，不能安全取消；请等待结果。", Ticket: cancel.Ticket);
                cancel.Cancellation.Cancel(); return new(true, Result: "已请求取消，等待操作确认。", Ticket: cancel.Ticket);
            case "set":
                foreach (var change in request.Changes ?? []) Set(change.Key, change.Value);
                return new(true);
            case "format":
                uint bit = request.Value.GetProperty("bit").GetUInt32(); bool selected = request.Value.GetProperty("selected").GetBoolean();
                if (request.Name == "capture") { var f = runtime.CaptureFormatOptions.Single(f => f.Bit == bit); if (!f.CanSelect) throw new InvalidOperationException("格式不可修改"); f.Selected = selected; }
                else { var f = runtime.FormatOptions.Single(f => f.Bit == bit); if (!f.CanSelect) throw new InvalidOperationException("格式不可修改"); f.Selected = selected; }
                return new(true);
            case "shutdown": return new(true);
            default: throw new InvalidOperationException("Unknown engine operation");
        }
    }
    EngineResponse QueueOperation(EngineRequest request, HashSet<string> owner)
    {
        string id = request.OperationId ?? Guid.NewGuid().ToString("N");
        if (operations.TryGetValue(id, out var existing)) return new(true, Ticket: existing.Ticket);
        if (operations.Values.Count(x => !x.Ticket.IsTerminal) >= 64) return new(false, "后台操作队列已满，未受理。");
        foreach (string old in operations.Where(x => x.Value.Ticket.IsTerminal).OrderBy(x => x.Value.Ticket.CreatedAt).Take(Math.Max(0, operations.Count - 127)).Select(x => x.Key).ToArray())
        { operations[old].Cancellation.Dispose(); operations.Remove(old); }
        var ticket = new OperationTicket(id, identity.InstanceId, request.Operation, request.Name, OperationState.Queued, null, DateTimeOffset.UtcNow, ReceivedTimestamp: Stopwatch.GetTimestamp());
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
        operations.Add(id, (ticket, cancellation));
        if(request.Operation=="connect" || request.Operation=="command" && request.Name is "DiscoverCommand" or "DiscoverBroadcastCommand" or "QuickSearchCommand" or "ConnectCommand" or "ConnectRememberedCommand" or "QuickConnectCommand" or "ToggleConnectionCommand")owner.Add(id);
        dispatcher.PostNamed("operation "+request.Operation+" "+request.Name,async _ => await ExecuteOperationAsync(request, id, cancellation.Token), null);
        return new(true, Ticket: ticket);
    }
    async Task ExecuteOperationAsync(EngineRequest request, string id, CancellationToken cancellation)
    {
        using var correlation = new UsbAudioBridge.ControlOperationContext(id);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var ticket = operations[id].Ticket with { State = OperationState.Executing, ExecutingTimestamp = Stopwatch.GetTimestamp() };
            operations[id] = (ticket, operations[id].Cancellation);
            string? result = null;
            switch (request.Operation)
            {
                case "command":
                    if (request.Name is null || !commands.TryGetValue(request.Name, out var property)) throw new InvalidOperationException("Unknown engine command");
                    if (BridgeRuntime.KeyForCommand(request.Name) is { } hostKey)
                    { result = await runtime.SendHostKeyAsync(hostKey, request.CreatedTimestamp); break; }
                    if (request.Name is "ExitCommand" or "ShutdownEngineCommand" or "HideCommand" or "OpenDataCommand" or "OpenWindowsSoundCommand" or "OpenReferenceCommand" or "ModifyDiscoveredIpCommand" or "ManualConnectCommand" or "ConfigureDeviceCommand")
                        throw new InvalidOperationException("This command belongs to the UI");
                    if(request.Name=="SaveUdpPortsCommand"&&request.Value.ValueKind==JsonValueKind.Object)
                    {
                        var ports=request.Value.Deserialize<UdpPortDraft>()??throw new ArgumentException("缺少 UDP 端口设置。");
                        await runtime.ExecuteOperationAsync(()=>runtime.SaveUdpPortsAsync(ports));break;
                    }
                    object? parameter = request.Value.ValueKind == JsonValueKind.String ? request.Value.GetString() : null;
                    if (request.Name is "ConnectRememberedCommand" or "ForgetBoardCommand") parameter = runtime.RememberedBoards.Single(r => r.Board.DeviceId == (string?)parameter);
                    if (!((ICommand)property.GetValue(runtime)!).CanExecute(parameter)) throw new InvalidOperationException("当前状态不允许此操作");
                    await runtime.InvokeCommandAsync(request.Name, parameter, cancellation); break;
                case "changeIp":
                    var value = request.Value;
                    result = await runtime.ChangeDiscoveredIpAsync(value.GetProperty("device").Deserialize<UsbAudioBridge.Device>()!, value.GetProperty("dhcp").GetBoolean(), value.GetProperty("ip").GetString()!, value.GetProperty("prefix").GetInt32(), value.GetProperty("gateway").GetString()!); break;
                case "connect":
                    var connect = request.Value.Deserialize<ConnectRequest>() ?? throw new ArgumentException("连接目标缺失。");
                    if (connect.DiscoveryPath is not null && connect.DiscoveryGeneration is { } generation)
                        runtime.ValidateDiscoveredTarget(connect.ExpectedDeviceId!, connect.DiscoveryPath, generation, connect.AdapterKey);
                    string connectTarget = runtime.Devices.FirstOrDefault(row => row.Device.DeviceId == connect.ExpectedDeviceId)?.Name ?? connect.ExpectedDeviceId ?? connect.Address;
                    await runtime.ExecuteOperationAsync(() => runtime.ConnectTargetAsync(connect.Address, connect.Port, connect.AdapterKey, connect.ExpectedDeviceId, cancellation), $"正在连接 {connectTarget}…");
                    if (!runtime.IsConnected) cancellation.ThrowIfCancellationRequested(); break;
                case "resolveDevice":
                    var target = request.Value.GetProperty("device").Deserialize<UsbAudioBridge.Device>()!;
                    string adapterKey = request.Value.GetProperty("adapterKey").GetString()!;
                    if (request.Value.TryGetProperty("discoveryGeneration", out var discoveryGeneration)) runtime.ValidateDiscoveredTarget(target.DeviceId, target.LocalAddress, discoveryGeneration.GetInt64(), adapterKey);
                    await runtime.ExecuteOperationAsync(async () =>
                    {
                        result = JsonSerializer.Serialize(await runtime.ResolveDeviceForConfigurationAsync(target, adapterKey));
                        if (request.Value.TryGetProperty("discoveryGeneration", out var finalGeneration)) runtime.ValidateDiscoveredTarget(target.DeviceId, target.LocalAddress, finalGeneration.GetInt64(), adapterKey);
                    }, $"正在打开 {ConnectionFeedback.Target(target.Model, target.DeviceId)} 的 IP 设置，核对UDP 广播路径…"); break;
                case "listen": await runtime.ExecuteOperationAsync(runtime.Listen); break;
                case "stopListening": await runtime.ExecuteOperationAsync(runtime.StopListening); break;
            }
            Finish(id, OperationState.Completed, null, result);
        }
        catch (OperationCanceledException) { Finish(id, OperationState.Canceled, "操作已取消。"); }
        catch (UsbAudioBridge.ControlRequestExpiredException error) { Finish(id, OperationState.Canceled, error.Message); }
        catch (Exception error)
        {
            bool readOnly=request.Operation is "connect" or "resolveDevice" || request.Name is "DiscoverCommand" or "DiscoverBroadcastCommand" or "QuickSearchCommand";
            Finish(id,!readOnly && error is IOException or TimeoutException ? OperationState.Unknown : OperationState.Failed,error.GetBaseException().Message);
        }
    }
    void Finish(string id, OperationState state, string? error, string? result = null)
    {
        var entry = operations[id]; operations[id] = (entry.Ticket with { State = state, Error = error, Result = result, CompletedAt = DateTimeOffset.UtcNow, CompletedTimestamp = Stopwatch.GetTimestamp() }, entry.Cancellation);
    }
    void Set(string name, JsonElement value)
    {
        switch (name)
        {
            case "SelectedOutput": runtime.SelectedOutput = runtime.Outputs.SingleOrDefault(x => x.Id == value.GetString()); return;
            case "SelectedInput": runtime.SelectedInput = runtime.Inputs.SingleOrDefault(x => x.Id == value.GetString()); return;
            case "SelectedSearchAdapter": runtime.SelectedSearchAdapter = runtime.SearchAdapters.SingleOrDefault(x => EngineProtocol.AdapterKey(x) == value.GetString()); return;
            case "SelectedDevice": runtime.SelectedDevice = runtime.Devices.SingleOrDefault(x => EngineProtocol.DeviceKey(x) == value.GetString()); return;
        }
        if (!setters.TryGetValue(name, out var property)) throw new InvalidOperationException("Read-only engine state: " + name);
        if (property.PropertyType == typeof(double) && !double.IsFinite(value.GetDouble())) throw new ArgumentException("Non-finite value");
        property.SetValue(runtime, value.Deserialize(property.PropertyType));
    }
}

public sealed record EngineIpcTrace(string? OperationId,string Operation,string? Name,long CreatedTimestamp,EngineReadTiming? Read,long DispatchedTimestamp,long HandlerStartedTimestamp,long HandlerCompletedTimestamp,long ReplyStartedTimestamp,long ReplyCompletedTimestamp,RuntimeExecutionSample CompletedSample);
