using System.Text.Json;
using System.Windows.Input;
using UsbAudioBridge;

namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    string? deviceConnectionOperation;
    bool deviceConnectionCancelRequested;
    bool deviceActionInFlight, deviceActionBusy, deviceActionConnecting;
    string deviceActionProgress = "";
    bool BeginDeviceAction(DeviceRow row, bool connecting)
    {
        if (deviceActionInFlight || IsBusy) return false;
        deviceActionInFlight = deviceActionBusy = true; deviceActionConnecting = connecting;
        deviceActionProgress = connecting ? $"正在连接 {ConnectionFeedback.Target(row.Name, row.Identity)}…" : $"正在打开 {ConnectionFeedback.Target(row.Name, row.Identity)} 的 IP 设置，核对UDP 广播路径…";
        localError = ""; Changed(nameof(GlobalError)); Changed(nameof(HasGlobalError));
        RefreshDeviceAction(); return true;
    }
    void RefreshDeviceAction()
    {
        foreach (string name in new[] { nameof(IsBusy), nameof(IsConnecting), nameof(OperationStatus), nameof(Status), nameof(CanDiscover), nameof(CanSelectSearchAdapter), nameof(CanToggleConnection), nameof(ConnectionButtonText), nameof(CanCancelConnection), nameof(CanCancelOperation), nameof(CanCancelSearch) }) Changed(name);
        foreach (var command in commands.Values) command.Refresh();
    }
    async Task FinishDeviceActionAsync()
    {
        deviceActionBusy=deviceActionConnecting=false;deviceActionProgress="";RefreshDeviceAction();
        try
        {
            if (client.ProcessId != 0)
            {
                var response = await client.RequestAsync(new("snapshot")).WaitAsync(TimeSpan.FromSeconds(3));
                if (response.Ok && response.Snapshot is { } snapshot) ApplySnapshot(snapshot);
            }
        }
        catch (Exception error) { Log("操作后状态未确认：" + ConnectionFeedback.Message(error)); }
        finally { deviceActionInFlight = deviceActionBusy = deviceActionConnecting = false; deviceActionProgress = ""; RefreshDeviceAction(); }
    }
    public Func<ManualConnectViewModel, Task>? RequestManualConnectDialog { get; set; }
    public Func<Task>? RequestShutdownExit { get; set; }
    public ICommand ShutdownEngineCommand => Command(nameof(ShutdownEngineCommand));
    public Task ShutdownEngineAsync() => client.ShutdownEngineAsync();
    public bool HasPendingNetworkTransaction => !string.IsNullOrEmpty(PendingTransaction);
    public string OutputSettingsStatus => Get<string>(nameof(OutputSettingsStatus));
    public string InputModeHint => Get<string>(nameof(InputModeHint));
    public bool HasInputModeHint => Get<bool>(nameof(HasInputModeHint));
    public ICommand ApplyOutputSettingsCommand => Command(nameof(ApplyOutputSettingsCommand));
    public ICommand ManualConnectCommand => Command(nameof(ManualConnectCommand));
    public ICommand ConnectDeviceCommand => Command(nameof(ConnectDeviceCommand));
    public ICommand ConfigureDeviceCommand => Command(nameof(ConfigureDeviceCommand));
    void OpenManualConnection()
    {
        if (RequestManualConnectDialog is null || SelectedSearchAdapter is not { } adapter || IsConnected) return;
        var editor = new ManualConnectViewModel(adapter.Label, Address, 49161, EngineProtocol.AdapterKey(adapter), async request =>
        {
            var response = await client.RequestAsync(new("connect", Value: EngineProtocol.Json(request), OperationId: request.OperationId));
            if (!response.Ok) throw new IOException(response.Error ?? "连接未完成。");
            return response.Result ?? "连接已建立。";
        }, async id =>
        {
            var response = await client.RequestAsync(new("cancel", Name: id));
            if (!response.Ok) throw new IOException(response.Error ?? "取消未确认。");
            return response.Result ?? "已请求取消，正在等待后台确认。";
        });
        _ = RequestManualConnectDialog(editor);
    }
    async Task ConnectDeviceRowAsync(DeviceRow row)
    {
        if (SelectedSearchAdapter is not { } adapter || IsConnected) return;
        if (!BeginDeviceAction(row, connecting: true)) return;
        Device target = row.Device; string key = EngineProtocol.AdapterKey(adapter);
        deviceConnectionCancelRequested=false;
        var request = new ConnectRequest(Guid.NewGuid().ToString("N"), target.Address, target.ControlPort, key, target.DeviceId, target.LocalAddress, row.DiscoveryGeneration);
        try
        {
            if (PendingFrameAsync is { } frame) await frame();
            if(deviceConnectionCancelRequested) { Log("连接已取消。"); return; }
            deviceConnectionOperation=request.OperationId;
            var response = await SendAsync(new("connect", Value: EngineProtocol.Json(request), OperationId: request.OperationId));
            if (!response.Ok) Log(response.Ticket?.State==OperationState.Canceled ? $"连接已取消（{ConnectionFeedback.Target(row.Name,row.Identity)}）。" : $"连接失败（{ConnectionFeedback.Target(row.Name, row.Identity)}）：{response.Error}");
        }
        catch (Exception error) { Log(ConnectionFeedback.Message(error)); }
        finally { deviceConnectionOperation=null;deviceConnectionCancelRequested=false;await FinishDeviceActionAsync(); }
    }
    async Task ConfigureDeviceRowAsync(DeviceRow row)
    {
        if (RequestIpAddressDialog is null || SelectedSearchAdapter is not { } adapter) return;
        if (!BeginDeviceAction(row, connecting: false)) return;
        Device target = row.Device; string key = EngineProtocol.AdapterKey(adapter);
        try
        {
            if (PendingFrameAsync is { } frame) await frame();
            var response = await SendAsync(new("resolveDevice", Value: EngineProtocol.Json(new { device = target, adapterKey = key, discoveryGeneration = row.DiscoveryGeneration })));
            if (!response.Ok) { Log($"打开 {ConnectionFeedback.Target(row.Name, row.Identity)} 的 IP 设置失败：{response.Error}"); return; }
            if(SelectedSearchAdapter is null||EngineProtocol.AdapterKey(SelectedSearchAdapter)!=key||!Devices.Any(current=>current.Device.DeviceId==target.DeviceId&&current.Device.LocalAddress==target.LocalAddress&&current.DiscoveryGeneration==row.DiscoveryGeneration))
                throw new IOException("此发现结果或原网卡已失效，请重新搜索并选择设备。");
            var resolved = JsonSerializer.Deserialize<Device>(response.Result ?? "") ?? throw new IOException("后台未返回设备UDP 广播路径。");
            deviceActionBusy = false; deviceActionProgress = ""; RefreshDeviceAction();
            await RequestIpAddressDialog(new(resolved, async (dhcp, ip, prefix, gateway) =>
            {
                var saved = await client.RequestAsync(new("changeIp", Value: EngineProtocol.Json(new { device = resolved, dhcp, ip, prefix, gateway, adapterKey = key })));
                if (!saved.Ok) throw new IOException(saved.Error ?? "网络配置未完成。");
                return saved.Result ?? "配置已保存。";
            }, canWrite: !IsConnected && !IsReconnecting && !HasPendingNetworkTransaction));
        }
        catch (Exception error) { Log(error.Message); }
        finally { await FinishDeviceActionAsync(); }
    }
    public bool IsDiscovering => pendingUiCommands.Overlaps(new[]{"DiscoverCommand","QuickSearchCommand","DiscoverBroadcastCommand"}) || Get<bool>(nameof(IsDiscovering));
    public bool CanCancelSearch => backendAvailable && IsDiscovering;
    public bool CanCancelOperation => CanCancelConnection || CanCancelSearch;
    public bool CanCancelConnection => backendAvailable && !deviceConnectionCancelRequested && (deviceActionConnecting || Get<bool>(nameof(CanCancelConnection)));
    public ICommand CancelSearchCommand => Command(nameof(CancelSearchCommand));
    public ICommand CancelConnectionCommand => Command(nameof(CancelConnectionCommand));
    async Task CancelConnectionUiAsync()
    {
        if(!CanCancelConnection)return;
        if(deviceActionConnecting)deviceConnectionCancelRequested=true;RefreshDeviceAction();
        if(deviceConnectionOperation is {} id) await client.CancelOperationAsync(id);
        await SendAsync(new("command","CancelConnectionCommand"));
    }

}
