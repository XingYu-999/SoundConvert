namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    public bool CanControlCaptureVolume{get=>Get<bool>(nameof(CanControlCaptureVolume));}
    public double CaptureUsbVolumeDb{get=>Get<double>(nameof(CaptureUsbVolumeDb));set=>SetRemote(nameof(CaptureUsbVolumeDb),value);}
    public bool CaptureUsbMuted{get=>Get<bool>(nameof(CaptureUsbMuted));set=>SetRemote(nameof(CaptureUsbMuted),value);}
    public string CaptureUsbControlSource{get=>Get<string>(nameof(CaptureUsbControlSource));}
}
