using System.Windows.Input;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    bool? usbSwitchTarget,listeningSwitchTarget;
    public bool IsUsbEmulating=>UsbState is "RUNNING" or "STOPPING";
    public string UsbButtonText=>usbSwitchTarget is bool start?start?"正在开始模拟…":"正在停止模拟…":UsbState switch{"STARTING"=>"正在开始模拟…","STOPPING"=>"正在停止模拟…","RUNNING"=>"停止模拟",_=>"开始模拟"};
    public bool CanToggleUsb=>!shuttingDown&&!IsConnecting&&CanEditUsb&&usbSwitchTarget is null;
    // A paused source or pending recovery still belongs to the user's listening session.
    public bool IsMonitoring=>listeningIntent||receiver is not null;
    public string ListeningButtonText=>listeningSwitchTarget is bool start?start?"正在开始输出…":"正在停止输出…":IsMonitoring?"停止输出":"开始输出";
    public bool CanToggleListening=>!shuttingDown&&!IsBusy&&!IsConnecting&&listeningSwitchTarget is null&&(IsMonitoring||IsConnected&&CanManage&&(UsbState is "RUNNING" or "STOPPED")&&SelectedOutput?.DeviceId is not null&&!IsBoardOutput(SelectedOutput)&&string.IsNullOrEmpty(PendingTransaction));
    public ICommand ToggleUsbCommand=>SyncCommand(nameof(ToggleUsbCommand),_=>_=ToggleUsbAsync(),()=>CanToggleUsb);
    async Task ToggleUsbAsync()
    {
        if(!CanToggleUsb)return;
        bool start=!IsUsbEmulating;usbSwitchTarget=start;RefreshToggleButtons();
        try{await Execute(()=>Usb(start),start?"正在开始模拟…":"正在停止模拟…");}
        finally{usbSwitchTarget=null;RefreshToggleButtons();}
    }
    public ICommand ToggleListeningCommand=>SyncCommand(nameof(ToggleListeningCommand),_=>_=ToggleListeningAsync(),()=>CanToggleListening);
    async Task ToggleListeningAsync()
    {
        if(!CanToggleListening)return;
        bool start=!IsMonitoring;listeningSwitchTarget=start;RefreshToggleButtons();
        try{await Execute(async()=>
        {
            try{if(start)await Listen();else await StopListening();}
            catch(DeviceException error)when(error.Code is "WAITING_USB" or "USB_EMULATION_STOPPED" or "USB_EMULATION_TRANSITION_BUSY")
            {PlaybackLabel="已连接，等待 USB 音源就绪。";Log(PlaybackLabel);}
            catch{await StopListening();throw;}
        },start?"正在开始输出…":"正在停止输出…");}
        finally{listeningSwitchTarget=null;RefreshToggleButtons();}
    }
    void RefreshToggleButtons()
    {
        foreach(string name in new[]{nameof(IsUsbEmulating),nameof(UsbButtonText),nameof(CanToggleUsb),nameof(IsMonitoring),nameof(ListeningButtonText),nameof(CanToggleListening)})Changed(name);
        RefreshCommandStates();
    }
}
