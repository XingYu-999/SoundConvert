using System.Diagnostics;
using System.Text.Json;
namespace UsbAudioBridge.Checks;
static class CaptureCallbackTimingChecks
{
    public static int Run()
    {
        int count=0;void Check(bool ok,string reason){count++;if(!ok)throw new Exception(reason);}
        long ms=Stopwatch.Frequency/1000,at=Stopwatch.Frequency;var timing=new AudioCaptureTiming();
        timing.Begin(at,480,0,0,0,0);timing.Complete(at+2*ms);
        timing.Begin(at+10*ms,480,0,0,0,0);timing.Complete(at+12*ms);
        timing.Begin(at+35*ms,960,TimeSpan.TicksPerMillisecond*3,1,1,0);timing.Complete(at+38*ms);
        var report=JsonSerializer.SerializeToElement(timing.Snapshot());var gap=report.GetProperty("gap_events")[0];
        Check(report.GetProperty("origin_timestamp").GetInt64()==at&&report.GetProperty("clock_frequency").GetInt64()==Stopwatch.Frequency,"capture callback timings have a cross-process monotonic origin");
        Check(report.GetProperty("gap_event_count").GetInt64()==1&&gap.GetProperty("Timestamp").GetInt64()==at+35*ms&&gap.GetProperty("Frames").GetInt32()==960&&Math.Abs(gap.GetProperty("IntervalMs").GetDouble()-25)<.001,"25ms callback delay retains entry QPC and actual delivered frames");
        Check(Math.Abs(gap.GetProperty("PreviousProcessingUs").GetDouble()-2000)<.01&&gap.GetProperty("GcPauseDeltaMs").GetDouble()==3&&gap.GetProperty("Gen1").GetInt32()==1,"capture gap keeps preceding subscriber processing and matching GC evidence");
        Check(Math.Abs(report.GetProperty("maximum_interval_us").GetDouble()-25000)<.01&&Math.Abs(report.GetProperty("maximum_processing_us").GetDouble()-3000)<.01,"callback service interval and processing maxima remain distinct");
        for(int i=1;i<=80;i++){timing.Begin(at+(35+i*20)*ms,480,TimeSpan.TicksPerMillisecond*3,1,1,0);timing.Complete(at+(36+i*20)*ms);}
        report=JsonSerializer.SerializeToElement(timing.Snapshot());
        Check(report.GetProperty("gap_events").GetArrayLength()==64&&report.GetProperty("gap_event_count").GetInt64()==81,"callback gap evidence is bounded and includes the exact 20ms boundary");
        var empty=new AudioCaptureTiming();empty.Begin(at,480,0,0,0,0);empty.Complete(at+ms);
        empty.Begin(at+10*ms,0,0,0,0,0);empty.Complete(at+11*ms);empty.Begin(at+20*ms,0,0,0,0,0);empty.Complete(at+21*ms);
        empty.Begin(at+30*ms,480,0,0,0,0);empty.Complete(at+31*ms);var emptyReport=JsonSerializer.SerializeToElement(empty.Snapshot());
        Check(emptyReport.GetProperty("zero_frame_callbacks").GetInt64()==2&&emptyReport.GetProperty("last_nonempty_callback_timestamp").GetInt64()==at+30*ms&&Math.Abs(emptyReport.GetProperty("maximum_nonempty_gap_us").GetDouble()-30000)<.01,"regular empty callbacks do not hide a 30ms microphone supply gap");
        Check(emptyReport.GetProperty("gap_events").GetArrayLength()==2&&emptyReport.GetProperty("gap_events")[0].GetProperty("Frames").GetInt32()==0&&emptyReport.GetProperty("gap_events")[1].GetProperty("NonemptyGapMs").GetDouble()==30,"nonempty-gap evidence identifies both empty and resumed callbacks");
        var allocation=new AudioCaptureTiming();long cursor=at;
        void Record(int interval){cursor+=interval*ms;allocation.Begin(cursor,480,0,0,0,0);allocation.Complete(cursor+ms);}
        for(int i=0;i<1024;i++){Record(10);Record(25);}
        long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++){Record(10);Record(25);}
        Check(GC.GetAllocatedBytesForCurrentThread()==before,"normal and anomalous callback timing paths both allocate zero steady bytes");
        Console.WriteLine(JsonSerializer.Serialize(new{stage="capture_callback_timing",checks=count,scope="pure monotonic timing model; not WASAPI driver or board acceptance"}));return count;
    }
}
