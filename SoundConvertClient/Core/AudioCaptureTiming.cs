using System.Diagnostics;
namespace UsbAudioBridge;

/// <summary>One capture callback writer; bounded gap evidence without callback allocations or reporting locks.</summary>
public sealed class AudioCaptureTiming
{
    public readonly record struct GapEvent(long Timestamp,double AtMs,double IntervalMs,double PreviousProcessingUs,int Frames,double NonemptyGapMs,double GcPauseDeltaMs,int Gen0,int Gen1,int Gen2);
    readonly GapEvent[] gaps=new GapEvent[64];readonly int[] versions=new int[64];readonly AudioTiming intervals=new();
    long origin,lastEntry,lastPause,previousProcessing,maximumInterval,maximumProcessing,count,gapCount,lastNonempty,maximumNonemptyGap,zeroFrameCallbacks;
    int lastGc0,lastGc1,lastGc2;
    public void Begin(long at,int frames,long pauseTicks,int gc0,int gc1,int gc2)
    {
        bool first=lastEntry==0;if(first)origin=at;
        long elapsed=first?0:Math.Max(0,at-lastEntry);if(!first)intervals.Record(elapsed);SetMax(ref maximumInterval,elapsed);
        long nonemptyGap=Math.Max(0,at-(lastNonempty==0?origin:lastNonempty));SetMax(ref maximumNonemptyGap,nonemptyGap);
        if(frames==0)Interlocked.Increment(ref zeroFrameCallbacks);else if(frames>0)Interlocked.Exchange(ref lastNonempty,at);
        if(!first&&(elapsed>=Stopwatch.Frequency/50||nonemptyGap>=Stopwatch.Frequency/50))
        {
            long index=Interlocked.Read(ref gapCount);int slot=(int)(index%gaps.Length),version=Interlocked.Increment(ref versions[slot]);
            gaps[slot]=new(at,(at-origin)*1000d/Stopwatch.Frequency,elapsed*1000d/Stopwatch.Frequency,Interlocked.Read(ref previousProcessing)*1_000_000d/Stopwatch.Frequency,frames,nonemptyGap*1000d/Stopwatch.Frequency,TimeSpan.FromTicks(Math.Max(0,pauseTicks-lastPause)).TotalMilliseconds,gc0-lastGc0,gc1-lastGc1,gc2-lastGc2);
            Volatile.Write(ref versions[slot],version+1);Interlocked.Increment(ref gapCount);
        }
        lastEntry=at;lastPause=pauseTicks;lastGc0=gc0;lastGc1=gc1;lastGc2=gc2;Interlocked.Increment(ref count);
    }
    public void Complete(long at){long elapsed=Math.Max(0,at-lastEntry);Interlocked.Exchange(ref previousProcessing,elapsed);SetMax(ref maximumProcessing,elapsed);}
    public object Snapshot()
    {
        long total=Interlocked.Read(ref gapCount);int n=(int)Math.Min(total,gaps.Length);var copy=new List<GapEvent>(n);
        for(long index=total-n;index<total;index++)
        {
            int slot=(int)(index%gaps.Length),version=Volatile.Read(ref versions[slot]);if((version&1)!=0)continue;
            var item=gaps[slot];if(version==Interlocked.CompareExchange(ref versions[slot],0,0))copy.Add(item);
        }
        return new{origin_timestamp=Interlocked.Read(ref origin),clock_frequency=Stopwatch.Frequency,callbacks=Interlocked.Read(ref count),zero_frame_callbacks=Interlocked.Read(ref zeroFrameCallbacks),last_nonempty_callback_timestamp=Interlocked.Read(ref lastNonempty),maximum_nonempty_gap_us=Interlocked.Read(ref maximumNonemptyGap)*1_000_000d/Stopwatch.Frequency,callback_interval=intervals.Snapshot(),previous_processing_us=Interlocked.Read(ref previousProcessing)*1_000_000d/Stopwatch.Frequency,maximum_interval_us=Interlocked.Read(ref maximumInterval)*1_000_000d/Stopwatch.Frequency,maximum_processing_us=Interlocked.Read(ref maximumProcessing)*1_000_000d/Stopwatch.Frequency,gap_event_count=total,gap_events=copy,scope="application callback entry QPC and preceding callback processing including synchronous subscribers; latest 64 callback intervals or nonempty ages >=20ms; empty callbacks do not reset the nonempty clock; excludes NAudio before callback; GC overlap is correlation only"};
    }
    static void SetMax(ref long target,long value){long seen=Volatile.Read(ref target);while(value>seen){long previous=Interlocked.CompareExchange(ref target,value,seen);if(previous==seen)break;seen=previous;}}
}
