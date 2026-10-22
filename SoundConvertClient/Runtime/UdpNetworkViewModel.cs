using UsbAudioBridge;
namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    public async Task<Device> ResolveDeviceForConfigurationAsync(Device selection, string adapterKey)
    {
        var adapter = SearchAdapters.SingleOrDefault(a => EngineProtocol.AdapterKey(a) == adapterKey)
            ?? throw new InvalidOperationException("原搜索网卡已失效，请重新选择并搜索。");
        if(EngineProtocol.AdapterKey(RequireSearchAdapter())!=adapterKey)throw new InvalidOperationException("原搜索网卡已改变，请重新搜索。");
        if (!adapter.IsUp || !adapter.HasIpv4) throw new InvalidOperationException("原搜索网卡不支持当前UDP 广播配置路径。");
        string originalAdapter = adapter.Id;
        if (!UdpDiscovery.SameAdapter(originalAdapter, adapter.Id) || selection.LocalAddress != adapter.Address)
            throw new InvalidOperationException("设备发现路径与原搜索网卡不一致，请重新搜索。");
        var peer = udpPeers.Values.FirstOrDefault(p => UdpDiscovery.CanReuseForConfiguration(p, selection, adapter.Id, DateTimeOffset.UtcNow))
            ?? await RefreshUdpPeer(selection.DeviceId, adapter.Id);
        if(EngineProtocol.AdapterKey(RequireSearchAdapter())!=adapterKey)throw new InvalidOperationException("原搜索网卡已改变，请重新搜索。");
        if (!string.IsNullOrWhiteSpace(selection.Mac) && !string.Equals(peer.Device.Mac, selection.Mac, StringComparison.OrdinalIgnoreCase))
            throw new IOException("设备 MAC 与原搜索结果不一致，已停止配置。");
        udpPeers[peer.Device.DeviceId + "|" + peer.AdapterName] = peer;
        return FreezeConfigurationIdentity(peer);
    }
    public bool CanModifyDiscoveredIp => !IsBusy && !IsConnected && !IsReconnecting && string.IsNullOrEmpty(PendingTransaction)
        && SelectedDevice is not null && SelectedSearchAdapter is { IsUp:true, HasIpv4:true };
    public Func<IpAddressEditor, Task>? RequestIpAddressDialog { get; set; }
    public System.Windows.Input.ICommand ModifyDiscoveredIpCommand => SyncCommand(nameof(ModifyDiscoveredIpCommand), _ =>
    {
        if (CanModifyDiscoveredIp && RequestIpAddressDialog is not null) _ = RequestIpAddressDialog(CreateIpAddressEditor());
    });

    public IpAddressEditor CreateIpAddressEditor()
    {
        if (!CanModifyDiscoveredIp) throw new InvalidOperationException("请在未连接时UDP 广播搜索并选择设备；已有事务须先确认或回滚。");
        Device selection = SelectedDevice!.Device;
        if(!udpPeers.TryGetValue(selection.DeviceId+"|"+RequireSearchAdapter().Id,out var peer))throw new InvalidOperationException("原UDP 广播设备身份已失效，请重新搜索。");
        selection=FreezeConfigurationIdentity(peer);
        return new(selection, (dhcp, ip, prefix, gateway) => ChangeDiscoveredIpAsync(selection, dhcp, ip, prefix, gateway));
    }

    public async Task<string> ChangeDiscoveredIpAsync(Device selection, bool dhcp, string ip, int prefix, string gateway)
    {
        if (!await commands.WaitAsync(0)) throw new InvalidOperationException("其他操作进行中，请稍候。");
        IsBusy = true;
        try
        {
            if (IsConnected || IsReconnecting || !string.IsNullOrEmpty(PendingTransaction) || selection.LocalAddress != RequireSearchAdapter().Address)
                throw new InvalidOperationException("请断开连接并处理已有事务后，再通过UDP 广播路径修改 IP。");
            IpAddressEditor.Validate(dhcp, ip, prefix, gateway);
            string adapter = RequireSearchAdapter().Id;
            udpPeers.Clear();
            if(!UdpDiscovery.SameAdapter(RequireSearchAdapter().Id,adapter))throw new InvalidOperationException("原配置网卡已改变，请重新搜索。");
            var original = await RefreshUdpPeer(selection.DeviceId, adapter);
            ValidateConfigurationIdentity(selection,original);
            var prepared = await UdpDiscovery.PrepareNetworkAsync(original, dhcp, ip, prefix, gateway, dhcp ? 120 : 30, lifetime.Token);
            UdpSuccess(prepared);
            byte[] transaction = prepared.TransactionId ?? throw new InvalidDataException("准备应答缺少事务编号。");
            PendingTransaction = Convert.ToHexString(transaction).ToLowerInvariant(); pendingDevice = selection.DeviceId;
            pendingBroadcastAdapter = adapter; pendingUsbRevision = ""; pendingRestartUsb = false;
            LocalStore.SaveSettings(CurrentSettings());
            NetworkStatus = "正在通过UDP 广播应用候选 IP，验证完成后自动确认保存。";
            // Writes retry only with the same request ID inside ExchangeAsync.
            // Once its result is unknown, leave the saved transaction for recovery.
            UdpSuccess(await UdpDiscovery.ApplyAsync(prepared.Peer, transaction, lifetime.Token));
            var watch = System.Diagnostics.Stopwatch.StartNew(); UdpPeer? candidate = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(dhcp ? 100 : 22))
            {
                var peers = await UdpDiscovery.DiscoverPeersAsync(lifetime.Token, original.LocalIpv4);
                var current = peers.SingleOrDefault(p => p.Device.DeviceId == selection.DeviceId && UdpDiscovery.SameAdapter(p.AdapterName, adapter));
                if (current is null) continue;
                if (current.BootId != original.BootId) throw new IOException("设备已重启，配置结果待查询。");
                if (current.TransactionId != PendingTransaction || current.ConfigRevision != original.ConfigRevision)
                    throw new IOException("设备事务或配置版本已改变，已停止自动确认。");
                if (current.TransactionStage == 4) { ClearPendingTransaction(); throw new IOException("设备已回滚候选配置，请检查地址冲突或 DHCP 服务。"); }
                if (current.TransactionStage == 2 && current.Device.NetworkState == (dhcp ? 3 : 5)
                    && current.Device.AddressMode == (dhcp ? "dhcp" : "static")
                    && current.Device.Address != "0.0.0.0" && (dhcp || current.Device.Address == ip))
                { candidate = current; break; }
            }
            if (candidate is null) throw new TimeoutException("未验证到可用的新地址。事务已保留，可在UDP 广播恢复中查询、确认或回滚；逾期设备自动回滚。");
            UdpPeer committed;
            try
            {
                var confirmed = await UdpDiscovery.ConfirmAsync(candidate, transaction, lifetime.Token);
                UdpSuccess(confirmed); committed = confirmed.Peer;
                if (confirmed.Reason != "COMMITTED") throw new IOException("设备未返回提交完成，结果待查询。");
            }
            catch (TimeoutException)
            {
                // A lost CONFIRM acknowledgment must never be reported as success
                // from the address alone: match transaction, boot, stage and revision.
                committed = await RefreshUdpPeer(selection.DeviceId, adapter);
                if (committed.BootId != original.BootId || committed.TransactionId != PendingTransaction
                    || committed.TransactionStage != 3 || committed.ConfigRevision != original.ConfigRevision + 1)
                    throw new IOException("确认应答丢失且未查到匹配的已提交结果。事务已保留，请使用UDP 广播恢复确认实际状态。");
            }
            var updated = candidate.Device with { Address = committed.Device.Address };
            ClearPendingTransaction();
            var old = Devices.FirstOrDefault(d => d.Device.DeviceId == selection.DeviceId && d.Device.LocalAddress == selection.LocalAddress);
            var row = new DeviceRow(updated, discoveryGeneration); if (old is not null) Devices[Devices.IndexOf(old)] = row; else Devices.Add(row);
            SelectedDevice = row; Address = updated.Address; LocalAddress = LocalAddressFor(Address);
            if (autoConnectBoard?.DeviceId == selection.DeviceId)
                autoConnectBoard = autoConnectBoard with { Address = Address, LocalAddress = LocalAddress };
            var remembered = RememberedBoards.FirstOrDefault(r => r.Board.DeviceId == selection.DeviceId);
            if (remembered is not null)
            {
                int index = RememberedBoards.IndexOf(remembered);
                RememberedBoards[index] = new(remembered.Board with { Address = Address, LocalAddress = LocalAddress }, ConnectRememberedCommand, ForgetBoardCommand);
                PersistBoards();
            }
            LocalStore.SaveSettings(CurrentSettings());
            NetworkStatus = $"已保存{(dhcp ? " DHCP" : "静态 IP")}：{Address}。USB 模拟已暂停，可连接后开始模拟。";
            Log(NetworkStatus); return NetworkStatus;
        }
        catch (Exception error)
        {
            NetworkStatus = error.Message + (string.IsNullOrEmpty(PendingTransaction) ? "" : " 待处理事务：" + PendingTransaction);
            Log(NetworkStatus); throw;
        }
        finally { IsBusy = false; commands.Release(); RefreshShell(); }
    }

    static Device FreezeConfigurationIdentity(UdpPeer peer)=>peer.Device with {ConfigurationBootId=peer.BootId,ConfigurationRevision=peer.ConfigRevision};
    public static void ValidateConfigurationIdentity(Device selection,UdpPeer current)
    {
        if(!selection.DeviceId.Equals(current.Device.DeviceId,StringComparison.OrdinalIgnoreCase)||selection.LocalAddress!=current.LocalIpv4||!string.Equals(current.Device.Mac,selection.Mac,StringComparison.OrdinalIgnoreCase))
            throw new IOException("设备身份、MAC 或原网卡路径已改变，请重新搜索。");
        if(selection.ConfigurationBootId is not ulong boot||selection.ConfigurationRevision is not ulong revision||boot!=current.BootId||revision!=current.ConfigRevision)
            throw new IOException("设备已重启或配置版本已改变，请重新打开 IP 配置并核对当前状态。");
    }

    public async Task QueryNetworkBroadcast()
    {
        if (string.IsNullOrEmpty(PendingTransaction) || string.IsNullOrEmpty(pendingDevice)) throw new InvalidOperationException("没有待处理配置事务。");
        var peer = await RefreshUdpPeer(pendingDevice, pendingBroadcastAdapter);
        if (peer.TransactionId != PendingTransaction) throw new IOException("设备未保留匹配的事务，可能已重启；请核对当前网络配置。");
        NetworkStatus = peer.TransactionStage switch
        {
            1 => "UDP 广播事务已准备，尚未应用。", 2 => "候选配置已应用，等待确认：" + peer.Device.Address,
            3 => "已查询到匹配事务，配置已确认保存：" + peer.Device.Address,
            4 => "已查询到匹配事务，设备已回滚。", _ => "事务状态未知。"
        };
        if (peer.TransactionStage is 3 or 4) ClearPendingTransaction();
        Log(NetworkStatus); RefreshShell();
    }

    readonly Dictionary<string,UdpPeer> udpPeers=new();string pendingBroadcastAdapter="";
    public bool CanDiscoverBroadcast => CanDiscover;
    public string UdpDiscoveryHint => "通过所选网卡发送 UDP 广播；支持同一广播域内的不同 IP 子网。";
    public string BroadcastAvailability => "UDP 广播无需额外驱动。";
    public Task DiscoverBroadcast() => Discover();
    async Task<UdpPeer> RefreshUdpPeer(string id,string adapter)
    {
        var selected=RequireSearchAdapter();
        if(!string.IsNullOrEmpty(adapter) && !UdpDiscovery.SameAdapter(selected.Id,adapter)) throw new IOException("原配置网卡已改变，请重新选择原网卡。");
        var peers=await UdpDiscovery.DiscoverPeersAsync(lifetime.Token,selected.Address);
        return peers.SingleOrDefault(p=>p.Device.DeviceId==id && UdpDiscovery.SameAdapter(p.AdapterName,selected.Id))
            ?? throw new IOException("未在原网卡收到设备的 UDP 广播应答；请确认固件版本和广播域。");
    }
    static void UdpSuccess(UdpResult result){if(result.Status!=0)throw new InvalidOperationException($"UDP 广播操作拒绝 ({result.Status})：{result.Reason}");}
    public async Task ApplyNetworkBroadcast(){if(!string.IsNullOrEmpty(PendingTransaction))throw new InvalidOperationException("已有待处理配置事务");var selection=SelectedDevice?.Device??throw new InvalidOperationException("请先UDP 广播搜索并选择一条设备路径");string adapter=RequireSearchAdapter().Id;var peer=await RefreshUdpPeer(selection.DeviceId,adapter);var prepared=await UdpDiscovery.PrepareNetworkAsync(peer,NetworkMode=="dhcp",NetworkIp,NetworkPrefix,NetworkGateway,NetworkMode=="dhcp"?120:30);UdpSuccess(prepared);if(prepared.TransactionId is null)throw new InvalidDataException("UDP 广播准备应答缺少事务编号");PendingTransaction=Convert.ToHexString(prepared.TransactionId).ToLowerInvariant();pendingDevice=peer.Device.DeviceId;pendingBroadcastAdapter=adapter;pendingUsbRevision="";LocalStore.SaveSettings(CurrentSettings());NetworkStatus="UDP 广播事务已准备，等待应用结果";var applied=await UdpDiscovery.ApplyAsync(prepared.Peer,prepared.TransactionId);UdpSuccess(applied);NetworkStatus="UDP 广播候选网络已应用；请重新UDP 广播搜索验证地址，再显式UDP 广播确认。逾期将回滚。";if(NetworkMode=="static")Address=NetworkIp;LocalStore.SaveSettings(CurrentSettings());}
    public async Task FinishNetworkBroadcast(bool confirm){if(string.IsNullOrEmpty(PendingTransaction)||string.IsNullOrEmpty(pendingDevice))throw new InvalidOperationException("没有待处理配置事务");var peer=await RefreshUdpPeer(pendingDevice,pendingBroadcastAdapter);var result=confirm?await UdpDiscovery.ConfirmAsync(peer,Convert.FromHexString(PendingTransaction)):await UdpDiscovery.RollbackAsync(peer,Convert.FromHexString(PendingTransaction));UdpSuccess(result);PendingTransaction="";pendingDevice="";pendingBroadcastAdapter="";pendingUsbRevision="";NetworkStatus=confirm?"UDP 广播配置已确认提交":"UDP 广播回滚完成，请重新搜索地址";LocalStore.SaveSettings(CurrentSettings());Log(NetworkStatus);}
}

