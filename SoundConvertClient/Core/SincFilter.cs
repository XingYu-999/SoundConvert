using System.Collections.Concurrent;
using System.Numerics;

namespace UsbAudioBridge;

public enum ResamplerQuality { LowLatency, Balanced, High }

/// <summary>Immutable, prewarmed ratio-aware sinc banks. Ratios consume source frames per output frame.</summary>
public static class SincFilter
{
    public const int Taps=96,Phases=1024,History=Taps/2-1,LookAhead=Taps/2;
    static readonly ConcurrentDictionary<(int Taps,long Cutoff),Lazy<Bank>> Banks=new();
    public static Bank ForRates(int sourceRate,int outputRate,ResamplerQuality quality=ResamplerQuality.Balanced)
    {
        if(sourceRate is <8000 or >192000||outputRate is <8000 or >384000||!Enum.IsDefined(quality))throw new ArgumentOutOfRangeException(nameof(sourceRate));
        int nominal=quality switch{ResamplerQuality.LowLatency=>48,ResamplerQuality.High=>128,_=>Taps};
        int taps=(int)Math.Ceiling(nominal*Math.Max(1d,sourceRate/(double)outputRate));taps+=(taps&1);
        double cutoff=(quality switch{ResamplerQuality.LowLatency=>.94,ResamplerQuality.High=>.97,_=>.96})*Math.Min(1,outputRate/(double)sourceRate);
        return Banks.GetOrAdd((taps,BitConverter.DoubleToInt64Bits(cutoff)),key=>new Lazy<Bank>(()=>new Bank(key.Taps,BitConverter.Int64BitsToDouble(key.Cutoff)))).Value;
    }
    public static void Prewarm(int outputRate,ResamplerQuality quality=ResamplerQuality.Balanced)
    {foreach(int rate in AudioFormat.Supported.Select(f=>f.SampleRate).Distinct())_ = ForRates(rate,outputRate,quality);}
    public static ReadOnlySpan<float> Phase(double fraction)=>ForRates(48000,48000).Phase(fraction);
    public sealed class Bank
    {
        readonly float[] values;
        public int Taps{get;}public int History=>Taps/2-1;public int LookAhead=>Taps/2;
        internal Bank(int taps,double cutoff)
        {
            Taps=taps;values=new float[(Phases+1)*taps];
            // Phase 1 uses the same source origin; the final interpolation must not wrap to phase zero.
            for(int p=0;p<=Phases;p++)
            {
                double fraction=p/(double)Phases,sum=0;
                for(int t=0;t<taps;t++)
                {
                    double x=t-History-fraction;
                    double sinc=Math.Abs(x)<1e-12?cutoff:Math.Sin(Math.PI*cutoff*x)/(Math.PI*x);
                    double window=.42-.5*Math.Cos(2*Math.PI*t/(taps-1))+.08*Math.Cos(4*Math.PI*t/(taps-1));
                    float v=(float)(sinc*window);values[p*taps+t]=v;sum+=v;
                }
                for(int t=0;t<taps;t++)values[p*taps+t]/=(float)sum;
            }
        }
        public ReadOnlySpan<float> Phase(double fraction)=>values.AsSpan(Math.Clamp((int)(fraction*Phases),0,Phases-1)*Taps,Taps);
        public void Convolve(ReadOnlySpan<float> left,ReadOnlySpan<float> right,double fraction,out double l,out double r)
        {
            double phase=Math.Clamp(fraction,0,1)*Phases;int p=Math.Min(Phases-1,(int)phase);float mix=(float)(phase-p);
            var a=values.AsSpan(p*Taps,Taps);var b=values.AsSpan((p+1)*Taps,Taps);
            int at=0;Vector<float> sumL=Vector<float>.Zero,sumR=Vector<float>.Zero,weight=new(mix);
            if(Vector.IsHardwareAccelerated)for(;at<=Taps-Vector<float>.Count;at+=Vector<float>.Count)
            {
                var ca=new Vector<float>(a.Slice(at));var cb=new Vector<float>(b.Slice(at));var coefficient=ca+(cb-ca)*weight;
                sumL+=new Vector<float>(left.Slice(at))*coefficient;sumR+=new Vector<float>(right.Slice(at))*coefficient;
            }
            l=Vector.Sum(sumL);r=Vector.Sum(sumR);
            for(;at<Taps;at++){double coefficient=a[at]+(b[at]-a[at])*mix;l+=left[at]*coefficient;r+=right[at]*coefficient;}
        }
    }
}
