using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace UsbAudioBridge;

public sealed record ClockExchangeRecord(long Sequence, ClockSessionIdentity Identity, ulong RequestId,
    ulong P1Ticks, ulong P4Ticks, ulong PcFrequency, ClockObservationMessage? Message, string Outcome);
public readonly record struct ClockReceiveRejection(long Sequence,ulong P4Ticks,string Reason,ClockSessionIdentity? Identity,ulong RequestId);

public sealed partial class AudioReceiver
{
    readonly bool observeClock;
    readonly object clockEvidenceGate=new();
    readonly Queue<ClockExchangeRecord> clockEvidence=new();
    readonly object clockRejectGate=new();
    readonly ClockReceiveRejection[] clockRejectEvents=new ClockReceiveRejection[64];
    Task? clockWorker;
    sealed record PendingClock(ClockSessionIdentity Identity,ulong RequestId,ulong P1Ticks,ulong P1Echo,TaskCompletionSource<ClockReply> Completion);
    readonly record struct ClockReply(ClockObservationMessage Message,ulong P4Ticks);
    sealed record ClockBinding(ClockSessionIdentity Identity);
    PendingClock? clockPending;
    ClockBinding? clockBinding;
    long clockRequests,clockReplies,clockRejected,clockTimeouts,clockEvidenceDropped,clockEvidenceSequence;
    long clockRejectSequence,clockSyntaxRejected,clockIdentityRejected,clockRequestRejected,clockUnexpected;
    volatile bool clockQualified;
    string clockState="Disabled",clockReason="";
    public ClockSessionIdentity? ClockIdentity=>Volatile.Read(ref clockBinding)?.Identity;
    public bool ClockObservationRequested=>observeClock;
    public string ClockObservationState=>clockState;
    public string ClockObservationFailureReason=>clockReason;
    public void SetClockObservationQualified(bool qualified)=>clockQualified=qualified;
    public bool TryDequeueClockExchange(out ClockExchangeRecord record)
    {
        lock(clockEvidenceGate){if(clockEvidence.Count>0){record=clockEvidence.Dequeue();return true;}}
        record=null!;return false;
    }
    void ClockEvidence(PendingClock pending,ulong p4,ClockObservationMessage? message,string outcome)
    {
        var record=new ClockExchangeRecord(Interlocked.Increment(ref clockEvidenceSequence),pending.Identity,pending.RequestId,pending.P1Ticks,p4,(ulong)Stopwatch.Frequency,message,outcome);
        lock(clockEvidenceGate){if(clockEvidence.Count==128){clockEvidence.Dequeue();Interlocked.Increment(ref clockEvidenceDropped);}clockEvidence.Enqueue(record);}
    }
    void ReceiveClockReply(ReadOnlySpan<byte> bytes,ulong p4)
    {
        var pending=Volatile.Read(ref clockPending);
        if(!ClockObservationProtocol.TryDecode(bytes,out var message,out string reason)){RejectClock(reason,p4,null,0);return;}
        if(!observeClock||pending is null){RejectClock("no_pending_exchange",p4,message,3);return;}
        if(p4<pending.P1Ticks||(p4-pending.P1Ticks)/(double)Stopwatch.Frequency>.250){RejectClock("reply_deadline",p4,message,2);return;}
        if(message.Kind!=ClockObservationKind.Reply){RejectClock("reply_kind",p4,message,0);return;}
        if(message.Identity!=pending.Identity||Descriptor is not {} current||current.StreamId!=pending.Identity.StreamId||current.Epoch!=pending.Identity.Epoch||current.Token!=pending.Identity.Token||recovering)
        {RejectClock("session_identity",p4,message,1);return;}
        if(message.RequestId!=pending.RequestId||message.P1Echo!=pending.P1Echo){RejectClock("request_or_echo",p4,message,2);return;}
        if(Interlocked.CompareExchange(ref clockPending,null,pending)!=pending){RejectClock("duplicate_reply",p4,message,2);return;}
        Interlocked.Increment(ref clockReplies);pending.Completion.TrySetResult(new(message,p4));
    }
    void RejectClock(string reason,ulong p4,ClockObservationMessage? message,int category)
    {
        Interlocked.Increment(ref clockRejected);
        if(category==0)Interlocked.Increment(ref clockSyntaxRejected);else if(category==1)Interlocked.Increment(ref clockIdentityRejected);else if(category==2)Interlocked.Increment(ref clockRequestRejected);else Interlocked.Increment(ref clockUnexpected);
        lock(clockRejectGate){long n=++clockRejectSequence;clockRejectEvents[(int)((n-1)%clockRejectEvents.Length)]=new(n,p4,reason,message?.Identity,message?.RequestId??0);}
    }
    ClockReceiveRejection[] ClockRejections(){lock(clockRejectGate)return clockRejectEvents.Where(x=>x.Sequence!=0).OrderBy(x=>x.Sequence).ToArray();}
    static ulong ClockMicroseconds(ulong ticks)=>(ulong)((UInt128)ticks*1_000_000/(ulong)Stopwatch.Frequency);
    async Task ClockLoop()
    {
        bool capabilitiesChecked=false;
        ulong nextId=0;
        ClockSessionIdentity requestIdentity=default;
        int consecutiveTimeouts=0;
        long nextRebindAt=0;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var active=Descriptor;
                if(active is null||recovering||State!="RUNNING")
                {
                    Volatile.Write(ref clockBinding,null);clockState="Acquiring";clockReason="audio_not_running";
                    await Task.Delay(100,stop.Token).ConfigureAwait(false);continue;
                }
                var identity=new ClockSessionIdentity(control.Boot,active.StreamId,active.Epoch,active.Token);
                if(Volatile.Read(ref clockBinding)?.Identity!=identity)
                {
                    if(!capabilitiesChecked)
                    {
                        var capabilities=await control.RequestAsync("GET_CAPABILITIES",ct:stop.Token).ConfigureAwait(false);
                        capabilitiesChecked=true;
                        if(!capabilities.TryGetProperty("clock_observation_v1",out var supported)||supported.ValueKind!=JsonValueKind.True)
                        {clockState="Fallback";clockReason="firmware_clock_observation_v1_unavailable";return;}
                    }
                    try
                    {
                        var enabled=await control.RequestAsync("SET_CLOCK_OBSERVATION",new{enabled=true,version=1,boot_id=identity.Boot.ToString(System.Globalization.CultureInfo.InvariantCulture),stream_id=identity.StreamId,epoch=identity.Epoch,session_token=identity.Token.ToString(System.Globalization.CultureInfo.InvariantCulture)},ct:stop.Token).ConfigureAwait(false);
                        if(!enabled.TryGetProperty("enabled",out var value)||value.ValueKind!=JsonValueKind.True||!enabled.TryGetProperty("version",out var version)||version.GetInt32()!=1||
                            !enabled.TryGetProperty("boot_id",out var ackBoot)||ackBoot.GetString()!=identity.Boot.ToString(System.Globalization.CultureInfo.InvariantCulture)||
                            !enabled.TryGetProperty("stream_id",out var ackStream)||Wire.JsonUInt32(ackStream)!=identity.StreamId||
                            !enabled.TryGetProperty("epoch",out var ackEpoch)||Wire.JsonUInt32(ackEpoch)!=identity.Epoch||
                            !enabled.TryGetProperty("session_token",out var ackToken)||ackToken.GetString()!=identity.Token.ToString(System.Globalization.CultureInfo.InvariantCulture)||
                            !enabled.TryGetProperty("request_bytes",out var ackBytes)||ackBytes.GetInt32()!=ClockObservationProtocol.Length||
                            !enabled.TryGetProperty("source_port",out var ackPort)||ackPort.GetInt32()!=active.SourcePort)
                            throw new InvalidDataException("Clock observation binding was not confirmed.");
                        // A format/epoch change while awaiting TCP invalidates
                        // the response; never send an old bound request.
                        if(Descriptor is not {} current||current.StreamId!=identity.StreamId||current.Epoch!=identity.Epoch||current.Token!=identity.Token)continue;
                        Volatile.Write(ref clockBinding,new(identity));
                        if(requestIdentity!=identity){nextId=0;requestIdentity=identity;}
                        clockQualified=false;clockState="Acquiring";clockReason="";
                    }
                    catch(DeviceException error)when(error.Code is "STREAM_NOT_FOUND" or "INVALID_FIELD")
                    {clockState="Acquiring";clockReason="binding_epoch_changed";await Task.Delay(500,stop.Token).ConfigureAwait(false);continue;}
                }
                if(nextId==ulong.MaxValue){clockState="Fallback";clockReason="request_id_exhausted";return;}
                ulong id=++nextId;
                var completion=new TaskCompletionSource<ClockReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                byte[] bytes=new byte[ClockObservationProtocol.Length];
                var endpoint=new IPEndPoint(IPAddress.Parse(control.Address),active.SourcePort);
                ulong p1=(ulong)Stopwatch.GetTimestamp(),echo=ClockMicroseconds(p1);
                var pending=new PendingClock(identity,id,p1,echo,completion);
                ClockObservationProtocol.Encode(new(identity,ClockObservationKind.Request,id,echo),bytes);
                Volatile.Write(ref clockPending,pending);Interlocked.Increment(ref clockRequests);
                using var requestDeadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);requestDeadline.CancelAfter(250);
                try
                {
                    await udp.SendAsync(bytes,endpoint,requestDeadline.Token).ConfigureAwait(false);
                    var reply=await completion.Task.WaitAsync(requestDeadline.Token).ConfigureAwait(false);
                    ClockEvidence(pending,reply.P4Ticks,reply.Message,"reply");consecutiveTimeouts=0;clockReason="";clockState=clockQualified?"Qualified":"Acquiring";
                }
                catch(OperationCanceledException)when(!stop.IsCancellationRequested)
                {
                    Interlocked.Increment(ref clockTimeouts);ClockEvidence(pending,(ulong)Stopwatch.GetTimestamp(),null,"timeout");clockReason="reply_timeout";
                    // A board may revoke only the clock service after a CLOCK
                    // send timeout. Rebind that service at most once per 10s;
                    // retain request monotonicity and the healthy audio stream.
                    if(++consecutiveTimeouts>=3&&Stopwatch.GetTimestamp()>=nextRebindAt)
                    {Volatile.Write(ref clockBinding,null);clockQualified=false;nextRebindAt=Stopwatch.GetTimestamp()+10*Stopwatch.Frequency;clockState="Fallback";}
                }
                finally{Interlocked.CompareExchange(ref clockPending,null,pending);}
                // The 250ms exchange deadline consumes the selected 500/1000ms
                // period. Lost replies must not silently lower acquisition to
                // 1.33Hz. A full-period overrun still starts from now, never a burst.
                await Task.Delay(ClockRequestCadence.DelayAfterExchange(p1,(ulong)Stopwatch.GetTimestamp(),
                    (ulong)Stopwatch.Frequency,clockQualified),stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(ObjectDisposedException)when(stop.IsCancellationRequested){}
        catch(Exception error){clockState="Fallback";clockReason=error.GetType().Name+": "+error.Message;}
        finally{Volatile.Write(ref clockPending,null);Volatile.Write(ref clockBinding,null);}
    }
    public object ClockObservationStatistics()=>new{requested=observeClock,state=clockState,reason=clockReason,identity=ClockIdentity,
        requests=Interlocked.Read(ref clockRequests),replies=Interlocked.Read(ref clockReplies),rejected=Interlocked.Read(ref clockRejected),timeouts=Interlocked.Read(ref clockTimeouts),
        syntax_rejected=Interlocked.Read(ref clockSyntaxRejected),identity_rejected=Interlocked.Read(ref clockIdentityRejected),request_rejected=Interlocked.Read(ref clockRequestRejected),unexpected=Interlocked.Read(ref clockUnexpected),receive_rejections=ClockRejections(),
        evidence_dropped=Interlocked.Read(ref clockEvidenceDropped),maximum_pending=1,exchange_timeout_ms=250,acquiring_hz=2,stable_hz=1,
        receive_owner="same dedicated audio socket reader; fit and logging outside receive callback",clock_frequency=Stopwatch.Frequency};
}
