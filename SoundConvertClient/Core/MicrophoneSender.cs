using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace UsbAudioBridge;

/// <summary>Independent microphone socket/worker. Input callbacks only copy bounded source data.</summary>
public sealed class MicrophoneSender:IAsyncDisposable
{
    readonly ControlSession control;
    readonly UdpClient udp;
    readonly CancellationTokenSource stop=new();
    readonly AutoResetEvent wake=new(false);
    readonly object gate=new();
    // Separate from the source lock: an epoch cannot be prepared while an old
    // synchronous send is in flight, but Push and Statistics never wait on TCP.
    readonly SemaphoreSlim epochGate=new(1,1);
    readonly float[] source=new float[65536],window=new float[4096];
    readonly byte[] pcm=new byte[288],packet=new byte[48+288];
    readonly AudioTiming pushWait=new(),workerWait=new(),packetProcessing=new();
    readonly AudioWorkerTiming workerService=new();
    readonly int sourceRate,sourceBits;
    long written,minimumPosition,generation;
    double position;
    SincFilter.Bank? bank;
    bool negotiating,started;
    CaptureDescriptor? descriptor;
    IPEndPoint? destination;
    Thread? worker;
    Task? keepalive;
    readonly TaskCompletionSource completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    uint sequence;
    ulong sampleIndex;
    int remainder,disposed;
    long sent,bytes,captured,overruns,expiredFrames,expiredPackets,generationDiscards;
    long workerAllocated,processingDeadlineMisses,processingMaxTicks,pushLockWaitTicks,pushLockMaxTicks,workerLockWaitTicks,workerLockMaxTicks;
    int workerMmcss,workerMmcssError;
    string state="STOPPED";
    public string? Fault{get;private set;}
    public bool PlaybackFirst{get;}
    public int LocalPort=>((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    float gain=1;bool mute;
    public float Gain{get=>Volatile.Read(ref gain);set=>Volatile.Write(ref gain,Math.Clamp(value,0,1));}
    public bool Mute{get=>Volatile.Read(ref mute);set=>Volatile.Write(ref mute,value);}
    public CaptureDescriptor? Descriptor=>Volatile.Read(ref descriptor);
    public string State=>Volatile.Read(ref state);
    readonly int? requestedBoardPort;
    public MicrophoneSender(ControlSession control,int sourceRate,int sourceBits,string local="0.0.0.0",int localPort=49164,bool playbackFirst=true,int? boardPort=null)
    {
        if(sourceRate is <8000 or >192000||sourceBits<16)throw new ArgumentOutOfRangeException(nameof(sourceRate));
        UdpPortSettings.ValidatePort(localPort);if(boardPort is {} bp)UdpPortSettings.ValidatePort(bp);requestedBoardPort=boardPort;
        this.control=control;this.sourceRate=sourceRate;this.sourceBits=sourceBits;PlaybackFirst=playbackFirst;
        udp=new(AddressFamily.InterNetwork);try{udp.Client.ExclusiveAddressUse=true;udp.Client.Bind(new IPEndPoint(IPAddress.Parse(local),localPort));udp.Client.SendBufferSize=65536;udp.Client.SendTimeout=5;}catch{udp.Dispose();throw;}
    }
    public void Push(ReadOnlySpan<float> mono)
    {
        long waiting=Stopwatch.GetTimestamp();
        lock(gate)
        {
            RecordWait(ref pushLockWaitTicks,ref pushLockMaxTicks,waiting,pushWait);
            if(stop.IsCancellationRequested)return;
            // Bound even an oversized source callback. The worker reports the
            // corresponding source-time hole instead of sending stale audio.
            long oldest=Math.Max(0,written+mono.Length-source.Length+window.Length);
            if(oldest>minimumPosition){overruns+=Math.Max(0,oldest-Math.Max(minimumPosition,(long)position));minimumPosition=oldest;}
            int skip=Math.Max(0,mono.Length-(source.Length-window.Length));written+=skip;
            for(int i=skip;i<mono.Length;i++){float v=mono[i];source[(int)(written++%source.Length)]=float.IsFinite(v)?Math.Clamp(v,-1,1):0;}
            captured+=mono.Length;
        }
        wake.Set();
    }
    public void PushArray(float[] mono)=>Push(mono);
    public async Task StartAsync(CancellationToken ct=default)
    {
        if(started)throw new InvalidOperationException("Microphone sender already started");started=true;
        await Negotiate(ct).ConfigureAwait(false);
        // playback_first controls board arbitration. BelowNormal can starve
        // this independent real-time source while UI processes are starting.
        worker=new Thread(Run){IsBackground=true,Name="UAB microphone send",Priority=ThreadPriority.Normal};worker.Start();keepalive=Keepalive();
    }
    async Task Negotiate(CancellationToken ct)
    {
        lock(gate){negotiating=true;generation++;state="NEGOTIATING";}
        bool leased=false;
        try
        {
            await epochGate.WaitAsync(ct).ConfigureAwait(false);leased=true;
            var info=await control.RequestAsync("GET_INFO",ct:ct).ConfigureAwait(false);var mic=info.GetProperty("capture");var format=CaptureFormat.FromJson(mic.GetProperty("format"));
            if(format.SampleRate>sourceRate||format.Bits>sourceBits)throw new NotSupportedException("开发板麦克风格式超过当前电脑输入源，请选择并保存较低的录音格式。");
            var p=await control.RequestAsync("CAPTURE_PREPARE",new{receive_port=LocalPort,source_port=requestedBoardPort,format=format.ToRequest(),priority=PlaybackFirst?"playback_first":"balanced"},ct:ct).ConfigureAwait(false);
            var next=new CaptureDescriptor(p.GetProperty("stream_id").GetUInt32(),p.GetProperty("epoch").GetUInt32(),ulong.Parse(p.GetProperty("session_token").GetString()!),p.GetProperty("source_port").GetInt32(),CaptureFormat.FromJson(p.GetProperty("format")),p.GetProperty("host_active").GetBoolean());
            if(next.Format!=format||next.DestinationPort is <1024 or >65535)throw new InvalidDataException("Invalid microphone negotiation");
            if(requestedBoardPort is >0&&next.DestinationPort!=requestedBoardPort)throw new NotSupportedException("开发板未确认指定的麦克风 UDP 端口，请更新配套固件。");
            ct.ThrowIfCancellationRequested();var remote=new IPEndPoint(IPAddress.Parse(control.Address),next.DestinationPort);
            // Connected Socket.Send avoids allocating a serialized endpoint for
            // each 1 ms datagram. Reconnect only while holding the epoch lease.
            udp.Connect(remote);byte[] probe=CaptureCodec.Encode(next,0,0,[],0,true);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct,stop.Token);deadline.CancelAfter(3000);
            await udp.SendAsync(probe,deadline.Token).ConfigureAwait(false);var response=await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if(!response.RemoteEndPoint.Equals(remote)||!response.Buffer.AsSpan().SequenceEqual(probe))throw new InvalidDataException("Invalid microphone probe");
            await control.RequestAsync("CAPTURE_START",new{stream_id=next.StreamId},ct:ct).ConfigureAwait(false);
            var preparedBank=SincFilter.ForRates(sourceRate,format.SampleRate,ResamplerQuality.LowLatency);
            lock(gate)
            {
                bank=preparedBank;position=Math.Max(minimumPosition,Math.Max(position,written-sourceRate*.02));
                sequence=0;sampleIndex=0;remainder=0;destination=remote;Volatile.Write(ref descriptor,next);state=next.HostActive?"RUNNING":"WAITING_HOST";
            }
        }
        finally{if(leased)epochGate.Release();lock(gate)negotiating=false;wake.Set();}
    }
    readonly record struct Work(CaptureDescriptor Descriptor,SincFilter.Bank Bank,long Generation,double Position,long First,int Frames,int Accumulated,uint Sequence,ulong SampleIndex);
    bool Prepare(out Work work,out double queuedMs,out long expired)
    {
        work=default;queuedMs=0;expired=0;long waiting=Stopwatch.GetTimestamp();
        lock(gate)
        {
            RecordWait(ref workerLockWaitTicks,ref workerLockMaxTicks,waiting,workerWait);
            queuedMs=(written-position)*1000/sourceRate;expired=expiredFrames;
            var d=descriptor;var b=bank;if(negotiating||d is null||b is null)return false;
            double step=sourceRate/(double)d.Format.SampleRate;
            // Normal source callbacks arrive in batches. Only an excessive
            // backlog (>80 ms) expires; retain 40 ms and preserve wire time.
            double floor=Math.Max(minimumPosition,position);
            if(written-position>sourceRate*.08)floor=Math.Max(floor,written-sourceRate*.04);
            if(floor>position)
            {
                long skipPackets=Math.Max(1,(long)Math.Ceiling((floor-position)*1000/sourceRate));
                long total=remainder+skipPackets*d.Format.SampleRate;ulong skipped=(ulong)(total/1000);
                double before=position;position+=skipped*step;sampleIndex+=skipped;sequence+=unchecked((uint)skipPackets);remainder=(int)(total%1000);
                expiredFrames+=(long)(position-before);expiredPackets+=skipPackets;
                expired=expiredFrames;
            }
            int accumulated=remainder+d.Format.SampleRate,frames=accumulated/1000;
            long first=(long)position,last=(long)Math.Ceiling(position+frames*step)+b.LookAhead;
            if(written<=last)return false;
            int length=checked((int)(last-first)+b.History+1);if(length>window.Length)throw new InvalidOperationException("Microphone FIR window exceeds bound");
            for(int i=0;i<length;i++){long at=first-b.History+i;window[i]=at<0?source[0]:source[(int)(at%source.Length)];}
            work=new(d,b,generation,position,first,frames,accumulated,sequence,sampleIndex);return true;
        }
    }
    void Run()
    {
        try
        {
            using var scheduling=new AudioReceiveScheduling();
            Volatile.Write(ref workerMmcss,scheduling.Registered?1:0);Volatile.Write(ref workerMmcssError,scheduling.Error);
            using var pacer=new AudioPacketPacer(wake);
            long allocationBase=GC.GetAllocatedBytesForCurrentThread(),burstStart=0,nextBurst=0;int batch=0;
            while(!stop.IsCancellationRequested)
            {
                long now=Stopwatch.GetTimestamp();if(now<nextBurst){pacer.WaitUntil(nextBurst);continue;}
                long due=nextBurst;nextBurst=0;
                if(batch>0&&now-burstStart>=Math.Max(1,Stopwatch.Frequency/1000))batch=0;
                bool available=Prepare(out var work,out double queuedMs,out long expired);
                workerService.Record(now,due,queuedMs,expired,GC.GetTotalPauseDuration().Ticks,GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2));
                if(!available){wake.WaitOne(5);continue;}
                long began=Stopwatch.GetTimestamp();var d=work.Descriptor;double step=sourceRate/(double)d.Format.SampleRate,cursor=work.Position,volume=Mute?0:Gain;
                for(int frame=0;frame<work.Frames;frame++)
                {
                    int at=(int)((long)cursor-work.First);var samples=window.AsSpan(at,work.Bank.Taps);
                    work.Bank.Convolve(samples,samples,cursor-Math.Floor(cursor),out double value,out _);
                    int scale=d.Format.Bits==24?8388608:32768,v=(int)Math.Clamp(Math.Round(value*volume*scale),-scale,scale-1),o=frame*d.Format.BytesPerFrame;
                    pcm[o]=(byte)v;pcm[o+1]=(byte)(v>>8);if(d.Format.Bits==24)pcm[o+2]=(byte)(v>>16);cursor+=step;
                }
                if(!CaptureCodec.TryEncode(d,work.Sequence,work.SampleIndex,pcm.AsSpan(0,work.Frames*d.Format.BytesPerFrame),work.Frames,packet,out int length))throw new InvalidDataException("Invalid microphone packet");
                bool sentNow=false;
                if(epochGate.Wait(0))
                {
                    try
                    {
                        bool valid;lock(gate)valid=!negotiating&&generation==work.Generation&&ReferenceEquals(descriptor,d)&&!stop.IsCancellationRequested;
                        if(valid)
                        {
                            int sentBytes=udp.Client.Send(packet.AsSpan(0,length),SocketFlags.None);if(sentBytes!=length)throw new IOException("Incomplete microphone datagram");
                            lock(gate)if(generation==work.Generation&&ReferenceEquals(descriptor,d)){position=cursor;sequence=work.Sequence+1;sampleIndex=work.SampleIndex+(uint)work.Frames;remainder=work.Accumulated%1000;}
                            Interlocked.Increment(ref sent);Interlocked.Add(ref bytes,length);sentNow=true;
                        }
                        else Interlocked.Increment(ref generationDiscards);
                    }
                    finally{epochGate.Release();}
                }
                else Interlocked.Increment(ref generationDiscards);
                long elapsed=Stopwatch.GetTimestamp()-began;if(elapsed>Stopwatch.Frequency/1000)Interlocked.Increment(ref processingDeadlineMisses);
                packetProcessing.Record(elapsed);
                SetMax(ref processingMaxTicks,elapsed);Interlocked.Exchange(ref workerAllocated,GC.GetAllocatedBytesForCurrentThread()-allocationBase);
                // Recheck time after every wake: source callbacks cannot bypass
                // the two-packet-per-millisecond catch-up bound.
                if(sentNow){if(batch++==0)burstStart=Stopwatch.GetTimestamp();if(batch>=2){batch=0;nextBurst=burstStart+Math.Max(1,Stopwatch.Frequency/1000);}}
                else wake.WaitOne(1);
            }
        }
        catch(Exception e){Fault=e.Message;state="FAULT";stop.Cancel();}
        finally{completed.TrySetResult();}
    }
    static void RecordWait(ref long total,ref long maximum,long began,AudioTiming timing){long elapsed=Stopwatch.GetTimestamp()-began;Interlocked.Add(ref total,elapsed);SetMax(ref maximum,elapsed);timing.Record(elapsed);}
    static void SetMax(ref long target,long value){long seen=Volatile.Read(ref target);while(value>seen){long previous=Interlocked.CompareExchange(ref target,value,seen);if(previous==seen)break;seen=previous;}}
    async Task Keepalive()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                await Task.Delay(250,stop.Token).ConfigureAwait(false);var d=Descriptor!;
                // Normal stop must finish the shared TCP response before
                // CAPTURE_STOP, preserving the playback control connection.
                var m=await control.RequestAsync("CAPTURE_KEEPALIVE",new{stream_id=d.StreamId}).ConfigureAwait(false);
                if(stop.IsCancellationRequested)break;bool active=m.GetProperty("active").GetBoolean();
                if(m.GetProperty("generation").GetUInt32()!=d.Epoch)await Negotiate(stop.Token).ConfigureAwait(false);else state=active?"RUNNING":"WAITING_HOST";
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception e){Fault=e.Message;state="FAULT";stop.Cancel();wake.Set();}
    }
    public object Statistics(object? captureCallback=null)
    {
        // Percentile copies/sorts belong to the telemetry caller, outside the
        // source lock and outside the packet worker's allocation accounting.
        object pushTiming=pushWait.Snapshot(),workerTiming=workerWait.Snapshot(),processingTiming=packetProcessing.Snapshot(),serviceTiming=workerService.Snapshot();
        lock(gate)return new{state,fault=Fault,local_port=LocalPort,remote_port=destination?.Port,format=descriptor?.Format,priority=PlaybackFirst?"playback_first":"balanced",worker_thread=worker?.ManagedThreadId,worker_alive=worker?.IsAlive,worker_priority="Normal",worker_mmcss_registered=Volatile.Read(ref workerMmcss)!=0,worker_mmcss_error=Volatile.Read(ref workerMmcssError),worker_service=serviceTiming,source_rate=sourceRate,captured_frames=captured,sent_packets=Interlocked.Read(ref sent),sent_uabm_bytes=Interlocked.Read(ref bytes),source_queued_ms=(written-position)*1000/sourceRate,source_overrun_frames=overruns,source_expired_frames=expiredFrames,source_expired_packets=expiredPackets,generation_discards=Interlocked.Read(ref generationDiscards),worker_allocated_bytes=Interlocked.Read(ref workerAllocated),processing_deadline_misses=Interlocked.Read(ref processingDeadlineMisses),processing_max_us=Interlocked.Read(ref processingMaxTicks)*1_000_000d/Stopwatch.Frequency,push_lock_wait_us=Interlocked.Read(ref pushLockWaitTicks)*1_000_000d/Stopwatch.Frequency,push_lock_max_wait_us=Interlocked.Read(ref pushLockMaxTicks)*1_000_000d/Stopwatch.Frequency,worker_lock_wait_us=Interlocked.Read(ref workerLockWaitTicks)*1_000_000d/Stopwatch.Frequency,worker_lock_max_wait_us=Interlocked.Read(ref workerLockMaxTicks)*1_000_000d/Stopwatch.Frequency,push_lock_wait=pushTiming,worker_lock_wait=workerTiming,packet_processing=processingTiming,capture_callback=captureCallback};
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();wake.Set();if(worker is not null)await completed.Task.ConfigureAwait(false);if(keepalive is not null)try{await keepalive.ConfigureAwait(false);}catch(OperationCanceledException){}
        if(Descriptor is {}d)try{await control.RequestAsync("CAPTURE_STOP",new{stream_id=d.StreamId}).ConfigureAwait(false);}catch{}
        udp.Dispose();epochGate.Dispose();wake.Dispose();stop.Dispose();state="STOPPED";
    }
}
