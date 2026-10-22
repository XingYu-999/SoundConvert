using System.Diagnostics;
using System.Text.Json;
using UsbAudioBridge;

namespace SoundConvertClient;

/// <summary>Engine-owned, bounded observer. No COM, fit or JSON work runs under the audio buffer lock.</summary>
sealed class ClockObservationSession : IAsyncDisposable
{
    readonly AudioReceiver receiver;
    readonly WindowsAudio audio;
    readonly ClockObservationMode mode;
    readonly ClockObservationStage stage;
    readonly CancellationTokenSource stop=new();
    readonly Task worker;
    readonly Queue<ClockExchangeRecord> exchanges=new();
    readonly Queue<RawOutputClockSample> outputSamples=new();
    readonly OutputClockTracker output=new();
    BoardClockMapper? board;
    SourceSampleClockTracker? source;
    ClockSessionIdentity identity;
    long outputSequence,boardHealthRevision,observedBoardHealthRevision,modelResets;
    ulong? previousBoardFaults,previousPhaseWaits;
    long previousAudioFaults;
    long audioUnhealthyUntil;
    long boardHealthAt,lastExchangeAcceptedAt,lastOutputAcceptedAt;
    volatile bool boardHealthComplete;
    ulong outputGeneration;
    string summary="Acquiring · 等待当前会话时间交换与真实输出时钟";
    string diagnostics="{}";
    public string Summary=>Volatile.Read(ref summary);
    public string Diagnostics=>Volatile.Read(ref diagnostics);
    public ClockObservationSession(AudioReceiver receiver,WindowsAudio audio,ClockObservationMode mode,ClockObservationStage stage=ClockObservationStage.Full)
    {
        this.receiver=receiver;this.audio=audio;this.mode=mode;this.stage=stage;
        audio.Buffer.SetClockAssistInput(mode,false,0,"acquiring");worker=Task.Run(Run);
    }
    public void ReportBoardHealth(JsonElement stats)
    {
        ulong faults=0;
        foreach(string name in new[]{"usb_expired_frames","usb_queue_drops","usb_underruns","usb_resyncs","audio_pending_expired","audio_send_timeouts"})
        {if(!stats.TryGetProperty(name,out var counter)||!counter.TryGetUInt64(out ulong n)){boardHealthComplete=false;return;}faults+=n;}
        if(!stats.TryGetProperty("usb_fifo_phase_waits",out var phase)||!phase.TryGetUInt64(out ulong waits)){boardHealthComplete=false;return;}
        if(previousBoardFaults is {} before&&(faults!=before||previousPhaseWaits is {} old&&waits>=old&&waits-old>30))Interlocked.Increment(ref boardHealthRevision);
        previousBoardFaults=faults;previousPhaseWaits=waits;
        Interlocked.Exchange(ref boardHealthAt,Stopwatch.GetTimestamp());boardHealthComplete=true;
    }
    async Task Run()
    {
        long nextPublish=0;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                ulong now=(ulong)Stopwatch.GetTimestamp();
                if(outputGeneration!=audio.OutputGeneration)
                {outputGeneration=audio.OutputGeneration;output.Reset(outputGeneration);lastOutputAcceptedAt=0;Deny("output_generation_changed");}
                var current=receiver.ClockIdentity;
                if(current is {} value&&value!=identity)
                {
                    Deny("source_session_identity_changed");lastExchangeAcceptedAt=0;
                    identity=value;board=stage==ClockObservationStage.SidebandOnly?null:new(identity);source=stage==ClockObservationStage.Full?new(identity):null;modelResets++;
                    previousAudioFaults=AudioFaults();audioUnhealthyUntil=0;
                }
                long faults=AudioFaults(),healthRevision=Interlocked.Read(ref boardHealthRevision);
                if(faults!=previousAudioFaults||healthRevision!=observedBoardHealthRevision)
                {
                    previousAudioFaults=faults;observedBoardHealthRevision=healthRevision;
                    source?.Reset(identity);audioUnhealthyUntil=(long)now+Stopwatch.Frequency*8;modelResets++;
                    Deny("audio_or_board_fault");
                }
                double boardHealthAge=Interlocked.Read(ref boardHealthAt)==0?double.PositiveInfinity:Stopwatch.GetElapsedTime(Interlocked.Read(ref boardHealthAt)).TotalSeconds;
                bool healthy=current==identity&&!receiver.IsRecovering&&receiver.Fault is null&&receiver.LastAudioAgeMs is >=0 and <40&&(long)now>=audioUnhealthyUntil&&boardHealthComplete&&boardHealthAge<=3.5;
                while(receiver.TryDequeueClockExchange(out var exchange))
                {
                    if(exchanges.Count==128)exchanges.Dequeue();exchanges.Enqueue(exchange);
                    if(exchange.Message is {} message&&message.Identity==identity)
                    {
                        if(stage==ClockObservationStage.SidebandOnly||board?.TryObserve(new(message,exchange.P1Ticks,exchange.P4Ticks,exchange.PcFrequency))==true)lastExchangeAcceptedAt=(long)exchange.P4Ticks;
                        source?.TryObserve(message,exchange.P4Ticks,exchange.PcFrequency,healthy);
                    }
                }
                while(audio.TryDequeueOutputClockSample(out var raw))
                {
                    outputSequence++;
                    if(outputSamples.Count==128)outputSamples.Dequeue();outputSamples.Enqueue(raw);
                    if(raw.OutputGeneration==audio.OutputGeneration)
                    {
                        int hr=raw.FrequencyHResult!=0?(raw.FrequencyHResult<0?raw.FrequencyHResult:unchecked((int)0x80004005)):raw.HResult;
                        if(!raw.Accepted&&hr==0&&raw.Reason is not("position_zero" or "position_stalled" or "qpc_stalled"))hr=unchecked((int)0x80004005);
                        if(output.TryObserve(new(raw.OutputGeneration,raw.Position,raw.Frequency,raw.Qpc100ns,hr,raw.Running)))lastOutputAcceptedAt=raw.QueryEndTimestamp;
                        else if(hr<0||!raw.Running||raw.Reason is "frequency_changed" or "position_backward" or "qpc_backward" or "position_stalled" or "qpc_stalled")Deny("output_clock_invalid");
                    }
                }
                bool clockFresh=lastExchangeAcceptedAt!=0&&lastOutputAcceptedAt!=0&&Stopwatch.GetElapsedTime(lastExchangeAcceptedAt).TotalSeconds<3&&Stopwatch.GetElapsedTime(lastOutputAcceptedAt).TotalSeconds<3;
                // Fast age/health veto, independent of the 1Hz JSON/model
                // publication. A failed device never keeps an old qualified FF.
                if(!healthy||!clockFresh||audio.OutputClockDiagnostics().Failure is not null)Deny(!healthy?"board_health_or_audio_stale":"clock_observation_stale_or_failed");
                if((long)now>=nextPublish)
                {
                    nextPublish=(long)now+Stopwatch.Frequency;
                    var b=board?.Snapshot(now);var s=source?.Snapshot(now);
                    ulong qpc100ns=(ulong)((UInt128)now*10_000_000/(ulong)Stopwatch.Frequency);
                    var o=output.Snapshot(qpc100ns);
                    double ff=0;
                    bool calculated=stage==ClockObservationStage.Full&&healthy&&clockFresh&&audio.OutputClockDiagnostics().Failure is null&&b is not null&&s is not null&&ClockAssistController.TryCalculateFeedforward(b,s,o,receiver.Descriptor?.Format.SampleRate??48000,audio.Buffer.OutputSampleRate,out ff);
                    bool qualified=calculated&&Math.Abs(ff)<=500;
                    string reason=receiver.ClockObservationState=="Fallback"?receiver.ClockObservationFailureReason:stage!=ClockObservationStage.Full?"staged_observation_no_control":!healthy?"audio_or_board_health":calculated&&!qualified?"frequency_limit_exceeded":qualified?"three_observers_qualified":"observer_not_qualified";
                    audio.Buffer.SetClockAssistInput(mode,qualified,ff,reason);receiver.SetClockObservationQualified(qualified);
                    string estimate=calculated?$"{ff:F2} ppm":"未合格";
                    Volatile.Write(ref summary,$"{mode}/{stage} · 板/PC {b?.State.ToString()??"Acquiring"} · 源 {s?.State.ToString()??"Acquiring"} · 输出 {o.State} · 频差估计 {estimate} · {reason}");
                    // Only explicit diagnostics fetches transfer this ring to a
                    // test/logger; the normal UI gets the cached summary only.
                    Volatile.Write(ref diagnostics,JsonSerializer.Serialize(new{mode,stage=stage.ToString(),identity,timestamp=now,clock_frequency=Stopwatch.Frequency,healthy,qualified,
                        feedforward_ppm=calculated?(double?)ff:null,reason,board=b,source=s,output=o,controller=audio.Buffer.ClockAssistSnapshot,board_health_complete=boardHealthComplete,board_health_age_seconds=double.IsFinite(boardHealthAge)?boardHealthAge:(double?)null,board_health_maximum_age_seconds=3.5,
                        transport=receiver.ClockObservationStatistics(),render_clock=audio.OutputClockDiagnostics(),model_resets=modelResets,
                        exchanges=exchanges.ToArray(),output_samples=outputSamples.ToArray(),output_sample_sequence=outputSequence,
                        evidence_scope="bounded recent exchange/output samples; no physical one-way or headphone latency claim; path asymmetry unknown"}));
                }
                await Task.Delay(100,stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception error)
        {
            audio.Buffer.SetClockAssistInput(mode,false,0,"observer_worker_fault");receiver.SetClockObservationQualified(false);
            Volatile.Write(ref summary,"Fallback · "+error.GetType().Name+": "+error.Message);
            Volatile.Write(ref diagnostics,JsonSerializer.Serialize(new{mode,state="Fallback",error=error.ToString(),timestamp=Stopwatch.GetTimestamp()}));
        }
    }
    void Deny(string reason){audio.Buffer.SetClockAssistInput(mode,false,0,reason);receiver.SetClockObservationQualified(false);}
    long AudioFaults()=>Interlocked.Read(ref receiver.SequenceGapPackets)+audio.Buffer.SteadyUnderruns+audio.Buffer.Overflows+audio.Buffer.Resyncs+audio.Buffer.PermanentGapSourceFrames;
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();await worker.ConfigureAwait(false);audio.Buffer.SetClockAssistInput(ClockObservationMode.Legacy,false,0,"observer_stopped");receiver.SetClockObservationQualified(false);stop.Dispose();
    }
}
