using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SoundConvertClient.Views;

public partial class TrayMenuWindow : Window
{
    PixelPoint anchor;
    PixelRect workingArea;
    bool above,placing;
    public event Action<string>? ActionRequested;
    public TrayMenuWindow()
    {
        InitializeComponent();
        Deactivated+=(_,_)=>Hide();
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){Hide();e.Handled=true;}};
        ScalingChanged+=(_,_)=>Dispatcher.UIThread.Post(()=>{if(IsVisible)Place();},DispatcherPriority.Background);
        SizeChanged+=(_,_)=>{if(IsVisible)Place();};
    }
    public void ShowAt(PixelPoint point,PixelRect work,bool openAbove,bool activate=true)
    {
        anchor=point;workingArea=work;above=openAbove;
        double scale=Screens.ScreenFromPoint(point)?.Scaling??1;
        MaxHeight=Math.Max(100,(work.Height-12)/scale);
        ShowActivated=activate;
        if(!IsVisible)
        {
            // Create inside the target work area before moving to the tray edge.
            // Creating at the edge lets Windows constrain the initial client width.
            Opacity=0;Width=286;SizeToContent=SizeToContent.Height;
            int left=Math.Clamp(point.X-(int)Math.Ceiling(286*scale)+16,work.X+6,Math.Max(work.X+6,work.Right-(int)Math.Ceiling(286*scale)-6));
            Position=new(left,work.Y+6);Show();
        }
        UpdateLayout();Place();Opacity=1;
        if(activate){Activate();this.FindControl<Button>("TrayPlayPauseButton")!.Focus();}
    }
    void Place()
    {
        if(placing||workingArea.Width==0)return;
        var handle=TryGetPlatformHandle()?.Handle??0;
        if(handle==0||!GetWindowRect(handle,out var rect))return;
        placing=true;
        try
        {
            uint dpi=GetDpiForWindow(handle);
            int expectedWidth=(int)Math.Round(286*dpi/96d);
            if(dpi>0&&(Math.Abs(rect.Right-rect.Left-expectedWidth)>1||Math.Abs(RenderScaling-dpi/96d)>.01))
            {
                nint rectangle=Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
                try
                {
                    Marshal.StructureToPtr(rect,rectangle,false);
                    SendMessage(handle,0x02e0,(nuint)(dpi|(dpi<<16)),rectangle);
                }
                finally{Marshal.FreeHGlobal(rectangle);}
                SetWindowPos(handle,0,rect.Left,rect.Top,expectedWidth,rect.Bottom-rect.Top,0x14);
                InvalidateMeasure();UpdateLayout();GetWindowRect(handle,out rect);
            }
            int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;
            int x=Math.Clamp(anchor.X-width+16,workingArea.X+6,Math.Max(workingArea.X+6,workingArea.Right-width-6));
            bool useAbove=above||anchor.Y+height+6>workingArea.Bottom;
            int y=Math.Clamp(useAbove?anchor.Y-height-6:anchor.Y+6,workingArea.Y+6,Math.Max(workingArea.Y+6,workingArea.Bottom-height-6));
            SetWindowPos(handle,0,x,y,0,0,0x15);
            // Reconcile the real native client size after mixed-DPI creation/moves.
            if(GetClientRect(handle,out var client)&&Content is Control root&&
               (Math.Abs(root.Bounds.Width*RenderScaling-client.Right)>2||Math.Abs(root.Bounds.Height*RenderScaling-client.Bottom)>2))
            {
                SendMessage(handle,0x0005,0,(nint)((client.Right&0xffff)|((client.Bottom&0xffff)<<16)));
                InvalidateMeasure();InvalidateArrange();UpdateLayout();
            }
        }
        finally{placing=false;}
    }
    void OutputMuteClicked(object? sender,RoutedEventArgs e)
    {if(DataContext is MainViewModel {CanControlOutput:true} model)model.OutputMuted=!model.OutputMuted;}
    void ActionClicked(object? sender,RoutedEventArgs e)
    {if(sender is Button {Tag:string action})ActionRequested?.Invoke(action);}
    [StructLayout(LayoutKind.Sequential)]struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")]static extern bool GetClientRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern nint SendMessage(nint window,uint message,nuint wParam,nint lParam);
}
