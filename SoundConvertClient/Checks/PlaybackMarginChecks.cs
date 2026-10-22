using System.Buffers.Binary;
namespace UsbAudioBridge.Checks;
static class PlaybackMarginChecks
{
    sealed class ManualClock:TimeProvider
    {
        long timestamp=1;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>timestamp;
        public void SetMilliseconds(int milliseconds)=>timestamp=1+milliseconds*TimeSpan.TicksPerMillisecond;
    }
    public static int Run()
    {
        int count=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        foreach(var scenario in new[]{(Capacity:40,Gap:12,Floor:21.5),(Capacity:80,Gap:30,Floor:56d),(Capacity:80,Gap:43,Floor:56d)})
        {
            var clock=new ManualClock();var buffer=new AudioBuffer(scenario.Capacity,clock);float[] output=new float[960];ulong index=0;int nextInput=0;
            Check(buffer.ConservativePrefillMs==scenario.Floor,"larger capacity reserves a visible initial margin");
            for(int ms=0;ms<=6000;ms++)
            {
                clock.SetMilliseconds(ms);
                if(ms<3850||ms>=3850+scenario.Gap)
                    while(nextInput<=ms)
                    {
                        byte[] pcm=new byte[192];for(int f=0;f<48;f++){BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f*4),(short)(2600*Math.Sin(2*Math.PI*440*(index+(ulong)f)/48000)));BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f*4+2),(short)(1600*Math.Sin(2*Math.PI*880*(index+(ulong)f)/48000)));}
                        buffer.Add(new AudioPacket(0,(uint)(index/48),index,48,pcm));index+=48;nextInput++;
                    }
                if(ms%10==0)buffer.Read(output,0,output.Length);
            }
            Check(buffer.OutputUnderruns==0,"conservative margin absorbs a later unobserved scheduling gap");
            Check(buffer.MissingFrames==0&&buffer.Resyncs==0&&buffer.Overflows==0,"gap recovery preserves timeline without reset");
            Check(buffer.TargetFrames>=scenario.Floor*48&&buffer.BufferedMs<=scenario.Capacity,"target respects floor and explicit capacity");
        }
        var low=new AudioBuffer(20,PlaybackTimingChecks.FrozenTime);low.Read(new float[960],0,960);Check(low.ConservativePrefillMs==0&&low.TargetFrames==576,"default 20ms remains the optional low-latency policy");
        foreach(int initialMs in new[]{12,19})
        {
            var startupClock=new ManualClock();var startup=new AudioBuffer(20,startupClock);ulong si=0;var render=new float[960];
            void Feed(){startup.Add(new AudioPacket(0,(uint)(si/48),si,48,new byte[192]));si+=48;}
            for(int ms=0;ms<initialMs;ms++)Feed();startup.Read(render,0,render.Length);
            Check(startup.DriftReferenceMs<=13&&startup.DriftReferenceMs>=12,"startup selects target plus at most one packet, independent of initial overshoot");
            long discarded=startup.PrefillTrimmedSourceFrames;
            for(int ms=1;ms<=60000;ms++){startupClock.SetMilliseconds(ms);Feed();if(ms%10==0)startup.Read(render,0,render.Length);}
            Check(Math.Abs(startup.DriftPpm)<1e-6&&startup.BufferedMs<=3.001,"overshoot does not remain as oscillator reference");
            Check(startup.PrefillTrimmedSourceFrames==discarded&&startup.OutputUnderruns==0&&startup.Overflows==0&&startup.Resyncs==0,"active playback never discards frames to chase startup target");
            Check(discarded==(initialMs==19?6*48:0),"startup discard is explicit and source-frame accurate");
        }
        // Five-ms delivery batches plus a small real clock mismatch must not
        // pin a restarted low-latency stream against its hard capacity.
        var batchedClock=new ManualClock();var batched=new AudioBuffer(20,batchedClock);ulong sample=0;int delivered=0;float[] block=new float[960];
        for(int ms=0;ms<=30000;ms++){
            batchedClock.SetMilliseconds(ms);int generated=(int)Math.Floor(ms*1.0001);
            if(ms%5==0)while(delivered<generated){batched.Add(new AudioPacket(0,(uint)delivered,sample,48,new byte[192]));sample+=48;delivered++;}
            if(ms%10==0)batched.Read(block,0,block.Length);
        }
        Check(batched.OutputUnderruns==0&&batched.Overflows==0&&batched.Resyncs==0,"20ms batches with +100ppm keep capacity headroom");
        Check(batched.PrefillTrimmedSourceFrames>0&&batched.RenderedFrames>29*48000,"only startup selects a fresh bounded prefill point");
        // A delivery pause longer than the playable reserve can cause one
        // real underrun. Recovery must not pin ANY capacity against its cap.
        foreach(var rates in new[]{(Source:48000,Output:96000),(Source:96000,Output:48000)})
        {
            var recoveryClock=new ManualClock();var recovery=new AudioBuffer(80,recoveryClock,rates.Output);
            var format=new AudioFormat(rates.Source,16);int sent=0;ulong si=0;float[] render=new float[rates.Output/100*2];
            long settledResyncs=0,settledUnder=0,settledRendered=0;
            for(int ms=0;ms<=15000;ms++)
            {
                recoveryClock.SetMilliseconds(ms);int generated=(int)Math.Floor(ms*1.0001);
                if(ms%5==0&&(ms<3850||ms>=3925))while(sent<generated)
                {recovery.Add(new AudioPacket(0,(uint)sent,si,(ushort)format.PacketFrames,new byte[format.PacketFrames*4],format,1));si+=(uint)format.PacketFrames;sent++;}
                if(ms%10==0)recovery.Read(render,0,render.Length);
                if(ms==4500){settledResyncs=recovery.Resyncs;settledUnder=recovery.OutputUnderruns;settledRendered=recovery.RenderedFrames;}
            }
            Check(settledUnder>0,"long delivery pause exercises genuine rebuffering");
            Check(recovery.Resyncs==settledResyncs&&recovery.OutputUnderruns==settledUnder,"80ms recovery stops recurring overflow/reset "+rates);
            Check(recovery.RenderedFrames-settledRendered>=10*rates.Output&&recovery.BufferedMs<=80,"recovered stream resumes bounded continuous playback "+rates);
            Check(recovery.MissingFrames==0&&recovery.PermanentGapSourceFrames==0,"recovery does not invent source loss "+rates);
        }
        return count+ConservativeStartupChecks.Run();
    }
}
