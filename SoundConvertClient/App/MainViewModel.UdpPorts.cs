using System.Windows.Input;

namespace SoundConvertClient;

public sealed partial class MainViewModel
{
    string UdpPortPolicy(string name)=>values.ContainsKey(name)?Get<string>(name):UsbAudioBridge.UdpPortSettings.Automatic;
    public string BoardPlaybackUdpPort { get => UdpPortPolicy(nameof(BoardPlaybackUdpPort)); set => SetRemote(nameof(BoardPlaybackUdpPort), value); }
    public string SoftwarePlaybackUdpPort { get => UdpPortPolicy(nameof(SoftwarePlaybackUdpPort)); set => SetRemote(nameof(SoftwarePlaybackUdpPort), value); }
    public string BoardCaptureUdpPort { get => UdpPortPolicy(nameof(BoardCaptureUdpPort)); set => SetRemote(nameof(BoardCaptureUdpPort), value); }
    public string SoftwareCaptureUdpPort { get => UdpPortPolicy(nameof(SoftwareCaptureUdpPort)); set => SetRemote(nameof(SoftwareCaptureUdpPort), value); }
    public int LastAutomaticBoardPlaybackUdpPort=>Get<int>(nameof(LastAutomaticBoardPlaybackUdpPort));
    public int LastAutomaticSoftwarePlaybackUdpPort=>Get<int>(nameof(LastAutomaticSoftwarePlaybackUdpPort));
    public int LastAutomaticBoardCaptureUdpPort=>Get<int>(nameof(LastAutomaticBoardCaptureUdpPort));
    public int LastAutomaticSoftwareCaptureUdpPort=>Get<int>(nameof(LastAutomaticSoftwareCaptureUdpPort));
    public bool BoardPlaybackUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(BoardPlaybackUdpPort);set=>BoardPlaybackUdpPort=UdpPortPresentation.Mode(BoardPlaybackUdpPort,LastAutomaticBoardPlaybackUdpPort,value);}
    public bool SoftwarePlaybackUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(SoftwarePlaybackUdpPort);set=>SoftwarePlaybackUdpPort=UdpPortPresentation.Mode(SoftwarePlaybackUdpPort,LastAutomaticSoftwarePlaybackUdpPort,value);}
    public bool BoardCaptureUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(BoardCaptureUdpPort);set=>BoardCaptureUdpPort=UdpPortPresentation.Mode(BoardCaptureUdpPort,LastAutomaticBoardCaptureUdpPort,value);}
    public bool SoftwareCaptureUdpPortAutomatic {get=>UdpPortPresentation.IsAutomatic(SoftwareCaptureUdpPort);set=>SoftwareCaptureUdpPort=UdpPortPresentation.Mode(SoftwareCaptureUdpPort,LastAutomaticSoftwareCaptureUdpPort,value);}
    public string BoardPlaybackUdpPortNumber {get=>UdpPortPresentation.Number(BoardPlaybackUdpPort,LastAutomaticBoardPlaybackUdpPort);set{if(!BoardPlaybackUdpPortAutomatic)BoardPlaybackUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string SoftwarePlaybackUdpPortNumber {get=>UdpPortPresentation.Number(SoftwarePlaybackUdpPort,LastAutomaticSoftwarePlaybackUdpPort);set{if(!SoftwarePlaybackUdpPortAutomatic)SoftwarePlaybackUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string BoardCaptureUdpPortNumber {get=>UdpPortPresentation.Number(BoardCaptureUdpPort,LastAutomaticBoardCaptureUdpPort);set{if(!BoardCaptureUdpPortAutomatic)BoardCaptureUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string SoftwareCaptureUdpPortNumber {get=>UdpPortPresentation.Number(SoftwareCaptureUdpPort,LastAutomaticSoftwareCaptureUdpPort);set{if(!SoftwareCaptureUdpPortAutomatic)SoftwareCaptureUdpPort=UdpPortPresentation.ManualInput(value);}}
    public string UdpPortSettingsStatus => Get<string>(nameof(UdpPortSettingsStatus));
    public string UdpPortNegotiationStatus => Get<string>(nameof(UdpPortNegotiationStatus));
    public ICommand SaveUdpPortsCommand => Command(nameof(SaveUdpPortsCommand));
    async Task SaveUdpPortDraftAsync() => await SendAsync(new("command",nameof(SaveUdpPortsCommand),
        EngineProtocol.Json(new UdpPortDraft(BoardPlaybackUdpPort,SoftwarePlaybackUdpPort,BoardCaptureUdpPort,SoftwareCaptureUdpPort))));
}
