using System.Text.Json;
using System.Windows.Input;
using UsbAudioBridge;
namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    bool runtimeStatusSupported;
    int monitorIntervalSeconds=3;
    DateTimeOffset nextMonitor,lastMonitorSample;
    long monitorSamples,monitorSkips;
    string monitorSkipReason="",monitorDetails="尚无状态快照",monitorStatus="等待连接",boardMac="—",boardUsbVersion="—",boardUacVersion="—",boardLinkSpeed="—";
    public bool MonitorEnabled=>true;
    public long MonitorSampleCount=>monitorSamples;
    public long MonitorSkippedCount=>monitorSkips;
    public double MonitorDataAgeSeconds=>lastMonitorSample==default?-1:Math.Max(0,(DateTimeOffset.UtcNow-lastMonitorSample).TotalSeconds);
    public string MonitorButtonText=>"自动监视";
    public int[] MonitorIntervals {get;}=[1,3,5,10];
    public int MonitorIntervalSeconds
    {
        get=>monitorIntervalSeconds;
        set
        {
            if(value is not (1 or 3 or 5 or 10)||value==monitorIntervalSeconds)return;
            Set(ref monitorIntervalSeconds,value);nextMonitor=DateTimeOffset.MinValue;
            if(!initializing)LocalStore.SaveSettings(CurrentSettings());RefreshMonitorAge();
        }
    }
    public string MonitorDetails{get=>monitorDetails;private set=>Set(ref monitorDetails,value);}
    public string MonitorStatus{get=>monitorStatus;private set=>Set(ref monitorStatus,value);}
    public string BoardMac{get=>boardMac;private set=>Set(ref boardMac,value);}public string BoardUsbVersion{get=>boardUsbVersion;private set=>Set(ref boardUsbVersion,value);}
    public string BoardUacVersion{get=>boardUacVersion;private set=>Set(ref boardUacVersion,value);}public string BoardLinkSpeed{get=>boardLinkSpeed;private set=>Set(ref boardLinkSpeed,value);}
    public string ControlPortLabel=>$"TCP {ControlPort}";
    public string PlaybackPorts=>receiver?.Descriptor is {} p?$"虚拟声卡 UDP {p.SourcePort} → 软件 UDP {p.ReceivePort} · {(receiver.Fault is null?"已协商":"异常："+receiver.Fault)}":$"未启用 · 虚拟声卡：{UdpPortSettings.Label(udpPorts.BoardPlayback)} → 软件：{UdpPortSettings.Label(udpPorts.SoftwarePlayback)}";
    public string CapturePorts=>microphoneSender?.Descriptor is {} c?$"软件 UDP {microphoneSender.LocalPort} → 虚拟声卡 UDP {c.DestinationPort} · {(microphoneSender.Fault is null?"已协商":"异常："+microphoneSender.Fault)}":$"未启用 · 软件：{UdpPortSettings.Label(udpPorts.SoftwareCapture)} → 虚拟声卡：{UdpPortSettings.Label(udpPorts.BoardCapture)}";
    // Kept for source compatibility with old tools. Monitoring is always on.
    public ICommand ToggleMonitorCommand=>SyncCommand(nameof(ToggleMonitorCommand),_=>{},()=>false);
    void CacheBoardIdentity(JsonElement data)
    {
        BoardMac=String(data,"mac",BoardMac);BoardUsbVersion=String(data,"usb_version",BoardUsbVersion);
        if(data.TryGetProperty("uac_version",out var uac))BoardUacVersion="UAC"+uac.GetInt32()+".0";
        BoardLinkSpeed=String(data,"usb_link_speed",BoardLinkSpeed);Changed(nameof(ControlPortLabel));
    }
    void RefreshMonitorAge()
    {
        if(!IsConnected){MonitorStatus=$"等待连接 · 自动监视 {MonitorIntervalSeconds}s";return;}
        double age=lastMonitorSample==default?-1:(DateTimeOffset.UtcNow-lastMonitorSample).TotalSeconds;
        MonitorStatus=$"配置间隔 {MonitorIntervalSeconds}s · "+(age<0?"等待首次采样":$"最近更新 {lastMonitorSample.LocalDateTime:HH:mm:ss} · 数据年龄 {age:F0}s" )+
            (age>MonitorIntervalSeconds*2?" · 状态暂未更新":"")+$" · 采样 {monitorSamples} / 跳过 {monitorSkips}"+
            (string.IsNullOrEmpty(monitorSkipReason)?"":" · "+monitorSkipReason);
    }
    async Task PollMonitor()
    {
        var now=DateTimeOffset.UtcNow;
        if(!IsConnected||IsConnecting||now<nextMonitor)return;
        nextMonitor=now.AddSeconds(MonitorIntervalSeconds);
        if(IsBusy||!await commands.WaitAsync(0)){monitorSkips++;monitorSkipReason="控制操作繁忙，本周期跳过";RefreshMonitorAge();return;}
        try
        {
            var stats=await Session.RequestAsync("GET_STATS");
            clockObservation?.ReportBoardHealth(stats);
            MonitorDetails=JsonSerializer.Serialize(stats,new JsonSerializerOptions{WriteIndented=true});
            lastMonitorSample=DateTimeOffset.UtcNow;monitorSamples++;monitorSkipReason="";
            ReadCaptureStatus(stats);
        }
        catch(Exception e){monitorSkips++;monitorSkipReason="采样失败："+e.Message;}
        finally{commands.Release();RefreshMonitorAge();}
    }
}
