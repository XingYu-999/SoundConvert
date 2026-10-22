using System.Buffers.Binary;
namespace UsbAudioBridge.Checks;
static class PlaybackTimingChecks
{
    public static TimeProvider FrozenTime{get;}=new FrozenClock();
    sealed class FrozenClock:TimeProvider{public override long GetTimestamp()=>1;}
    public static int Run()
    {
        int checks=0;
        void Require(bool condition,string name){if(!condition)throw new Exception(name);checks++;}
        AudioPacket Packet(ulong index){byte[] pcm=new byte[192];for(int f=0;f<48;f++){BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f*4),1000);BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f*4+2),-1000);}return new(0,(uint)(index/48),index,48,pcm);}
        var burst=new AudioBuffer(20,FrozenTime);ulong next=0;void Feed(AudioBuffer b,int packets){for(int p=0;p<packets;p++){b.Add(Packet(next));next+=48;}}
        Feed(burst,1);burst.Read(new float[2112],0,2112);float[] output=new float[960];long nonzero=0;
        for(int tick=0;tick<100;tick++){Feed(burst,10);burst.Read(output,0,output.Length);if(tick>10)nonzero+=output.Count(v=>v!=0);Require(burst.BufferedMs<=20,"burst buffer stays within configured limit");}
        Require(nonzero>80000,"1ms initial phase must not starve 10ms burst playback");
        Require(burst.Overflows<=1&&burst.Resyncs<=1,"startup overflow preserves a playable window");
        var recovery=new AudioBuffer(20,FrozenTime);next=0;Feed(recovery,12);recovery.Read(output,0,output.Length);recovery.Read(output,0,output.Length);
        Require(recovery.OutputUnderruns==1&&recovery.RebufferCount==1,"low frontier starts rebuffering");
        Feed(recovery,10);recovery.Read(output,0,output.Length);
        Require(output.Any(v=>v!=0),"late input recovers after one prefill");
        Require(recovery.Late==0&&recovery.MissingFrames==0,"underrun must not advance cursor beyond received frontier");
        return checks;
    }
}

