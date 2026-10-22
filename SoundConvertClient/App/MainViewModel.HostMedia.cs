using System.Windows.Input;
namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    bool hostKeyInFlight;
    public bool CanControlBoardGain=>Get<bool>(nameof(CanControlBoardGain));
    public double BoardVolumePercent{get=>Get<double>(nameof(BoardVolumePercent));set=>SetRemote(nameof(BoardVolumePercent),Math.Round(Math.Clamp(value,0,100)));}
    public bool BoardMuted{get=>Get<bool>(nameof(BoardMuted));set=>SetRemote(nameof(BoardMuted),value);}
    public string BoardGainReadout=>$"{BoardVolumePercent:0}%";
    public string HostKeyBlockReason=>!IsConnected?"请先连接开发板。":Get<string>(nameof(HostKeyBlockReason));
    public string HostKeyFeedback=>Get<string>(nameof(HostKeyFeedback));
    public string HostKeyTransport=>Get<string>(nameof(HostKeyTransport));
    public string HostMediaCapability=>Get<string>(nameof(HostMediaCapability));
    public ICommand HostPlayPauseCommand=>Command(nameof(HostPlayPauseCommand));
    public ICommand HostNextTrackCommand=>Command(nameof(HostNextTrackCommand));
    public ICommand HostPreviousTrackCommand=>Command(nameof(HostPreviousTrackCommand));
    public ICommand HostVolumeUpCommand=>Command(nameof(HostVolumeUpCommand));
    public ICommand HostVolumeDownCommand=>Command(nameof(HostVolumeDownCommand));
    public ICommand HostMuteCommand=>Command(nameof(HostMuteCommand));
    async Task SendHostKeyCommand(string name)
    {
        // Ignore overlapping taps without changing the whole row's enabled appearance.
        if(hostKeyInFlight)return;
        hostKeyInFlight=true;
        try{await SendAsync(new("command",name));}
        finally{hostKeyInFlight=false;}
    }
}
