using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UsbAudioBridge;

namespace UsbAudioBridge.Checks;

/// <summary>Real loopback sockets with a UI context that never pumps posted callbacks.</summary>
static class AudioThreadingChecks
{
    public static object? LastResult { get; private set; }
    sealed class BlockedUiContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }

    public static async Task<int> RunAsync()
    {
        await CheckReceiveGapDiagnostics();
        await CheckThreadPoolStarvation();
        await CheckPersistentSubscription();
        await using var device = new LoopbackDevice();
        using var releaseUi = new ManualResetEventSlim();
        var context = new BlockedUiContext();
        var ready = new TaskCompletionSource<AudioReceiver>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new AudioBuffer();
        int callbackCount = 0, wrongThread = 0, uiThreadId = 0;
        var uiThread = new Thread(() =>
        {
            uiThreadId = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(context);
            ControlSession? control = null;
            AudioReceiver? receiver = null;
            try
            {
                control = new ControlSession();
                control.ConnectAsync("127.0.0.1", device.ControlPort)
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                receiver = new AudioReceiver(control, "127.0.0.1", 0);
                receiver.Packet += packet =>
                {
                    if (SynchronizationContext.Current is not null || Environment.CurrentManagedThreadId == uiThreadId)
                        Interlocked.Exchange(ref wrongThread, 1);
                    buffer.Add(packet); // Same thread-safe subscriber as the real Avalonia application.
                    Interlocked.Increment(ref callbackCount);
                };
                receiver.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                ready.TrySetResult(receiver);
                if (!releaseUi.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test UI release was not signaled.");
                // Deliberately block the caller: transport cancellation/STOP cannot depend on UI pumping.
                receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                receiver = null;
                control.DisposeAsync().GetAwaiter().GetResult();
                control = null;
                finished.TrySetResult();
            }
            catch (Exception error) { ready.TrySetException(error); finished.TrySetException(error); }
            finally
            {
                if (receiver is not null) try { receiver.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
                if (control is not null) control.DisposeAsync().GetAwaiter().GetResult();
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }) { IsBackground = true, Name = "Blocked UI regression" };
        uiThread.Start();
        try
        {
            var receiver = await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (uint sequence = 0; sequence < 3; sequence++) await device.SendAudioAsync(sequence);
            for (int retry = 0; retry < 100 && Volatile.Read(ref callbackCount) < 3; retry++) await Task.Delay(10);
            if (callbackCount != 3 || receiver.Accepted != 3) throw new Exception("UDP stopped while UI context was blocked.");
            if (wrongThread != 0) throw new Exception("Audio callback inherited the UI synchronization context.");
            if (buffer.BufferedMs <= 0) throw new Exception("Thread-safe audio buffer subscriber did not receive PCM.");
            var originalDescriptor = receiver.Descriptor;
            await Task.Delay(70);
            for (int retry = 0; retry < 50 && receiver.ValidStreamGapProbes == 0; retry++) await Task.Delay(5);
            if (receiver.ValidStreamGapProbes == 0 || receiver.Descriptor != originalDescriptor || receiver.FormatRecoveries != 0 || device.PrepareCount != 1)
                throw new Exception("Short network silence unnecessarily renegotiated a valid epoch.");
            await device.SendAudioAsync(3);
            for (int retry = 0; retry < 100 && callbackCount < 4; retry++) await Task.Delay(5);
            if (callbackCount != 4 || buffer.Resyncs != 0 || buffer.Initializations != 1)
                throw new Exception("A delayed contiguous packet discarded the original buffered timeline.");
            device.InvalidateStream();
            var gapWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int retry = 0; retry < 100 && receiver.FastRecoveries == 0; retry++) await Task.Delay(5);
            if (receiver.FastRecoveries == 0 || gapWatch.ElapsedMilliseconds > 400) throw new Exception("Short interruption waited for the one-second keepalive.");
            for (int retry = 0; retry < 100 && receiver.IsRecovering; retry++) await Task.Delay(5);
            if (receiver.IsRecovering) throw new Exception("Fast recovery negotiation did not finish.");
            await device.SendAudioAsync(0);
            for (int retry = 0; retry < 100 && callbackCount < 5; retry++) await Task.Delay(5);
            double fastRecoveryGapMs = receiver.LastRecoveryGapMs;
            if (callbackCount != 5 || fastRecoveryGapMs is <= 0 or >= 400) throw new Exception("First restored audio packet did not arrive promptly.");
            await device.KeepAliveSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            device.SwitchFormat();
            for (int retry = 0; retry < 400 && (receiver.Descriptor?.Format != AudioFormat.Pcm24 || receiver.IsRecovering); retry++) await Task.Delay(10);
            if (receiver.Descriptor?.Format != AudioFormat.Pcm24 || receiver.IsRecovering) throw new Exception("Format generation did not renegotiate.");
            for (uint sequence = 0; sequence < 3; sequence++) await device.SendAudioAsync(sequence);
            for (int retry = 0; retry < 100 && Volatile.Read(ref callbackCount) < 8; retry++) await Task.Delay(10);
            if (callbackCount != 8 || buffer.SourceFormat != AudioFormat.Pcm24 || receiver.FormatRecoveries < 2)
                throw new Exception("96k/24-bit recovery lost the existing audio subscriber.");
            releaseUi.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(6));
            await device.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!uiThread.Join(TimeSpan.FromSeconds(2))) throw new Exception("Blocked UI thread could not finish disposal.");
            if (context.Posts != 0) throw new Exception("Transport posted work to the non-pumping UI context.");
            LastResult = new { scope = "real loopback sockets; UI synchronization context never pumped; no board", received_audio_packets = receiver.Accepted, subscriber_calls = callbackCount, valid_stream_gap_preserved_epoch = true, valid_stream_gap_probes = receiver.ValidStreamGapProbes, fast_recovery_gap_ms = fastRecoveryGapMs, background_callback_context = wrongThread == 0, ui_context_posts = context.Posts, keepalive_received = device.KeepAliveSeen.Task.IsCompletedSuccessfully, stop_received = device.Stopped.Task.IsCompletedSuccessfully, blocked_ui_dispose_completed = true };
            return 26; // recovery, receive gap attribution, persistent timelines and worker-pool starvation
        }
        finally { releaseUi.Set(); uiThread.Join(TimeSpan.FromSeconds(6)); }
    }

    static async Task CheckReceiveGapDiagnostics()
    {
        await using var device=new LoopbackDevice();await using var control=new ControlSession();
        await control.ConnectAsync("127.0.0.1",device.ControlPort);
        await using var receiver=new AudioReceiver(control,"127.0.0.1",0);
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int callbacks=0;
        receiver.Packet+=_=>
        {
            if(Interlocked.Increment(ref callbacks)==1){entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(3)))throw new TimeoutException("Receive diagnostic subscriber not released");}
        };
        await receiver.StartAsync();await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await device.SendAudioAsync(0);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await device.SendAudioAsync(1);await device.SendAudioAsync(2);await Task.Delay(40);release.Set();
            for(int retry=0;retry<100&&Volatile.Read(ref callbacks)<3;retry++)await Task.Delay(5);
            if(callbacks!=3)throw new Exception("Receive diagnostic regression lost contiguous packets");
            var snapshot=JsonSerializer.SerializeToElement(receiver.Statistics());var gaps=snapshot.GetProperty("receive_gap_events");
            if(gaps.GetArrayLength()==0)throw new Exception("A blocked receive subscriber was not captured as a delivery gap");
            var gap=gaps.EnumerateArray().First(x=>x.GetProperty("PreviousProcessingMs").GetDouble()>=25);
            long origin=snapshot.GetProperty("receive_origin_timestamp").GetInt64(),returned=gap.GetProperty("Timestamp").GetInt64(),started=gap.GetProperty("ReceiveStartedTimestamp").GetInt64();
            if(origin<=0||returned<started||started<=origin||snapshot.GetProperty("clock_frequency").GetInt64()!=System.Diagnostics.Stopwatch.Frequency)throw new Exception("Receive diagnostics cannot be aligned to the monotonic kernel timeline");
            if(gap.GetProperty("ReceiveCallMs").GetDouble()<0||gap.GetProperty("BeforeReceiveMs").GetDouble()<0||gap.GetProperty("SocketAvailableBytes").GetInt32()<0)throw new Exception("Receive gap diagnostics omitted the socket wait/backlog observation");
            Console.WriteLine(JsonSerializer.Serialize(new{stage="receive_gap_diagnostics",scope="offline loopback with deliberately blocked packet subscriber; not hardware scheduling evidence",gap,origin_timestamp=origin}));
        }
        finally{release.Set();}
    }

    static Task CheckThreadPoolStarvation()
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(()=>
        {
            LoopbackDevice? device=null;ControlSession? control=null;AudioReceiver? receiver=null;
            using var release=new ManualResetEventSlim();using var occupied=new CountdownEvent(2);
            ThreadPool.GetMinThreads(out int minWorkers,out int minIo);ThreadPool.GetMaxThreads(out int maxWorkers,out int maxIo);
            try
            {
                device=new LoopbackDevice{FollowUsb=true};control=new ControlSession();
                control.ConnectAsync("127.0.0.1",device.ControlPort).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                receiver=new AudioReceiver(control,"127.0.0.1",0);int callbacks=0,pooled=0,worker=0,changed=0;
                receiver.Packet+=_=>{if(Thread.CurrentThread.IsThreadPoolThread)Interlocked.Exchange(ref pooled,1);int id=Environment.CurrentManagedThreadId;if(worker==0)worker=id;else if(worker!=id)changed=1;Interlocked.Increment(ref callbacks);};
                receiver.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                try
                {
                    if(!ThreadPool.SetMinThreads(1,minIo)||!ThreadPool.SetMaxThreads(2,maxIo))throw new Exception("Cannot establish bounded worker-pool regression");
                    for(int n=0;n<2;n++)ThreadPool.QueueUserWorkItem(_=>{occupied.Signal();release.Wait();});
                    if(!occupied.Wait(TimeSpan.FromSeconds(5)))throw new Exception("Worker pool did not reach controlled starvation");
                    for(uint n=0;n<8;n++)device.SendAudioSynchronous(n);
                    if(!SpinWait.SpinUntil(()=>Volatile.Read(ref callbacks)==8,300))throw new Exception("Audio receive stalled behind unrelated worker-pool work");
                    if(pooled!=0||changed!=0||worker==0)throw new Exception("Audio packets did not retain a dedicated receive owner");
                    if(receiver.Invalid!=0||receiver.SequenceGapPackets!=0)throw new Exception("Worker-pool stress damaged packet continuity");
                }
                finally{release.Set();ThreadPool.SetMaxThreads(maxWorkers,maxIo);ThreadPool.SetMinThreads(minWorkers,minIo);}
                receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();receiver=null;
                completion.TrySetResult();
            }
            catch(Exception error){completion.TrySetException(error);}
            finally
            {
                release.Set();ThreadPool.SetMaxThreads(maxWorkers,maxIo);ThreadPool.SetMinThreads(minWorkers,minIo);
                if(receiver is not null)try{receiver.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));}catch{}
                if(control is not null)control.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if(device is not null)device.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }){IsBackground=true,Name="Receive worker starvation regression"}.Start();
        return completion.Task;
    }

    static async Task CheckPersistentSubscription()
    {
        await using var device = new LoopbackDevice { FollowUsb = true };
        await using var control = new ControlSession();
        await control.ConnectAsync("127.0.0.1", device.ControlPort);
        await using var receiver = new AudioReceiver(control,"127.0.0.1",0);
        var packets = new System.Collections.Concurrent.ConcurrentQueue<AudioPacket>();
        receiver.Packet += packets.Enqueue;
        await receiver.StartAsync();
        var original = receiver.Descriptor!;
        if (!original.FollowUsb) throw new Exception("Persistent mode was not negotiated.");
        // The first actual playback starts after idle and selects another format.
        device.SwitchFormat();
        await device.SendAudioAsync(1); // First packet of the generation was lost.
        for(int retry=0;retry<100&&receiver.Accepted<1;retry++)await Task.Delay(5);
        if(receiver.Accepted!=1||receiver.Descriptor?.Format!=AudioFormat.Pcm24||receiver.Descriptor.Token!=original.Token||device.PrepareCount!=1)
            throw new Exception("Idle-to-audio still depends on a control renegotiation.");
        uint oldEpoch=receiver.Descriptor.Epoch;
        device.InvalidateStream();await device.SendAudioAsync(2);
        for(int retry=0;retry<100&&receiver.Accepted<2;retry++)await Task.Delay(5);
        if(receiver.Accepted!=2||receiver.UsbTimelineChanges!=2||receiver.FormatRecoveries!=0||packets.Select(x=>x.TimelineId).Distinct().Count()!=2)
            throw new Exception("Repeated same-format burst reused its old source timeline.");
        await device.SendAudioAsync(0,oldEpoch);
        for(int retry=0;retry<100&&receiver.Invalid<1;retry++)await Task.Delay(5);
        if(receiver.Invalid!=1||receiver.Accepted!=2)throw new Exception("Delayed old USB generation was accepted.");
        await device.SendAudioAsync(3);
        for(int retry=0;retry<100&&receiver.Accepted<3;retry++)await Task.Delay(5);
        if(receiver.Accepted!=3||receiver.Fault is not null)throw new Exception("Stale packet damaged the current subscription.");
    }

    sealed class LoopbackDevice : IAsyncDisposable
    {
        const ulong Boot = 123456789;
        ulong Token = 99887766;
        uint StreamId = 7;
        uint Epoch = 3;
        AudioFormat format = AudioFormat.Pcm16;
        public int PrepareCount;
        public bool FollowUsb;
        public void InvalidateStream() { Epoch++; if(!FollowUsb){Token++; StreamId++;} }
        public void SwitchFormat() { format = AudioFormat.Pcm24; InvalidateStream(); }
        readonly byte[] identity = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly CancellationTokenSource cancellation = new();
        readonly Task controlTask, probeTask;
        TcpClient? accepted;
        IPEndPoint? destination;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource KeepAliveSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ControlPort => ((IPEndPoint)listener.LocalEndpoint).Port;
        int AudioPort => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        public LoopbackDevice()
        {
            listener.Start();
            controlTask = Task.Run(ServeAsync);
            probeTask = Task.Run(async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var probe = await udp.ReceiveAsync(cancellation.Token);
                    destination = probe.RemoteEndPoint;
                    await udp.SendAsync(probe.Buffer, probe.RemoteEndPoint, cancellation.Token);
                }
            });
        }
        async Task ServeAsync()
        {
            accepted = await listener.AcceptTcpClientAsync(cancellation.Token);
            using var stream = accepted.GetStream();
            var hello = await ReadRequest(stream);
            using var helloJson = JsonDocument.Parse(hello.Payload);
            if (helloJson.RootElement.GetProperty("op").GetString() != "HELLO" || helloJson.RootElement.GetProperty("args").GetProperty("connection_mode").GetString() != "open")
                throw new Exception("Expected a direct HELLO without pairing material.");
            await stream.WriteAsync(Response(hello.Id, 0, new
            {
                device_id = Convert.ToHexString(identity).ToLowerInvariant(), boot_id = Boot.ToString(),
                connection_mode = "open", protocol = 1, owner = true
            }), cancellation.Token);
            ulong sequence = 0;
            while (!cancellation.IsCancellationRequested)
            {
                var request = await ReadRequest(stream);
                using var json = JsonDocument.Parse(request.Payload);
                string operation = json.RootElement.GetProperty("op").GetString()!;
                if (operation == "STREAM_PREPARE") Interlocked.Increment(ref PrepareCount);
                if (operation == "STREAM_KEEPALIVE" && json.RootElement.GetProperty("args").GetProperty("stream_id").GetUInt32() != StreamId)
                {
                    await stream.WriteAsync(Wire.Control(2, request.Id, ++sequence, Boot,
                        JsonSerializer.SerializeToUtf8Bytes(new { ok = false, code = "STREAM_NOT_FOUND", message = "Format generation changed", data = new { } })), cancellation.Token);
                    continue;
                }
                object data = operation == "STREAM_PREPARE"
                    ? new { stream_id = StreamId, epoch = Epoch, session_token = Token.ToString(), source_port = AudioPort,
                        format = format.ToRequest(), packet_frames = format.PacketFrames, follow_usb = FollowUsb }
                    : operation == "GET_INFO" ? new { usb = new { format = format.ToRequest(), generation = Epoch }, usb_emulation = new { state = "RUNNING" } }
                    : new { };
                await stream.WriteAsync(Response(request.Id, ++sequence, data), cancellation.Token);
                if (operation == "STREAM_START") Started.TrySetResult();
                if (operation == "STREAM_KEEPALIVE") KeepAliveSeen.TrySetResult();
                if (operation == "STREAM_STOP") { Stopped.TrySetResult(); break; }
            }
        }
        byte[] Response(uint id, ulong sequence, object data) => Wire.Control(2, id, sequence, Boot,
            JsonSerializer.SerializeToUtf8Bytes(new { ok = true, code = "OK", message = "", data, config_revision = "1" }));
        async Task<(uint Id, byte[] Payload, byte[] Raw)> ReadRequest(NetworkStream stream)
        {
            byte[] header = new byte[32]; await stream.ReadExactlyAsync(header, cancellation.Token);
            int length = checked((int)Wire.Read32(header, 8));
            if (length > 8192) throw new InvalidDataException("Test frame too large.");
            if (Wire.Read16(header, 6) != 0) throw new Exception("Direct requests must not carry pairing tags.");
            byte[] raw = new byte[32 + length]; header.CopyTo(raw, 0);
            await stream.ReadExactlyAsync(raw.AsMemory(32), cancellation.Token);
            return (Wire.Read32(header, 12), raw.AsSpan(32, length).ToArray(), raw);
        }
        public async Task SendAudioAsync(uint sequence, uint? epoch = null)
        {await udp.SendAsync(AudioBytes(sequence,epoch),destination??throw new InvalidOperationException("PROBE missing."),cancellation.Token);}
        public void SendAudioSynchronous(uint sequence)
        {byte[] packet=AudioBytes(sequence,null);udp.Send(packet,packet.Length,destination??throw new InvalidOperationException("PROBE missing."));}
        byte[] AudioBytes(uint sequence,uint? epoch)
        {
            int frames = format.PacketFrames;
            byte[] packet = new byte[48 + frames * format.BytesPerFrame]; "UABA"u8.CopyTo(packet);
            packet[4] = 1; packet[5] = 48;
            Wire.Write32(packet, 8, StreamId); Wire.Write32(packet, 12, epoch ?? Epoch); Wire.Write32(packet, 16, sequence);
            Wire.Write64(packet, 20, (ulong)sequence * (uint)frames); Wire.Write32(packet, 28, (uint)format.SampleRate); Wire.Write16(packet, 32, (ushort)frames);
            packet[34] = 2; packet[35] = format.Code; Wire.Write16(packet, 36, (ushort)(frames * format.BytesPerFrame)); Wire.Write64(packet, 40, Token);
            for (int offset = 48; offset < packet.Length; offset += format.BytesPerFrame)
            {
                if (format.Bits == 16) { BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(offset), 1000); BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(offset + 2), -1000); }
                else { packet[offset]=0x00;packet[offset+1]=0xe8;packet[offset+2]=0x03;packet[offset+3]=0x00;packet[offset+4]=0x18;packet[offset+5]=0xfc; }
            }
            return packet;
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel(); listener.Stop(); udp.Dispose(); accepted?.Dispose();
            try { await Task.WhenAll(controlTask, probeTask); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
            cancellation.Dispose();
        }
    }
}

