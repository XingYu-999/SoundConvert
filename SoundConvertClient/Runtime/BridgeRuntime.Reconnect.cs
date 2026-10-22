using UsbAudioBridge;

namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    sealed record ReconnectTarget(string Identity, string Name, string Address, int Port, string AdapterId, string LocalAddress, bool WasOwner);
    readonly Func<string, CancellationToken, Task<IReadOnlyList<Device>>> reconnectDiscovery;
    ReconnectTarget? reconnectTarget;
    CancellationTokenSource? reconnectCancellation;
    bool autoReconnectEnabled = true, reconnectPending, reconnectResumeListening, reconnectResumeMicrophone;
    bool reconnectAttemptRunning;
    int reconnectAttempts;
    DateTimeOffset reconnectDue;
    string reconnectStatus = "";

    public bool AutoReconnectEnabled
    {
        get => autoReconnectEnabled;
        set
        {
            if (value == autoReconnectEnabled) return;
            Set(ref autoReconnectEnabled, value);
            if (!value) StopAutomaticReconnect(clearTarget: false, "断链后自动重连已关闭。");
            else if (!IsConnected && control is null && reconnectTarget is not null) ScheduleAutomaticReconnect();
            LocalStore.SaveSettings(CurrentSettings());
        }
    }
    public bool IsReconnecting => reconnectPending;
    public string ReconnectStatus => reconnectStatus;

    void RememberReconnectTarget()
    {
        if (control is not { IsConnected: true } session || SelectedSearchAdapter is not { } adapter) return;
        reconnectTarget = new(session.DeviceId, ConnectedName, session.Address, ControlPort, adapter.Id, LocalAddress, CanManage);
    }
    void ScheduleAutomaticReconnect()
    {
        if (shuttingDown || !AutoReconnectEnabled || reconnectTarget is null || reconnectPending) return;
        if (!string.IsNullOrEmpty(PendingTransaction))
        { reconnectTarget = null; Log("网络配置事务待处理，请先查询或回滚后手动连接。"); return; }
        reconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        reconnectPending = true; reconnectAttempts = 0; reconnectDue = DateTimeOffset.UtcNow.AddSeconds(1);
        reconnectStatus = $"与 {reconnectTarget.Name} 的连接中断，1 秒后自动重连；可点击取消连接。";
        Status = reconnectStatus; GlobalError = ""; Log(Status); RefreshReconnectState();
    }
    void StopAutomaticReconnect(bool clearTarget, string? message = null)
    {
        bool pending = reconnectPending;
        reconnectPending = false;
        var cancellation = reconnectCancellation; reconnectCancellation = null;
        cancellation?.Cancel();
        // An active attempt retains its source until its continuation drains.
        if (!reconnectAttemptRunning) cancellation?.Dispose();
        if (clearTarget) reconnectTarget = null;
        if (pending && message is not null) { Status = reconnectStatus = message; GlobalError = ""; Log(message); }
        RefreshReconnectState();
    }
    void RefreshReconnectState()
    {
        foreach (string name in new[] { nameof(IsReconnecting), nameof(ReconnectStatus), nameof(HeaderStatus), nameof(CanSelectSearchAdapter), nameof(CanToggleConnection) }) Changed(name);
        RefreshCancellationState();
    }
    async Task HandleUnexpectedDisconnectAsync(string reason)
    {
        if (shuttingDown || reconnectPending) return;
        reconnectResumeListening = listeningIntent;
        reconnectResumeMicrophone = microphoneIntent;
        // Drop stale writers and media objects even when automatic reconnect is off.
        await DisconnectCore(cancelAutoConnect: false);
        if (AutoReconnectEnabled && reconnectTarget is not null) ScheduleAutomaticReconnect();
        else { Status = "设备连接已中断。"; GlobalError = reason; Log(Status + " " + reason); }
    }
    async Task PollAutomaticReconnect()
    {
        if (!reconnectPending || IsBusy || polling || DateTimeOffset.UtcNow < reconnectDue || shuttingDown ||
            reconnectCancellation is not { } cancellation || reconnectTarget is not { } target) return;
        reconnectAttemptRunning = true;
        try
        {
            await ExecuteOperationAsync(async () =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                ++reconnectAttempts;
                if (!string.IsNullOrEmpty(PendingTransaction)) throw new InvalidOperationException("网络配置事务待处理，自动重连已停止。");
                var adapters = networkAdapters().Where(a => a.Id == target.AdapterId && a.IsUp && a.HasIpv4).ToArray();
                var adapter = adapters.SingleOrDefault(a => a.Address == target.LocalAddress)
                    ?? (adapters.Length == 1 ? adapters[0] : null)
                    ?? throw new IOException("原网卡未连接、没有 IPv4 或存在多条不明确地址，等待原网卡恢复。");
                LoadSearchAdapters(adapter.Id, adapter.Address);
                // A successful manual connection may use a gateway. Keep trying
                // that known address on its original NIC; discovered replacements
                // still have to match the same device and the selected subnet.
                bool ReachableSubnet(string address) => address == target.Address || FormatPolicy.SameSubnet(address, adapter.Address, adapter.Mask);
                await AutoConnectFlow.RunAsync(target.Identity, target.Address, target.Port,
                    async (address, port, identity) =>
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        Address = address; ControlPort = port; SelectedDevice = null;
                        await ConnectCore(identity, startListening: false, announce: false, cancellationToken: cancellation.Token, recovering: true);
                    }, () => reconnectDiscovery(adapter.Address, cancellation.Token), ReachableSubnet);
                cancellation.Token.ThrowIfCancellationRequested();
                using var resume = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                resume.CancelAfter(TimeSpan.FromSeconds(20)); activeConnectionCancellation = resume;
                if (control is { } session) session.OperationCancellation = resume.Token;
                try
                {
                    if (target.WasOwner && CanManage)
                    {
                        if (reconnectResumeListening)
                        {
                            try { if (UsbState == "RUNNING") await ApplyOutputFormats(); await Listen(); }
                            catch (DeviceException e) when (e.Code is "WAITING_USB" or "USB_EMULATION_STOPPED" or "USB_EMULATION_TRANSITION_BUSY")
                            { PlaybackLabel = "连接已恢复，等待 USB 音源就绪。"; }
                            catch (Exception e) when (control?.IsConnected == true && !resume.IsCancellationRequested)
                            { listeningIntent = false; Log("连接已恢复，输出未恢复：" + e.Message); }
                        }
                        if (reconnectResumeMicrophone && MicrophoneSupported)
                        {
                            try
                            {
                                microphoneIntent = true;
                                if (UsbState == "RUNNING") await StartMicrophoneCore();
                                else InputStatus = "连接已恢复，等待 USB 模拟启动后恢复麦克风。";
                            }
                            catch (Exception e) when (control?.IsConnected == true && !resume.IsCancellationRequested)
                            { microphoneIntent = false; Log("连接已恢复，麦克风未恢复：" + e.Message); }
                        }
                    }
                    resume.Token.ThrowIfCancellationRequested();
                    if (!IsConnected) throw new IOException("恢复媒体通路时控制连接再次中断。");
                }
                finally { if (control is not null) control.OperationCancellation = default; activeConnectionCancellation = null; }
                if (reconnectCancellation != cancellation || !reconnectPending) throw new OperationCanceledException(cancellation.Token);
                reconnectPending = false; reconnectCancellation = null;
                reconnectStatus = $"已自动重连 {ConnectedName}。"; Status = ConnectedStatus; GlobalError = "";
                var previous = Devices.FirstOrDefault(row => row.Identity == target.Identity && row.Device.LocalAddress == adapter.Address);
                if (previous is not null)
                {
                    var updated = previous.Device with { Address = Address, ControlPort = ControlPort, Firmware = FirmwareVersion };
                    Devices[Devices.IndexOf(previous)] = new(updated, ++discoveryGeneration);
                }
                RememberReconnectTarget(); Log(reconnectStatus); RefreshReconnectState();
            }, $"正在自动重连 {target.Name}（第 {reconnectAttempts + 1} 次）…", cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (control is not null) await DisconnectCore(cancelAutoConnect: false);
            if (!shuttingDown) { GlobalError = ""; Status = "自动重连已取消。"; }
        }
        catch (Exception error)
        {
            if (control is not null) await DisconnectCore(cancelAutoConnect: false);
            if (reconnectCancellation != cancellation || !reconnectPending) return;
            if (!string.IsNullOrEmpty(PendingTransaction))
            { StopAutomaticReconnect(clearTarget: true, "网络配置事务待处理，自动重连已停止。"); return; }
            int delay = reconnectAttempts switch { <= 1 => 2, 2 => 5, _ => 10 };
            reconnectDue = DateTimeOffset.UtcNow.AddSeconds(delay);
            reconnectStatus = $"自动重连 {target.Name} 第 {reconnectAttempts} 次未成功：{ConnectionFeedback.Message(error)} {delay} 秒后重试，可取消连接。";
            Status = reconnectStatus; GlobalError = ""; Log(Status); RefreshReconnectState();
        }
        finally
        {
            reconnectAttemptRunning = false;
            if (reconnectCancellation != cancellation) cancellation.Dispose();
            RefreshReconnectState();
        }
    }
}
