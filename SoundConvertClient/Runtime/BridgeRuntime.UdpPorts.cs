using System.Text.Json;
using System.Windows.Input;
using UsbAudioBridge;

namespace SoundConvertClient;

public sealed record UdpPortDraft(string BoardPlayback, string SoftwarePlayback, string BoardCapture, string SoftwareCapture);

public sealed partial class BridgeRuntime
{
    UdpPortSettings udpPorts = new();
    UdpPortHistory lastAutomaticUdpPorts = new();
    UdpPortSettings? activePlaybackUdpPorts,activeCaptureUdpPorts;
    string boardPlaybackUdpPort = UdpPortSettings.Automatic, softwarePlaybackUdpPort = UdpPortSettings.Automatic;
    string boardCaptureUdpPort = UdpPortSettings.Automatic, softwareCaptureUdpPort = UdpPortSettings.Automatic;
    string udpPortSettingsStatus = "保存后在下一次开始输出或启用麦克风时生效。";
    bool udpPortNegotiationSupported;
    public string BoardPlaybackUdpPort { get => boardPlaybackUdpPort; set => Set(ref boardPlaybackUdpPort, value); }
    public string SoftwarePlaybackUdpPort { get => softwarePlaybackUdpPort; set => Set(ref softwarePlaybackUdpPort, value); }
    public string BoardCaptureUdpPort { get => boardCaptureUdpPort; set => Set(ref boardCaptureUdpPort, value); }
    public string SoftwareCaptureUdpPort { get => softwareCaptureUdpPort; set => Set(ref softwareCaptureUdpPort, value); }
    public int LastAutomaticBoardPlaybackUdpPort=>lastAutomaticUdpPorts.BoardPlayback;
    public int LastAutomaticSoftwarePlaybackUdpPort=>lastAutomaticUdpPorts.SoftwarePlayback;
    public int LastAutomaticBoardCaptureUdpPort=>lastAutomaticUdpPorts.BoardCapture;
    public int LastAutomaticSoftwareCaptureUdpPort=>lastAutomaticUdpPorts.SoftwareCapture;
    public bool BoardPlaybackUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(BoardPlaybackUdpPort);set=>BoardPlaybackUdpPort=UdpPortPresentation.Mode(BoardPlaybackUdpPort,LastAutomaticBoardPlaybackUdpPort,value);}
    public bool SoftwarePlaybackUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(SoftwarePlaybackUdpPort);set=>SoftwarePlaybackUdpPort=UdpPortPresentation.Mode(SoftwarePlaybackUdpPort,LastAutomaticSoftwarePlaybackUdpPort,value);}
    public bool BoardCaptureUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(BoardCaptureUdpPort);set=>BoardCaptureUdpPort=UdpPortPresentation.Mode(BoardCaptureUdpPort,LastAutomaticBoardCaptureUdpPort,value);}
    public bool SoftwareCaptureUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(SoftwareCaptureUdpPort);set=>SoftwareCaptureUdpPort=UdpPortPresentation.Mode(SoftwareCaptureUdpPort,LastAutomaticSoftwareCaptureUdpPort,value);}
    public string BoardPlaybackUdpPortNumber {get=>UdpPortPresentation.Number(BoardPlaybackUdpPort,LastAutomaticBoardPlaybackUdpPort);set{if(!BoardPlaybackUdpPortAutomatic)BoardPlaybackUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string SoftwarePlaybackUdpPortNumber {get=>UdpPortPresentation.Number(SoftwarePlaybackUdpPort,LastAutomaticSoftwarePlaybackUdpPort);set{if(!SoftwarePlaybackUdpPortAutomatic)SoftwarePlaybackUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string BoardCaptureUdpPortNumber {get=>UdpPortPresentation.Number(BoardCaptureUdpPort,LastAutomaticBoardCaptureUdpPort);set{if(!BoardCaptureUdpPortAutomatic)BoardCaptureUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string SoftwareCaptureUdpPortNumber {get=>UdpPortPresentation.Number(SoftwareCaptureUdpPort,LastAutomaticSoftwareCaptureUdpPort);set{if(!SoftwareCaptureUdpPortAutomatic)SoftwareCaptureUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string UdpPortSettingsStatus { get => udpPortSettingsStatus; private set => Set(ref udpPortSettingsStatus, value); }
    public string UdpPortNegotiationStatus => !IsConnected ? "未连接；开始音频传输时协商端口。" : udpPortNegotiationSupported
        ? "当前固件支持 UDP 端口自动协商及指定端口。" : "兼容模式：当前固件使用固定声卡端口；软件端仍可自动分配。声卡自动选端口需更新配套固件。";
    public ICommand SaveUdpPortsCommand => AsyncCommand(nameof(SaveUdpPortsCommand), SaveUdpPorts);
    void LoadUdpPorts(Settings settings)
    {
        var loaded = settings.UdpPorts ?? new();
        try { loaded.Validate(); } catch (ArgumentException) { loaded = new(); }
        lastAutomaticUdpPorts=(settings.LastAutomaticUdpPorts??new()).Normalize();
        udpPorts = loaded; ResetUdpPortDraft();
    }
    void ResetUdpPortDraft()
    {
        BoardPlaybackUdpPort = UdpPortSettings.Label(udpPorts.BoardPlayback); SoftwarePlaybackUdpPort = UdpPortSettings.Label(udpPorts.SoftwarePlayback);
        BoardCaptureUdpPort = UdpPortSettings.Label(udpPorts.BoardCapture); SoftwareCaptureUdpPort = UdpPortSettings.Label(udpPorts.SoftwareCapture);
    }
    Task SaveUdpPorts() => SaveUdpPortsAsync(new(BoardPlaybackUdpPort,SoftwarePlaybackUdpPort,BoardCaptureUdpPort,SoftwareCaptureUdpPort));
    public Task SaveUdpPortsAsync(UdpPortDraft draft)
    {
        var next = new UdpPortSettings(UdpPortSettings.Parse(draft.BoardPlayback), UdpPortSettings.Parse(draft.SoftwarePlayback),
            UdpPortSettings.Parse(draft.BoardCapture), UdpPortSettings.Parse(draft.SoftwareCapture));
        next.Validate();
        // Persist all four ports together; unrelated automatic saves keep the last committed policy.
        LocalStore.SaveSettings(CurrentSettings() with { UdpPorts = next }); udpPorts = next; ResetUdpPortDraft();
        UdpPortSettingsStatus = "端口设置已保存；在下一次开始输出或启用麦克风时生效，当前传输继续使用现有端口。";
        RefreshUdpPortStatus(); return Task.CompletedTask;
    }
    void ReadUdpPortCapabilities(JsonElement data)
    {
        udpPortNegotiationSupported = data.TryGetProperty("udp_port_negotiation_v1", out var p) && p.ValueKind == JsonValueKind.True;
        RefreshUdpPortStatus();
    }
    void EnsureBoardUdpPort(int port, int legacy)
    {
        if (!udpPortNegotiationSupported && port != 0 && port != legacy)
            throw new NotSupportedException("当前固件不支持指定声卡 UDP 端口，请更新配套固件或选择自动协商。");
    }
    void RefreshUdpPortStatus()
    {
        if(receiver is {Fault:null,IsRecovering:false,Ended:false,Descriptor:{} p}&&activePlaybackUdpPorts is {} playback)
            RememberAutomaticPlaybackPorts(playback,p.SourcePort,p.ReceivePort);
        if(microphoneSender is {Fault:null,Descriptor:{} c} sender&&activeCaptureUdpPorts is {} capture)
            RememberAutomaticCapturePorts(capture,c.DestinationPort,sender.LocalPort);
        Changed(nameof(PlaybackPorts)); Changed(nameof(CapturePorts)); Changed(nameof(UdpPortNegotiationStatus));
    }
    void RememberAutomaticPlaybackPorts(UdpPortSettings policy,int board,int software)=>RememberAutomaticUdpPorts(lastAutomaticUdpPorts.RememberPlayback(policy,board,software));
    void RememberAutomaticCapturePorts(UdpPortSettings policy,int board,int software)=>RememberAutomaticUdpPorts(lastAutomaticUdpPorts.RememberCapture(policy,board,software));
    void RememberAutomaticUdpPorts(UdpPortHistory next)
    {
        if(next==lastAutomaticUdpPorts)return;
        var previous=lastAutomaticUdpPorts;lastAutomaticUdpPorts=next;
        if(previous.BoardPlayback!=next.BoardPlayback)Changed(nameof(LastAutomaticBoardPlaybackUdpPort));
        if(previous.SoftwarePlayback!=next.SoftwarePlayback)Changed(nameof(LastAutomaticSoftwarePlaybackUdpPort));
        if(previous.BoardCapture!=next.BoardCapture)Changed(nameof(LastAutomaticBoardCaptureUdpPort));
        if(previous.SoftwareCapture!=next.SoftwareCapture)Changed(nameof(LastAutomaticSoftwareCaptureUdpPort));
        try{LocalStore.SaveSettings(CurrentSettings());}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException)
        {UdpPortSettingsStatus="自动端口记录未能保存："+error.Message;}
    }
}
