using System.Diagnostics;
namespace UsbAudioBridge;

/// <summary>Bounded recent timings. Recording does not allocate or acquire a reporting lock.</summary>
public sealed class AudioTiming
{
    readonly long[] ticks=new long[2048];long count;
    public void Record(long elapsed){long index=Interlocked.Increment(ref count)-1;Volatile.Write(ref ticks[(int)(index%ticks.Length)],elapsed);}
    public object Snapshot()
    {
        int n=(int)Math.Min(Interlocked.Read(ref count),ticks.Length);if(n==0)return new{samples=0,p50_us=0d,p95_us=0d,p99_us=0d,p999_us=0d,max_us=0d};
        var copy=new long[n];for(int i=0;i<n;i++)copy[i]=Volatile.Read(ref ticks[i]);Array.Sort(copy);
        double Q(double q)=>copy[Math.Min(n-1,(int)(n*q))]*1_000_000d/Stopwatch.Frequency;
        return new{samples=n,p50_us=Q(.5),p95_us=Q(.95),p99_us=Q(.99),p999_us=Q(.999),max_us=Q(1)};
    }
}

/// <summary>Arrival jitter is measured against source time; decay depends on duration, not packet count.</summary>
public sealed class ArrivalTiming
{
    bool initialized;ulong lastIndex;long lastAt,burstFrames;
    public double JitterMs{get;private set;}public double BurstMs{get;private set;}
    public bool SchedulingGap{get;private set;}public long Reordered{get;private set;}
    public void Reset(){initialized=false;lastIndex=0;lastAt=burstFrames=0;JitterMs=BurstMs=0;SchedulingGap=false;Reordered=0;}
    public void Observe(ulong index,int frames,long now,TimeProvider clock,int rate)
    {
        if(initialized&&index<=lastIndex){Reordered++;return;}
        if(initialized)
        {
            double elapsed=Math.Max(0,clock.GetElapsedTime(lastAt,now).TotalMilliseconds);
            double source=(index-lastIndex)*1000d/rate,decay=Math.Exp(-elapsed/10000d);
            JitterMs=Math.Max(JitterMs*decay,Math.Max(0,elapsed-source));SchedulingGap|=elapsed>=2;
            burstFrames=elapsed>.25?frames:burstFrames+frames;
            BurstMs=Math.Max(BurstMs*decay,SchedulingGap?burstFrames*1000d/rate:0);
        }
        else burstFrames=frames;
        initialized=true;lastIndex=index;lastAt=now;
    }
}

/// <summary>Shadow-only affine source/monotonic clock fit; never changes the audio ratio.</summary>
public sealed class SourceClockObserver
{
    readonly double[] x=new double[32],y=new double[32];int count,next;long origin,lastAt;ulong firstIndex,lastIndex;double bucketX,bucketY;bool bucket;
    public double Ppm{get;private set;}public bool Confident{get;private set;}public long Rejected{get;private set;}
    public void Reset(){count=next=0;origin=lastAt=0;firstIndex=lastIndex=0;bucket=false;Ppm=0;Confident=false;Rejected=0;}
    public void Observe(ulong index,long at,TimeProvider clock,int rate)
    {
        if(origin==0){origin=lastAt=at;firstIndex=lastIndex=index;bucketX=bucketY=0;bucket=true;return;}
        if(index<=lastIndex){Rejected++;return;}lastIndex=index;
        double sx=(index-firstIndex)/(double)rate,sy=clock.GetElapsedTime(origin,at).TotalSeconds;
        if(sy<0){Rejected++;return;}
        // Least-delayed observation per second reduces bias from batched arrivals.
        if(!bucket||sy-sx<bucketY-bucketX){bucketX=sx;bucketY=sy;bucket=true;}
        if(clock.GetElapsedTime(lastAt,at).TotalSeconds<1)return;
        x[next]=bucketX;y[next]=bucketY;next=(next+1)%x.Length;count=Math.Min(count+1,x.Length);lastAt=at;bucket=false;
        if(count<8)return;
        double mx=0,my=0;for(int i=0;i<count;i++){mx+=x[i];my+=y[i];}mx/=count;my/=count;
        double covariance=0,variance=0;for(int i=0;i<count;i++){covariance+=(x[i]-mx)*(y[i]-my);variance+=(x[i]-mx)*(x[i]-mx);}
        if(variance<1e-9)return;double slope=covariance/variance,ppm=(1/slope-1)*1_000_000;
        if(!double.IsFinite(ppm)||Math.Abs(ppm)>5000){Rejected++;Confident=false;return;}
        Ppm=ppm;Confident=true;
    }
}
