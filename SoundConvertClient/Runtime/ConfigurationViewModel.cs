using System.Net;
using System.Text.Json;
using Microsoft.Win32;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    string networkMode = "dhcp", networkIp = "192.168.8.73", networkGateway = "192.168.8.1", networkDns = "192.168.8.1", pendingTransaction = "", pendingDevice = "", networkStatus = "未进行网络事务"; int networkPrefix = 24,maximumBufferMs=20;string pendingUsbRevision=""; bool pendingRestartUsb; bool closeToTray = true, startInTray, startAtLogin;
    public int MaximumBufferMs{get=>maximumBufferMs;set{Set(ref maximumBufferMs,value);Changed(nameof(BufferBudgetSummary));}}
    ResamplerQuality resamplingQuality=ResamplerQuality.Balanced;
    public IReadOnlyList<string> ResamplingQualities{get;}=new[]{"低延迟","均衡","高质量"};
    public ResamplerQuality ResamplingQuality{get=>resamplingQuality;set{if(!Enum.IsDefined(value))return;Set(ref resamplingQuality,value);Changed(nameof(ResamplingQualityIndex));}}
    public int ResamplingQualityIndex{get=>(int)ResamplingQuality;set{if(value is >=0 and <=2)ResamplingQuality=(ResamplerQuality)value;}}
    bool networkFecEnabled;
    public bool NetworkFecEnabled{get=>networkFecEnabled;set=>Set(ref networkFecEnabled,value);}
    AudioTransportCodec transportCodec;bool compressionSupported;
    public AudioTransportCodec TransportCodec{get=>transportCodec;set{if(!Enum.IsDefined(value))return;Set(ref transportCodec,value);Changed(nameof(TransportCodecIndex));}}
    public int TransportCodecIndex{get=>(int)TransportCodec;set{if(value is >=0 and <=1)TransportCodec=(AudioTransportCodec)value;}}
    public bool CompressionSupported{get=>compressionSupported;private set=>Set(ref compressionSupported,value);}
    public string BufferBudgetSummary=>audio?.Buffer is {BufferConfigurationInsufficient:true} buffer
        ?$"当前 {MaximumBufferMs} ms 缓冲预算不足；实际输出/到包需要更高余量，建议选择至少 {Math.Ceiling(buffer.RecommendedBufferMs)} ms 的稳定档位。"
        :MaximumBufferMs>=40?$"预填充策略参考 {Math.Min(MaximumBufferMs,Math.Max(MaximumBufferMs/2d+1.5,MaximumBufferMs-24)):F1} ms；运行时按实际取数调整，起播最多保留目标加一次实际取数的已到数据，仍受原上限及预留空间限制。":"低延迟档：按实际输出与到包调度预填充，起播对齐目标水位；缓冲上限不等于耳机端到端延迟。";
    public string[] NetworkModes { get; } = ["dhcp", "static"];
    public string NetworkMode { get => networkMode; set => Set(ref networkMode, value); }
    public string NetworkIp { get => networkIp; set => Set(ref networkIp, value); }
    public string NetworkGateway { get => networkGateway; set => Set(ref networkGateway, value); }
    public string NetworkDns { get => networkDns; set => Set(ref networkDns, value); }
    public int NetworkPrefix { get => networkPrefix; set => Set(ref networkPrefix, value); }
    public string PendingTransaction { get => pendingTransaction; set { Set(ref pendingTransaction, value); Changed(nameof(CanModifyDiscoveredIp)); RefreshToggleButtons(); } }
    public string NetworkStatus { get => networkStatus; set => Set(ref networkStatus, value); }
    public bool CloseToTray { get => closeToTray; set => Set(ref closeToTray, value); }
    public bool StartInTray { get => startInTray; set => Set(ref startInTray, value); }
    public bool StartAtLogin { get => startAtLogin; set => Set(ref startAtLogin, value); }
    Settings CurrentSettings() => new(Address, rememberedDevice, LocalAddress, SelectedOutput?.Id, CloseToTray, StartInTray, StartAtLogin, Volume, Muted, PendingTransaction, pendingDevice,MaximumBufferMs,pendingUsbRevision,pendingBroadcastAdapter,IsNavigationExpanded,MinimizeToTray,ConnectionAnnouncementEnabled,autoConnectBoard,pendingRestartUsb,SelectedSearchAdapter?.Id,SelectedSearchAdapter?.Address,ResamplingQuality,NetworkFecEnabled,TransportCodec,SelectedInput?.Id,MicrophonePlaybackFirst,MicrophoneGain,MicrophoneMuted,processingSettings,pendingResumeListening,pendingResumeMicrophone,2,autoConnectLastDevice,autoConnectBoard,MonitorIntervalSeconds,autoConnectMigrationNotice,ClockModeIndex,udpPorts,lastAutomaticUdpPorts,AutoReconnectEnabled);
    public Task SaveSettings() { using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); const string name = "UsbAudioBridge"; object? old = run.GetValue(name); try { if (StartAtLogin) { string executable = EngineProtocol.FrontendExecutable; if (!File.Exists(executable)) throw new IOException("Application executable missing"); run.SetValue(name, '"' + executable + '"' + (StartInTray ? " --tray" : "")); } else run.DeleteValue(name, false); LocalStore.SaveSettings(CurrentSettings()); Log("本地设置已保存（当前用户）"); } catch { if (old is null) run.DeleteValue(name, false); else run.SetValue(name, old); throw; } return Task.CompletedTask; }
    public async Task ReadNetwork() { var cfg = await Session.RequestAsync("GET_CONFIG"); var n = cfg.GetProperty("network"); NetworkMode = n.GetProperty("mode").GetString()!; if (n.TryGetProperty("ip", out var ip)) NetworkIp = ip.GetString()!; if (n.TryGetProperty("prefix_length", out var prefix)) NetworkPrefix = prefix.GetInt32(); if (n.TryGetProperty("gateway", out var gw)) NetworkGateway = gw.GetString()!; if (n.TryGetProperty("dns", out var dns)) NetworkDns = dns.GetString()!; NetworkStatus = "已读取已提交网络配置"; }
    public async Task ApplyNetwork()
    {
        EnsureOwner();if(!string.IsNullOrEmpty(PendingTransaction))throw new InvalidOperationException("已有待处理事务，请先查询、确认或回滚。");
        if(NetworkMode is not ("dhcp" or "static"))throw new ArgumentException("请选择 DHCP 或静态网络。");
        string desiredMode=NetworkMode,desiredIp=NetworkIp,identity=Session.DeviceId,previousIp=Session.Address;
        object network;
        if(desiredMode=="static")
        {
            foreach(string value in new[]{NetworkIp,NetworkGateway,NetworkDns})if(!IPAddress.TryParse(value,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)throw new ArgumentException("请输入有效 IPv4。");
            if(NetworkPrefix is <1 or >30)throw new ArgumentException("前缀应为 1–30。");
            network=new{mode="static",ip=NetworkIp,prefix_length=NetworkPrefix,gateway=NetworkGateway,dns=NetworkDns};
        }
        else network=new{mode="dhcp"};
        var intent=CaptureUsbRuntimeIntent();bool restart=intent.Restart,resume=intent.Listen;
        await StopMicrophoneCore();await StopPlaybackCore();await SetUsbState(false);
        var config=await Session.RequestAsync("GET_CONFIG");string revision=config.GetProperty("config_revision").GetString()!;
        var candidate=new{network};JsonElement prepared;
        try{await Session.RequestAsync("VALIDATE_CONFIG",new{candidate},revision);prepared=await Session.RequestAsync("PREPARE_CONFIG",new{candidate},revision);}
        catch{await RestoreUsbRuntimeIntentAsync(intent);throw;}
        PendingTransaction=prepared.GetProperty("transaction_id").GetString()!;pendingDevice=identity;pendingUsbRevision="";pendingRestartUsb=restart;pendingResumeListening=intent.Listen;pendingResumeMicrophone=intent.Microphone;LocalStore.SaveSettings(CurrentSettings());
        NetworkStatus="正在应用网络配置并连接新地址…";
        try
        {
            try{await Session.RequestAsync("APPLY_CONFIG",new{transaction_id=PendingTransaction});}
            catch(IOException){ /* Reconnect and inspect the saved transaction before confirming. */ }
            disconnectAnnouncementPending=false;await Disconnect();await Task.Delay(750,lifetime.Token);
            var watch=System.Diagnostics.Stopwatch.StartNew();Exception? last=null;
            while(watch.Elapsed<TimeSpan.FromSeconds(desiredMode=="dhcp"?70:20))
            {
                lifetime.Token.ThrowIfCancellationRequested();
                try
                {
                    string? address=desiredMode=="static"?desiredIp:null;
                    if(address is null)
                    {
                        var devices=await DiscoveryService.DiscoverAsync(RequireSearchAdapter().Address,ct:lifetime.Token);
                        address=devices.FirstOrDefault(x=>x.DeviceId.Equals(identity,StringComparison.OrdinalIgnoreCase)&&IsSameSubnet(x.Address))?.Address;
                        if(address is null){await Task.Delay(500,lifetime.Token);continue;}
                    }
                    Address=address;LocalAddress=LocalAddressFor(address);SelectedDevice=null;
                    await ConnectCore(identity,false,false);EnsureOwner();
                    var info=await Session.RequestAsync("GET_INFO");var actual=info.GetProperty("network");
                    if(String(actual,"mode","")!=desiredMode||(desiredMode=="static"&&String(actual,"ip","")!=desiredIp))throw new IOException("正在等待候选网络生效。");
                    CheckTransaction();var operation=await Session.RequestAsync("GET_OPERATION",new{transaction_id=PendingTransaction});
                    string stage=String(operation,"stage","");
                    if(stage is not ("APPLIED" or "COMMITTED"))throw new IOException("候选网络未处于已应用状态，请查询事务。");
                    if(stage=="APPLIED")await Session.RequestAsync("CONFIRM_CONFIG",new{transaction_id=PendingTransaction});
                    ClearPendingTransaction();await ReadNetwork();RememberConnectedBoard();UpdateAutoConnectAddress();LocalStore.SaveSettings(CurrentSettings());
                    await RefreshOutputState();await RestoreUsbRuntimeIntentAsync(intent);
                    NetworkStatus="网络配置已保存，已自动连接新地址"+(restart?"并恢复模拟。":"。");Log(NetworkStatus);return;
                }
                catch(Exception error)when(error is not OperationCanceledException)
                {
                    last=error;
                    // Once CONFIRM has an unknown outcome, preserve its identity for explicit recovery.
                    if(string.IsNullOrEmpty(PendingTransaction))throw;
                    disconnectAnnouncementPending=false;await Disconnect();await Task.Delay(600,lifetime.Token);
                }
            }
            throw new IOException("新地址连接失败；已保留事务，设备未确认时会自动回滚。请检查是否在同一网段后手动连接。"+last?.Message,last);
        }
        catch{NetworkStatus="网络配置结果待确认。事务已保留，请重新连接后查询、确认或回滚。";throw;}
    }
    void ClearPendingTransaction(){PendingTransaction=pendingDevice=pendingUsbRevision=pendingBroadcastAdapter="";pendingRestartUsb=pendingResumeListening=pendingResumeMicrophone=false;LocalStore.SaveSettings(CurrentSettings());}
    void CheckTransaction() { if (string.IsNullOrEmpty(PendingTransaction)) throw new InvalidOperationException("没有待处理事务"); if (Session.DeviceId != pendingDevice) throw new InvalidOperationException("当前连接设备身份与事务不一致"); }
    public async Task QueryTransaction()
    {
        CheckTransaction();var data=await Session.RequestAsync("GET_OPERATION",new{transaction_id=PendingTransaction});NetworkStatus=Pretty(data);
        if(data.TryGetProperty("stage",out var stage)&&stage.GetString() is "COMMITTED" or "ROLLED_BACK")
        {
            var intent=new UsbRuntimeIntent(pendingRestartUsb,pendingResumeListening,pendingResumeMicrophone);ClearPendingTransaction();configLoaded=false;await ReadUsbConfiguration();await ReadCapabilities();await ReadNetwork();await RefreshOutputState();await RestoreUsbRuntimeIntentAsync(intent);
        }
    }
    public async Task FinishNetwork(bool confirm)
    {
        CheckTransaction();var intent=new UsbRuntimeIntent(pendingRestartUsb,pendingResumeListening,pendingResumeMicrophone);bool restart=intent.Restart;
        await Session.RequestAsync(confirm?"CONFIRM_CONFIG":"ROLLBACK_CONFIG",new{transaction_id=PendingTransaction,expected_emulation_revision=string.IsNullOrEmpty(pendingUsbRevision)?null:pendingUsbRevision});
        ClearPendingTransaction();NetworkStatus=confirm?"配置已确认提交":"已请求回滚；请重新搜索实际地址";
        configLoaded=false;await ReadUsbConfiguration();await ReadCapabilities();await ReadNetwork();UpdateAutoConnectAddress();LocalStore.SaveSettings(CurrentSettings());await RefreshOutputState();await RestoreUsbRuntimeIntentAsync(intent);
        Log(NetworkStatus);
    }
}
