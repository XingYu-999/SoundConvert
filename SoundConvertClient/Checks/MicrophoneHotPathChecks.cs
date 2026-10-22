using System.Diagnostics;
using System.Text.Json;
namespace UsbAudioBridge.Checks;

static class MicrophoneHotPathChecks
{
    public static async Task<int> RunAsync()
    {
        int count=0;void Check(bool value,string reason){count++;if(!value)throw new Exception(reason);}
        count+=CaptureCallbackTimingChecks.Run();
        var timing=new AudioWorkerTiming();long second=Stopwatch.Frequency;
        timing.Record(second,0,10,0,0,0,0,0);
        timing.Record(second+second/1000,second+second/1000,9,0,0,0,0,0);
        timing.Record(second+second/20,second+second/500,89,2352,TimeSpan.TicksPerMillisecond*3,1,0,0);
        var observed=JsonSerializer.SerializeToElement(timing.Snapshot());var delayEvent=observed.GetProperty("delay_events")[0];
        Check(observed.GetProperty("service_attempts").GetInt64()==3&&observed.GetProperty("service_interval").GetProperty("samples").GetInt32()==2&&observed.GetProperty("origin_timestamp").GetInt64()==second&&observed.GetProperty("clock_frequency").GetInt64()==Stopwatch.Frequency,"worker service timing records actual intervals and a correlatable monotonic origin rather than only DSP work");
        Check(delayEvent.GetProperty("ExpiredFramesDelta").GetInt64()==2352&&delayEvent.GetProperty("SourceQueuedMs").GetDouble()==89,"worker delay evidence preserves pre-expiry backlog and explicit lost source time");
        Check(Math.Abs(delayEvent.GetProperty("WakeLatenessMs").GetDouble()-48)<.01&&delayEvent.GetProperty("GcPauseDeltaMs").GetDouble()==3&&delayEvent.GetProperty("Gen0").GetInt32()==1,"worker scheduled lateness keeps the matching GC evidence");
        for(int i=1;i<=80;i++)timing.Record(second+second/20+i*second/40,0,1,2352,TimeSpan.TicksPerMillisecond*3,1,0,0);
        observed=JsonSerializer.SerializeToElement(timing.Snapshot());
        Check(observed.GetProperty("delay_events").GetArrayLength()==64&&observed.GetProperty("delay_event_count").GetInt64()==81,"worker delay evidence is bounded while retaining total event count");
        var allocationTiming=new AudioWorkerTiming();for(int i=1;i<=1024;i++)allocationTiming.Record(i*second/1000,0,2,0,0,0,0,0);
        long timingAllocated=GC.GetAllocatedBytesForCurrentThread();for(int i=1025;i<=11024;i++)allocationTiming.Record(i*second/1000,0,2,0,0,0,0,0);
        Check(GC.GetAllocatedBytesForCurrentThread()==timingAllocated,"worker interval instrumentation allocates zero steady bytes");
        foreach(var format in CaptureFormat.Supported)
        {
            var d=new CaptureDescriptor(71,4,99,49163,format,true);
            byte[] pcm=new byte[format.PacketFrames*format.BytesPerFrame],destination=new byte[336];
            for(int i=0;i<pcm.Length;i++)pcm[i]=(byte)(i*71);
            Array.Fill(destination,(byte)0xee);
            byte[] expected=CaptureCodec.Encode(d,uint.MaxValue,ulong.MaxValue-(uint)format.PacketFrames,pcm,format.PacketFrames);
            Check(CaptureCodec.TryEncode(d,uint.MaxValue,ulong.MaxValue-(uint)format.PacketFrames,pcm,format.PacketFrames,destination,out int written)&&destination.AsSpan(0,written).SequenceEqual(expected),"span microphone datagram matches complete 16/24-bit wire format");
            Check(!CaptureCodec.TryEncode(d,0,0,pcm,format.PacketFrames,destination.AsSpan(0,written-1),out int failed)&&failed==0,"short destination rejected atomically");
            Check(!CaptureCodec.TryEncode(d,0,ulong.MaxValue,pcm,format.PacketFrames,destination,out failed)&&failed==0,"span encoder rejects sample time overflow");
            Check(CaptureCodec.TryEncode(d,0,0,[],0,destination,out written,true)&&written==48&&destination.AsSpan(16,24).IndexOfAnyExcept((byte)0)<0,"dirty reused packet clears all probe/reserved fields");
            for(int i=0;i<1024;i++)CaptureCodec.TryEncode(d,1,0,pcm,format.PacketFrames,destination,out _);
            long before=GC.GetAllocatedBytesForCurrentThread();bool all=true;
            for(int i=0;i<10000;i++)all&=CaptureCodec.TryEncode(d,(uint)i,0,pcm,format.PacketFrames,destination,out _);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Check(all&&allocated==0,"span microphone encoding allocates zero steady bytes");
        }
        await using var board=new CaptureChecks.CaptureDevice(new CaptureFormat(48000,16)){PrepareDelayMs=80};
        await using var control=new ControlSession();await control.ConnectAsync("127.0.0.1",board.Port);
        await using var sender=new MicrophoneSender(control,48000,16,"127.0.0.1",0);await sender.StartAsync();
        using var produceStop=new CancellationTokenSource();float[] source=Enumerable.Repeat(.25f,480).ToArray();
        var producer=Task.Run(async()=>{try{while(!produceStop.IsCancellationRequested){sender.Push(source);await Task.Delay(10,produceStop.Token);}}catch(OperationCanceledException){}});
        try
        {
            await Wait(()=>board.Packets.Count>=150);long allocatedBefore=Snapshot(sender).GetProperty("worker_allocated_bytes").GetInt64();
            int before=board.Packets.Count;await Wait(()=>board.Packets.Count>=before+250);
            var stable=Snapshot(sender);long allocatedAfter=stable.GetProperty("worker_allocated_bytes").GetInt64();
            Check(allocatedAfter==allocatedBefore,"microphone worker packet loop allocates zero steady bytes across UDP sends");
            Check(sender.Fault is null&&stable.GetProperty("worker_alive").GetBoolean(),"independent packet worker remains healthy during concurrent Push/statistics");
            Check(stable.GetProperty("worker_priority").GetString()=="Normal"&&stable.TryGetProperty("worker_mmcss_registered",out _)&&stable.TryGetProperty("worker_mmcss_error",out _),"microphone scheduling policy and optional MMCSS registration are visible");
            Check(stable.GetProperty("worker_service").GetProperty("service_attempts").GetInt64()>0&&stable.GetProperty("worker_service").GetProperty("wake_lateness").GetProperty("samples").GetInt32()>0,"packet worker reports actual service cadence and paced wake lateness");
            // A deliberately excessive callback must discard old source time,
            // leave a wire-visible hole and recover without an unbounded burst.
            sender.Push(new float[96000]);await Wait(()=>Snapshot(sender).GetProperty("source_expired_frames").GetInt64()>0);
            await Wait(()=>board.Packets.Any(p=>Wire.Read64(p,20)>48000));var expired=Snapshot(sender);
            Check(expired.GetProperty("source_overrun_frames").GetInt64()>0&&expired.GetProperty("source_expired_packets").GetInt64()>0&&sender.Fault is null,"oversized source expires bounded backlog with explicit counters instead of stopping sender");
            var packets=board.Packets.ToArray();bool gap=false;
            for(int i=1;i<packets.Length;i++)if(Wire.Read64(packets[i],20)>Wire.Read64(packets[i-1],20)+Wire.Read16(packets[i-1],32))gap=true;
            Check(gap,"expired microphone samples preserve sampleIndex holes for board gap handling");
            board.Generation=2;await Wait(()=>board.Packets.Any(p=>Wire.Read32(p,12)==2));
            packets=board.Packets.ToArray();int firstNew=Array.FindIndex(packets,p=>Wire.Read32(p,12)==2);
            Check(board.Prepares==2&&packets.Skip(firstNew).All(p=>Wire.Read32(p,12)==2),"renegotiation lease prevents old epoch sends after new epoch starts");
            Check(Wire.Read64(packets[firstNew],20)==0&&Wire.Read32(packets[firstNew],16)==0,"new epoch restarts its independent sample/sequence timeline");
            Console.WriteLine(JsonSerializer.Serialize(new{stage="microphone_hot_path",checks=count,worker_allocated_delta=allocatedAfter-allocatedBefore,stable,expired,scope="offline loopback only; not host microphone acceptance"}));
        }
        finally{produceStop.Cancel();await producer;}
        return count;
    }
    static JsonElement Snapshot(MicrophoneSender sender)=>JsonSerializer.SerializeToElement(sender.Statistics());
    static async Task Wait(Func<bool> condition)
    {
        long began=Stopwatch.GetTimestamp();while(!condition()){if(Stopwatch.GetElapsedTime(began)>TimeSpan.FromSeconds(5))throw new TimeoutException("Microphone hot-path check stalled");await Task.Delay(5);}
    }
}
