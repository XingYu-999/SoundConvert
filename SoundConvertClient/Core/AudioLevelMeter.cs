using System.Diagnostics;
namespace UsbAudioBridge;

public readonly record struct AudioLevelSnapshot(float LeftPeak,float RightPeak,double LeftRms,double RightRms,long FullScaleSamples,int Frames,bool Valid,uint Generation,long Timestamp);

/// <summary>One producer under AudioBuffer's gate; bounded, allocation-free seqlock publication.</summary>
public sealed class AudioLevelMeter
{
    int version,frames,windowFrames=2400;uint generation;float leftPeak,rightPeak,fullScale=32767f/32768;
    double leftSquares,rightSquares;long fullScaleSamples,totalFullScaleSamples;AudioLevelSnapshot published;
    public void Reset(uint value,int sampleRate=48000,int bits=16)
    {
        generation=value;windowFrames=Math.Max(1,sampleRate/20);fullScale=(float)(1-1d/(1L<<(bits-1)));
        totalFullScaleSamples=0;Clear();Publish(default(AudioLevelSnapshot) with{Generation=value});
    }
    void Clear(){frames=0;leftPeak=rightPeak=0;leftSquares=rightSquares=0;fullScaleSamples=0;}
    public void ObserveInterleaved(ReadOnlySpan<float> samples,int channels)
    {
        if(channels is <1 or >2||samples.Length%channels!=0)throw new ArgumentException("Expected mono or stereo frames");
        for(int i=0;i<samples.Length;i+=channels)ObserveStereoFrame(samples[i],samples[i+channels-1]);
    }
    public void ObserveStereoFrame(float left,float right)
    {
        if(!float.IsFinite(left)||!float.IsFinite(right))return;
        leftPeak=Math.Max(leftPeak,Math.Abs(left));rightPeak=Math.Max(rightPeak,Math.Abs(right));
        leftSquares+=(double)left*left;rightSquares+=(double)right*right;
        if(Math.Abs(left)>=fullScale)fullScaleSamples++;if(Math.Abs(right)>=fullScale)fullScaleSamples++;
        if(++frames>=windowFrames)PublishWindow();
    }
    public void PublishWindow(){if(frames==0)return;totalFullScaleSamples+=fullScaleSamples;Publish(new(leftPeak,rightPeak,Math.Sqrt(leftSquares/frames),Math.Sqrt(rightSquares/frames),totalFullScaleSamples,frames,true,generation,Stopwatch.GetTimestamp()));Clear();}
    void Publish(AudioLevelSnapshot snapshot){Interlocked.Increment(ref version);published=snapshot;Interlocked.Increment(ref version);}
    public AudioLevelSnapshot ReadSnapshot()
    {
        for(int i=0;i<4;i++){int before=Volatile.Read(ref version);if((before&1)!=0)continue;var result=published;Thread.MemoryBarrier();if(before==Volatile.Read(ref version))return result;}
        return default;
    }
}
