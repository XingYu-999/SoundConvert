using System.Windows.Input;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    RememberedBoard? autoConnectBoard;
    bool autoConnectLastDevice=true,autoConnectMigrationNotice;
    CancellationTokenSource? autoConnectCancellation;
    bool startupConnectionAttempted,isConnecting;
    string? startupConnectionLabel;
    bool? connectionSwitchTarget;
    readonly CancellationTokenSource lifetime=new();
    public bool IsConnecting{get=>isConnecting;private set{Set(ref isConnecting,value);Changed(nameof(ConnectionButtonText));Changed(nameof(CanToggleConnection));RefreshToggleButtons();}}
    public string ConnectionButtonText=>connectionSwitchTarget is bool connect?connect?"正在连接…":"正在断开连接…":IsConnecting?"正在连接…":IsConnected?"断开连接":"连接";
    public bool CanToggleConnection=>!shuttingDown&&!IsBusy&&!IsConnecting&&!IsReconnecting&&connectionSwitchTarget is null&&(IsConnected||(SelectedSearchAdapter is { IsUp:true, HasIpv4:true }&&!string.IsNullOrWhiteSpace(Address)));
    public string AutoConnectSummary=>(autoConnectMigrationNotice?"升级后默认自动连接上次设备，可关闭。 ":"")+(!autoConnectLastDevice?"自动连接已关闭；保留最近设备。":autoConnectBoard is null?"已开启，首次成功连接后记住设备。":$"后台启动时连接 {autoConnectBoard.Name} · {autoConnectBoard.Address}；只在原网卡按唯一 ID 搜索一次。");
    public bool AutoConnectCurrent
    {
        get=>autoConnectLastDevice;
        set
        {
            if(value==autoConnectLastDevice)return;
            autoConnectLastDevice=value;autoConnectMigrationNotice=false;
            if(!value)
            {
                autoConnectCancellation?.Cancel();
                if(startupConnectionLabel is { } label && (!IsConnected||IsConnecting))
                { Status=OperationStatus=$"正在取消自动连接 {label}…";Log(Status); }
            }
            LocalStore.SaveSettings(CurrentSettings());Changed();Changed(nameof(AutoConnectSummary));Changed(nameof(CanConfigureAutoConnect));
        }
    }
    public bool CanConfigureAutoConnect=>!shuttingDown;
    public ICommand ToggleConnectionCommand=>SyncCommand(nameof(ToggleConnectionCommand),_=>_=ToggleConnectionAsync(),()=>CanToggleConnection);
    async Task ToggleConnectionAsync(CancellationToken cancellation=default)
    {
        if(!CanToggleConnection)return;
        bool connect=!IsConnected;connectionSwitchTarget=connect;RefreshShell();
        try{await Execute(async()=>{if(connect)await Connect(cancellation);else await Disconnect();},connect?"正在连接…":"正在断开连接…");}
        finally{connectionSwitchTarget=null;RefreshShell();}
    }
    public ICommand ClearAutoConnectCommand=>SyncCommand(nameof(ClearAutoConnectCommand),_=>{AutoConnectCurrent=false;});
    void ReportStartupProgress(string target,string stage="")
    {
        Status=$"正在自动连接 {target}…"+(stage.Length==0?"":" "+stage);
        OperationStatus=Status;Log(Status);
    }
    void ReportStartupCanceled(string target)
    {
        GlobalError="";Status=$"操作已取消：已取消自动连接 {target}。";Log(Status);
    }
    void ReportStartupFailure(string target,string reason,string check)
    {
        Status=GlobalError=$"连接失败（{target}）：{reason}。{check}";Log(Status);
    }
    public async Task InitializeAsync()
    {
        if(startupConnectionAttempted)return;startupConnectionAttempted=true;
        bool intended=autoConnectLastDevice&&autoConnectBoard is not null;
        string? initialTarget=intended?ConnectionFeedback.Target(autoConnectBoard!.Name,autoConnectBoard.DeviceId):null;
        await Execute(async()=>
        {
            Status=initialTarget is null?"正在初始化音频设备…":$"正在准备自动连接 {initialTarget} · 初始化音频设备…";
            await RefreshOutputState();await RefreshInputState();
            if(!autoConnectLastDevice)
            {Status=intended?"操作已取消：自动连接已关闭。":"自动连接已关闭，请搜索设备或手动连接。";Log(Status);return;}
            if(autoConnectBoard is not { } target)
            {Status="尚无已保存的设备，请搜索设备或手动连接。";Log(Status);return;}
            string label=ConnectionFeedback.Target(target.Name,target.DeviceId);
            if(!string.IsNullOrEmpty(PendingTransaction))
            {ReportStartupFailure(label,"网络配置事务待确认","请先在虚拟声卡页查询、确认或回滚原事务，再连接设备。");return;}
            var matches=networkAdapters().Where(x=>target.AdapterId is not null?x.Id==target.AdapterId&&x.Address==target.LocalAddress:x.Address==target.LocalAddress).ToArray();
            if(matches.Length!=1||!matches[0].IsUp||!matches[0].HasIpv4)
            {ReportStartupFailure(label,"原网卡不可用或 IPv4 地址已变化","请检查网线、原网卡连接状态与 IPv4，刷新并选择正确网卡后重新搜索或手动连接。");return;}
            SelectedSearchAdapter=SearchAdapters.FirstOrDefault(x=>x.Id==matches[0].Id&&x.Address==matches[0].Address);
            startupConnectionLabel=label;
            autoConnectCancellation?.Dispose();autoConnectCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var cancellation=autoConnectCancellation.Token;
            try
            {
                var adapter=RequireSearchAdapter();ReportStartupProgress(label);
                await AutoConnectFlow.RunAsync(target.DeviceId,target.Address,target.ControlPort,
                    async(ip,port,id)=>{cancellation.ThrowIfCancellationRequested();SelectedDevice=null;Address=ip;ControlPort=port;LocalAddress=adapter.Address;await ConnectCore(id,cancellationToken:cancellation);},
                    async()=>{cancellation.ThrowIfCancellationRequested();ReportStartupProgress(label,$"正在通过 {adapter.Name} 搜索当前地址。");return await DiscoveryService.DiscoverAsync(adapter.Address,ct:cancellation);},IsSameSubnet);
                if(IsConnected){Status=ConnectedStatus;Log($"自动连接成功：{ConnectedName}。");}
                else if(cancellation.IsCancellationRequested&&!shuttingDown)ReportStartupCanceled(label);
            }
            catch(Exception)when(cancellation.IsCancellationRequested&&!IsConnected&&!shuttingDown)
            {ReportStartupCanceled(label);}
            catch(Exception error)when(!shuttingDown)
            {LocalStore.Log("自动连接失败详情："+error);Status=GlobalError=ConnectionFeedback.BoardFailure(label,error,target.ControlPort);Log(Status);}
            finally{autoConnectCancellation.Dispose();autoConnectCancellation=null;startupConnectionLabel=null;RefreshCancellationState();}
        },initialTarget is null?"正在初始化音频设备…":$"正在自动连接 {initialTarget}…");
    }
    bool IsSameSubnet(string address)=>SelectedSearchAdapter is { IsUp:true, HasIpv4:true } adapter&&FormatPolicy.SameSubnet(address,adapter.Address,adapter.Mask);
    string LocalAddressFor(string address)=>RequireSearchAdapter().Address;
    void UpdateAutoConnectAddress()
    {
        if(!CanManage)return;
        autoConnectBoard=new(Session.DeviceId,ConnectedName,Session.Address,LocalAddress,ControlPort,UsbSerial,DateTimeOffset.UtcNow,AdapterId:SelectedSearchAdapter?.Id);
        Changed(nameof(AutoConnectCurrent));Changed(nameof(AutoConnectSummary));
    }
    public async Task ConnectTargetAsync(string address,int port,string adapterKey,string? expectedDeviceId,CancellationToken cancellationToken=default)
    {
        if(IsConnected)throw new InvalidOperationException("先断开当前设备。");
        if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port),"端口须为 1–65535。");
        if(!System.Net.IPAddress.TryParse(address,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||ip.ToString()!=address||ip.GetAddressBytes()[0] is 0 or >=224)
            throw new ArgumentException("请输入有效的单播点分 IPv4 地址。");
        var adapter=SearchAdapters.SingleOrDefault(x=>EngineProtocol.AdapterKey(x)==adapterKey&&x.IsUp&&x.HasIpv4)??throw new InvalidOperationException("所选网卡不可用，请刷新后重新选择。");
        var targetBytes=ip.GetAddressBytes();var adapterBytes=System.Net.IPAddress.Parse(adapter.Address).GetAddressBytes();var maskBytes=System.Net.IPAddress.Parse(adapter.Mask).GetAddressBytes();
        if(Enumerable.Range(0,4).All(i=>(targetBytes[i]&maskBytes[i])==(adapterBytes[i]&maskBytes[i]))&&Enumerable.Range(0,4).All(i=>(targetBytes[i]|maskBytes[i])==255))
            throw new ArgumentException("不能连接所选网段的广播地址。");
        cancellationToken.ThrowIfCancellationRequested();
        SelectedSearchAdapter=adapter;RequireSearchAdapter();SelectedDevice=null;Address=address;ControlPort=port;
        await ConnectCore(expectedDeviceId,cancellationToken:cancellationToken);
    }
    async Task SetUsbState(bool enabled)
    {
        if(!enabled){++captureWriteGeneration;captureUsbPending=false;await StopMicrophoneCore();}
        var info=await Session.RequestAsync("GET_INFO");var emulation=info.GetProperty("usb_emulation");string target=enabled?"RUNNING":"STOPPED";
        if(String(emulation,"state","")!=target)
        {
            await Session.RequestAsync(enabled?"USB_EMULATION_START":"USB_EMULATION_STOP",new{expected_emulation_revision=emulation.GetProperty("revision").GetString()});
            var watch=System.Diagnostics.Stopwatch.StartNew();
            do
            {
                await Task.Delay(60,lifetime.Token);info=await Session.RequestAsync("GET_INFO");
                string state=String(info.GetProperty("usb_emulation"),"state","");
                if(state==target)break;
                if(state=="FAULT")throw new IOException("虚拟声卡模拟异常，请检查设备。");
                if(watch.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("等待模拟状态切换超时，请查询设备实际状态。");
            }while(true);
        }
        await Refresh();RefreshFormatOptions();if(enabled&&MicrophoneSupported&&microphoneIntent&&!captureReconfiguring)await StartMicrophoneCore();
    }
}
