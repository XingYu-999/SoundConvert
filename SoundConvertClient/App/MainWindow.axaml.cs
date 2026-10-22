using Avalonia.Controls;
namespace SoundConvertClient;

public partial class MainWindow:Window
{
    readonly MainViewModel vm=new();
    readonly bool preview=Environment.GetEnvironmentVariable("UAB_UI_PREVIEW")=="1";
    readonly bool previewAutoAttach=Environment.GetEnvironmentVariable("UAB_UI_PREVIEW")=="1"&&Environment.GetEnvironmentVariable("UAB_PREVIEW_AUTOSTART")=="1";
    bool exiting,exitInProgress;
    TrayService? tray;
    Views.FirmwareFlashWindow? firmwareFlashWindow;
    public MainWindow()
    {
        InitializeComponent();DataContext=vm;
        if(preview)
        {
            ShowActivated=false;ShowInTaskbar=false;
            // Avalonia can normalize an off-screen startup position back onto a monitor.
            // Move after Show as well, so automated windows never cover the user's work.
            Opened+=(_,_)=>
            {
                if(Environment.GetEnvironmentVariable("UAB_PREVIEW_ALLOW_WINDOWS")!="1")Position=new Avalonia.PixelPoint(-10000,-10000);
            };
        }
        Opened+=(_,_)=>QueueViewportCheck();
        ScalingChanged+=(_,_)=>QueueViewportCheck();
        PositionChanged+=(_,_)=>QueueViewportCheck();
        vm.RequestIpAddressDialog=editor=>new Views.IpAddressDialog(editor).ShowDialog(this);
        vm.RequestManualConnectDialog=editor=>new Views.ManualConnectDialog(editor).ShowDialog(this);
        vm.PendingFrameAsync=WaitForPendingFrameAsync;
        if(!preview)try{tray=new TrayService(this,vm);}catch(Exception error){vm.Log("托盘不可用："+error.Message);}
        vm.RequestExit=ExitAsync;vm.RequestShutdownExit=ShutdownAndExitAsync;vm.RequestHide=()=>{if(tray is not null)Hide();else WindowState=WindowState.Minimized;};
        if(!preview)SingleInstance.WindowReady(ShowFromTray);
        vm.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(MainViewModel.SelectedPage))MainContentScroller.Offset=default;};
        Opened+=async(_,_)=>{if(!preview&&tray is not null&&vm.StartInTray&&!string.IsNullOrEmpty(vm.Address))Hide();if(!preview||previewAutoAttach)await vm.InitializeAsync();};
        PropertyChanged+=(_,args)=>{if(args.Property==WindowStateProperty&&WindowState==WindowState.Minimized&&vm.MinimizeToTray&&tray is not null)Hide();};
        Closing+=async(_,args)=>{if(exiting)return;args.Cancel=true;if(!preview&&tray is not null&&vm.CloseToTray&&!exitInProgress){Hide();return;}await ExitAsync();};
    }
    public void ShowFromTray(){Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;QueueViewportCheck();if(!preview)Activate();}
    async Task WaitForPendingFrameAsync()
    {
        if(!IsVisible||WindowState==WindowState.Minimized)return;
        var rendered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestAnimationFrame(_=>rendered.TrySetResult());
        try{await rendered.Task.WaitAsync(TimeSpan.FromMilliseconds(250));}catch(TimeoutException){}
    }
    public void ShowPage(string page){vm.Navigate(page);ShowFromTray();}
    void FirmwareFlashClicked(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>ShowFirmwareFlasher();
    public Views.FirmwareFlashWindow ShowFirmwareFlasher()
    {
        if(firmwareFlashWindow is null)
        {
            firmwareFlashWindow=new Views.FirmwareFlashWindow();
            firmwareFlashWindow.Closed+=(_,_)=>firmwareFlashWindow=null;
            firmwareFlashWindow.Show(this);
        }
        else
        {
            if(firmwareFlashWindow.WindowState==WindowState.Minimized)firmwareFlashWindow.WindowState=WindowState.Normal;
            firmwareFlashWindow.Show();if(!preview)firmwareFlashWindow.Activate();
        }
        return firmwareFlashWindow;
    }
    public void ShowPage(int index)=>ShowPage(index switch{0=>"虚拟声卡",1=>"播放",2=>"虚拟声卡",3=>"虚拟声卡",4=>"日志",5=>"设置",_=>"主页"});
    public async Task ExitAsync()
    {
        if(exiting||exitInProgress)return;
        if(firmwareFlashWindow?.ViewModel.IsWriting==true){vm.Log("正在传输固件，请等待完成后退出应用。");if(!preview)firmwareFlashWindow.Activate();return;}
        firmwareFlashWindow?.Close();exitInProgress=true;
        try{await vm.DisposeAsync();tray?.Dispose();tray=null;exiting=true;Close();}
        catch(Exception error){exitInProgress=false;vm.Log("退出未完成："+error.Message);ShowFromTray();}
    }
    // Retained command alias: every user-facing exit uses the same cleanup path.
    public Task ShutdownAndExitAsync() => ExitAsync();
}
