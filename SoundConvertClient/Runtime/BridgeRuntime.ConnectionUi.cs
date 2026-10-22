using System.Windows.Input;
using UsbAudioBridge;

namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    CancellationTokenSource? manualConnectionCancellation;
    string manualConnectionOperation = "";
    bool connectionUiTracking, lastUiMonitoring;
    string appliedOutputSettings = "";
    public Func<ManualConnectViewModel, Task>? RequestManualConnectDialog { get; set; }
    public bool HasPendingNetworkTransaction { get { EnsureConnectionUiTracking(); return !string.IsNullOrEmpty(PendingTransaction); } }
    string OutputSettingsKey => $"{MaximumBufferMs}|{TransportCodec}|{NetworkFecEnabled}|{ResamplingQuality}|{ClockModeIndex}";
    public string OutputSettingsStatus
    {
        get
        {
            EnsureConnectionUiTracking();
            if (!IsMonitoring) return "开始输出时应用当前稳定性设置。";
            return appliedOutputSettings == OutputSettingsKey ? "当前稳定性设置已应用。" : "待应用：稳定性设置已更改，请应用并重新开始输出。";
        }
    }
    void EnsureConnectionUiTracking()
    {
        if (connectionUiTracking) return;
        connectionUiTracking = true; lastUiMonitoring = IsMonitoring; appliedOutputSettings = OutputSettingsKey;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PendingTransaction)) Changed(nameof(HasPendingNetworkTransaction));
            if (e.PropertyName == nameof(IsMonitoring))
            {
                if (IsMonitoring && !lastUiMonitoring) appliedOutputSettings = OutputSettingsKey;
                lastUiMonitoring = IsMonitoring; Changed(nameof(OutputSettingsStatus));
            }
            if (e.PropertyName is nameof(MaximumBufferMs) or nameof(TransportCodec) or nameof(NetworkFecEnabled) or nameof(ResamplingQuality)) Changed(nameof(OutputSettingsStatus));
        };
    }
    public ICommand ManualConnectCommand => SyncCommand(nameof(ManualConnectCommand), _ =>
    {
        var adapter = SelectedSearchAdapter;
        if (adapter is null || RequestManualConnectDialog is null) return;
        var editor = new ManualConnectViewModel(adapter.Label, Address, 49161, EngineProtocol.AdapterKey(adapter), ConnectManualUiAsync, CancelManualUiAsync);
        _ = RequestManualConnectDialog(editor);
    }, () => CanSelectSearchAdapter && SelectedSearchAdapter is { IsUp: true, HasIpv4: true } && !IsConnected && !IsConnecting);
    async Task<string> ConnectManualUiAsync(ConnectRequest request)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        manualConnectionCancellation = cancellation; manualConnectionOperation = request.OperationId;
        try { await ConnectTargetAsync(request.Address, request.Port, request.AdapterKey, request.ExpectedDeviceId, cancellation.Token); return "连接已建立。"; }
        finally { if (manualConnectionOperation == request.OperationId) { manualConnectionCancellation = null; manualConnectionOperation = ""; } }
    }
    Task<string> CancelManualUiAsync(string id)
    {
        if (manualConnectionOperation == id && (!IsConnected || IsConnecting)) manualConnectionCancellation?.Cancel();
        return Task.FromResult(IsConnected && !IsConnecting ? "连接已成功，保留当前连接。" : "已请求取消，正在等待后台确认。" );
    }
    public ICommand ConnectDeviceCommand => SyncCommand(nameof(ConnectDeviceCommand), parameter =>
    {
        if (parameter is not DeviceRow row || SelectedSearchAdapter is not { } adapter) return;
        Device target = row.Device; string adapterKey = EngineProtocol.AdapterKey(adapter);
        _ = ConnectRowUiAsync(target, adapterKey, row.DiscoveryGeneration);
    }, () => !IsConnected && !IsConnecting && !IsBusy && SelectedSearchAdapter is { IsUp: true, HasIpv4: true });
    async Task ConnectRowUiAsync(Device target, string adapterKey, long generation)
    {
        try { await ExecuteOperationAsync(async () => { ValidateDiscoveredTarget(target.DeviceId, target.LocalAddress, generation, adapterKey); await ConnectTargetAsync(target.Address, target.ControlPort, adapterKey, target.DeviceId); }, $"正在连接 {ConnectionFeedback.Target(target.Model, target.DeviceId)}…"); }
        catch (Exception error) { GlobalError = error.Message; Log(error.Message); }
    }
    public ICommand ConfigureDeviceCommand => SyncCommand(nameof(ConfigureDeviceCommand), parameter =>
    {
        if (parameter is not DeviceRow row || SelectedSearchAdapter is not { } adapter) return;
        Device target = row.Device; string adapterKey = EngineProtocol.AdapterKey(adapter);
        _ = ConfigureRowUiAsync(target, adapterKey, row.DiscoveryGeneration);
    }, () => !configurationDialogOpen && !IsBusy && SelectedSearchAdapter is { IsUp: true });
    bool configurationDialogOpen;
    async Task ConfigureRowUiAsync(Device target, string adapterKey, long generation)
    {
        if (RequestIpAddressDialog is null || configurationDialogOpen || IsBusy) return;
        configurationDialogOpen = true; RefreshCommandStates();
        try
        {
            Device? original = null;
            await ExecuteOperationAsync(async () =>
            {
                ValidateDiscoveredTarget(target.DeviceId, target.LocalAddress, generation, adapterKey);
                original = await ResolveDeviceForConfigurationAsync(target, adapterKey);
                ValidateDiscoveredTarget(target.DeviceId, target.LocalAddress, generation, adapterKey);
            }, $"正在打开 {ConnectionFeedback.Target(target.Model, target.DeviceId)} 的 IP 设置，核对UDP 广播路径…");
            await RequestIpAddressDialog(new(original!, (dhcp, ip, prefix, gateway) => ChangeDiscoveredIpAsync(original!, dhcp, ip, prefix, gateway), canWrite: !IsConnected && !IsReconnecting && !HasPendingNetworkTransaction));
        }
        catch (Exception error) { GlobalError = error.Message; Log(error.Message); }
        finally { configurationDialogOpen = false; RefreshCommandStates(); }
    }
    public void ValidateDiscoveredTarget(string deviceId, string path, long generation, string adapterKey)
    {
        var adapter = SearchAdapters.SingleOrDefault(a => EngineProtocol.AdapterKey(a) == adapterKey)
            ?? throw new InvalidOperationException("原搜索网卡已失效，请重新搜索。");
        if(SelectedSearchAdapter is null||EngineProtocol.AdapterKey(SelectedSearchAdapter)!=adapterKey||!networkAdapters().Any(a=>EngineProtocol.AdapterKey(a)==adapterKey&&a.IsUp))
            throw new InvalidOperationException("原搜索网卡已断开或地址已变化，请重新搜索。");
        if (path != adapter.Address || !Devices.Any(r => r.Device.DeviceId == deviceId && r.Device.LocalAddress == path && r.DiscoveryGeneration == generation))
            throw new InvalidOperationException("此发现结果已失效，请重新搜索并选择设备。");
    }
    public async Task ApplyOutputSettingsAsync()
    {
        await StopListening(); await Listen(); appliedOutputSettings = OutputSettingsKey; Changed(nameof(OutputSettingsStatus));
    }
    public ICommand ApplyOutputSettingsCommand => SyncCommand(nameof(ApplyOutputSettingsCommand), _ => _ = Execute(ApplyOutputSettingsAsync, "正在应用稳定性设置并重新开始输出…"), () => CanToggleListening && IsMonitoring);
}
