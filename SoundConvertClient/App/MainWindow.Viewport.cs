using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SoundConvertClient;

public partial class MainWindow
{
    bool viewportCheckQueued;
    public int ViewportRepairCount { get; private set; }
    void QueueViewportCheck()
    {
        if(viewportCheckQueued||exiting)return;
        viewportCheckQueued=true;
        Dispatcher.UIThread.Post(()=>
        {
            viewportCheckQueued=false;
            if(exiting||!IsVisible||WindowState==WindowState.Minimized||!OperatingSystem.IsWindows())return;
            var handle=TryGetPlatformHandle()?.Handle??0;
            if(handle==0||!GetClientRect(handle,out var client)||client.Right<=0||client.Bottom<=0)return;
            double scale=RenderScaling;
            var actual=new Size(client.Right/scale,client.Bottom/scale);
            bool wrongClient=Math.Abs(actual.Width-ClientSize.Width)>.75||Math.Abs(actual.Height-ClientSize.Height)>.75;
            bool wrongRoot=Content is Control content && (Math.Abs(content.Bounds.Width*scale-client.Right)>2||Math.Abs(content.Bounds.Height*scale-client.Bottom)>2);
            if(!wrongClient&&!wrongRoot)return;
            if(wrongClient)
            {
                // Startup can retain the client DIP size from the previous monitor's DPI.
                // Re-deliver the real native client size through the existing Win32 resize path.
                // No physical resize, focus change, or fabricated one-pixel window nudge.
                nint dimensions=(nint)((client.Right&0xffff)|((client.Bottom&0xffff)<<16));
                SendMessage(handle,0x0005,WindowState==WindowState.Maximized?2u:0u,dimensions);
                if(Math.Abs(client.Right/scale-ClientSize.Width)>.75||Math.Abs(client.Bottom/scale-ClientSize.Height)>.75)
                {
                    // The Win32 backend itself can still hold the creation monitor's DPI.
                    // Reconcile it through its normal DPI message with the current, unchanged frame.
                    uint dpi=GetDpiForWindow(handle);
                    if(dpi>0&&GetWindowRect(handle,out var frame))
                    {
                        nint rectangle=Marshal.AllocHGlobal(Marshal.SizeOf<ClientRectangle>());
                        try
                        {
                            Marshal.StructureToPtr(frame,rectangle,false);
                            SendMessage(handle,0x02e0,(nuint)(dpi|(dpi<<16)),rectangle);
                        }
                        finally { Marshal.FreeHGlobal(rectangle); }
                        if(GetClientRect(handle,out client))
                        {
                            dimensions=(nint)((client.Right&0xffff)|((client.Bottom&0xffff)<<16));
                            SendMessage(handle,0x0005,WindowState==WindowState.Maximized?2u:0u,dimensions);
                        }
                    }
                }
                ++ViewportRepairCount;
            }
            InvalidateMeasure();InvalidateArrange();
            if(Content is Control root){root.InvalidateMeasure();root.InvalidateArrange();root.InvalidateVisual();}
            UpdateLayout();InvalidateVisual();
        },DispatcherPriority.Background);
    }
    [StructLayout(LayoutKind.Sequential)] struct ClientRectangle { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")]static extern bool GetClientRect(nint window,out ClientRectangle rectangle);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]static extern bool GetWindowRect(nint window,out ClientRectangle rectangle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern nint SendMessage(nint window,uint message,nuint wParam,nint lParam);
}
