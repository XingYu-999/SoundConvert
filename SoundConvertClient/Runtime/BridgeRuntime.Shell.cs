using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    string selectedPage="主页",globalError="",operationStatus="";
    bool navigationExpanded=true,isBusy,userCommandPending,minimizeToTray,connectionAnnouncementEnabled=true;
    readonly Dictionary<string,ICommand> uiCommands=new();
    public bool IsBusy{get=>isBusy||userCommandPending;private set{Set(ref isBusy,value);RefreshBusyState();}}
    void SetUserCommandPending(bool value){userCommandPending=value;Changed(nameof(IsBusy));RefreshBusyState();}
    void RefreshBusyState(){RefreshIndicatorState();RefreshVolumeControls();RefreshHostKeyControls();Changed(nameof(CanConnect));RefreshAdapterState();Changed(nameof(CanConfigureAutoConnect));Changed(nameof(CanControlOutput));Changed(nameof(CanToggleConnection));Changed(nameof(CanEditUsb));Changed(nameof(CanEditUsbProfile));Changed(nameof(CanEditHidConsumerCompat));Changed(nameof(CanControlCaptureVolume));Changed(nameof(CanEditHiRes));RefreshFormatOptions();RefreshInputOptions();RefreshToggleButtons();}
    public string GlobalError{get=>globalError;private set{Set(ref globalError,value);Changed(nameof(HasGlobalError));}}
    public bool HasGlobalError=>!string.IsNullOrWhiteSpace(GlobalError);
    public string OperationStatus{get=>string.IsNullOrEmpty(operationStatus)?"正在执行操作…":operationStatus;private set=>Set(ref operationStatus,value);}
    public string SelectedPage{get=>selectedPage;set{Set(ref selectedPage,value);foreach(string n in new[]{nameof(IsOverview),nameof(IsDevices),nameof(IsAudio),nameof(IsInput),nameof(IsUsb),nameof(IsNetwork),nameof(IsLogs),nameof(IsSettings),nameof(IsAbout),nameof(IsChangelog)})Changed(n);}}
    public bool IsOverview=>SelectedPage=="主页";public bool IsDevices=>SelectedPage=="虚拟声卡";public bool IsAudio=>SelectedPage=="播放";public bool IsInput=>SelectedPage=="输入";public bool IsUsb=>false;public bool IsNetwork=>false;public bool IsLogs=>SelectedPage=="日志";public bool IsSettings=>SelectedPage=="设置";public bool IsAbout=>SelectedPage=="关于";public bool IsChangelog=>SelectedPage=="更新日志";
    public bool IsNavigationExpanded{get=>navigationExpanded;set{Set(ref navigationExpanded,value);Changed(nameof(NavigationWidth));}}
    public double NavigationWidth=>IsNavigationExpanded?216:72;
    public bool MinimizeToTray{get=>minimizeToTray;set=>Set(ref minimizeToTray,value);}
    public bool ConnectionAnnouncementEnabled{get=>connectionAnnouncementEnabled;set=>Set(ref connectionAnnouncementEnabled,value);}
    public string UserDataDirectory=>LocalStore.Root;
    public string AppVersion=>"V"+(typeof(BridgeRuntime).Assembly.GetName().Version?.ToString(3)??"0.0.1");
    public string RuntimeVersion=>$"独立音频后台 · .NET {Environment.Version}";
    public bool HasRemembered=>RememberedBoards.Count>0;
    public string RememberedSummary=>HasRemembered?$"已记住 {RememberedBoards.Count} 块虚拟声卡，选择后即可直接连接。":"连接成功后，虚拟声卡会保留在这里。";
    public string DiscoverySummary=>Devices.Count==0?"尚未发现虚拟声卡。请选择正确网卡后搜索。":$"通过 {SelectedSearchAdapter?.Name} 发现 {Devices.Count} 条设备路径；选择设备后即可连接。";
    public string HeaderStatus=>IsReconnecting?ReconnectStatus:IsConnected?$"{ConnectedName} · {(CanManage?"管理连接":"只读观察")}":IsBusy?Message:"未连接虚拟声卡";
    public string OverviewHeading=>IsListening?"声音已接入这台电脑":IsConnected?CanManage?"虚拟声卡已连接，准备接入声音":"已建立只读观察连接":HasRemembered?"连接已记住的虚拟声卡，即可开始聆听":"先连接局域网中的虚拟声卡";
    public string OverviewHint=>IsConnected?PlaybackLabel:"快速搜索同一局域网中的虚拟声卡；连接成功后会播放连接提示，并自动开始监听。";
    public bool CanConnect=>!IsBusy&&SelectedSearchAdapter is { IsUp:true, HasIpv4:true }&&!string.IsNullOrWhiteSpace(Address);
    public ObservableCollection<RememberedBoardRow> RememberedBoards{get;}=[];
    public Func<Task>? RequestExit{get;set;}
    public Action? RequestHide{get;set;}

    ICommand SyncCommand(string key,Action<object?> action,Func<bool>? canExecute=null)=>uiCommands.TryGetValue(key,out var command)?command:uiCommands[key]=new UiCommand(action,canExecute);
    ICommand AsyncCommand(string key,Func<Task> action)=>SyncCommand(key,_=>_=Execute(action),()=>!IsBusy&&!shuttingDown);
    void RefreshCommandStates(){foreach(var command in uiCommands.Values.OfType<UiCommand>().ToArray())command.Refresh();}
    public ICommand NavigateCommand=>SyncCommand(nameof(NavigateCommand),p=>Navigate(p?.ToString()??"概览"));
    public ICommand ToggleNavigationCommand=>SyncCommand(nameof(ToggleNavigationCommand),_=>IsNavigationExpanded=!IsNavigationExpanded);
    public ICommand DiscoverCommand=>SyncCommand(nameof(DiscoverCommand),_=>_=Execute(Discover),()=>CanDiscover);
    public ICommand DiscoverBroadcastCommand=>SyncCommand(nameof(DiscoverBroadcastCommand),_=>_=Execute(DiscoverBroadcast),()=>CanDiscoverBroadcast);
    public ICommand ConnectCommand=>AsyncCommand(nameof(ConnectCommand),Connect);
    public ICommand DisconnectCommand=>AsyncCommand(nameof(DisconnectCommand),Disconnect);
    public ICommand QuickSearchCommand=>AsyncCommand(nameof(QuickSearchCommand),async()=>{Navigate("虚拟声卡连接");if(SelectedSearchAdapter is null){Log("请先选择搜索网卡，再点击搜索。");return;}await Discover();});
    public ICommand QuickConnectCommand=>AsyncCommand(nameof(QuickConnectCommand),async()=>{if(RememberedBoards.FirstOrDefault() is { } board)await ConnectRemembered(board);else Navigate("虚拟声卡连接");});
    public ICommand ConnectRememberedCommand=>SyncCommand(nameof(ConnectRememberedCommand),p=>{if(p is RememberedBoardRow row)_=Execute(()=>ConnectRemembered(row));},()=>!IsBusy&&!shuttingDown);
    public ICommand ForgetBoardCommand=>SyncCommand(nameof(ForgetBoardCommand),p=>{if(p is RememberedBoardRow row){RememberedBoards.Remove(row);PersistBoards();Log("已移除记住的虚拟声卡记录。");}});
    public ICommand RefreshCommand=>AsyncCommand(nameof(RefreshCommand),Refresh);
    public ICommand CapabilitiesCommand=>AsyncCommand(nameof(CapabilitiesCommand),()=>Query("GET_CAPABILITIES"));
    public ICommand ConfigCommand=>AsyncCommand(nameof(ConfigCommand),ReadUsbConfiguration);
    public ICommand StatsCommand=>AsyncCommand(nameof(StatsCommand),()=>Query("GET_STATS"));
    public ICommand SaveUsbCommand=>AsyncCommand(nameof(SaveUsbCommand),SaveName);
    public ICommand DiscardUsbDraftCommand=>SyncCommand(nameof(DiscardUsbDraftCommand),_=>{ProductName=committedProductName;HiResEnabled=committedHiRes;SetFormatDraft(committedFormats);SetCaptureDraft(committedCaptureFormats);UsbDeviceProfile=committedUsbDeviceProfile;UsbHidConsumerCompat=committedHidConsumerCompat;DiscardIndicatorDraft();Log("已恢复已提交的 USB 配置。");});
    public ICommand RefreshOutputsCommand=>AsyncCommand(nameof(RefreshOutputsCommand),RefreshAudioDeviceState);
    public ICommand BufferProfileCommand=>SyncCommand(nameof(BufferProfileCommand),p=>{if(int.TryParse(p?.ToString(),out int value))MaximumBufferMs=value;});
    public ICommand SaveSettingsCommand=>AsyncCommand(nameof(SaveSettingsCommand),SaveSettings);
    public ICommand ExportCommand=>AsyncCommand(nameof(ExportCommand),Export);
    public ICommand OpenDataCommand=>SyncCommand(nameof(OpenDataCommand),_=>{Directory.CreateDirectory(LocalStore.Root);Process.Start(new ProcessStartInfo(LocalStore.Root){UseShellExecute=true});});
    public ICommand ClearLogsCommand=>SyncCommand(nameof(ClearLogsCommand),_=>{LogEntries.Clear();Logs="";Message="已清空界面日志，磁盘日志保留。";});
    public ICommand ExitCommand=>SyncCommand(nameof(ExitCommand),_=>{if(RequestExit is not null)_=RequestExit();});
    public ICommand HideCommand=>SyncCommand(nameof(HideCommand),_=>RequestHide?.Invoke());
    public ICommand OpenWindowsSoundCommand=>SyncCommand(nameof(OpenWindowsSoundCommand),_=>Process.Start(new ProcessStartInfo("ms-settings:sound"){UseShellExecute=true}));
    public ICommand OpenReferenceCommand=>SyncCommand(nameof(OpenReferenceCommand),_=>Process.Start(new ProcessStartInfo("https://github.com/XingYu-999/ControllerForwardingTool"){UseShellExecute=true}));
    public ICommand ReadNetworkCommand=>AsyncCommand(nameof(ReadNetworkCommand),ReadNetwork);
    public ICommand ApplyNetworkCommand=>AsyncCommand(nameof(ApplyNetworkCommand),ApplyNetwork);
    public ICommand QueryTransactionCommand=>AsyncCommand(nameof(QueryTransactionCommand),QueryTransaction);
    public ICommand ConfirmNetworkCommand=>AsyncCommand(nameof(ConfirmNetworkCommand),()=>FinishNetwork(true));
    public ICommand RollbackNetworkCommand=>AsyncCommand(nameof(RollbackNetworkCommand),()=>FinishNetwork(false));
    public ICommand ApplyBroadcastCommand=>AsyncCommand(nameof(ApplyBroadcastCommand),ApplyNetworkBroadcast);
    public ICommand ConfirmBroadcastCommand=>AsyncCommand(nameof(ConfirmBroadcastCommand),()=>FinishNetworkBroadcast(true));
    public ICommand QueryBroadcastCommand=>AsyncCommand(nameof(QueryBroadcastCommand),QueryNetworkBroadcast);
    public ICommand RollbackBroadcastCommand=>AsyncCommand(nameof(RollbackBroadcastCommand),()=>FinishNetworkBroadcast(false));

    public void Navigate(string page){SelectedPage=page switch{"概览"=>"主页","虚拟声卡连接" or "USB 声卡" or "网络"=>"虚拟声卡","声音"=>"播放",_=>page};}
    void RefreshShell(){RefreshIndicatorState();RefreshVolumeControls();RefreshHostKeyControls();foreach(string n in new[]{nameof(HeaderStatus),nameof(OverviewHeading),nameof(OverviewHint),nameof(HasRemembered),nameof(RememberedSummary),nameof(DiscoverySummary),nameof(IsConnected),nameof(IsListening),nameof(IsObserver),nameof(CanConnect),nameof(CanEditHiRes),nameof(CanEditUsb),nameof(ConnectionButtonText),nameof(CanToggleConnection),nameof(CanModifyDiscoveredIp),nameof(AutoConnectCurrent),nameof(CanConfigureAutoConnect),nameof(ConnectedAddress)})Changed(n);RefreshAdapterState();RefreshToggleButtons();}
    void LoadRemembered(Settings settings)
    {
        var boards=LocalStore.LoadBoards().ToList();
        if(boards.Count==0&&!File.Exists(Path.Combine(LocalStore.Root,"devices.json"))&&!string.IsNullOrWhiteSpace(settings.DeviceId)&&!string.IsNullOrWhiteSpace(settings.Address))boards.Add(new(settings.DeviceId,"W5500-EVB-Pico2",settings.Address,settings.LocalAddress));
        foreach(var board in boards.OrderByDescending(x=>x.LastConnected).Take(12))RememberedBoards.Add(new(board,ConnectRememberedCommand,ForgetBoardCommand));
    }
    void RememberConnectedBoard()
    {
        var old=RememberedBoards.FirstOrDefault(x=>x.Board.DeviceId.Equals(Session.DeviceId,StringComparison.OrdinalIgnoreCase));if(old is not null)RememberedBoards.Remove(old);
        RememberedBoards.Insert(0,new(new(Session.DeviceId,ConnectedName,Address,LocalAddress,ControlPort,UsbSerial,DateTimeOffset.UtcNow,lastHostFormat??old?.Board.LastHostFormat,SelectedSearchAdapter?.Id),ConnectRememberedCommand,ForgetBoardCommand));
        while(RememberedBoards.Count>12)RememberedBoards.RemoveAt(RememberedBoards.Count-1);PersistBoards();
    }
    void PersistBoards(){LocalStore.SaveBoards(RememberedBoards.Select(x=>x.Board));RefreshShell();}
    async Task ConnectRemembered(RememberedBoardRow row,CancellationToken cancellation=default)
    {
        SelectedDevice=null;Address=row.Board.Address;ControlPort=row.Board.ControlPort;rememberedDevice=row.Board.DeviceId;
        await ConnectCore(row.Board.DeviceId,cancellationToken:cancellation);
    }
}



