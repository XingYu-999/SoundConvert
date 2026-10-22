namespace UsbAudioBridge;
public sealed class OutputGainPolicy
{
    int fadeFrames,remaining;
    public double ResolveTargetGain(AudioProcessingSettings s)=>s.OutputLimitEnabled?s.OutputGainLimit:1;
    public void BeginResumeFade(int sampleRate,int milliseconds){fadeFrames=remaining=sampleRate*milliseconds/1000;}
    public void OnEndpointChanged(int sampleRate,int milliseconds)=>BeginResumeFade(sampleRate,milliseconds);
    public double Next(){if(remaining==0)return 1;return 1-(double)--remaining/fadeFrames;}
}
