using System.Collections.ObjectModel;
using System.Windows.Input;
using UsbAudioBridge;

namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    readonly Func<IReadOnlyList<NetworkAdapterInfo>> networkAdapters;
    NetworkAdapterInfo? selectedSearchAdapter;
    bool refreshingAdapters;
    public ObservableCollection<NetworkAdapterInfo> SearchAdapters { get; } = [];
    public NetworkAdapterInfo? SelectedSearchAdapter
    {
        get => selectedSearchAdapter;
        set
        {
            if (selectedSearchAdapter == value) return;
            Set(ref selectedSearchAdapter, value);
            LocalAddress = value is { IsUp: true, HasIpv4: true } ? value.Address : "0.0.0.0";
            SelectedDevice = null; Devices.Clear(); udpPeers.Clear();
            RefreshAdapterState();
            if (!initializing && !refreshingAdapters) LocalStore.SaveSettings(CurrentSettings());
        }
    }
    public bool CanSelectSearchAdapter => !IsBusy && !IsConnected && !IsReconnecting;
    public bool CanDiscover => !IsBusy && SelectedSearchAdapter is { IsUp: true, HasIpv4: true };
    public string SearchAdapterSummary => SelectedSearchAdapter is { } adapter
        ? $"{adapter.Description} · {adapter.Status} · IPv4：{(adapter.HasIpv4 ? adapter.Address : "无")} · 搜索仅使用此网卡"
        : "请先选择搜索网卡；普通搜索需要已连接且具有 IPv4 地址的网卡。";
    public ICommand RefreshSearchAdaptersCommand => SyncCommand(nameof(RefreshSearchAdaptersCommand), _ => RefreshSearchAdapters(), () => CanSelectSearchAdapter);

    public void RefreshSearchAdapters()
    {
        if (IsConnected) return;
        string? id = SelectedSearchAdapter?.Id, ip = SelectedSearchAdapter?.Address;
        LoadSearchAdapters(id, ip);
    }
    void LoadSearchAdapters(string? id, string? ip)
    {
        refreshingAdapters = true;
        try
        {
            var adapters = networkAdapters();
            SearchAdapters.Clear(); foreach (var adapter in adapters) SearchAdapters.Add(adapter);
            SelectedSearchAdapter = adapters.FirstOrDefault(a => a.Id == id && a.Address == ip)
                ?? adapters.FirstOrDefault(a => a.Id == id)
                ?? (string.IsNullOrEmpty(id) && adapters.Count(a=>a.Address==ip&&a.HasIpv4)==1 ? adapters.Single(a => a.Address == ip && a.HasIpv4) : null);
            RefreshAdapterState();
        }
        finally { refreshingAdapters = false; }
    }
    void RefreshAdapterState()
    {
        foreach (string name in new[] { nameof(CanSelectSearchAdapter), nameof(CanDiscover), nameof(CanDiscoverBroadcast), nameof(CanConnect), nameof(CanToggleConnection), nameof(UdpDiscoveryHint), nameof(SearchAdapterSummary), nameof(DiscoverySummary) }) Changed(name);
        RefreshCommandStates();
    }
    NetworkAdapterInfo RequireSearchAdapter(bool broadcast = false)
    {
        var selected = SelectedSearchAdapter ?? throw new InvalidOperationException("请先选择搜索网卡。");
        var current = networkAdapters().FirstOrDefault(a => a.Id == selected.Id && a.Address == selected.Address);
        if (current is not { IsUp: true }) throw new InvalidOperationException("所选网卡已断开或地址已变化，请刷新网卡后重新选择。");
        if (broadcast ? !current.SupportsBroadcast : !current.HasIpv4)
            throw new InvalidOperationException(broadcast ? "所选网卡不支持UDP 广播搜索。" : "所选网卡没有可用的 IPv4 地址。");
        return current;
    }
}
