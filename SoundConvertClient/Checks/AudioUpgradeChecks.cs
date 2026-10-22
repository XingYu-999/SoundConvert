using UsbAudioBridge;
namespace UsbAudioBridge.Checks;

static class AudioUpgradeChecks
{
    sealed class Clock:TimeProvider{long now=1;public override long TimestampFrequency=>1000000;public override long GetTimestamp()=>now;public void At(double ms)=>now=1+(long)(ms*1000);}
    static AudioPacket Packet(AudioFormat f,ulong index,int frames)
    {var data=new byte[frames*f.BytesPerFrame];for(int n=0;n<frames;n++)for(int ch=0;ch<2;ch++){int at=n*f.BytesPerFrame+ch*f.BytesPerSample;int v=ch==0?1000:-1000;data[at]=(byte)v;data[at+1]=(byte)(v>>8);if(f.Bits==24)data[at+2]=(byte)(v>>16);}return new(0,(uint)(index/(uint)f.PacketFrames),index,(ushort)frames,data,f,1);}
    public static int Run()
    {
        int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
        foreach(var f in AudioFormat.Supported)
        {
            var clock=new Clock();var b=new AudioBuffer(100,clock,f.SampleRate);ulong index=0;
            for(int ms=0;ms<90;ms++){clock.At(ms);int n=(ms+1)*f.SampleRate/1000-ms*f.SampleRate/1000;b.Add(Packet(f,index,n));index+=(uint)n;}
            // Verify all physical source storage before playback, so startup
            // selection cannot hide the old 16384-frame wrap bug.
            var has=typeof(AudioBuffer).GetMethod("Has",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.CreateDelegate<Func<ulong,bool>>(b);
            Check(Enumerable.Range(0,(int)index).All(n=>has((ulong)n)),"all 90ms source indices exist before startup for "+f.Label);
            b.Read(new float[f.SampleRate/100*2],0,f.SampleRate/100*2);
            Check(b.ConcealedSourceFrames==0&&b.MissingFrames==0&&b.Overflows==0&&b.PrefillTrimmedSourceFrames>0,"90ms prefill is intact then explicitly aligned at startup for "+f.Label);
            for(int ms=90;ms<350;ms++)
            {clock.At(ms);int n=(ms+1)*f.SampleRate/1000-ms*f.SampleRate/1000;b.Add(Packet(f,index,n));index+=(uint)n;if(ms%10==9)b.Read(new float[f.SampleRate/100*2],0,f.SampleRate/100*2);}
            Check(b.MissingFrames==0&&b.OutputUnderruns==0,"ring wrap keeps FIR history "+f.Label);
        }
        foreach(var quality in Enum.GetValues<ResamplerQuality>())
        {
            var bank=SincFilter.ForRates(44100,48000,quality);
            Check(bank.Taps==(quality==ResamplerQuality.LowLatency?48:quality==ResamplerQuality.Balanced?96:128),"explicit quality profile");
            foreach(double phase in new[]{0d,.25,.999999})
            {
                float[] left=Enumerable.Repeat(.25f,bank.Taps).ToArray(),right=Enumerable.Repeat(-.25f,bank.Taps).ToArray();
                bank.Convolve(left,right,phase,out double l,out double r);Check(Math.Abs(l-.25)<1e-6&&Math.Abs(r+.25)<1e-6,"interpolated phase preserves DC/stereo");
            }
        }
        double Gain(int source,int output,double hz){var c=SincFilter.ForRates(source,output).Phase(.25);double re=0,im=0;for(int i=0;i<c.Length;i++){re+=c[i]*Math.Cos(2*Math.PI*hz*i/source);im+=c[i]*Math.Sin(2*Math.PI*hz*i/source);}return 20*Math.Log10(Math.Sqrt(re*re+im*im));}
        Check(Math.Abs(Gain(44100,48000,20000))<.2,"44.1k source 20k passband quality");
        Check(Gain(192000,48000,28000)<-70,"ratio-expanded filter rejects 28k before 192-to-48 conversion");
        Check(ReferenceEquals(SincFilter.ForRates(44100,96000),SincFilter.ForRates(48000,96000)),"identical filter banks share immutable storage");
        var timing=new ArrivalTiming();var time=new Clock();time.At(0);timing.Observe(0,48,time.GetTimestamp(),time,48000);time.At(61);timing.Observe(48,48,time.GetTimestamp(),time,48000);
        Check(timing.JitterMs>59,"arrival estimate does not discard delays above 50ms");
        timing.Observe(0,48,time.GetTimestamp(),time,48000);Check(timing.Reordered==1,"out-of-order packet cannot update clock progression");
        timing.Reset();time.At(0);timing.Observe(0,48,time.GetTimestamp(),time,48000);time.At(60);timing.Observe(2880,48,time.GetTimestamp(),time,48000);
        Check(timing.JitterMs<.001,"a source sample-index hole is not automatically a network-delay spike");
        foreach(double ppm in new[]{-300d,0,300})
        {
            var observer=new SourceClockObserver();for(int ms=0;ms<=20000;ms++){time.At(ms+.1*Math.Sin(ms*.01));observer.Observe((ulong)Math.Floor(ms*48*(1+ppm/1e6)),time.GetTimestamp(),time,48000);}
            Check(observer.Confident&&Math.Abs(observer.Ppm-ppm)<20,"shadow source-clock estimate "+ppm);
        }
        var b0=new AudioBuffer(80,new Clock());ulong sample=0;float[] outputFrames=new float[960];
        for(int batch=0;batch<100;batch++){for(int n=0;n<10;n++){b0.Add(Packet(AudioFormat.Pcm16,sample,48));sample+=48;}b0.Read(outputFrames,0,outputFrames.Length);}
        Check(Math.Abs(b0.DriftPpm)<1e-6,"zero-drift scheduling phase is not a permanent oscillator error");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{stage="audio_upgrade_checks",checks,scope="offline production capacity, phase interpolation, quality, arrival and shadow-clock checks"}));return checks;
    }
}
