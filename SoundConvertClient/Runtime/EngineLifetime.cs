using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoundConvertClient;

/// <summary>Owns launched engines while keeping audio processing in a separate process.</summary>
public sealed class EngineLifetime : IDisposable
{
    readonly string name = "Local\\UabOwner-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
    readonly List<Process> launched = [];
    SafeJobHandle? job;
    public string Name => name;
    public void Configure(ProcessStartInfo start)
    {
        if (job is null)
        {
            job = CreateJobObject(IntPtr.Zero, name);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        start.ArgumentList.Add("--owner-job"); start.ArgumentList.Add(name);
    }
    public void Track(Process process)
    {
        // Also cover injected launchers; the production engine joins before opening audio or IPC.
        if (process.HasExited) return;
        if (job is null) throw new InvalidOperationException("后台缺少所属前台生命周期。");
        if (!Owns(process.Id) && !AssignProcessToJobObject(job, process.Handle))
        {
            if (process.HasExited) return;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        for (int i=launched.Count-1;i>=0;i--) if(launched[i].HasExited){launched[i].Dispose();launched.RemoveAt(i);}
        var tracked=Process.GetProcessById(process.Id); _=tracked.Handle; launched.Add(tracked);
    }
    public static void JoinOwner(string[] args)
    {
        int index=Array.IndexOf(args,"--owner-job");
        if(index<0||index+1>=args.Length)throw new InvalidOperationException("后台必须由存活的前台启动，不能独立运行。");
        using var owner=OpenJobObject(0x0005,false,args[index+1]);
        if(owner.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"所属前台已退出，后台不会启动。");
        using var process=Process.GetCurrentProcess();
        if(!IsProcessInJob(process.Handle,owner,out bool contained))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(!contained&&!AssignProcessToJobObject(owner,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public bool Owns(int processId)
    {
        if(job is null||job.IsClosed||processId<=0)return false;
        try { using var process=Process.GetProcessById(processId); return IsProcessInJob(process.Handle,job,out bool contained)&&contained; }
        catch(ArgumentException){return false;}
    }
    public static Process ValidatePeer(NamedPipeClientStream pipe, EngineSnapshot snapshot, string expectedExecutable)
    {
        if (snapshot.Identity is not { } identity || !Guid.TryParse(identity.InstanceId, out _))
            throw new InvalidDataException("后台未提供可验证的实例身份，请通过旧版退出旧后台。");
        if (snapshot.Protocol != EngineProtocol.Version || identity.ProtocolMin > EngineProtocol.Version || identity.ProtocolMax < EngineProtocol.Version || snapshot.Version != EngineProtocol.Release)
            throw new InvalidDataException("后台协议或发布版本不兼容，已有后台保持运行。");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != snapshot.ProcessId)
            throw new InvalidDataException("后台管道服务进程身份不匹配。");
        var process = Process.GetProcessById(snapshot.ProcessId);
        try
        {
            int session=process.SessionId,currentSession=Process.GetCurrentProcess().SessionId;
            long actualStart=process.StartTime.ToUniversalTime().Ticks;
            string actualPath=Path.GetFullPath(process.MainModule!.FileName),expectedPath=Path.GetFullPath(expectedExecutable);
            if(session!=currentSession||identity.SessionId!=session)
                throw new InvalidDataException($"后台登录会话不匹配（实际 {session}，当前 {currentSession}，报告 {identity.SessionId}）；未停止已有后台。");
            if(actualStart!=identity.StartedUtcTicks)
                throw new InvalidDataException($"后台启动时间不匹配（差 {actualStart-identity.StartedUtcTicks} ticks）；未停止已有后台。");
            if(!string.Equals(actualPath,expectedPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"后台路径不匹配：{actualPath}，预期 {expectedPath}；未停止已有后台。");
            _ = process.Handle;
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    public void Dispose()
    {
        job?.Dispose();
        foreach(var process in launched)
        {
            try { if(!process.HasExited&&!process.WaitForExit(5000))throw new TimeoutException("所属后台尚未结束。"); }
            finally { process.Dispose(); }
        }
        launched.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits
    { public long ProcessTime,JobTime; public uint Flags; public nuint MinimumWorkingSet,MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters
    { public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
    sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public SafeJobHandle():base(true){} protected override bool ReleaseHandle()=>CloseHandle(handle); }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeJobHandle CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeJobHandle OpenJobObject(uint access,bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeJobHandle job,int infoClass,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(SafeJobHandle job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool IsProcessInJob(IntPtr process,SafeJobHandle job,[MarshalAs(UnmanagedType.Bool)] out bool contained);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
