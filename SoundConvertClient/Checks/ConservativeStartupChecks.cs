using System.Text.Json;
namespace UsbAudioBridge.Checks;

/// <summary>First scheduling gap at every callback phase, checked against an independent integer frontier budget.</summary>
static class ConservativeStartupChecks
{
    sealed class ManualClock:TimeProvider
    {
        long timestamp=1;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>timestamp;
        public void SetMilliseconds(int ms)=>timestamp=1+ms*TimeSpan.TicksPerMillisecond;
    }

    // The model contains no production prefill or resampler logic: delivery
    // adds 48 frames/ms, a read consumes 480, and 48 future FIR frames are
    // needed. It predicts only the first insufficient-frontier callback.
    static bool PredictFirstUnderrun(double initialMs,int gap,int phase,bool readFirst)
    {
        double frames=initialMs*48-480;int nextPacket=1;
        for(int ms=1;ms<=180;ms++)
        {
            if(readFirst&&ms%10==0){if(frames<528)return true;frames-=480;}
            if(ms<40+phase||ms>=40+phase+gap)while(nextPacket<=ms){frames+=48;nextPacket++;}
            if(!readFirst&&ms%10==0){if(frames<528)return true;frames-=480;}
        }
        return false;
    }

    public static int Run()
    {
        int count=0,scenarios=0,limited25=0,avoided25=0;
        void Check(bool ok,string message){count++;if(!ok)throw new Exception(message);}
        byte[] pcm=new byte[192];
        foreach(int initialMs in new[]{25,30})foreach(int gap in new[]{16,17})foreach(int phase in Enumerable.Range(0,10))foreach(bool readFirst in new[]{false,true})
        {
            var clock=new ManualClock();var buffer=new AudioBuffer(40,clock);float[] render=new float[960];ulong index=0;int nextPacket=1;
            // Match the physical endpoint's initial whole 22 ms refill, then
            // ordinary 10 ms reads. The first empty callback is not playback.
            buffer.Read(new float[2112],0,2112);
            void Feed(){buffer.Add(new AudioPacket(0,(uint)(index/48),index,48,pcm));index+=48;}
            for(int packet=0;packet<initialMs;packet++)Feed();buffer.Read(render,0,render.Length);
            Check(buffer.PrefillTrimmedSourceFrames==0&&Math.Abs(buffer.DriftReferenceMs-initialMs)<1e-9,"40ms conservative startup retains arrived PCM and binds its real oscillator reference");
            bool predicted=PredictFirstUnderrun(initialMs,gap,phase,readFirst);
            for(int ms=1;ms<=180;ms++)
            {
                clock.SetMilliseconds(ms);
                if(readFirst&&ms%10==0)buffer.Read(render,0,render.Length);
                if(ms<40+phase||ms>=40+phase+gap)while(nextPacket<=ms){Feed();nextPacket++;}
                if(!readFirst&&ms%10==0)buffer.Read(render,0,render.Length);
            }
            scenarios++;
            Check((buffer.OutputUnderruns>0)==predicted,"first 16/17ms gap agrees with independent frontier budget: "+new{initialMs,gap,phase,readFirst,predicted,actual=buffer.OutputUnderruns});
            Check(buffer.Overflows==0&&buffer.Resyncs==0&&buffer.BufferedMs<=40&&buffer.MissingFrames==0&&buffer.PermanentGapSourceFrames==0,"phase sweep preserves hard capacity and the contiguous source timeline");
            Check(buffer.OutputUnderruns<=1&&buffer.RenderedFrames>=120*48,"an insufficient initial reserve reports one real underrun and then recovers");
            if(initialMs==25)
            {
                if(predicted)limited25++;
                if(!predicted&&PredictFirstUnderrun(22.5,gap,phase,readFirst))avoided25++;
            }
        }
        Check(limited25>0&&avoided25>0,"25ms reserve improves the old 22.5ms startup while keeping its unprotected phases explicit");

        // A large capacity is not permission to keep an arbitrary startup
        // backlog. Its extra arrived reserve is bounded to one actual callback.
        var fresh=new AudioBuffer(100,new ManualClock());
        for(uint packet=0;packet<99;packet++)fresh.Add(new AudioPacket(0,packet,packet*48,48,pcm));
        fresh.Read(new float[960],0,960);
        Check(Math.Abs(fresh.TargetFrames/48d-76)<.001&&Math.Abs(fresh.DriftReferenceMs-86)<.001&&fresh.PrefillTrimmedSourceFrames==13*48&&fresh.Overflows==0&&fresh.Resyncs==0&&fresh.OutputUnderruns==0,"100ms capacity trims a coarse 99ms startup backlog to its 76ms target plus one 10ms callback");

        // Unobserved pauses can exceed the selected reserve. Keep that a real
        // underrun, without inventing source frames or relaxing the cap.
        var limitedClock=new ManualClock();var limited=new AudioBuffer(40,limitedClock);var output=new float[960];
        limited.Read(new float[2112],0,2112);for(uint packet=0;packet<30;packet++)limited.Add(new AudioPacket(0,packet,packet*48,48,pcm));limited.Read(output,0,output.Length);
        for(int ms=10;ms<=80;ms+=10){limitedClock.SetMilliseconds(ms);limited.Read(output,0,output.Length);}
        Check(limited.OutputUnderruns==1&&limited.RebufferCount==1&&limited.BufferedMs<=40&&limited.PermanentGapSourceFrames==0,"a pause beyond the retained reserve remains a reported scheduling failure");
        Console.WriteLine(JsonSerializer.Serialize(new{stage="conservative_startup_phase_model",scenarios,checks=count,initial_25ms_unprotected_phases=limited25,initial_25ms_improved_phases=avoided25,initial_30ms_all_phases_continuous=true,scope="offline 48k/10ms callback/1ms FIR phase and first-gap model; no arbitrary pause guarantee or physical acceptance"}));
        return count;
    }
}
