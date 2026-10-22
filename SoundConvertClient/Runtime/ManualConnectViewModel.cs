using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SoundConvertClient;

public sealed record ConnectRequest(string OperationId, string Address, int Port, string AdapterKey, string? ExpectedDeviceId = null,
    string? DiscoveryPath = null, long? DiscoveryGeneration = null);

/// <summary>Local dialog draft. No runtime setting is written until one atomic request is submitted.</summary>
public sealed class ManualConnectViewModel : INotifyPropertyChanged
{
    readonly Func<ConnectRequest, Task<string>> connect;
    readonly Func<string, Task<string>> cancel;
    readonly string adapterKey;
    string address, port, addressError = "", portError = "", status = "", operationId = "";
    bool busy, cancelPending, connected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? CloseRequested;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public string AdapterSummary { get; }
    public string Address { get => address; set { address = value; addressError = ""; Changed(); Changed(nameof(AddressError)); } }
    public string Port { get => port; set { port = value; portError = ""; Changed(); Changed(nameof(PortError)); } }
    public string AddressError => addressError;
    public string PortError => portError;
    public string Status { get => status; private set { status = value; Changed(); } }
    public bool Busy { get => busy; private set { busy = value; Changed(); Changed(nameof(CanSubmit)); Changed(nameof(CanClose)); Changed(nameof(CancelLabel)); RefreshCommands(); } }
    public bool CanSubmit => !Busy && !connected;
    public bool CanClose => !Busy;
    public string CancelLabel => Busy ? cancelPending ? "正在取消…" : "取消连接" : connected ? "关闭" : "取消";
    public ICommand ConnectCommand { get; }
    public ICommand CancelCommand { get; }

    public ManualConnectViewModel(string adapterSummary, string address, int port, string adapterKey,
        Func<ConnectRequest, Task<string>> connect, Func<string, Task<string>> cancel)
    {
        AdapterSummary = adapterSummary; this.address = address; this.port = port.ToString(CultureInfo.InvariantCulture);
        this.adapterKey = adapterKey; this.connect = connect; this.cancel = cancel;
        ConnectCommand = new UiCommand(_ => _ = SubmitAsync(), () => CanSubmit);
        CancelCommand = new UiCommand(_ => _ = CancelAsync(), () => !cancelPending);
    }
    void RefreshCommands() { ((UiCommand)ConnectCommand).Refresh(); ((UiCommand)CancelCommand).Refresh(); }
    public static bool IsUsableAddress(string value)
    {
        string[] parts = value.Trim().Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 3 || p.Length > 1 && p[0] == '0' || !p.All(char.IsAsciiDigit) || !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        byte[] bytes = parts.Select(p => byte.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return bytes[0] is > 0 and < 224 && !bytes.All(b => b == 255) && IPAddress.TryParse(value.Trim(), out _);
    }
    public async Task SubmitAsync()
    {
        if (!CanSubmit) return;
        addressError = IsUsableAddress(Address) ? "" : "请输入标准点分 IPv4，不能使用未指定、广播或多播地址。";
        bool validPort = int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort) && parsedPort is >= 1 and <= 65535;
        portError = validPort ? "" : "TCP 控制端口须为 1–65535。";
        Changed(nameof(AddressError)); Changed(nameof(PortError));
        if (addressError.Length != 0 || portError.Length != 0) return;
        operationId = Guid.NewGuid().ToString("N"); Busy = true; Status = "正在通过所选网卡连接并校验设备…";
        try { Status = await connect(new(operationId, Address.Trim(), parsedPort, adapterKey)); connected = true; CloseRequested?.Invoke(); }
        catch (OperationCanceledException) { Status = "连接已取消。"; }
        catch (Exception error) { Status = error.Message; }
        finally { cancelPending = false; Busy = false; }
    }
    public async Task CancelAsync()
    {
        if (!Busy) { CloseRequested?.Invoke(); return; }
        if (cancelPending) return;
        cancelPending = true; Changed(nameof(CancelLabel)); RefreshCommands();
        try { Status = await cancel(operationId); }
        catch (Exception error) { Status = "取消结果待确认：" + error.Message; }
        finally { cancelPending = false; Changed(nameof(CancelLabel)); RefreshCommands(); }
    }
}
