using System.Text.Json;
using System.Windows.Input;
namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    readonly HashSet<string> hostKeys=[];
    bool hostHidReady,hostUsbMounted,hostUsbSuspended,hostKeyPending;
    string hostKeyFeedback="尚未发送媒体键；播放器状态未知。";
    string hostKeyTransport="尚未读取 HID 状态。";
    public string HostKeyFeedback{get=>hostKeyFeedback;private set=>Set(ref hostKeyFeedback,value);}
    public string HostKeyTransport{get=>hostKeyTransport;private set=>Set(ref hostKeyTransport,value);}
    public string HostKeyBlockReason=>!IsConnected?"请先连接开发板。":!CanManage?"当前为只读观察，需要管理权限。":UsbState!="RUNNING"?"请先启动 USB 模拟。":!hostUsbMounted?"USB 尚未枚举。":hostUsbSuspended?"USB Host 已挂起。":!hostHidReady?"HID 尚未就绪。":"HID 已就绪；按键交给 USB Host，播放器响应尚未验证。";
    public string HostMediaCapability=>hostKeys.Contains("play_pause")&&hostKeys.Contains("next_track")&&hostKeys.Contains("previous_track")?"固件接受播放／暂停和上下曲；按键不返回播放器状态，请在 USB Host 确认响应。":"固件未声明完整媒体键能力，请更新配套固件。";
    bool IsHostKeyAvailable(string key)=>!shuttingDown&&IsConnected&&CanManage&&UsbState=="RUNNING"&&hostUsbMounted&&!hostUsbSuspended&&hostHidReady&&hostKeys.Contains(key);
    public ICommand HostPlayPauseCommand=>HostKeyCommand(nameof(HostPlayPauseCommand),"play_pause");
    public ICommand HostNextTrackCommand=>HostKeyCommand(nameof(HostNextTrackCommand),"next_track");
    public ICommand HostPreviousTrackCommand=>HostKeyCommand(nameof(HostPreviousTrackCommand),"previous_track");
    public ICommand HostVolumeUpCommand=>HostKeyCommand(nameof(HostVolumeUpCommand),"volume_up");
    public ICommand HostVolumeDownCommand=>HostKeyCommand(nameof(HostVolumeDownCommand),"volume_down");
    public ICommand HostMuteCommand=>HostKeyCommand(nameof(HostMuteCommand),"mute");
    ICommand HostKeyCommand(string name,string key)=>SyncCommand(name,_=>_=SendHostKeyFromCommand(key),()=>IsHostKeyAvailable(key));
    async Task SendHostKeyFromCommand(string key){try{await SendHostKeyAsync(key);}catch(Exception error){GlobalError=error.Message;Log(error.Message);}}
    public static string? KeyForCommand(string? name)=>name switch{
        nameof(HostPlayPauseCommand)=>"play_pause",nameof(HostNextTrackCommand)=>"next_track",nameof(HostPreviousTrackCommand)=>"previous_track",
        nameof(HostVolumeUpCommand)=>"volume_up",nameof(HostVolumeDownCommand)=>"volume_down",nameof(HostMuteCommand)=>"mute",_=>null};
    public async Task<string> SendHostKeyAsync(string key,long createdTimestamp=0)
    {
        if(!IsHostKeyAvailable(key))throw new InvalidOperationException(hostKeys.Contains(key)?HostKeyBlockReason:"固件不支持此按键。");
        // Availability describes the device; in-flight exclusion is an execution guard.
        if(hostKeyPending)throw new InvalidOperationException("正在发送一个按键，请等待结果。");
        var target=Session;hostKeyPending=true;
        try
        {
            if(target!=control||!IsConnected||!CanManage||UsbState!="RUNNING"||!hostUsbMounted||hostUsbSuspended||!hostHidReady)throw new InvalidOperationException("设备状态已改变，按键未发送。");
            long created=createdTimestamp==0?System.Diagnostics.Stopwatch.GetTimestamp():createdTimestamp;
            var response=await target.RequestGuardedAsync("USB_HOST_KEY",new{key},
                ()=>target==control&&IsConnected&&CanManage&&UsbState=="RUNNING"&&hostUsbMounted&&!hostUsbSuspended&&hostHidReady&&!shuttingDown,
                created+System.Diagnostics.Stopwatch.Frequency/5);
            if(!response.TryGetProperty("accepted",out var accepted)||accepted.ValueKind!=JsonValueKind.True)throw new IOException("未收到按键接受确认。");
            HostKeyFeedback=$"{KeyLabel(key)}：开发板已接受；HID 完成情况见计数，播放器状态未知。";
            Log(HostKeyFeedback);return HostKeyFeedback;
        }
        catch(UsbAudioBridge.DeviceException error){HostKeyFeedback=$"按键被拒绝（{error.Code}）：{error.Message}";throw;}
        catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException)
        {
            HostKeyFeedback="按键结果未知；不会自动重发，请确认 Host 后再操作。";
            throw new IOException(HostKeyFeedback,error);
        }
        catch(UsbAudioBridge.ControlRequestExpiredException){HostKeyFeedback="媒体键已过期，未发送。";throw;}
        finally{hostKeyPending=false;}
    }
    static string KeyLabel(string key)=>key switch{"play_pause"=>"播放／暂停","next_track"=>"下一曲","previous_track"=>"上一曲","volume_up"=>"Host 音量加","volume_down"=>"Host 音量减","mute"=>"Host 静音键",_=>key};
    void ReadHostKeyCapabilities(JsonElement data)
    {
        hostKeys.Clear();
        if(data.TryGetProperty("host_control_keys",out var keys)&&keys.ValueKind==JsonValueKind.Array)
        {
            foreach(var key in keys.EnumerateArray())if(key.ValueKind==JsonValueKind.String&&IsKnownHostKey(key.GetString()))hostKeys.Add(key.GetString()!);
        }
        else if(data.TryGetProperty("host_volume_keys_supported",out var old)&&old.ValueKind==JsonValueKind.True)
            hostKeys.UnionWith(["volume_up","volume_down","mute"]);
        RefreshHostKeyControls();Changed(nameof(HostMediaCapability));
    }
    static bool IsKnownHostKey(string? key)=>key is "play_pause" or "next_track" or "previous_track" or "volume_up" or "volume_down" or "mute";
    void ReadHostKeyState(JsonElement info)
    {
        hostHidReady=info.TryGetProperty("host_control",out var state)&&state.TryGetProperty("ready",out var ready)&&ready.ValueKind==JsonValueKind.True;
        hostUsbMounted=hostUsbSuspended=false;
        if(info.TryGetProperty("usb",out var usb))
        {
            hostUsbMounted=usb.TryGetProperty("mounted",out var mounted)&&mounted.ValueKind==JsonValueKind.True;
            hostUsbSuspended=usb.TryGetProperty("suspended",out var suspended)&&suspended.ValueKind==JsonValueKind.True;
        }
        HostKeyTransport=state.ValueKind==JsonValueKind.Object?$"HID：待处理 {Number(state,"pending"):0} · 已完成按下／释放 {Number(state,"completed"):0} · 已丢弃 {Number(state,"dropped"):0}（不是播放器确认）":"固件未提供 HID 传输状态。";
        RefreshHostKeyControls();
    }
    void ResetHostKeys(){hostKeys.Clear();hostHidReady=hostUsbMounted=hostUsbSuspended=false;HostKeyFeedback="尚未发送媒体键；播放器状态未知。";HostKeyTransport="尚未读取 HID 状态。";RefreshHostKeyControls();Changed(nameof(HostMediaCapability));}
    void RefreshHostKeyControls(){Changed(nameof(HostKeyBlockReason));RefreshCommandStates();}
}
