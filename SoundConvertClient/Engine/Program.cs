using SoundConvertClient;

try { EngineLifetime.JoinOwner(args); }
catch(Exception error) { LocalStore.Log("后台启动被拒绝："+error.Message); return 2; }

using var singleton = new Mutex(true, "Local\\" + EngineProtocol.Name, out bool first);
if (!first) return 0;
try
{
    using var dispatcher = new RuntimeDispatcher();
    var runtime = dispatcher.InvokeAsync(() => Task.FromResult(new BridgeRuntime())).GetAwaiter().GetResult();
    var server = new EngineHost(dispatcher, runtime);
    // Start listening before doing endpoint initialization or optional auto-connect.
    var serving = server.RunAsync();
    dispatcher.Post(async _ => await runtime.InitializeAsync(), null);
    serving.GetAwaiter().GetResult();
    dispatcher.InvokeAsync(async () => await runtime.DisposeAsync()).GetAwaiter().GetResult();
    return 0;
}
catch (Exception error) { LocalStore.Log("后台异常退出：" + error); return 1; }
finally { singleton.ReleaseMutex(); }
