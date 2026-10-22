using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;

namespace SoundConvertClient;
internal readonly record struct TrayMenuAnchor(PixelPoint Point,PixelRect WorkingArea,bool Above);

/// <summary>Windows notification icon and physical-pixel popup anchors.</summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    const uint CallbackMessage=0x8001;
    readonly string className="UabTray-"+Guid.NewGuid().ToString("N");
    readonly nint module=GetModuleHandle(null);
    readonly WindowProcedure procedure;
    readonly Action<TrayMenuAnchor> showMenu;
    readonly Action clicked;
    readonly uint taskbarCreated=RegisterWindowMessage("TaskbarCreated");
    nint window,icon;
    bool registered,visible,version4,disposed;
    string tooltip="USB Audio Bridge";
    public WindowsTrayIcon(Stream iconStream,Action<TrayMenuAnchor> showMenu,Action clicked)
    {
        this.showMenu=showMenu;this.clicked=clicked;procedure=WindowProc;
        try
        {
            var type=new WindowClass { Instance=module,ClassName=className,Procedure=Marshal.GetFunctionPointerForDelegate(procedure) };
            if(RegisterClass(ref type)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            registered=true;
            window=CreateWindowEx(0,className,"USB Audio Bridge tray",0x80000000,0,0,0,0,0,0,module,0);
            if(window==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            ChangeWindowMessageFilterEx(window,taskbarCreated,1,0);
            using var memory=new MemoryStream();iconStream.CopyTo(memory);var bytes=memory.ToArray();
            if(bytes.Length<22||BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))!=1)throw new InvalidDataException("Invalid tray ICO.");
            int count=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
            if(count==0||6+count*16>bytes.Length)throw new InvalidDataException("Invalid ICO directory.");
            int best=Enumerable.Range(0,count).MinBy(i=>Math.Abs((bytes[6+16*i]==0?256:bytes[6+16*i])-32));
            int entry=6+best*16,size=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry+8))),offset=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry+12)));
            if(size<=0||offset<0||offset>bytes.Length-size)throw new InvalidDataException("Invalid ICO image.");
            icon=CreateIconFromResourceEx(bytes.AsSpan(offset,size).ToArray(),(uint)size,true,0x30000,32,32,0);
            if(icon==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            AddIcon();
        }
        catch { Dispose(); throw; }
    }
    NotifyData Data()=>new() { Size=(uint)Marshal.SizeOf<NotifyData>(),Window=window,Id=1,Flags=0x87,Callback=CallbackMessage,Icon=icon,Tip=tooltip.Length>127?tooltip[..127]:tooltip,Info="",Title="" };
    void AddIcon()
    {
        var data=Data();
        if(!Shell_NotifyIcon(0,ref data))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法创建系统托盘图标。");
        visible=true;data.Version=4;version4=Shell_NotifyIcon(4,ref data);
    }
    public void SetTooltip(string text)
    {
        tooltip=text;if(!visible||disposed)return;
        var data=Data();data.Flags=0x84;Shell_NotifyIcon(1,ref data);
    }
    nint WindowProc(nint hwnd,uint message,nuint wParam,nint lParam)
    {
        try
        {
            if(disposed)return DefWindowProc(hwnd,message,wParam,lParam);
            if(message==taskbarCreated) { AddIcon(); return 0; }
            if(message==CallbackMessage)
            {
                uint code=version4?(uint)((long)lParam&0xffff):(uint)lParam;
                if(code==(version4?0x7bu:0x205u))
                {
                    var position=version4?new PixelPoint((short)(wParam&0xffff),(short)((wParam>>16)&0xffff)):(PixelPoint?)null;
                    ShowMenu(position is { X:-1,Y:-1 }?null:position);
                }
                else if(code==(version4?0x400u:0x202u)||version4&&code==0x401)Dispatcher.UIThread.Post(clicked);
                return 0;
            }
        }
        catch(Exception error) { LocalStore.Log("托盘操作失败："+error.Message); }
        return DefWindowProc(hwnd,message,wParam,lParam);
    }
    public void ShowMenu(PixelPoint? location=null,bool respectIcon=true)
    {
        if(disposed)return;
        if(!GetCursorPos(out var cursor))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(location is { } point)cursor=new(){X=point.X,Y=point.Y};
        var monitor=new MonitorInfo { Size=(uint)Marshal.SizeOf<MonitorInfo>() };
        if(!GetMonitorInfo(MonitorFromPoint(cursor,2),ref monitor))throw new Win32Exception(Marshal.GetLastWin32Error());
        var identity=new IconIdentifier { Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=window,Id=1 };
        bool above=cursor.Y>=monitor.Work.Bottom;
        if(respectIcon&&Shell_NotifyIconGetRect(ref identity,out var rectangle)==0)
        {
            cursor=new(){X=(rectangle.Left+rectangle.Right)/2,Y=(rectangle.Top+rectangle.Bottom)/2};
            monitor=new(){Size=(uint)Marshal.SizeOf<MonitorInfo>()};
            if(!GetMonitorInfo(MonitorFromPoint(cursor,2),ref monitor))throw new Win32Exception(Marshal.GetLastWin32Error());
            above=rectangle.Top>=monitor.Work.Bottom-2;
            if(above)cursor.Y=rectangle.Top;
            else cursor.Y=rectangle.Bottom;
        }
        var anchor=new TrayMenuAnchor(new(cursor.X,cursor.Y),new(monitor.Work.Left,monitor.Work.Top,monitor.Work.Right-monitor.Work.Left,monitor.Work.Bottom-monitor.Work.Top),above);
        Dispatcher.UIThread.Post(()=>{if(!disposed)showMenu(anchor);});
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(visible){var data=Data();Shell_NotifyIcon(2,ref data);visible=false;}
        if(window!=0){DestroyWindow(window);window=0;}
        if(icon!=0){DestroyIcon(icon);icon=0;}
        if(registered){UnregisterClass(className,module);registered=false;}
        GC.KeepAlive(procedure);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate nint WindowProcedure(nint window,uint message,nuint wParam,nint lParam);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WindowClass
    { public uint Style;public nint Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? MenuName;public string ClassName; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public uint Size;public NativeRect Monitor,Work;public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct IconIdentifier { public uint Size;public nint Window;public uint Id;public Guid Guid; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct NotifyData
    {
        public uint Size;public nint Window;public uint Id,Flags,Callback;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Title;
        public uint InfoFlags;public Guid Guid;public nint BalloonIcon;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern ushort RegisterClass(ref WindowClass type);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool UnregisterClass(string name,nint instance);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern nint CreateWindowEx(uint extended,string type,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")]static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern nint DefWindowProc(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]static extern bool ChangeWindowMessageFilterEx(nint window,uint message,uint action,nint filter);
    [DllImport("user32.dll",SetLastError=true)]static extern nint CreateIconFromResourceEx(byte[] bytes,uint length,bool icon,uint version,int width,int height,uint flags);
    [DllImport("user32.dll")]static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Shell_NotifyIcon(uint message,ref NotifyData data);
    [DllImport("shell32.dll")]static extern int Shell_NotifyIconGetRect(ref IconIdentifier identity,out NativeRect rectangle);
    [DllImport("user32.dll",SetLastError=true)]static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]static extern nint MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
}
