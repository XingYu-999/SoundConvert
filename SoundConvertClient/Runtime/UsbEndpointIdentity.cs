using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SoundConvertClient;

public sealed record UsbEndpointSnapshot(bool Available, string? EndpointId, string DisplayName, string Reason);

/// <summary>Identifies the physical board by USB serial ancestry, without reading or changing Host volume.</summary>
public static class UsbEndpointIdentity
{
    private const string HardwareId = "VID_CAFE&PID_401B";
    sealed record CachedEndpoint(string InstanceId,bool Verified,string? BoardSerial,long Seen);
    static readonly ConcurrentDictionary<string,CachedEndpoint> ancestry=new(StringComparer.OrdinalIgnoreCase);
    static long nativeProbeCount;
    public static long NativeProbeCount=>Interlocked.Read(ref nativeProbeCount);

    // Called only while enumerating endpoints. Snapshot/CanExecute must never
    // open COM devices or walk the PnP tree.
    public static void CacheEndpoint(MMDevice endpoint)
    {
        string id=endpoint.ID,instance;
        try{instance=endpoint.InstanceId;}catch(COMException){instance="";}
        long now=Stopwatch.GetTimestamp();
        if(ancestry.TryGetValue(id,out var cached)&&cached.Verified&&cached.InstanceId==instance)
        {ancestry[id]=cached with{Seen=now};return;}
        Interlocked.Increment(ref nativeProbeCount);
        bool first=TryBoardAncestry(instance,out string? firstSerial);
        bool second=TryBoardAncestry(@"SWD\MMDEVAPI\"+id,out string? secondSerial);
        ancestry[id]=new(instance,first||second,firstSerial??secondSerial,now);
        if(ancestry.Count>256)
            foreach(var old in ancestry.OrderBy(x=>x.Value.Seen).Take(64))ancestry.TryRemove(old.Key,out _);
    }
    public static bool IsBoardOrUnverified(string endpointId,string? expectedSerial)
    {
        if(!ancestry.TryGetValue(endpointId,out var endpoint)||!endpoint.Verified)return true;
        return endpoint.BoardSerial is not null&&(string.IsNullOrWhiteSpace(expectedSerial)||endpoint.BoardSerial.Equals(expectedSerial,StringComparison.OrdinalIgnoreCase));
    }
    public static string CachedSafetyReason(string endpointId,string? expectedSerial)
    {
        if(!ancestry.TryGetValue(endpointId,out var endpoint)||!endpoint.Verified)return "音频端点身份尚未确认，请等待设备刷新后重试。";
        return IsBoardOrUnverified(endpointId,expectedSerial)?"这是开发板自身的 USB 音频端点，请选择电脑耳机或麦克风。":"";
    }
    static bool TryBoardAncestry(string instanceId,out string? boardSerial)
    {
        boardSerial=null;
        if(string.IsNullOrWhiteSpace(instanceId)||CM_Locate_DevNodeW(out uint node,instanceId,0)!=0)return false;
        for(int depth=0;depth<16;depth++)
        {
            var id=new StringBuilder(1024);
            if(CM_Get_Device_IDW(node,id,id.Capacity,0)!=0)return false;
            string[] parts=id.ToString().Split('\\');
            if(parts.Length==3&&parts[0].Equals("USB",StringComparison.OrdinalIgnoreCase)&&parts[1].Contains(HardwareId,StringComparison.OrdinalIgnoreCase)&&!parts[1].Contains("&MI_",StringComparison.OrdinalIgnoreCase))
            {boardSerial=parts[2];return true;}
            if(parts.Length==3&&parts[0].Equals("HTREE",StringComparison.OrdinalIgnoreCase)&&parts[1].Equals("ROOT",StringComparison.OrdinalIgnoreCase))return true;
            if(CM_Get_Parent(out uint parent,node,0)!=0||parent==node)return false;
            node=parent;
        }
        return false;
    }

    public static Task<UsbEndpointSnapshot> FindAsync(string? usbSerial, CancellationToken ct = default) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(usbSerial))
            return Unavailable("开发板未提供 USB 序列号，无法确认本机 USB 音频端点。");
        ct.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var matches = new List<UsbEndpointSnapshot>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (device)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!MatchesEndpoint(device, usbSerial)) continue;
                matches.Add(new(true, device.ID, device.FriendlyName, ""));
            }
            catch (COMException) { /* Endpoint disappeared while enumerating. */ }
        }
        if (matches.Count != 1)
            return Unavailable(matches.Count == 0
                ? "本机未找到这块板的 USB 音频端点。远端 Host 的系统音量需在 Host 上调整。"
                : "这块板对应多个音频端点，无法安全确定 USB 音频端点。");
        var result = matches[0];
        return result;
    }, ct);

    private static UsbEndpointSnapshot Unavailable(string reason) => new(false, null, "USB", reason);
    public static bool IsEndpointForSerial(string endpointId,string serial)
    {
        if(string.IsNullOrWhiteSpace(serial))return false;
        Interlocked.Increment(ref nativeProbeCount);
        try{using var e=new MMDeviceEnumerator();using var d=e.GetDevice(endpointId);return MatchesEndpoint(d,serial);}catch(COMException){return false;}
    }

    // Some drivers omit PKEY_Device_InstanceId. The MMDEVAPI software devnode still
    // exists and its PnP parents identify the physical USB device unambiguously.
    private static bool MatchesEndpoint(MMDevice endpoint, string serial) =>
        MatchesSerial(endpoint.InstanceId, serial) ||
        MatchesSerial(@"SWD\MMDEVAPI\" + endpoint.ID, serial);

    internal static bool MatchesSerial(string instanceId, string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Length > 128) return false;
        if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0) return false;
        for (int depth = 0; depth < 16; depth++)
        {
            var id = new StringBuilder(1024);
            if (CM_Get_Device_IDW(node, id, id.Capacity, 0) != 0) return false;
            string[] parts = id.ToString().Split('\\');
            if (parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
                && parts[1].Contains(HardwareId, StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals(serial, StringComparison.OrdinalIgnoreCase)) return true;
            if (CM_Get_Parent(out uint parent, node, 0) != 0 || parent == node) break;
            node = parent;
        }
        return false;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
