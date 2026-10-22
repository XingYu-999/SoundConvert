using UsbAudioBridge;

namespace SoundConvertClient;

public static class UdpPortPresentation
{
    public static bool IsAutomatic(string policy)=>policy==UdpPortSettings.Automatic;
    public static string Number(string policy,int lastAutomatic)=>IsAutomatic(policy)?lastAutomatic>0?UdpPortSettings.Label(lastAutomatic):"":policy;
    public static string Mode(string policy,int lastAutomatic,bool automatic)=>IsAutomatic(policy)==automatic?policy:automatic?UdpPortSettings.Automatic:Number(policy,lastAutomatic);
    public static string ManualInput(string? text)=>text?.Trim()==UdpPortSettings.Automatic?"":text??"";
    public static (string? Mode,string? Number) Related(string? property)=>property switch
    {
        nameof(IBridgeView.BoardPlaybackUdpPort)=>(nameof(IBridgeView.BoardPlaybackUdpPortAutomatic),nameof(IBridgeView.BoardPlaybackUdpPortNumber)),
        nameof(IBridgeView.SoftwarePlaybackUdpPort)=>(nameof(IBridgeView.SoftwarePlaybackUdpPortAutomatic),nameof(IBridgeView.SoftwarePlaybackUdpPortNumber)),
        nameof(IBridgeView.BoardCaptureUdpPort)=>(nameof(IBridgeView.BoardCaptureUdpPortAutomatic),nameof(IBridgeView.BoardCaptureUdpPortNumber)),
        nameof(IBridgeView.SoftwareCaptureUdpPort)=>(nameof(IBridgeView.SoftwareCaptureUdpPortAutomatic),nameof(IBridgeView.SoftwareCaptureUdpPortNumber)),
        nameof(IBridgeView.LastAutomaticBoardPlaybackUdpPort)=>(null,nameof(IBridgeView.BoardPlaybackUdpPortNumber)),
        nameof(IBridgeView.LastAutomaticSoftwarePlaybackUdpPort)=>(null,nameof(IBridgeView.SoftwarePlaybackUdpPortNumber)),
        nameof(IBridgeView.LastAutomaticBoardCaptureUdpPort)=>(null,nameof(IBridgeView.BoardCaptureUdpPortNumber)),
        nameof(IBridgeView.LastAutomaticSoftwareCaptureUdpPort)=>(null,nameof(IBridgeView.SoftwareCaptureUdpPortNumber)),
        _=>default
    };
}
