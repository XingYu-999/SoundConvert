using System.Diagnostics;
namespace UsbAudioBridge;

/// <summary>Single worker timing writer; reporting copies bounded data without locking the worker.</summary>
public sealed class AudioWorkerTiming
{
    public readonly record struct DelayEvent(double AtMs,double ServiceIntervalMs,double WakeLatenessMs,double SourceQueuedMs,long ExpiredFramesDelta,double GcPauseDeltaMs,int Gen0,int Gen1,int Gen2);
    readonly AudioTiming serviceIntervals=new(),wakeLateness=new();
    readonly DelayEvent[] delays=new DelayEvent[64];readonly int[] versions=new int[64];
    long count,delayCount,origin,lastAt,lastPause,firstPause,lastExpired,maximumInterval,maximumLateness;
    int lastGc0,lastGc1,lastGc2,firstGc0,firstGc1,firstGc2;

    // GC observations are supplied by the caller so deterministic checks can
    // distinguish a scheduling gap with GC from the same gap without GC.
    public void Record(long at,long due,double sourceQueuedMs,long expiredFrames,long pauseTicks,int gc0,int gc1,int gc2)
    {
        if(lastAt==0){origin=at;firstPause=pauseTicks;firstGc0=gc0;firstGc1=gc1;firstGc2=gc2;}
        long interval=lastAt==0?0:Math.Max(0,at-lastAt),late=due==0?0:Math.Max(0,at-due),expired=Math.Max(0,expiredFrames-lastExpired);
        if(lastAt!=0)serviceIntervals.Record(interval);if(due!=0)wakeLateness.Record(late);
        SetMax(ref maximumInterval,interval);SetMax(ref maximumLateness,late);
        if(interval>=Stopwatch.Frequency/50||late>=Stopwatch.Frequency/500||expired>0)
        {
            long index=Interlocked.Read(ref delayCount);int slot=(int)(index%delays.Length);
            // An odd version marks an in-progress struct copy. The reporting
            // reader discards a raced slot instead of blocking packet service.
            int version=Interlocked.Increment(ref versions[slot]);
            delays[slot]=new((at-origin)*1000d/Stopwatch.Frequency,interval*1000d/Stopwatch.Frequency,late*1000d/Stopwatch.Frequency,sourceQueuedMs,expired,lastAt==0?0:TimeSpan.FromTicks(Math.Max(0,pauseTicks-lastPause)).TotalMilliseconds,lastAt==0?0:gc0-lastGc0,lastAt==0?0:gc1-lastGc1,lastAt==0?0:gc2-lastGc2);
            Volatile.Write(ref versions[slot],version+1);Interlocked.Increment(ref delayCount);
        }
        lastAt=at;lastPause=pauseTicks;lastGc0=gc0;lastGc1=gc1;lastGc2=gc2;lastExpired=expiredFrames;Interlocked.Increment(ref count);
    }
    public object Snapshot()
    {
        long total=Interlocked.Read(ref delayCount);int n=(int)Math.Min(total,delays.Length);var copy=new List<DelayEvent>(n);
        for(long i=total-n;i<total;i++)
        {
            int slot=(int)(i%delays.Length),before=Volatile.Read(ref versions[slot]);if((before&1)!=0)continue;
            var item=delays[slot];if(before==Interlocked.CompareExchange(ref versions[slot],0,0))copy.Add(item);
        }
        return new{origin_timestamp=Interlocked.Read(ref origin),clock_frequency=Stopwatch.Frequency,service_attempts=Interlocked.Read(ref count),service_interval=serviceIntervals.Snapshot(),wake_lateness=wakeLateness.Snapshot(),maximum_service_interval_us=Interlocked.Read(ref maximumInterval)*1_000_000d/Stopwatch.Frequency,maximum_wake_lateness_us=Interlocked.Read(ref maximumLateness)*1_000_000d/Stopwatch.Frequency,delay_events=copy,delay_event_count=total,gc_total_pause_ms=TimeSpan.FromTicks(Math.Max(0,Interlocked.Read(ref lastPause)-firstPause)).TotalMilliseconds,gc_gen0=Math.Max(0,Volatile.Read(ref lastGc0)-firstGc0),gc_gen1=Math.Max(0,Volatile.Read(ref lastGc1)-firstGc1),gc_gen2=Math.Max(0,Volatile.Read(ref lastGc2)-firstGc2),window="latest 2048 service/wake samples; latest 64 gaps >=20ms, wakes >=2ms or source expiry; queued source is observed before expiry; GC deltas are correlation only"};
    }
    static void SetMax(ref long target,long value){long seen=Volatile.Read(ref target);while(value>seen){long previous=Interlocked.CompareExchange(ref target,value,seen);if(previous==seen)break;seen=previous;}}
}
