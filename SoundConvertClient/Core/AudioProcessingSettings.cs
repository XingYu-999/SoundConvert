namespace UsbAudioBridge;
public sealed record AudioProcessingSettings(bool Enabled=false,double Balance=0,bool Mono=false,bool EqEnabled=false,double LowDb=0,double MidDb=0,double HighDb=0,bool LimiterEnabled=false,double CeilingDb=-1,bool OutputLimitEnabled=false,double OutputGainLimit=1,int ResumeFadeMs=0)
{
    public void Validate()
    {
        if(!double.IsFinite(Balance)||Balance is <-1 or >1||!double.IsFinite(LowDb)||LowDb is <-12 or >12||!double.IsFinite(MidDb)||MidDb is <-12 or >12||!double.IsFinite(HighDb)||HighDb is <-12 or >12||!double.IsFinite(CeilingDb)||CeilingDb is <-24 or >0||!double.IsFinite(OutputGainLimit)||OutputGainLimit is <0 or >1||ResumeFadeMs is <0 or >5000)throw new ArgumentOutOfRangeException(nameof(AudioProcessingSettings));
    }
}
