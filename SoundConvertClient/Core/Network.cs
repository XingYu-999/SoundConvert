using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
namespace UsbAudioBridge;

public sealed class ControlSession : IAsyncDisposable
{
    readonly ControlRequestTrace?[] traces = new ControlRequestTrace[64]; long traceSequence;
    readonly TcpClient client = new(); readonly SemaphoreSlim gate = new(1); readonly ControlRequestScheduler scheduler = new(); bool connected; uint nextId = 1; ulong txSeq, rxSeq; public ulong Boot { get; private set; }
    public bool IsOwner{get;private set;}public string DeviceId { get; private set; } = ""; public string Address { get; private set; } = ""; public event Action<JsonElement>? EventReceived;
    public bool IsConnected => connected;
    public CancellationToken OperationCancellation { get; set; }
    public async Task ConnectAsync(string address, int port, CancellationToken ct = default, string? local = null)
    {
        var caller=ct;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5)); ct = timeout.Token;
        try
        {
        if (local is not null) client.Client.Bind(new IPEndPoint(IPAddress.Parse(local), 0));
        await client.ConnectAsync(IPAddress.Parse(address), port, ct).ConfigureAwait(false); client.NoDelay = true; Address = address;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { op = "HELLO", args = new { protocol_min = 1, protocol_max = 1, client_id = "SoundConvertClient", connection_mode = "open" } });
        await client.GetStream().WriteAsync(Wire.Control(1, nextId++, 0, 0, payload), ct).ConfigureAwait(false);
        ControlFrame response;
        try { response = await Wire.ReadAsync(client.GetStream(), ct).ConfigureAwait(false); }
        catch (EndOfStreamException error)
        {
            client.Dispose();
            throw new InvalidDataException("开发板未完成直接连接握手，请确认已更新为免配对固件。", error);
        }
        if (response.Kind != 2 || response.RequestId != 1 || response.Sequence != 0) throw new InvalidDataException("Invalid HELLO response");
        var data = CheckResponse(response.Payload);
        if (!data.TryGetProperty("connection_mode", out var mode) || mode.GetString() != "open")
            throw new InvalidDataException("开发板固件不支持直接连接，请更新为免配对固件。");
        DeviceId = data.GetProperty("device_id").GetString()!;
        if (DeviceId.Length != 32 || Convert.FromHexString(DeviceId).Length != 16) throw new InvalidDataException("Invalid device identity");
        Boot = ulong.Parse(data.GetProperty("boot_id").GetString()!);
        if (Boot == 0 || response.Boot != Boot || data.GetProperty("protocol").GetInt32() != 1) throw new InvalidDataException("HELLO boot/protocol mismatch");
        IsOwner = data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.True;
        connected = true;
        }
        catch(OperationCanceledException error)when(!caller.IsCancellationRequested&&timeout.IsCancellationRequested)
        { throw new TimeoutException("连接开发板超时（TCP 或身份校验未响应）。",error); }
    }
    public object ControlStatistics => new { scheduler = scheduler.Statistics, clock_frequency = System.Diagnostics.Stopwatch.Frequency,
        requests = Enumerable.Range(0, traces.Length).Select(i => Volatile.Read(ref traces[i])).Where(x => x is not null).OrderBy(x => x!.EnqueuedTimestamp).ToArray() };
    public Task<JsonElement> RequestAsync(string op, object? args = null, string? revision = null, CancellationToken ct = default) =>
        ScheduleTracedAsync(op, args, revision, ct, null, 0);
    public Task<JsonElement> RequestGuardedAsync(string op, object? args, Func<bool> canSend, long deadlineTimestamp, CancellationToken ct = default) =>
        ScheduleTracedAsync(op, args, null, ct, canSend, deadlineTimestamp);
    async Task<JsonElement> ScheduleTracedAsync(string op, object? args, string? revision, CancellationToken ct, Func<bool>? canSend, long deadlineTimestamp)
    {
        string? operationId = ControlOperationContext.Current; long enqueued = System.Diagnostics.Stopwatch.GetTimestamp(); bool recorded = false;
        void Record(uint id, long sent, long ack, string state, string? error)
        {
            recorded = true; long slot = Interlocked.Increment(ref traceSequence) - 1;
            Volatile.Write(ref traces[(int)(slot % traces.Length)], new(operationId, op, id, enqueued, sent, ack, System.Diagnostics.Stopwatch.GetTimestamp(), state, error));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, OperationCancellation);
        ct = linked.Token;
        try
        {
            return await scheduler.ScheduleAsync(op, token => canSend is null || canSend() ? RequestCoreAsync(op, args, revision, token, Record) : Task.FromException<JsonElement>(new InvalidOperationException("设备会话或 USB 状态已改变，操作未发送。")), ct, deadlineTimestamp).ConfigureAwait(false);
        }
        catch (Exception error) { if (!recorded) Record(0, 0, 0, "NotSent", error.Message); throw; }
    }
    async Task<JsonElement> RequestCoreAsync(string op, object? args, string? revision, CancellationToken ct, Action<uint, long, long, string, string?> record)
    {
        uint requestId = 0; long sent = 0, ack = 0; string state = "Unknown"; string? failure = null;
        await gate.WaitAsync(ct).ConfigureAwait(false); try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3)); ct = timeout.Token;
            if (!connected) throw new InvalidOperationException("Not connected");
            uint id = nextId++; requestId = id; var request = new Dictionary<string, object?> { { "op", op }, { "args", args ?? new { } } }; if (revision is not null) request["expected_revision"] = revision;
            byte[] wire = Wire.Control(1, id, ++txSeq, Boot, JsonSerializer.SerializeToUtf8Bytes(request));
            sent = System.Diagnostics.Stopwatch.GetTimestamp();
            await client.GetStream().WriteAsync(wire, ct).ConfigureAwait(false);
            while (true)
            {
                var frame = await Wire.ReadAsync(client.GetStream(), ct).ConfigureAwait(false); if (frame.Boot != Boot || frame.Sequence <= rxSeq) throw new InvalidDataException("Stale control response"); rxSeq = frame.Sequence;
                if (frame.Kind == 3 && frame.RequestId == 0) { using var e = JsonDocument.Parse(frame.Payload); EventReceived?.Invoke(e.RootElement.Clone()); continue; }
                if (frame.Kind != 2 || frame.RequestId != id) throw new InvalidDataException("Response request mismatch");
                ack = System.Diagnostics.Stopwatch.GetTimestamp(); var result = CheckResponse(frame.Payload); state = "Completed"; return result;
            }
        }
        catch (DeviceException error) { state = "Rejected"; failure = error.Message; throw; }
        catch (Exception error) { failure = error.Message; connected = false; client.Dispose(); throw; }
        finally { record(requestId, sent, ack, sent == 0 ? "NotSent" : state, failure); gate.Release(); }
    }
    static JsonElement CheckResponse(byte[] p) { using var d = JsonDocument.Parse(p); var r = d.RootElement; if (!r.GetProperty("ok").GetBoolean()) throw new DeviceException(r.GetProperty("code").ToString(), r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""); return r.GetProperty("data").Clone(); }
    public async ValueTask DisposeAsync() { connected = false; client.Dispose(); await scheduler.DisposeAsync().ConfigureAwait(false); }
}
public sealed class DeviceException(string code, string message) : Exception($"{code}: {message}") { public string Code { get; } = code; }
public static class DiscoveryService
{
    public static IEnumerable<(string Name, IPAddress Address, IPAddress Mask)> Interfaces() => NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up).SelectMany(x => x.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)).Select(a => (x.Name, a.Address, a.IPv4Mask)));
    public static async Task<IReadOnlyList<Device>> DiscoverAsync(string? local = null, int milliseconds = 1800, CancellationToken ct = default)
    {
        var paths = Interfaces().Where(x => local is null || x.Address.ToString() == local).ToArray();
        if (local is not null && paths.Length == 0) throw new InvalidOperationException("所选网卡 IPv4 已不可用，请刷新网卡后重新选择。");
        var groups = await Task.WhenAll(paths.Select(x => Scan(x.Address, x.Mask, milliseconds, ct))).ConfigureAwait(false); return groups.SelectMany(x => x).DistinctBy(x => (x.DeviceId, x.LocalAddress)).ToList();
    }
    static async Task<List<Device>> Scan(IPAddress local, IPAddress mask, int ms, CancellationToken ct)
    {
        var result = new Dictionary<string, Device>(); using var channel = new UdpBroadcastChannel(local.ToString());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(ms);
        ulong id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)); byte[] request = channel.PrepareRequest(DiscoveryCodec.Request(id));
        async Task Send() { for(int i=0;i<3;i++) { await channel.SendAsync(request,stop.Token).ConfigureAwait(false); await Task.Delay(500,stop.Token).ConfigureAwait(false); } }
        var sender=Send();
        try { while(true) { var packet=await channel.ReceiveAsync(stop.Token).ConfigureAwait(false); try { var device=DiscoveryCodec.Parse(packet,id,local.ToString()); result[device.DeviceId]=device; } catch(Exception e) when(e is InvalidDataException or System.Text.DecoderFallbackException or FormatException) { } } }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
        finally { stop.Cancel(); try { await sender.ConfigureAwait(false); } catch(OperationCanceledException) { } }
        ct.ThrowIfCancellationRequested(); return result.Values.ToList();
    }
}
public sealed partial class AudioReceiver : IAsyncDisposable
{
    public sealed record ReceiveGapEvent(double AtMs,double GapMs,double GcPauseDeltaMs,int Gen0,int Gen1,int Gen2,long Timestamp,long ReceiveStartedTimestamp,double PreviousProcessingMs,double BeforeReceiveMs,double ReceiveCallMs,int SocketAvailableBytes);
    readonly ReceiveGapEvent?[] receiveGaps=new ReceiveGapEvent[16];long receiveGapCount,lastDatagramAt,lastGcPauseTicks,maximumDeliveryGapTicks;
    int lastGc0,lastGc1,lastGc2,receiveThreadId,receiveMmcssError;bool receiveMmcss;
    Thread? receiveThread;
    public sealed record SequenceGapEvent(uint FirstMissing,uint Count,uint Received,ulong SampleIndex,double AtMs);
    readonly SequenceGapEvent?[] gapEvents=new SequenceGapEvent[32];long gapEventCount;
    readonly long createdAt=System.Diagnostics.Stopwatch.GetTimestamp();
    object[] GapSnapshot()=>gapEvents.Select(x=>(object?)x).Where(x=>x is not null).Cast<object>().ToArray();
    readonly UdpClient udp; readonly ControlSession control; readonly CancellationTokenSource stop = new();
    readonly AudioFec fec=new();readonly bool requestFec;readonly AudioTransportCodec requestedCodec;readonly int receiveBufferBytes,diagnosticDropEvery;
    long transportBytes,decodedPcmBytes;readonly AudioTiming decodeTiming=new();
    long injectedLossPackets;
    readonly SemaphoreSlim negotiation = new(1);
    readonly SemaphoreSlim recoverySignal = new(0, 1);
    Task? loop, keepalive;
    StreamDescriptor? descriptor;
    TaskCompletionSource? probeReceived;
    byte[]? pendingProbe;
    uint generation;
    volatile bool recovering;
    bool haveSequence;
    uint highestSequence;
    long lastAudioTimestamp, recoveryOrigin;
    public StreamDescriptor? Descriptor => Volatile.Read(ref descriptor);
    public string? Fault { get; private set; }
    public bool Ended { get; private set; }
    public bool IsRecovering => recovering;
    public double? LastAudioAgeMs
    {
        get
        {
            long timestamp = Interlocked.Read(ref lastAudioTimestamp);
            return timestamp == 0 ? null : System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        }
    }
    public string State { get; private set; } = "STOPPED";
    public long Received, Accepted, Invalid, NonzeroSamples, Peak;
    public long LeftNonzero, RightNonzero, LeftPeak, RightPeak, FormatRecoveries, StalePackets, ReorderedPackets, SequenceGapPackets;
    public long FastRecoveries, UsbTimelineChanges;
    public long SilenceProbes, ValidStreamGapProbes;
    public double LastRecoveryGapMs { get; private set; }
    public double MaximumRecoveryGapMs { get; private set; }
    public string? RecoveryReason { get; private set; }
    public event Action<AudioPacket>? Packet;
    public event Action<AudioFormat>? FormatChanged;
    readonly int? requestedBoardPort;
    public AudioReceiver(ControlSession control, string local = "0.0.0.0", int port = 49162,bool requestFec=false,int diagnosticDropEvery=0,AudioTransportCodec codec=AudioTransportCodec.Pcm,bool observeClock=false,int? boardPort=null) { UdpPortSettings.ValidatePort(port);if(boardPort is {} bp)UdpPortSettings.ValidatePort(bp);requestedBoardPort=boardPort;if(!Enum.IsDefined(codec))throw new ArgumentOutOfRangeException(nameof(codec));requestedCodec=codec;if(diagnosticDropEvery!=0&&(!requestFec||diagnosticDropEvery<8||diagnosticDropEvery%4!=0))throw new ArgumentException("Diagnostic loss requires XOR FEC and an aligned period >=8");this.control = control;this.requestFec=requestFec;this.diagnosticDropEvery=diagnosticDropEvery;this.observeClock=observeClock; udp = new UdpClient(AddressFamily.InterNetwork);try{udp.Client.ExclusiveAddressUse=true;udp.Client.Bind(new IPEndPoint(IPAddress.Parse(local),port));udp.Client.ReceiveBufferSize=1048576;receiveBufferBytes=udp.Client.ReceiveBufferSize;}catch{udp.Dispose();throw;} }
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (loop is not null) throw new InvalidOperationException("Audio receiver already started.");
        // Socket continuations on the shared pool can stall behind unrelated
        // work. Keep the single PROBE/audio owner on a blocking receive thread.
        udp.Client.ReceiveTimeout=100;
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);loop=completed.Task;
        receiveThread=new Thread(()=>{try{ReceiveLoop();}finally{completed.TrySetResult();}}){IsBackground=true,Name="UAB audio receive"};
        if(OperatingSystem.IsWindows())receiveThread.Priority=ThreadPriority.AboveNormal;
        receiveThread.Start();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try { await NegotiateAsync(ct).ConfigureAwait(false); break; }
                catch (DeviceException error) when (attempt < 3 && error.Code is "UNSUPPORTED_FORMAT" or "WAITING_USB" or "USB_EMULATION_TRANSITION_BUSY")
                { await Task.Delay(250, ct).ConfigureAwait(false); }
            }
            keepalive = Task.Run(KeepAlive);
            if(observeClock)clockWorker=Task.Run(ClockLoop);
        }
        catch { stop.Cancel(); throw; }
    }
    static AudioFormat InfoFormat(JsonElement info) => AudioFormat.FromJson(info.GetProperty("usb").GetProperty("format"));
    static uint InfoGeneration(JsonElement info) => info.GetProperty("usb").TryGetProperty("generation", out var value) ? Wire.JsonUInt32(value) : 0;
    static bool UsbRunning(JsonElement info) => !info.TryGetProperty("usb_emulation", out var emulation) || emulation.GetProperty("state").GetString() == "RUNNING";
    async Task NegotiateAsync(CancellationToken ct)
    {
        await negotiation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            recovering = true; State = "NEGOTIATING";
            var info = await control.RequestAsync("GET_INFO", ct: ct).ConfigureAwait(false);
            if (!UsbRunning(info))
            {
                string? state = info.GetProperty("usb_emulation").GetProperty("state").GetString();
                throw new DeviceException(state == "FAULT" ? "USB_EMULATION_FAULT" : state == "STOPPED" ? "USB_EMULATION_STOPPED" : "USB_EMULATION_TRANSITION_BUSY",
                    "USB emulation state: " + state);
            }
            AudioFormat requested = InfoFormat(info); uint observedGeneration = InfoGeneration(info);
            int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var prepared = await control.RequestAsync("STREAM_PREPARE", new { direction = "playback", receive_port = port, source_port = requestedBoardPort,
                packet_frames = requested.PacketFrames, buffer_ms = 4, format = requested.ToRequest(), follow_usb = true,fec_mode=requestFec?"xor4":"none",codec=requestedCodec==AudioTransportCodec.ImaAdpcm?"ima_adpcm":"pcm" }, ct: ct).ConfigureAwait(false);
            AudioFormat actual = AudioFormat.FromJson(prepared.GetProperty("format"));
            if (actual != requested) throw new InvalidDataException("Prepared format does not match the observed USB format.");
            int packetFrames = prepared.TryGetProperty("packet_frames", out var frames) ? frames.GetInt32() : actual.PacketFrames;
            if (packetFrames != actual.PacketFrames) throw new InvalidDataException("Unsupported negotiated packet duration.");
            var next = new StreamDescriptor(Wire.JsonUInt32(prepared.GetProperty("stream_id")), Wire.JsonUInt32(prepared.GetProperty("epoch")),
                ulong.Parse(prepared.GetProperty("session_token").GetString()!), prepared.GetProperty("source_port").GetInt32(), port, actual, packetFrames,
                prepared.TryGetProperty("follow_usb", out var follow) && follow.ValueKind == JsonValueKind.True,
                prepared.TryGetProperty("fec_mode",out var fecMode)&&fecMode.GetString()=="xor4",
                prepared.TryGetProperty("codec",out var codec)&&codec.GetString()=="ima_adpcm"?AudioTransportCodec.ImaAdpcm:AudioTransportCodec.Pcm);
            if(next.Codec!=requestedCodec)throw new NotSupportedException("开发板未确认所选压缩方式，请更新配套固件或选择 PCM 无压缩。");
            if(requestFec&&!next.FecEnabled)throw new NotSupportedException("开发板不支持网络纠错，请更新配套固件或关闭网络纠错。");
            if (next.SourcePort is < 1 or > 65535) throw new InvalidDataException("Invalid source port.");
            if (requestedBoardPort is > 0 && next.SourcePort != requestedBoardPort) throw new NotSupportedException("开发板未确认指定的播放 UDP 端口，请更新配套固件。");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] probe = AudioCodec.Probe(next);
            Volatile.Write(ref pendingProbe, probe); Volatile.Write(ref probeReceived, completion); Volatile.Write(ref descriptor, next);
            var remote = new IPEndPoint(IPAddress.Parse(control.Address), next.SourcePort);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); deadline.CancelAfter(3000);
            await udp.SendAsync(probe, remote, deadline.Token).ConfigureAwait(false);
            await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await control.RequestAsync("STREAM_START", new { stream_id = next.StreamId, epoch = next.Epoch }, ct: deadline.Token).ConfigureAwait(false);
            generation = observedGeneration; haveSequence = false; Ended = false;
            State = "WAITING_USB"; // Only receipt of a current-epoch audio packet establishes RUNNING.
            recovering = false; FormatChanged?.Invoke(actual);
        }
        finally { Volatile.Write(ref pendingProbe, null); Volatile.Write(ref probeReceived, null); negotiation.Release(); }
    }
    void ReceiveLoop()
    {
        using var scheduling=new AudioReceiveScheduling();
        receiveThreadId=Environment.CurrentManagedThreadId;receiveMmcss=scheduling.Registered;receiveMmcssError=scheduling.Error;
        lastGcPauseTicks=GC.GetTotalPauseDuration().Ticks;lastGc0=GC.CollectionCount(0);lastGc1=GC.CollectionCount(1);lastGc2=GC.CollectionCount(2);
        long previousProcessingTicks=0,previousProcessingEnded=0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                IPEndPoint remote=new(IPAddress.Any,0);byte[] raw;
                long receiveStarted=System.Diagnostics.Stopwatch.GetTimestamp();
                try{raw=udp.Receive(ref remote);}
                catch(SocketException error)when(error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock){continue;}
                long receiveReturned=System.Diagnostics.Stopwatch.GetTimestamp();
                var received=new UdpReceiveResult(raw,remote);
                var active = Descriptor;
                if (active is null) continue;
                try
                {
                    if (!received.RemoteEndPoint.Address.Equals(IPAddress.Parse(control.Address)) || received.RemoteEndPoint.Port != active.SourcePort)
                        throw new InvalidDataException("Wrong audio endpoint.");
                    var probe = Volatile.Read(ref pendingProbe);
                    if (probe is not null && received.Buffer.AsSpan().SequenceEqual(probe)) { Volatile.Read(ref probeReceived)?.TrySetResult(); continue; }
                    // P4 was captured immediately on socket return. The sole
                    // reader only validates/completes a pending exchange here;
                    // fitting and JSON/logging run outside the audio thread.
                    if(ClockObservationProtocol.IsClockPacket(received.Buffer)){ReceiveClockReply(received.Buffer,(ulong)receiveReturned);continue;}
                    long delivered=receiveReturned,pause=GC.GetTotalPauseDuration().Ticks;
                    int gc0=GC.CollectionCount(0),gc1=GC.CollectionCount(1),gc2=GC.CollectionCount(2);
                    if(lastDatagramAt!=0)
                    {
                        long gap=delivered-lastDatagramAt;
                        if(gap>Interlocked.Read(ref maximumDeliveryGapTicks))Interlocked.Exchange(ref maximumDeliveryGapTicks,gap);
                        double gapMs=gap*1000d/System.Diagnostics.Stopwatch.Frequency;
                        if(gapMs>=20)
                        {
                            // Only anomalous gaps query queued socket bytes. A
                            // queued burst alone cannot distinguish NIC batching
                            // from thread scheduling; align these QPC values to
                            // kernel ingress/ReadyThread evidence before attribution.
                            int available=-1;try{available=udp.Available;}catch(SocketException){}catch(ObjectDisposedException){}
                            long slot=Interlocked.Increment(ref receiveGapCount)-1;
                            Volatile.Write(ref receiveGaps[(int)(slot%receiveGaps.Length)],new ReceiveGapEvent(System.Diagnostics.Stopwatch.GetElapsedTime(createdAt,delivered).TotalMilliseconds,gapMs,TimeSpan.FromTicks(pause-lastGcPauseTicks).TotalMilliseconds,gc0-lastGc0,gc1-lastGc1,gc2-lastGc2,delivered,receiveStarted,previousProcessingTicks*1000d/System.Diagnostics.Stopwatch.Frequency,previousProcessingEnded==0?0:Math.Max(0,receiveStarted-previousProcessingEnded)*1000d/System.Diagnostics.Stopwatch.Frequency,(receiveReturned-receiveStarted)*1000d/System.Diagnostics.Stopwatch.Frequency,available));
                        }
                    }
                    lastDatagramAt=delivered;lastGcPauseTicks=pause;lastGc0=gc0;lastGc1=gc1;lastGc2=gc2;
                    Interlocked.Increment(ref Received);
                    if (recovering) { Interlocked.Increment(ref StalePackets); continue; }
                    // Explicit bench-only erasure, before FEC. It is disabled in normal playback and reported separately.
                    if(diagnosticDropEvery>0&&!AudioFec.IsParity(received.Buffer))
                    {
                        var tested=AudioCodec.Parse(received.Buffer,active);
                        if(tested.Flags==0&&tested.Sequence%(uint)diagnosticDropEvery==1){Interlocked.Increment(ref injectedLossPackets);continue;}
                    }
                    bool parity=AudioFec.IsParity(received.Buffer);byte[]? repair=null;
                    long decodeAt=System.Diagnostics.Stopwatch.GetTimestamp();
                    AudioPacket? primary=parity?null:AudioCodec.Parse(received.Buffer,active);
                    if(primary is not null){decodeTiming.Record(System.Diagnostics.Stopwatch.GetTimestamp()-decodeAt);Interlocked.Add(ref transportBytes,received.Buffer.Length);Interlocked.Add(ref decodedPcmBytes,primary.Pcm.Length);}
                    if(active.FecEnabled)repair=fec.ObserveValidated(received.Buffer,active);
                    else if(parity)throw new InvalidDataException("Unnegotiated FEC packet");
                    for(int pass=0;pass<2;pass++)
                    {
                    byte[]? datagram=pass==0?(parity?null:received.Buffer):repair;if(datagram is null)continue;
                    var packet = pass==0?primary!:AudioCodec.Parse(datagram, active);
                    if ((packet.Flags & 4) != 0) { Ended = true; MarkRecovery("end_packet", true); continue; }
                    if ((packet.Flags & 8) != 0) continue;
                    if (active.FollowUsb && Wire.Read32(datagram, 12) != active.Epoch)
                    {
                        generation = Wire.Read32(datagram, 12);
                        Volatile.Write(ref descriptor, active with { Epoch = generation, NegotiatedFormat = packet.Format, PacketFrames = packet.Format.PacketFrames });
                        Interlocked.Increment(ref UsbTimelineChanges);
                        if (packet.Format != active.Format) FormatChanged?.Invoke(packet.Format);
                    }
                    if (haveSequence)
                    {
                        int distance = unchecked((int)(packet.Sequence - highestSequence));
                        if (distance > 1){Interlocked.Add(ref SequenceGapPackets, distance - 1);long slot=Interlocked.Increment(ref gapEventCount)-1;Volatile.Write(ref gapEvents[(int)(slot%gapEvents.Length)],new SequenceGapEvent(unchecked(highestSequence+1),(uint)(distance-1),packet.Sequence,packet.SampleIndex,System.Diagnostics.Stopwatch.GetElapsedTime(createdAt).TotalMilliseconds));}
                        if (distance <= 0) Interlocked.Increment(ref ReorderedPackets);
                        else highestSequence = packet.Sequence;
                    }
                    else { haveSequence = true; highestSequence = packet.Sequence; }
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long recoveryFrom = Interlocked.Exchange(ref recoveryOrigin, 0);
                    if (recoveryFrom != 0)
                    {
                        LastRecoveryGapMs = System.Diagnostics.Stopwatch.GetElapsedTime(recoveryFrom, now).TotalMilliseconds;
                        MaximumRecoveryGapMs = Math.Max(MaximumRecoveryGapMs, LastRecoveryGapMs);
                    }
                    Interlocked.Exchange(ref lastAudioTimestamp, now);
                    Interlocked.Increment(ref Accepted); State = "RUNNING";
                    int bytes = packet.Format.BytesPerSample;
                    for (int i = 0; i < packet.Pcm.Length; i += bytes)
                    {
                        long sample = Math.Abs((long)PcmCodec.ReadSigned(packet.Pcm.AsSpan(i), packet.Format.Bits)); bool left = i % packet.Format.BytesPerFrame == 0;
                        if (sample != 0) { NonzeroSamples++; if (left) LeftNonzero++; else RightNonzero++; }
                        Peak = Math.Max(Peak, sample); if (left) LeftPeak = Math.Max(LeftPeak, sample); else RightPeak = Math.Max(RightPeak, sample);
                    }
                    Packet?.Invoke(packet);
                    }
                }
                catch (InvalidDataException) { Interlocked.Increment(ref Invalid); }
                finally
                {
                    // Includes validation, decode/FEC, peak counters and packet
                    // subscribers. It excludes the next blocking Receive call.
                    previousProcessingEnded=System.Diagnostics.Stopwatch.GetTimestamp();previousProcessingTicks=previousProcessingEnded-receiveReturned;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Fault = error.Message; State = "FAULT"; stop.Cancel(); }
    }
    void MarkRecovery(string reason, bool wake)
    {
        if (!recovering)
        {
            long previous = Interlocked.Read(ref lastAudioTimestamp);
            if (previous != 0) Interlocked.CompareExchange(ref recoveryOrigin, previous, 0);
        }
        recovering = true; State = "RECOVERING"; RecoveryReason = reason;
        if (wake && recoverySignal.CurrentCount == 0) try { recoverySignal.Release(); } catch (SemaphoreFullException) { }
    }
    async Task KeepAlive()
    {
        int failures = 0;
        long nextKeepalive = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency;
        long nextSilenceProbe = 0, lastObservedAudio = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await recoverySignal.WaitAsync(TimeSpan.FromMilliseconds(10), stop.Token).ConfigureAwait(false);
                long now = System.Diagnostics.Stopwatch.GetTimestamp(), lastAudio = Interlocked.Read(ref lastAudioTimestamp);
                if (lastAudio != lastObservedAudio) { lastObservedAudio = lastAudio; nextSilenceProbe = 0; }
                bool silenceProbe = !recovering && State == "RUNNING" && lastAudio != 0 && udp.Available == 0 &&
                    now >= nextSilenceProbe && System.Diagnostics.Stopwatch.GetElapsedTime(lastAudio, now).TotalMilliseconds >= 40;
                if (!recovering && !silenceProbe && now < nextKeepalive) continue;
                try
                {
                    // Persistent subscriptions survive USB pauses; legacy firmware still
                    // invalidates them. KEEPALIVE detects actual subscription loss without
                    // serializing full GET_INFO responses on the audio receive path.
                    if (recovering) { await NegotiateAsync(stop.Token).ConfigureAwait(false); FormatRecoveries++; }
                    else
                    {
                        if (silenceProbe) Interlocked.Increment(ref SilenceProbes);
                        await control.RequestAsync("STREAM_KEEPALIVE", new { stream_id = Descriptor!.StreamId }, ct: stop.Token).ConfigureAwait(false);
                        // A delayed Wi-Fi/UDP burst does not invalidate an otherwise live
                        // stream. Preserve its epoch and buffer instead of calling PREPARE.
                        if (silenceProbe) Interlocked.Increment(ref ValidStreamGapProbes);
                        nextSilenceProbe = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 10;
                    }
                    failures = 0;
                    nextKeepalive = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency;
                }
                catch (DeviceException error) when (error.Code == "USB_EMULATION_STOPPED")
                { recovering = true; State = "WAITING_USB_EMULATION"; failures = 0; await Task.Delay(250, stop.Token).ConfigureAwait(false); }
                catch (DeviceException error) when (error.Code is "STREAM_NOT_FOUND" or "WAITING_USB" or "UNSUPPORTED_FORMAT" or "USB_EMULATION_TRANSITION_BUSY" or "USB_EMULATION_STOPPED")
                { if (silenceProbe && error.Code == "STREAM_NOT_FOUND") FastRecoveries++; MarkRecovery(error.Code, false); if (++failures > 10) throw; if (error.Code != "STREAM_NOT_FOUND") await Task.Delay(100, stop.Token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Fault = error.Message; State = "FAULT"; stop.Cancel(); }
    }
    public object Statistics() => new { state = State, ended = Ended, recovering, fault = Fault, format = Descriptor?.Format, source_generation = generation,stream_id=Descriptor?.StreamId,epoch=Descriptor?.Epoch,
        transport_codec=Descriptor?.Codec==AudioTransportCodec.ImaAdpcm?"ima_adpcm":"pcm",transport_audio_bytes=Interlocked.Read(ref transportBytes),decoded_pcm_bytes=Interlocked.Read(ref decodedPcmBytes),decode_timing=decodeTiming.Snapshot(),
        receive_worker="dedicated_blocking_socket",receive_thread_id=receiveThreadId,receive_thread_alive=receiveThread?.IsAlive==true,receive_mmcss_registered=receiveMmcss,receive_mmcss_error=receiveMmcssError,receive_origin_timestamp=createdAt,clock_frequency=System.Diagnostics.Stopwatch.Frequency,receive_gap_timing_scope="QPC Timestamp is immediately after UDP Receive; PreviousProcessingMs includes the preceding packet subscriber; ReceiveCallMs includes socket wait and scheduling while inside Receive; BeforeReceiveMs includes intervening timeouts; SocketAvailableBytes is sampled only on a >=20ms gap (-1 if unavailable)",
        maximum_inter_datagram_gap_ms=Interlocked.Read(ref maximumDeliveryGapTicks)*1000d/System.Diagnostics.Stopwatch.Frequency,receive_gap_events=receiveGaps.Where(x=>x is not null).ToArray(),gc_total_pause_ms=GC.GetTotalPauseDuration().TotalMilliseconds,
        format_recoveries = FormatRecoveries, fast_recoveries = FastRecoveries, usb_timeline_changes = UsbTimelineChanges, follow_usb = Descriptor?.FollowUsb, recovery_reason = RecoveryReason,
        last_audio_age_ms = LastAudioAgeMs, silence_probes = SilenceProbes, valid_stream_gap_probes = ValidStreamGapProbes,
        last_recovery_gap_ms = LastRecoveryGapMs, maximum_recovery_gap_ms = MaximumRecoveryGapMs,
        stale_packets = StalePackets, reordered_packets = ReorderedPackets,
        diagnostic_drop_every=diagnosticDropEvery,injected_loss_packets=Interlocked.Read(ref injectedLossPackets),fec_mode=Descriptor?.FecEnabled==true?"xor4":"none",fec_repaired_packets=fec.Repairs,fec_parity_packets=fec.ParityPackets,fec_expired_groups=fec.ExpiredGroups,receive_socket_buffer_bytes=receiveBufferBytes,observed_sequence_gap_events=GapSnapshot(),observed_sequence_gap_packets = SequenceGapPackets, received_datagrams = Received, accepted_audio_packets = Accepted,
        invalid_packets = Invalid, nonzero_samples = NonzeroSamples, peak = Peak, left_nonzero = LeftNonzero,
        right_nonzero = RightNonzero, left_peak = LeftPeak, right_peak = RightPeak };
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); udp.Dispose();
        if (loop is not null) try { await loop.ConfigureAwait(false); } catch (ObjectDisposedException) { }
        if (keepalive is not null) await keepalive.ConfigureAwait(false);
        if(clockWorker is not null)await clockWorker.ConfigureAwait(false);
        if (Descriptor is not null) try { await control.RequestAsync("STREAM_STOP", new { stream_id = Descriptor.StreamId }).ConfigureAwait(false); } catch { }
        State = "STOPPED"; stop.Dispose(); negotiation.Dispose(); recoverySignal.Dispose();
    }
}





