using System.Windows.Input;

namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    public ICommand ShutdownEngineCommand => SyncCommand(nameof(ShutdownEngineCommand), _ => { }, () => false);
    // Unlike ICommand.Execute, this completes when the actual operation completes.
    public async Task ExecuteOperationAsync(Func<Task> action, string progress = "正在执行操作…", CancellationToken cancellation = default)
    {
        if (shuttingDown || IsBusy) throw new InvalidOperationException("正在执行相互冲突的操作，请等待完成。");
        SetUserCommandPending(true); GlobalError = ""; bool acquired = false;
        try
        {
            OperationStatus = commands.CurrentCount == 0 ? progress + " 等待当前状态刷新完成…" : progress;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);
            await commands.WaitAsync(linked.Token); acquired = true;
            if (shuttingDown) throw new OperationCanceledException(lifetime.Token);
            OperationStatus = progress;
            await action();
        }
        catch (Exception error) { GlobalError = ConnectionFeedback.Message(error); Log(GlobalError); throw; }
        finally { if (acquired) commands.Release(); SetUserCommandPending(false); OperationStatus = ""; RefreshShell(); }
    }
    public async Task InvokeCommandAsync(string name, object? parameter, CancellationToken cancellation = default)
    {
        Func<Task>? action = name switch
        {
            "DiscoverCommand" or "DiscoverBroadcastCommand" => () => DiscoverAsync(cancellation),
            "ConnectCommand" => () => Connect(cancellation), "DisconnectCommand" => Disconnect,
            "ConnectRememberedCommand" when parameter is RememberedBoardRow row => () => ConnectRemembered(row,cancellation),
            "QuickSearchCommand" => async () => { Navigate("虚拟声卡"); await DiscoverAsync(cancellation); },
            "QuickConnectCommand" => async () => { if (RememberedBoards.FirstOrDefault() is { } board) await ConnectRemembered(board,cancellation); else Navigate("虚拟声卡"); },
            "RefreshCommand" => Refresh, "CapabilitiesCommand" => () => Query("GET_CAPABILITIES"),
            "ConfigCommand" => ReadUsbConfiguration, "StatsCommand" => () => Query("GET_STATS"),
            "SaveUsbCommand" => SaveName, "RefreshOutputsCommand" or "RefreshInputsCommand" => RefreshAudioDeviceState,
            "SaveSettingsCommand" => SaveSettings, "SaveUdpPortsCommand" => SaveUdpPorts, "ExportCommand" => Export,
            "ReadNetworkCommand" => ReadNetwork, "ApplyNetworkCommand" => ApplyNetwork,
            "QueryTransactionCommand" => QueryTransaction, "ConfirmNetworkCommand" => () => FinishNetwork(true),
            "RollbackNetworkCommand" => () => FinishNetwork(false), "ApplyBroadcastCommand" => ApplyNetworkBroadcast,
            "ConfirmBroadcastCommand" => () => FinishNetworkBroadcast(true), "QueryBroadcastCommand" => QueryNetworkBroadcast,
            "RollbackBroadcastCommand" => () => FinishNetworkBroadcast(false),
            "StartMicrophoneCommand" => StartMicrophone, "StopMicrophoneCommand" => StopMicrophone,
            "DetectInputCommand" => DetectInput,
            "ApplyOutputSettingsCommand" => ApplyOutputSettingsAsync,
            _ => null
        };
        if (action is not null) { await ExecuteOperationAsync(action, cancellation:cancellation); return; }
        switch (name)
        {
            case "ToggleUsbCommand": await ToggleUsbAsync(); break;
            case "ToggleListeningCommand": await ToggleListeningAsync(); break;
            case "ToggleConnectionCommand": await ToggleConnectionAsync(cancellation); break;
            default:
                var property = typeof(BridgeRuntime).GetProperty(name) ?? throw new ArgumentException("Unknown command");
                ((ICommand)property.GetValue(this)!).Execute(parameter); return;
        }
        if (!string.IsNullOrEmpty(GlobalError)) throw new InvalidOperationException(GlobalError);
    }
}
