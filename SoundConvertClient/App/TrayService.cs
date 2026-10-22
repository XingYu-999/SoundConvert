using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
namespace SoundConvertClient;
public sealed class TrayService : IDisposable
{
    readonly MainWindow window;
    readonly MainViewModel model;
    readonly WindowsTrayIcon icon;
    Views.TrayMenuWindow? menu;
    bool disposed;
    public TrayService(MainWindow window,MainViewModel vm)
    {
        this.window=window;model=vm;
        using var image=AssetLoader.Open(new Uri("avares://SoundConvertClient/Assets/AppIcon.ico"));
        icon=new WindowsTrayIcon(image,ShowMenu,()=>{DismissContextMenu();window.ShowFromTray();});
        icon.SetTooltip("USB Audio Bridge · "+model.Status);model.PropertyChanged+=Changed;
    }
    public Window? ContextMenuWindow=>menu;
    public IReadOnlyList<string?> MenuItems=>["上一曲","播放 / 暂停","下一曲","Host 音量减","Host 静音","Host 音量加","打开主窗口","设备管理","播放输出","日志","设置","退出"];
    void ShowMenu(TrayMenuAnchor anchor)
    {
        if(disposed)return;
        if(menu is null){menu=new(){DataContext=model};menu.ActionRequested+=MenuAction;menu.Closed+=(_,_)=>menu=null;}
        menu.ShowAt(anchor.Point,anchor.WorkingArea,anchor.Above,Environment.GetEnvironmentVariable("UAB_UI_PREVIEW")!="1");
    }
    void MenuAction(string action)
    {
        DismissContextMenu();
        switch(action)
        {
            case "open":window.ShowFromTray();break;
            case "devices":model.Navigate("虚拟声卡");window.ShowFromTray();break;
            case "audio":model.Navigate("播放");window.ShowFromTray();break;
            case "logs":model.Navigate("日志");window.ShowFromTray();break;
            case "settings":model.Navigate("设置");window.ShowFromTray();break;
            case "exit":_=window.ExitAsync();break;
        }
    }
    public void ShowContextMenu(PixelPoint? screenPoint=null)=>icon.ShowMenu(screenPoint,respectIcon:screenPoint is null);
    public void DismissContextMenu()=>menu?.Hide();
    void Changed(object? sender,PropertyChangedEventArgs args)
    { if(args.PropertyName==nameof(MainViewModel.Status))icon.SetTooltip("USB Audio Bridge · "+model.Status); }
    public void Dispose() { if(disposed)return;disposed=true;model.PropertyChanged-=Changed;menu?.Close();menu=null;icon.Dispose(); }
}
