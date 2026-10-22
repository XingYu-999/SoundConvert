namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    public bool AudioProcessingEnabled{get=>Get<bool>(nameof(AudioProcessingEnabled));set=>SetRemote(nameof(AudioProcessingEnabled),value);}
    public double AudioBalance{get=>Get<double>(nameof(AudioBalance));set=>SetRemote(nameof(AudioBalance),value);}
    public bool AudioMono{get=>Get<bool>(nameof(AudioMono));set=>SetRemote(nameof(AudioMono),value);}
    public bool AudioEqEnabled{get=>Get<bool>(nameof(AudioEqEnabled));set=>SetRemote(nameof(AudioEqEnabled),value);}
    public double AudioEqLowDb{get=>Get<double>(nameof(AudioEqLowDb));set=>SetRemote(nameof(AudioEqLowDb),value);}
    public double AudioEqMidDb{get=>Get<double>(nameof(AudioEqMidDb));set=>SetRemote(nameof(AudioEqMidDb),value);}
    public double AudioEqHighDb{get=>Get<double>(nameof(AudioEqHighDb));set=>SetRemote(nameof(AudioEqHighDb),value);}
    public bool AudioLimiterEnabled{get=>Get<bool>(nameof(AudioLimiterEnabled));set=>SetRemote(nameof(AudioLimiterEnabled),value);}
    public double AudioLimiterCeilingDb{get=>Get<double>(nameof(AudioLimiterCeilingDb));set=>SetRemote(nameof(AudioLimiterCeilingDb),value);}
    public bool OutputLimitEnabled{get=>Get<bool>(nameof(OutputLimitEnabled));set=>SetRemote(nameof(OutputLimitEnabled),value);}
    public double OutputGainLimit{get=>Get<double>(nameof(OutputGainLimit));set=>SetRemote(nameof(OutputGainLimit),value);}
    public int ResumeFadeMs{get=>Get<int>(nameof(ResumeFadeMs));set=>SetRemote(nameof(ResumeFadeMs),value);}
    public double LevelLeftDb=>Get<double>(nameof(LevelLeftDb));
    public double LevelRightDb=>Get<double>(nameof(LevelRightDb));
    public double LevelLeftRmsDb=>Get<double>(nameof(LevelLeftRmsDb));
    public double LevelRightRmsDb=>Get<double>(nameof(LevelRightRmsDb));
    public string LevelStatus=>Get<string>(nameof(LevelStatus));
    public string LevelClipStatus=>Get<string>(nameof(LevelClipStatus));
    public string AudioProcessingStatus=>Get<string>(nameof(AudioProcessingStatus));
}
