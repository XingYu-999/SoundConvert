using System.Runtime.InteropServices;
namespace UsbAudioBridge;

/// <summary>Optional Windows audio scheduling, scoped to the receive owner thread.</summary>
internal sealed class AudioReceiveScheduling:IDisposable
{
    IntPtr handle;
    public bool Registered=>handle!=IntPtr.Zero;
    public int Error{get;private set;}
    [DllImport("avrt.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr AvSetMmThreadCharacteristicsW(string task,ref uint index);
    [DllImport("avrt.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    public AudioReceiveScheduling()
    {
        if(!OperatingSystem.IsWindows())return;
        try{uint index=0;handle=AvSetMmThreadCharacteristicsW("Audio",ref index);if(handle==IntPtr.Zero)Error=Marshal.GetLastWin32Error();}
        catch(DllNotFoundException){Error=126;}
        catch(EntryPointNotFoundException){Error=127;}
    }
    public void Dispose(){if(handle!=IntPtr.Zero){AvRevertMmThreadCharacteristics(handle);handle=IntPtr.Zero;}}
}
