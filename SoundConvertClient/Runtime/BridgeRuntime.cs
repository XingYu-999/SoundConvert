using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

using UsbAudioBridge;
namespace SoundConvertClient;

public sealed record DeviceRow(Device Device,long DiscoveryGeneration=0)
{
    public string DiscoveryPath=>Device.LocalAddress;
    public string Name=>Device.Model;
    public string Detail=>$"{Device.Address}:{Device.ControlPort} · {Device.LocalAddress} · 固件 {Device.Firmware switch { ""=>"未知",var v=>v }}";
    public string Label=>$"{Name} · {Detail}";
    public string Identity=>Device.DeviceId;
}
public sealed record UiLogEntry(string Time,string Message);

public sealed partial class BridgeRuntime:INotifyPropertyChanged,IAsyncDisposable,IBridgeView
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName]string? name=null)
    {
        PropertyChanged?.Invoke(this,new(name));
        var udpRelated=UdpPortPresentation.Related(name);
        if(udpRelated.Mode is {} mode)PropertyChanged?.Invoke(this,new(mode));
        if(udpRelated.Number is {} number)PropertyChanged?.Invoke(this,new(number));
        if(name is nameof(IsConnected) or nameof(IsConnecting))PropertyChanged?.Invoke(this,new(nameof(IsConnectionReady)));
    }
    void Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;Changed(name);}
    string address="",localAddress="0.0.0.0",status="未连接",message="准备就绪",deviceInfo="",logs="",audioStats="尚未监听",productName="W5500-EVB-Pico2",rememberedDevice="";
    string connectedName="开发板",usbSerial="",usbState="未知",playbackLabel="尚未连接开发板",declaredFormats="16-bit / 48 kHz · 双声道",actualFormat="USB 主机尚未选择音频格式",firmwareVersion="—",networkLabel="—",deviceIdentity="—",committedProductName="W5500-EVB-Pico2";
    string packetCount="—",bufferLevel="—",driftText="—",integrityText="尚未开始",outputBackend="尚未打开输出";
    double volume=100;bool muted,canManage,canEditUsb,hiResEnabled,committedHiRes,hiResSupported,connectionHealthy,configLoaded,usbHostStreaming,usbHostFormatSelected;
    int controlPort=49161;
    string usbBlockReason="请先连接开发板";
    DeviceRow? selectedDevice;OutputEndpoint? selectedOutput;
    long discoveryGeneration;
    ControlSession? control;AudioReceiver? receiver;WindowsAudio? audio;
    bool listeningIntent,polling,shuttingDown,wasManagementSession,disconnectAnnouncementPending;long lastAccepted=-1;int recoveryFailures;
    readonly SemaphoreSlim commands=new(1);
    readonly RuntimeTimer timer=new(){Interval=TimeSpan.FromMilliseconds(100)};
    public ObservableCollection<DeviceRow> Devices{get;}=[];
    public ObservableCollection<OutputEndpoint> Outputs{get;}=[];
    public ObservableCollection<UiLogEntry> LogEntries{get;}=[];
    public bool IsConnected=>connectionHealthy&&control is { IsConnected:true };
    public bool IsConnectionReady=>IsConnected&&!IsConnecting;
    string ConnectedStatus=>$"已连接 · {ConnectionFeedback.Target(ConnectedName,DeviceIdentity)}";
    public bool IsObserver=>IsConnected&&!CanManage;
    public bool IsListening=>receiver is not null&&receiver.Fault is null&&!receiver.Ended&&!receiver.IsRecovering&&receiver.State=="RUNNING"&&usbHostStreaming&&usbHostFormatSelected&&receiver.LastAudioAgeMs is >=0 and <=250;
    public bool CanManage{get=>canManage;private set{Set(ref canManage,value);RefreshShell();}}
    public bool CanEditUsb{get=>IsConnected&&CanManage&&!IsBusy&&string.IsNullOrEmpty(PendingTransaction)&&UsbState is "RUNNING" or "STOPPED";private set{Set(ref canEditUsb,value);Changed(nameof(CanEditHiRes));}}
    public bool CanEditHiRes=>CanEditUsb&&HiResSupported;
    public string UsbBlockReason{get=>usbBlockReason;private set=>Set(ref usbBlockReason,value);}
    public string Address{get=>address;set{Set(ref address,value);Changed(nameof(CanConnect));Changed(nameof(CanToggleConnection));RefreshCommandStates();}}
    public string LocalAddress{get=>localAddress;private set=>Set(ref localAddress,value);}
    public int ControlPort{get=>controlPort;set=>Set(ref controlPort,value);}
    public string Status{get=>status;private set=>Set(ref status,value);}
    public string Message{get=>message;set=>Set(ref message,value);}
    public string DeviceInfo{get=>deviceInfo;set=>Set(ref deviceInfo,value);}
    public string Logs{get=>logs;set=>Set(ref logs,value);}
    public string AudioStats{get=>audioStats;set=>Set(ref audioStats,value);}
    public string ConnectedName{get=>connectedName;private set{Set(ref connectedName,value);if(IsConnectionReady)Status=ConnectedStatus;}}
    public string UsbSerial{get=>usbSerial;private set=>Set(ref usbSerial,value);}
    public string UsbState{get=>usbState;private set{Set(ref usbState,value);RefreshToggleButtons();}}
    public string PlaybackLabel{get=>playbackLabel;private set{Set(ref playbackLabel,value);RefreshShell();}}
    public string DeclaredFormats{get=>declaredFormats;private set=>Set(ref declaredFormats,value);}
    public string ActualFormat{get=>actualFormat;private set=>Set(ref actualFormat,value);}
    public string FirmwareVersion{get=>firmwareVersion;private set=>Set(ref firmwareVersion,value);}
    public string NetworkLabel{get=>networkLabel;private set=>Set(ref networkLabel,value);}
    public string DeviceIdentity{get=>deviceIdentity;private set=>Set(ref deviceIdentity,value);}
    public string PacketCount{get=>packetCount;private set=>Set(ref packetCount,value);}
    public string BufferLevel{get=>bufferLevel;private set=>Set(ref bufferLevel,value);}
    public string DriftText{get=>driftText;private set=>Set(ref driftText,value);}
    public string IntegrityText{get=>integrityText;private set=>Set(ref integrityText,value);}
    public string OutputBackend{get=>outputBackend;private set=>Set(ref outputBackend,value);}
    public string NetworkAudioDiagnostics => receiver is null ? "{}" : JsonSerializer.Serialize(receiver.Statistics());
    public string ProductName{get=>productName;set{Set(ref productName,value);Changed(nameof(UsbDraftState));}}
    public bool HiResEnabled{get=>hiResEnabled;set{Set(ref hiResEnabled,value);Changed(nameof(UsbDraftState));}}
    public bool HiResSupported{get=>hiResSupported;private set{Set(ref hiResSupported,value);Changed(nameof(CanEditHiRes));}}
    public string UsbDraftState=>!IsConnected||!configLoaded?"未读取当前设备配置；连接后读取已保存配置。":HasUsbConfigurationDraftChanges()?"存在未保存修改；保存时自动停止模拟、提交配置，再恢复原模拟状态。":"当前显示已提交配置。";
    public double Volume{get=>volume;set{Set(ref volume,value);if(audio is not null)audio.Buffer.Gain=(float)value/100;}}
    public bool Muted{get=>muted;set{Set(ref muted,value);if(audio is not null)audio.Buffer.Mute=value;}}
    public DeviceRow? SelectedDevice{get=>selectedDevice;set{Set(ref selectedDevice,value);Changed(nameof(CanModifyDiscoveredIp));if(value is not null){Address=value.Device.Address;ControlPort=value.Device.ControlPort;rememberedDevice=value.Device.DeviceId;}}}
    public OutputEndpoint? SelectedOutput{get=>selectedOutput;set{if(selectedOutput==value)return;bool changed=selectedOutput?.Id!=value?.Id||selectedOutput?.DeviceId!=value?.DeviceId;Set(ref selectedOutput,value);if(changed)OutputSelectionChanged();}}
    ControlSession Session=>control??throw new InvalidOperationException("请先连接开发板");

    public BridgeRuntime(IAudioOutputControl? outputControl=null, Func<IReadOnlyList<NetworkAdapterInfo>>? networkAdapters=null,IAudioInputControl? inputControl=null,Func<string,CancellationToken,Task<IReadOnlyList<Device>>>? reconnectDiscovery=null)
    {
        this.outputControl=outputControl??new WindowsOutputAudioControl();this.inputControl=inputControl??new WindowsInputAudioControl();
        this.networkAdapters=networkAdapters??NetworkAdapterInfo.GetAll;
        this.reconnectDiscovery=reconnectDiscovery??((local,ct)=>DiscoveryService.DiscoverAsync(local,ct:ct));
        InitializeFormats();var settings=LocalStore.LoadSettings();LoadUdpPorts(settings);try{settings.Processing?.Validate();processingSettings=settings.Processing??new();}catch(ArgumentException){processingSettings=new();}autoConnectBoard=settings.LastSuccessfulDevice??settings.AutoConnectBoard;autoConnectLastDevice=settings.AutoConnectLastDevice;autoReconnectEnabled=settings.AutoReconnectEnabled;autoConnectMigrationNotice=settings.AutoConnectMigrationNotice;monitorIntervalSeconds=LocalStore.ValidMonitorInterval(settings.MonitorIntervalSeconds);Address=settings.Address;rememberedDevice=settings.DeviceId;
        LoadSearchAdapters(settings.SearchAdapterId,settings.SearchAdapterAddress??settings.LocalAddress);
        CloseToTray=settings.CloseToTray;StartInTray=settings.StartInTray;StartAtLogin=settings.StartAtLogin;Volume=settings.Volume;Muted=settings.Muted;
        PendingTransaction=settings.PendingTransaction??"";pendingDevice=settings.PendingDevice??"";MaximumBufferMs=settings.MaximumBufferMs;ResamplingQuality=settings.ResamplingQuality;NetworkFecEnabled=settings.NetworkFecEnabled;TransportCodec=settings.TransportCodec;ClockModeIndex=settings.ClockModeIndex;
        pendingResumeListening=settings.PendingResumeListening;pendingResumeMicrophone=settings.PendingResumeMicrophone;pendingRestartUsb=settings.PendingRestartUsb;pendingUsbRevision=settings.PendingUsbRevision??"";pendingBroadcastAdapter=settings.PendingBroadcastAdapter??"";
        IsNavigationExpanded=settings.NavigationExpanded;MinimizeToTray=settings.MinimizeToTray;ConnectionAnnouncementEnabled=settings.ConnectionAnnouncementEnabled;
        LoadRemembered(settings);
        try{RefreshOutputs();SelectedOutput=settings.OutputId is null?Outputs.FirstOrDefault():Outputs.FirstOrDefault(x=>x.Id==settings.OutputId);}catch(Exception error){Log(error.Message);}
        InitializeInputs(settings.InputId);MicrophonePlaybackFirst=settings.MicrophonePlaybackFirst;MicrophoneGain=settings.MicrophoneGain;MicrophoneMuted=settings.MicrophoneMuted;
        initializing=false;timer.Tick+=async(_,_)=>await Tick();timer.Start();RefreshShell();
    }
    Task outputPoll=Task.CompletedTask,recoveryPoll=Task.CompletedTask,statusPoll=Task.CompletedTask,monitorPoll=Task.CompletedTask;
    DateTimeOffset nextRecoveryPoll,nextStatusPoll,nextMetrics;
    Task Tick()
    {
        if(shuttingDown)return Task.CompletedTask;
        var now=DateTimeOffset.UtcNow;
        // A due detailed sample gets one nonblocking opportunity before routine
        // endpoint/status polls; otherwise coincident 1s/3s polls starve it forever.
        if(monitorPoll.IsCompleted)monitorPoll=RunPoll(PollMonitor);
        if(recoveryPoll.IsCompleted&&now>=nextRecoveryPoll){nextRecoveryPoll=now.AddSeconds(1);recoveryPoll=RunPoll(PollRecovery);}
        if(statusPoll.IsCompleted&&now>=nextStatusPoll){nextStatusPoll=now.AddSeconds(runtimeStatusSupported?1:3);statusPoll=RunPoll(PollStatus);}
        if(outputPoll.IsCompleted)outputPoll=RunPoll(PollOutputState);
        if(now>=nextMetrics){nextMetrics=now.AddSeconds(1);UpdatePlaybackMetrics();UpdateInputMetrics();}
        RefreshMonitorAge();return Task.CompletedTask;
    }
    async Task RunPoll(Func<Task> poll){try{await poll();}catch(Exception error){if(!shuttingDown)Log("状态刷新："+error.Message);}}
    public async Task Execute(Func<Task> action,string progress="正在执行操作…")
    {
        if(shuttingDown||IsBusy)return;
        // Reserve the user's action before waiting for a background poll. Further
        // clicks are disabled while this first action waits, rather than dropped.
        SetUserCommandPending(true);GlobalError="";bool acquired=false;
        try
        {
            OperationStatus=commands.CurrentCount==0?progress+" 等待当前状态刷新完成…":progress;
            await commands.WaitAsync(lifetime.Token);acquired=true;
            if(shuttingDown)return;
            OperationStatus=progress;await action();
        }
        catch(OperationCanceledException)when(shuttingDown){}
        catch(Exception error){GlobalError=ConnectionFeedback.Message(error);Log(GlobalError);}
        finally{if(acquired)commands.Release();SetUserCommandPending(false);OperationStatus="";RefreshShell();}
    }
    public void Log(string text)
    {
        Message=text;LogEntries.Insert(0,new(DateTime.Now.ToString("HH:mm:ss"),text));while(LogEntries.Count>200)LogEntries.RemoveAt(LogEntries.Count-1);
        Logs=$"{DateTime.Now:HH:mm:ss}  {text}\n"+Logs;if(Logs.Length>24000)Logs=Logs[..24000];LocalStore.Log(text);
    }
    public Task Discover() => DiscoverAsync(lifetime.Token);
    public async Task DiscoverAsync(CancellationToken cancellation)
    {
        var adapter=RequireSearchAdapter();
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);
        searchCancellation=stop; RefreshCancellationState();
        long generation=++discoveryGeneration; SelectedDevice=null; Devices.Clear(); udpPeers.Clear();
        Status=$"正在通过 {adapter.Name} 使用 UDP 广播搜索设备…"; Log(Status);
        try
        {
            var peers=await UdpDiscovery.DiscoverPeersAsync(stop.Token,adapter.Address);
            stop.Token.ThrowIfCancellationRequested();
            foreach(var peer in peers) { udpPeers[peer.Device.DeviceId+"|"+peer.AdapterName]=peer; Devices.Add(new(peer.Device,generation)); }
            if(Devices.Count>0&&!IsConnected)SelectedDevice=Devices[0];
            Status=$"UDP 广播发现 {Devices.Count} 台设备。";Log(Status);
        }
        catch(OperationCanceledException) { Status="搜索已取消。"; Log(Status); throw; }
        finally { searchCancellation=null;RefreshCancellationState();RefreshShell(); }
    }
    public Task Connect()=>Connect(default);
    public Task Connect(CancellationToken cancellation)=>ConnectCore(SelectedDevice?.Device.Address==Address?SelectedDevice.Device.DeviceId:null,cancellationToken:cancellation);
    async Task ConnectCore(string? expectedIdentity=null,bool startListening=true,bool announce=true,CancellationToken cancellationToken=default,bool recovering=false)
    {
        if(!recovering){StopAutomaticReconnect(clearTarget:true);connectionCancelRequested=false;}
        string connectingTarget=ConnectionFeedback.Target(Devices.FirstOrDefault(row=>row.Identity==expectedIdentity)?.Name,expectedIdentity??Address);
        using var attempt=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(20)); activeConnectionCancellation=attempt;
        IsConnecting=true;RefreshCancellationState();try{
        await DisconnectCore(cancelAutoConnect:false);configLoaded=false;lastMonitorSample=default;nextMonitor=DateTimeOffset.MinValue;MonitorDetails="正在等待当前连接的首次状态快照";
        if(!System.Net.IPAddress.TryParse(Address,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||ip.Equals(System.Net.IPAddress.Any))throw new ArgumentException("请选择可达的开发板 IPv4；无地址设备请使用UDP 广播网络恢复。");
        var session=new ControlSession { OperationCancellation = attempt.Token };
        try
        {
            var adapter=RequireSearchAdapter();LocalAddress=adapter.Address;
            var connectionCancellation=attempt;
            if(startupConnectionLabel is { } targetLabel)ReportStartupProgress(targetLabel);
            else Status=$"正在连接 {connectingTarget}…";
            Log(startupConnectionLabel is { } label?$"正在自动连接 {label} · 校验开发板身份…":"正在连接并校验开发板身份…");await session.ConnectAsync(Address,ControlPort,connectionCancellation.Token,LocalAddress);
            if(expectedIdentity is not null&&!session.DeviceId.Equals(expectedIdentity,StringComparison.OrdinalIgnoreCase))throw new IOException("设备唯一标识与目标不一致，已拒绝连接。");
            attempt.Token.ThrowIfCancellationRequested();
            control=session;connectionHealthy=true;rememberedDevice=session.DeviceId;CanManage=session.IsOwner;wasManagementSession=session.IsOwner;
            await Refresh();await ReadCapabilities();await ReadUsbConfiguration();RefreshOutputs();RefreshInputs();await RefreshInputState();await RefreshOutputState();await ReadNetwork();
            if(!recovering&&CanManage&&UsbState=="RUNNING"&&string.IsNullOrEmpty(PendingTransaction))try{await ApplyOutputFormats();}catch(InvalidOperationException error){GlobalError=error.Message;Log(error.Message);}
            attempt.Token.ThrowIfCancellationRequested();
            RememberConnectedBoard();UpdateAutoConnectAddress();LocalStore.SaveSettings(CurrentSettings());Log(CanManage?"管理连接已建立。":"已连接为只读观察，不接管现有监听。");
        }
        catch(Exception error)
        {
            connectionHealthy=false;control=null;CanManage=false;disconnectAnnouncementPending=false;InvalidateCurrentBoardState();await session.DisposeAsync();Status="未连接";RefreshMonitorAge();
            if(error is OperationCanceledException&&!cancellationToken.IsCancellationRequested&&!lifetime.IsCancellationRequested&&!attempt.IsCancellationRequested)
                throw new TimeoutException("读取开发板状态或配置超时。",error);
            throw;
        }
        disconnectAnnouncementPending=CanManage;
        if(!startListening){PlaybackLabel=CanManage?"虚拟声卡已连接，播放已停止。":"已连接为只读观察，由管理端控制播放。";return;}
        if(!CanManage){PlaybackLabel="只读观察；已有管理端控制声音。";return;}
        if(SelectedOutput is null){PlaybackLabel="已连接，但没有安全的电脑输出设备。";Log("请连接耳机或扬声器；开发板自己的 USB 声卡不能作为监听输出。");return;}
        if(announce&&ConnectionAnnouncementEnabled)
            try{if(SelectedOutput.DeviceId is {} endpoint&&!IsBoardOutput(SelectedOutput))await ConnectionAnnouncement.PlayAsync(endpoint);}catch(Exception error){Log("连接提示音未播放："+error.Message);}
        attempt.Token.ThrowIfCancellationRequested();
        try{await Listen();attempt.Token.ThrowIfCancellationRequested();}
        catch(OperationCanceledException) when(attempt.IsCancellationRequested) { throw; }
        catch(DeviceException error)when(error.Code is "WAITING_USB" or "USB_EMULATION_STOPPED" or "USB_EMULATION_TRANSITION_BUSY"){PlaybackLabel="已连接，等待 USB 音源就绪。";Log(PlaybackLabel);}
        catch(Exception error){listeningIntent=false;PlaybackLabel="已连接，监听启动失败。";GlobalError=error.Message;Log(error.Message);}
        }
        catch(OperationCanceledException) when(attempt.IsCancellationRequested)
        {
            await DisconnectCore(cancelAutoConnect:false);
            if(cancellationToken.IsCancellationRequested || connectionCancelRequested || lifetime.IsCancellationRequested) { Status="连接已取消。";Log(Status);throw; }
            Status="连接超时，已停止本次连接。";throw new TimeoutException(Status);
        }
        catch(Exception) { if(!IsConnected) { Status="连接失败，请检查设备后重试。";RefreshMonitorAge(); } throw; }
        finally { if(control is not null)control.OperationCancellation=default;activeConnectionCancellation=null;connectionCancelRequested=false;IsConnecting=false;RefreshCancellationState();if(IsConnected){Status=ConnectedStatus;RememberReconnectTarget();}RefreshShell(); }
    }
    public Task Refresh()=>RefreshState(false);
    async Task RefreshState(bool runtimePoll)
    {
        var data=await Session.RequestAsync(runtimePoll&&runtimeStatusSupported?"GET_STATUS":"GET_INFO");if(!runtimePoll)CacheBoardIdentity(data);DeviceInfo=Pretty(data);connectionHealthy=true;CanManage=Session.IsOwner;CanEditUsb=false;
        DeviceIdentity=Session.DeviceId;FirmwareVersion=String(data,"firmware",String(data,"firmware_version",FirmwareVersion));
        if(data.TryGetProperty("model",out var model)&&string.IsNullOrWhiteSpace(committedProductName))ConnectedName=model.GetString()??"开发板";
        if(data.TryGetProperty("usb_emulation",out var emulation))
        {
            UsbState=String(emulation,"state","未知");
            CanEditUsb=CanManage&&UsbState=="STOPPED"&&emulation.TryGetProperty("can_modify_usb_config",out var allowed)&&allowed.GetBoolean();
            UsbBlockReason=CanManage?"运行时可编辑；保存会自动停止模拟、提交配置并恢复模拟。":"本连接为只读观察；无法修改设备配置。";
        }
        if(data.TryGetProperty("usb",out var usb))
        {
            ReadHidCompatibilityStatus(usb);activeUsbDeviceProfile=String(usb,"active_device_profile","unknown");Changed(nameof(ActiveUsbDeviceProfile));BoardLinkSpeed=String(usb,"link_speed",BoardLinkSpeed);UsbSerial=String(usb,"serial",UsbSerial);UsbProtocol=String(usb,"protocol","UAC1 / USB Full-Speed");if(usb.TryGetProperty("formats_mask",out var mask))activeFormats=mask.GetUInt32();
            usbHostStreaming=usb.TryGetProperty("streaming",out var streaming)&&streaming.GetBoolean();
            bool selected=usb.TryGetProperty("format_selected",out var formatSelected)?formatSelected.GetBoolean():usbHostStreaming;usbHostFormatSelected=selected;
            ReadHostFormat(usb,selected);
        }
        if(data.TryGetProperty("network",out var network))NetworkLabel=$"{String(network,"ip",Address)} · {String(network,"state",String(network,"mode",""))}";
        Status=IsConnecting?startupConnectionLabel is { } label?$"正在自动连接 {label}…":$"正在连接 {ConnectionFeedback.Target(ConnectedName,DeviceIdentity)}…":ConnectedStatus;
        ReadUsbHostState(data);ReadCaptureStatus(data);RefreshFormatOptions();RefreshInputOptions();RefreshShell();
    }
    async Task ReadCapabilities()
    {
        var data=await Session.RequestAsync("GET_CAPABILITIES");ReadHostKeyCapabilities(data);ReadUsbProfileCapabilities(data);ReadIndicatorCapabilities(data);ReadUdpPortCapabilities(data);captureVolumeSupported=data.TryGetProperty("capture_volume_supported",out var cv)&&cv.GetBoolean();Changed(nameof(CanControlCaptureVolume));HiResSupported=data.TryGetProperty("hi_res_supported",out var available)&&available.GetBoolean();MicrophoneSupported=data.TryGetProperty("capture_supported",out var cap)&&cap.GetBoolean();runtimeStatusSupported=data.TryGetProperty("runtime_status_supported",out var runtime)&&runtime.GetBoolean();
        formatSelectionSupported=data.TryGetProperty("format_selection_supported",out var selection)&&selection.GetBoolean();
        internalVolumeSupported=data.TryGetProperty("internal_volume_control_supported",out var master)&&master.GetBoolean();
        CompressionSupported=data.TryGetProperty("audio_codecs",out var codecs)&&codecs.ValueKind==JsonValueKind.Array&&codecs.EnumerateArray().Any(x=>x.GetString()=="ima_adpcm");
        if(data.TryGetProperty("formats",out var formats))DeclaredFormats=FormatsLabel(formats);RefreshFormatOptions();
    }
    public async Task ReadUsbConfiguration()
    {
        var data=await Session.RequestAsync("GET_CONFIG");bool dirty=configLoaded&&HasUsbConfigurationDraftChanges();
        if(data.TryGetProperty("usb",out var usb))
        {
            ReadUsbProfileConfiguration(usb,dirty);ReadIndicatorConfiguration(usb,dirty);committedProductName=String(usb,"product_name","W5500-EVB-Pico2");ConnectedName=committedProductName;
            committedHiRes=usb.TryGetProperty("hi_res_enabled",out var hiRes)&&hiRes.GetBoolean();
            committedFormats=usb.TryGetProperty("formats_mask",out var mask)?mask.GetUInt32():committedHiRes?3u:1u;
            if(!dirty){ProductName=committedProductName;HiResEnabled=committedHiRes;SetFormatDraft(committedFormats);}
            if(usb.TryGetProperty("declared_formats",out var declared))DeclaredFormats=FormatsLabel(declared);
        }
        if(data.TryGetProperty("usb",out var configUsb)&&configUsb.TryGetProperty("capture_formats_mask",out var cm)){committedCaptureFormats=cm.GetUInt32();if(!dirty)SetCaptureDraft(committedCaptureFormats);}
        configLoaded=true;DeviceInfo=Pretty(data);Changed(nameof(UsbDraftState));Changed(nameof(CommittedUsbDeviceProfile));Changed(nameof(UsbHidCompatibilityState));RefreshShell();
        if(dirty)Log("已刷新板端配置，保留界面未保存草稿。");
    }
    public async Task Query(string operation){DeviceInfo=Pretty(await Session.RequestAsync(operation));if(operation=="GET_CAPABILITIES")await ReadCapabilities();Log(operation+" 查询完成。");}
    public async Task Usb(bool start)
    {
        EnsureOwner();if(!start){await StopListening();await SetUsbState(false);}
        else
        {
            await RefreshOutputState();await ApplyOutputFormats();await SetUsbState(true);
            try{await Listen();}
            catch(DeviceException error)when(error.Code is "WAITING_USB" or "USB_EMULATION_STOPPED" or "USB_EMULATION_TRANSITION_BUSY")
            {PlaybackLabel="已开始模拟，等待 USB 音源就绪。";}
        }
        Log(start?"虚拟声卡已开始模拟。":"虚拟声卡已停止模拟。");
    }
    public Task SaveName()=>SaveUsbConfigurationAsync();
    public async Task SaveUsbConfigurationAsync()
    {
        EnsureOwner();if(!string.IsNullOrEmpty(PendingTransaction))throw new InvalidOperationException("请先处理现有配置事务。");
        ValidateUsbProfileDraft();ValidateIndicatorDraft();string savedIndicatorMode=indicatorMode;string profile=UsbDeviceProfile;bool hidCompat=UsbHidConsumerCompat;uint requestedCapture=DraftCaptureFormats;string name=ProductName.Trim();if(name.Length is <1 or >63||name.Any(char.IsControl))throw new ArgumentException("名称须为 1–63 个字符，不能含控制字符。");
        uint requested=DraftFormats;if(formatSelectionSupported&&requested==0)throw new ArgumentException("请至少勾选一种双声道格式。");
        if(MicrophoneSupported&&DraftCaptureFormats==0)throw new ArgumentException("至少勾选一种麦克风格式");
        await RefreshOutputState();
        if(formatSelectionSupported&&UsbState=="RUNNING"&&FormatPolicy.Effective(requested,supportedOutputFormats)==0)throw new InvalidOperationException("勾选格式与当前输出没有交集，请保留一种可播放格式，或停止模拟后保存。");
        var intent=CaptureUsbRuntimeIntent();bool restart=intent.Restart,resume=intent.Listen;
        await StopMicrophoneCore();await StopPlaybackCore();await SetUsbState(false);
        var info=await Session.RequestAsync("GET_INFO");var config=await Session.RequestAsync("GET_CONFIG");
        string revision=config.GetProperty("config_revision").GetString()!,emulationRevision=info.GetProperty("usb_emulation").GetProperty("revision").GetString()!;
        var usb=new Dictionary<string,object>{{"product_name",name},{"playback_name",name}};
        if(IndicatorConfigurationSupported)usb["indicator_mode"]=savedIndicatorMode;
        if(usbDeviceProfiles.Length>0)usb["device_profile"]=profile;if(HidConsumerCompatSupported)usb["hid_consumer_compat"]=hidCompat;
        if(MicrophoneSupported||usbDeviceProfiles.Length>0)usb["capture_formats_mask"]=requestedCapture;
        if(formatSelectionSupported)usb["formats_mask"]=requested;else if(HiResSupported)usb["hi_res_enabled"]=HiResEnabled;
        var candidate=new{usb};JsonElement prepared;
        try{await Session.RequestAsync("VALIDATE_CONFIG",new{candidate,expected_emulation_revision=emulationRevision},revision);prepared=await Session.RequestAsync("PREPARE_CONFIG",new{candidate,expected_emulation_revision=emulationRevision},revision);}
        catch{await RestoreUsbRuntimeIntentAsync(intent);throw;}
        PendingTransaction=prepared.GetProperty("transaction_id").GetString()!;pendingDevice=Session.DeviceId;pendingUsbRevision=emulationRevision;pendingRestartUsb=restart;pendingResumeListening=intent.Listen;pendingResumeMicrophone=intent.Microphone;LocalStore.SaveSettings(CurrentSettings());
        await Session.RequestAsync("APPLY_CONFIG",new{transaction_id=PendingTransaction,expected_emulation_revision=emulationRevision});
        await Session.RequestAsync("CONFIRM_CONFIG",new{transaction_id=PendingTransaction,expected_emulation_revision=emulationRevision});
        ClearPendingTransaction();configLoaded=false;await ReadUsbConfiguration();await ReadCapabilities();await Refresh();
        await RestoreUsbRuntimeIntentAsync(intent);
        UpdateAutoConnectAddress();LocalStore.SaveSettings(CurrentSettings());Log("虚拟声卡配置已保存"+(restart?"，已恢复模拟。":"。"));
    }
    public void RefreshOutputs()
    {
        refreshingOutputs=true;try{
        string? previous=SelectedOutput?.Id;ReplaceEndpoints(Outputs,outputControl.Endpoints().Where(endpoint=>endpoint.IsSystemDefault||!IsBoardOutput(endpoint)));
        SelectedOutput=previous is null?(initializing?Outputs.FirstOrDefault():null):Outputs.FirstOrDefault(x=>x.Id==previous);
        if(previous is not null&&SelectedOutput?.Id!=previous)Log("原输出不可用，已停止输出；请明确选择新的电脑输出设备。");
        }finally{refreshingOutputs=false;}
    }
    bool IsBoardOutput(OutputEndpoint endpoint)=>endpoint.DeviceId is {} id&&(endpoint.Name.Contains("W5500-EVB-Pico2",StringComparison.OrdinalIgnoreCase)||(!string.IsNullOrWhiteSpace(committedProductName)&&committedProductName.Length>=4&&endpoint.Name.Contains(committedProductName,StringComparison.OrdinalIgnoreCase))||(outputControl is WindowsOutputAudioControl&&UsbEndpointIdentity.IsBoardOrUnverified(id,UsbSerial)));
    public async Task Listen()
    {
        EnsureOwner();listeningIntent=true;recoveryFailures=0;RefreshToggleButtons();
        if(UsbState!="RUNNING"){PlaybackLabel="已连接，等待开始 USB 模拟。";return;}
        await StartPlayback();
    }
    async Task StartPlayback()
    {
        await StopPlaybackCore();if(SelectedOutput?.DeviceId is not {} endpoint||IsBoardOutput(SelectedOutput))throw new InvalidOperationException("请选择可用的电脑耳机或扬声器；不能把开发板自己的 USB 声卡作为监听输出。");
        WindowsAudio? output=null;AudioReceiver? input=null;
        try
        {
            if(TransportCodec==AudioTransportCodec.ImaAdpcm&&!CompressionSupported)throw new NotSupportedException("当前开发板未声明 IMA ADPCM 支持，请更新配套固件或选择 PCM 无压缩。");
            var ports=udpPorts;EnsureBoardUdpPort(ports.BoardPlayback,49162);bool observeClock=clockMode!=ClockObservationMode.Legacy;
            PlaybackLabel="正在准备电脑输出与网络音频…";await WindowsAudio.PrewarmAsync(endpoint,ResamplingQuality);output=new WindowsAudio(endpoint,MaximumBufferMs,ResamplingQuality,observeClock:observeClock&&clockStage==ClockObservationStage.Full);output.Buffer.Gain=(float)Volume/100;output.Buffer.Mute=Muted;output.PostProcessor.SetTargetSettings(processingSettings);output.PostProcessor.BeginResumeFade();
            input=new AudioReceiver(Session,LocalAddress,ports.SoftwarePlayback,requestFec:NetworkFecEnabled,codec:TransportCodec,observeClock:observeClock,boardPort:ports.BoardPlayback);input.Packet+=output.Buffer.Add;await output.StartAndWarmUpAsync();await input.StartAsync();
            if(observeClock)clockObservation=new(input,output,clockMode,clockStage);
            audio=output;receiver=input;activePlaybackUdpPorts=ports;RefreshUdpPortStatus();PlaybackLabel="监听已建立，等待 USB 主机音频。";Log("正在监听 → "+output.Name);RefreshShell();
        }
        catch{if(input is not null)await input.DisposeAsync();output?.Dispose();throw;}
    }
    public async Task StopListening(){listeningIntent=false;await StopPlaybackCore();PlaybackLabel=IsConnected?"已停止本地监听；USB 模拟状态保持不变。":"尚未连接开发板";RefreshShell();}
    async Task StopPlaybackCore(){ResetPlaybackMetrics();ResetLevelMeters();await StopClockObservation();var current=receiver;receiver=null;activePlaybackUdpPorts=null;RefreshUdpPortStatus();if(current is not null)await current.DisposeAsync();audio?.Dispose();audio=null;AudioStats="已停止监听";BufferLevel="—";DriftText="—";OutputBackend="输出已停止";RefreshShell();}
    void UpdatePlaybackMetrics()
    {
        RefreshUdpPortStatus();
        if(receiver is null)return;PacketCount=receiver.Accepted.ToString("N0");
        PlaybackLabel=receiver.Fault is not null?"音频链路异常："+receiver.Fault:!usbHostFormatSelected||!usbHostStreaming?"监听已建立，等待 USB 主机播放":IsListening?"正在监听 · "+(SelectedOutput?.Name??"电脑输出"):"音源暂时中断，等待 USB 音频恢复";
        if(audio is null)return;var data=JsonSerializer.SerializeToElement(audio.OutputDiagnostics());
        PublishPlaybackMetrics(Number(data,"buffer_ms"),Number(data,"estimated_drift_ppm"));
        BufferLevel=$"{Number(data,"buffer_ms"):F1} ms";DriftText=$"{Number(data,"estimated_drift_ppm"):F1} ppm";
        IntegrityText=$"非法包 {receiver.Invalid:N0} · 欠载 {Number(data,"output_underrun_callbacks"):N0} · 缺帧 {Number(data,"missing_frames"):N0} · 重同步 {Number(data,"resync_count"):N0}";
        OutputBackend=$"{(receiver.Descriptor?.Codec==AudioTransportCodec.ImaAdpcm?"IMA ADPCM 有损":"PCM 无压缩")} · 预填充 {Number(data,"prefill_ms"):F1} ms / 起播水位 {Number(data,"drift_reference_ms"):F1} ms · 上限 {MaximumBufferMs} ms · WASAPI 实际缓冲 {Number(data,"actual_buffer_ms"):F1} ms";
        AudioStats=Pretty(data);Changed(nameof(BufferBudgetSummary));RefreshShell();
    }
    async Task PollRecovery()
    {
        if(IsBusy||polling)return;
        if(reconnectPending){await PollAutomaticReconnect();return;}
        if(control is not null&&!IsConnected&&reconnectTarget is not null)
        {
            if(!await commands.WaitAsync(0))return;
            try{await HandleUnexpectedDisconnectAsync("控制连接已中断。");}finally{commands.Release();}
            return;
        }
        if(IsBusy||!listeningIntent||!wasManagementSession||polling||!await commands.WaitAsync(0))return;polling=true;
        try
        {
            if(!connectionHealthy||control is null)throw new IOException("控制连接已失效，需要重新连接。");if(UsbState!="RUNNING")return;
            if(audio?.Failure is string failure){if(SelectedOutput?.IsSystemDefault==true){PlaybackLabel="正在等待系统默认输出恢复…";return;}listeningIntent=false;await StopPlaybackCore();Log("输出端点不可用，监听已停止："+failure);return;}
            if(SelectedOutput?.DeviceId is null||IsBoardOutput(SelectedOutput))return;
            if(receiver is not null&&receiver.IsRecovering&&receiver.Fault is null){PlaybackLabel="USB 格式变化，正在重新建立音频流…";return;}
            if(receiver is not null&&receiver.Accepted!=lastAccepted&&receiver.Fault is null){lastAccepted=receiver.Accepted;return;}
            if(receiver is not null){var stats=await Session.RequestAsync("GET_STATS");if(stats.TryGetProperty("stream_running",out var running)&&running.GetBoolean()&&receiver.Fault is null&&!receiver.Ended)return;}
            await StartPlayback();recoveryFailures=0;
        }
        catch(DeviceException error)when(error.Code is "WAITING_USB" or "USB_EMULATION_STOPPED" or "USB_EMULATION_TRANSITION_BUSY"){PlaybackLabel="等待 USB 音源就绪。";}
        catch(ControlRequestExpiredException){monitorSkipReason="恢复状态采样排队过期，等待下次检查";}
        catch(Exception error)
        {
            if(control is null || !control.IsConnected || !connectionHealthy)
                await HandleUnexpectedDisconnectAsync(error.Message);
            else
            {
                await StopPlaybackCore();
                if(++recoveryFailures>5){listeningIntent=false;Log("音频恢复失败，请检查输出设备："+error.Message);}
                else PlaybackLabel="控制连接正常，正在恢复音频通路…";
            }
        }
        finally{polling=false;commands.Release();}
    }
    void EnsureOwner(){if(!Session.IsOwner||!CanManage)throw new InvalidOperationException("当前连接没有管理权限，请重新连接以申请管理权限。");}
    async Task PollStatus()
    {
        if(IsBusy||control is null||polling||!await commands.WaitAsync(0))return;
        try{await RefreshState(true);}
        catch(ControlRequestExpiredException){monitorSkipReason="轻量状态排队过期，本周期跳过";RefreshMonitorAge();}
        catch(DeviceException error){Log("状态查询被设备拒绝："+error.Message);}
        catch(Exception error)when(!shuttingDown){await HandleUnexpectedDisconnectAsync(error.Message);}
        finally{commands.Release();}
    }
    string? TakeDisconnectionAnnouncementOutput()
    {
        bool pending=disconnectAnnouncementPending;disconnectAnnouncementPending=false;
        return pending&&!shuttingDown&&ConnectionAnnouncementEnabled&&SelectedOutput is { } output&&!IsBoardOutput(output)?output.DeviceId:null;
    }
    async Task PlayDisconnectionAnnouncement(string? output)
    {
        if(output is null)return;
        try{await ConnectionAnnouncement.PlayDisconnectedAsync(output);}catch(Exception error){Log("断开提示音未播放："+error.Message);}
    }
    public Task Disconnect()=>DisconnectCore(cancelAutoConnect:true);
    async Task DisconnectCore(bool cancelAutoConnect)
    {
        string? canceledTarget=cancelAutoConnect&&autoConnectCancellation is { IsCancellationRequested:false }&&(!IsConnected||IsConnecting)?startupConnectionLabel:null;
        if(cancelAutoConnect){autoConnectCancellation?.Cancel();StopAutomaticReconnect(clearTarget:true);}
        string? output=TakeDisconnectionAnnouncementOutput();
        ResetBoardVolume();wasManagementSession=false;connectionHealthy=false;CanManage=CanEditUsb=false;UsbBlockReason="未连接";InvalidateCurrentBoardState();
        captureWriteGeneration++;captureUsbPending=false;captureVolumeSupported=false;Changed(nameof(CanControlCaptureVolume));ResetLevelMeters();await StopMicrophone();runtimeStatusSupported=false;MicrophoneSupported=false;await StopListening();if(control is not null)await control.DisposeAsync();control=null;Status="未连接";lastHostFormat=null;ActualFormat="USB 主机尚未选择音频格式";UsbState="未知";RefreshFormatOptions();RefreshShell();RefreshMonitorAge();
        await PlayDisconnectionAnnouncement(output);
        if(canceledTarget is not null&&!shuttingDown)ReportStartupCanceled(canceledTarget);
    }
    public async Task Export(){string directory=Path.Combine(LocalStore.Root,"diagnostics");Directory.CreateDirectory(directory);string path=Path.Combine(directory,$"diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.json");await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,status=Status,device_id=rememberedDevice,actual_format=ActualFormat,declared_formats=DeclaredFormats,audio=AudioStats,microphone=MicrophoneDiagnostics,control=ControlDiagnostics,polling=PollingDiagnostics,monitor=new{interval_seconds=MonitorIntervalSeconds,samples=MonitorSampleCount,skipped=MonitorSkippedCount,age_seconds=MonitorDataAgeSeconds,status=MonitorStatus},logs=Logs,limitations=new[]{"End-to-end latency requires separate measurement","Long-duration and hardware results are scope-specific"}},new JsonSerializerOptions{WriteIndented=true}));Log("诊断已导出："+path);}
    static string String(JsonElement element,string name,string fallback)=>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??fallback:fallback;
    static double Number(JsonElement element,string name)=>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var result)?result:0;
    static string FormatLabel(JsonElement format){int rate=(int)Number(format,"sample_rate"),bits=(int)Number(format,"bits");if(bits==0)bits=String(format,"sample_format","") switch{"S24LE_PACKED"=>24,_=>16};return $"{bits}-bit / {rate/1000d:0.###} kHz · 双声道";}
    static string FormatsLabel(JsonElement array)=>array.ValueKind==JsonValueKind.Array?string.Join("  +  ",array.EnumerateArray().Select(FormatLabel)):"未提供格式声明";
    static string Pretty(JsonElement element)=>JsonSerializer.Serialize(element,new JsonSerializerOptions{WriteIndented=true});
    Task? runtimeDisposal;
    public ValueTask DisposeAsync()=>new(runtimeDisposal??=DisposeRuntimeCoreAsync());
    async Task DisposeRuntimeCoreAsync()
    {
        timer.Stop();inputMeterTimer.Stop();shuttingDown=true;StopAutomaticReconnect(clearTarget:true);
        inputChangeCancellation?.Cancel();outputChangeCancellation?.Cancel();lifetime.Cancel();
        // Background endpoint reads no longer own commands. Keep the dispatcher
        // and native resources alive until their continuations have drained.
        try{await Task.WhenAll(outputPoll,recoveryPoll,statusPoll,monitorPoll).WaitAsync(TimeSpan.FromSeconds(5));}
        catch(TimeoutException error){throw new TimeoutException($"后台停止失败：后台采样未在 5 秒内结束（输出阶段：{OutputPollingStage}）。",error);}
        if(boardWriter is not null)await boardWriter.DisposeAsync();
        if(outputWriter is not null)await outputWriter.DisposeAsync();
        if(captureVolumeWriter is not null)await captureVolumeWriter.DisposeAsync();
        if(!await commands.WaitAsync(TimeSpan.FromSeconds(3)))throw new TimeoutException("后台停止失败：设备操作未在 3 秒内释放控制锁。");
        try{await Disconnect();}finally{commands.Release();}
    }
}




