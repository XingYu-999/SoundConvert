namespace UsbAudioBridge;

/// <summary>Stereo balance/mono, three peaking EQ bands, memoryless peak limiter, application gain and resume fade. No lookahead.</summary>
public sealed class AudioPostProcessor
{
    readonly record struct Biquad(double B0,double B1,double B2,double A1,double A2);
    sealed record Target(AudioProcessingSettings Settings,Biquad Low,Biquad Mid,Biquad High);
    sealed class FilterState{double z1,z2;public double Run(double x,Biquad c){double y=c.B0*x+z1;z1=c.B1*x-c.A1*y+z2;z2=c.B2*x-c.A2*y;return y;}public void Clear(){z1=z2=0;}}
    Target target=new(new(),default,default,default);readonly FilterState[] states=Enumerable.Range(0,6).Select(_=>new FilterState()).ToArray();
    readonly OutputGainPolicy outputGain=new();int rate=48000;long generation;double left=1,right=1,mono,eqMix,limitMix,gain=1,ceiling=1;
    int resumeRequested,coefficientFrames;bool wasActive;Target? previous;
    Biquad low=new(1,0,0,0,0),mid=new(1,0,0,0,0),high=new(1,0,0,0,0);
    public int LatencyFrames=>0;
    public void SetTargetSettings(AudioProcessingSettings settings)
    {
        settings.Validate();int sr=Volatile.Read(ref rate);
        Volatile.Write(ref target,new(settings,Peak(120,settings.LowDb,sr),Peak(1000,settings.MidDb,sr),Peak(Math.Min(8000,sr*.4),settings.HighDb,sr)));
    }
    static Biquad Peak(double frequency,double db,int sr)
    {
        double a=Math.Pow(10,db/40),w=2*Math.PI*Math.Min(frequency,sr*.4)/sr,alpha=Math.Sin(w)/2,c=Math.Cos(w),a0=1+alpha/a;
        return new((1+alpha*a)/a0,-2*c/a0,(1-alpha*a)/a0,-2*c/a0,(1-alpha/a)/a0);
    }
    public void Reset(int sampleRate,long value)
    {
        if(sampleRate is <8000 or >384000)throw new ArgumentOutOfRangeException(nameof(sampleRate));
        rate=sampleRate;generation=value;foreach(var f in states)f.Clear();left=right=gain=ceiling=1;mono=eqMix=limitMix=0;low=mid=high=new(1,0,0,0,0);wasActive=false;previous=null;SetTargetSettings(target.Settings);BeginResumeFade();
    }
    public void BeginResumeFade()=>Interlocked.Exchange(ref resumeRequested,1);
    static double Approach(double value,double goal,double step)=>value<goal?Math.Min(goal,value+step):Math.Max(goal,value-step);
    static Biquad Blend(Biquad a,Biquad b,double w)=>new(a.B0+(b.B0-a.B0)*w,a.B1+(b.B1-a.B1)*w,a.B2+(b.B2-a.B2)*w,a.A1+(b.A1-a.A1)*w,a.A2+(b.A2-a.A2)*w);
    public void Process(Span<float> stereoSamples)
    {
        if((stereoSamples.Length&1)!=0)throw new ArgumentException("Stereo frames required");
        var t=Volatile.Read(ref target);var s=t.Settings;bool active=s.Enabled||s.OutputLimitEnabled||s.ResumeFadeMs>0;
        if(Interlocked.Exchange(ref resumeRequested,0)!=0)outputGain.BeginResumeFade(rate,s.ResumeFadeMs);
        if(!active&&!wasActive)return;
        double l=s.Enabled?1-Math.Max(0,s.Balance):1,r=s.Enabled?1+Math.Min(0,s.Balance):1,m=s.Enabled&&s.Mono?1:0,eq=s.Enabled&&s.EqEnabled?1:0,lim=s.Enabled&&s.LimiterEnabled?1:0;
        double targetGain=outputGain.ResolveTargetGain(s),targetCeiling=Math.Pow(10,s.CeilingDb/20),step=1d/Math.Max(1,rate/200);
        if(!ReferenceEquals(previous,t))coefficientFrames=Math.Max(1,rate/200);previous=t;
        for(int i=0;i<stereoSamples.Length;i+=2)
        {
            left=Approach(left,l,step);right=Approach(right,r,step);mono=Approach(mono,m,step);eqMix=Approach(eqMix,eq,step);limitMix=Approach(limitMix,lim,step);gain=Approach(gain,targetGain,step);ceiling=Approach(ceiling,targetCeiling,step);
            if(coefficientFrames>0){double w=1d/coefficientFrames--;low=Blend(low,t.Low,w);mid=Blend(mid,t.Mid,w);high=Blend(high,t.High,w);}
            double x=float.IsFinite(stereoSamples[i])?stereoSamples[i]:0,y=float.IsFinite(stereoSamples[i+1])?stereoSamples[i+1]:0,center=(x+y)*.5;
            x=(x+(center-x)*mono)*left;y=(y+(center-y)*mono)*right;
            double ex=states[2].Run(states[1].Run(states[0].Run(x,low),mid),high),ey=states[5].Run(states[4].Run(states[3].Run(y,low),mid),high);
            x+=(ex-x)*eqMix;y+=(ey-y)*eqMix;
            x+=(Math.Clamp(x,-ceiling,ceiling)-x)*limitMix;y+=(Math.Clamp(y,-ceiling,ceiling)-y)*limitMix;
            double g=(s.OutputLimitEnabled?Math.Min(gain,targetGain):gain)*outputGain.Next();stereoSamples[i]=(float)(x*g);stereoSamples[i+1]=(float)(y*g);
        }
        wasActive=active||left!=1||right!=1||mono!=0||eqMix!=0||limitMix!=0||gain!=1;
    }
}
