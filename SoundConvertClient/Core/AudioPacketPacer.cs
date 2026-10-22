using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace UsbAudioBridge;

/// <summary>Process-local high resolution waitable timer; no global timer-resolution changes.</summary>
sealed class AudioPacketPacer:IDisposable
{
    sealed class TimerWait:WaitHandle{public TimerWait(IntPtr h)=>SafeWaitHandle=new SafeWaitHandle(h,true);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateWaitableTimerExW(IntPtr attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetWaitableTimerEx(SafeWaitHandle timer,ref long dueTime,int period,IntPtr completion,IntPtr argument,IntPtr wakeContext,uint tolerableDelay);
    readonly TimerWait? timer;readonly WaitHandle[] waits;
    public AudioPacketPacer(AutoResetEvent wake){if(OperatingSystem.IsWindows()){IntPtr h=CreateWaitableTimerExW(IntPtr.Zero,null,2,0x100002);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Unable to create microphone packet timer");timer=new(h);waits=[wake,timer];}else waits=[wake];}
    public void WaitUntil(long deadline)
    {
        long remaining=deadline-Stopwatch.GetTimestamp();if(remaining<=0)return;
        if(timer is null){waits[0].WaitOne((int)Math.Clamp(Math.Ceiling(remaining*1000d/Stopwatch.Frequency),1,int.MaxValue));return;}
        // A source callback can wake us before the current burst deadline. Arm
        // only its remaining duration; restarting a whole millisecond on each
        // callback would add avoidable delay under callback/CPU load.
        long due=-Math.Max(1,(long)Math.Ceiling(remaining*10_000_000d/Stopwatch.Frequency));
        if(!SetWaitableTimerEx(timer.SafeWaitHandle,ref due,0,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        WaitHandle.WaitAny(waits);
    }
    public void Dispose()=>timer?.Dispose();
}
