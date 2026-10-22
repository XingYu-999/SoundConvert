namespace UsbAudioBridge.Checks;
public static class AudioExtensionChecks
{
    public static int Run()
    {
        int count=0;void Check(bool ok,string name){count++;if(!ok)throw new Exception("Audio extension: "+name);}
        var meter=new AudioLevelMeter();meter.Reset(7);for(int i=0;i<2400;i++)meter.ObserveStereoFrame((float)Math.Sin(2*Math.PI*i/48),.25f);
        var s=meter.ReadSnapshot();Check(s.Valid&&s.Frames==2400&&s.Generation==7,"window/generation");Check(Math.Abs(s.LeftRms-Math.Sqrt(.5))<1e-6&&s.RightRms==.25,"sine RMS/channel isolation");Check(s.FullScaleSamples>0,"fullscale");meter.Reset(8);Check(!meter.ReadSnapshot().Valid,"reset clears old stream");
        var p=new AudioPostProcessor();p.Reset(48000,1);float[] original=Enumerable.Range(0,2048).Select(i=>(float)Math.Sin(i)*.8f).ToArray(),samples=original.ToArray();p.Process(samples);Check(samples.SequenceEqual(original)&&p.LatencyFrames==0,"bit-exact default bypass");
        void Settle(AudioProcessingSettings settings){p.SetTargetSettings(settings);p.Process(new float[1024]);}
        Settle(new(Enabled:true,Balance:1));samples=Enumerable.Repeat(.8f,512).ToArray();p.Process(samples);Check(samples.Where((_,i)=>i%2==0).All(x=>x==0)&&samples.Where((_,i)=>i%2==1).All(x=>x==.8f),"balance attenuates only left");
        Settle(new(Enabled:true,Mono:true));samples=Enumerable.Range(0,512).Select(i=>i%2==0?.8f:-.8f).ToArray();p.Process(samples);Check(samples.All(x=>Math.Abs(x)<1e-6),"antiphase mono cancellation");
        samples=Enumerable.Repeat(.5f,512).ToArray();p.Process(samples);Check(samples.All(x=>x==.5f),"in-phase mono no boost");
        Settle(new(OutputLimitEnabled:true,OutputGainLimit:.25));samples=Enumerable.Repeat(.8f,512).ToArray();p.Process(samples);Check(samples.All(x=>Math.Abs(x-.2)<1e-6),"application gain cap");
        Settle(new(Enabled:true,LimiterEnabled:true,CeilingDb:-6));samples=Enumerable.Repeat(1f,512).ToArray();p.Process(samples);Check(samples.All(x=>Math.Abs(x-Math.Pow(10,-6d/20))<1e-6),"limiter ceiling");
        Settle(new(Enabled:true,EqEnabled:true,LowDb:12,MidDb:-12,HighDb:12,LimiterEnabled:true));samples=original.ToArray();p.Process(samples);Check(samples.All(float.IsFinite)&&samples.All(x=>Math.Abs(x)<=1),"EQ finite with limiter");
        Settle(new(ResumeFadeMs:10));p.BeginResumeFade();samples=Enumerable.Repeat(.5f,1200).ToArray();p.Process(samples);Check(samples[0]>0&&samples[0]<.01&&samples[958]>.49,"resume fade curve");
        try{p.SetTargetSettings(new(Balance:double.NaN));throw new Exception("NaN accepted");}catch(ArgumentException){count++;}
        p.SetTargetSettings(new(OutputLimitEnabled:true,OutputGainLimit:0));samples=Enumerable.Repeat(.5f,128).ToArray();p.Process(samples);Check(samples.All(x=>x==0),"zero gain cap applies immediately");
        p.SetTargetSettings(new());p.Process(new float[1024]);samples=original.ToArray();p.Process(samples);Check(samples.SequenceEqual(original),"bypass restored");
        return count;
    }
}
