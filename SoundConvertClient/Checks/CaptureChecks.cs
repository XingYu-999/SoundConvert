using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace UsbAudioBridge.Checks;
static class CaptureChecks
{
    public static async Task<int> RunAsync()
    {
        int count=0;void Check(bool x,string why){count++;if(!x)throw new Exception(why);}
        Check(CaptureFormat.Supported.Count==9&&CaptureFormat.DefaultMask==1,"mono format catalog/default");
        Check(CaptureFormat.FromSource(48000,16,1)==31&&CaptureFormat.FromSource(16000,16,1)==6,"source maximum intersects rate and real precision");
        Check(CaptureFormat.FromSource(48000,24,2)==223&&CaptureFormat.FromSource(48000,24,0)==0,"24bit source capability with downmix and missing channels");
        foreach(var f in CaptureFormat.Supported)
        {
            var d=new CaptureDescriptor(71,1,99,49163,f,true);var raw=CaptureCodec.Encode(d,7,100,new byte[f.PacketFrames*f.BytesPerFrame],f.PacketFrames);
            Check(raw.AsSpan(0,4).SequenceEqual("UABM"u8)&&raw[34]==1&&raw.Length==48+f.PacketFrames*f.BytesPerFrame,"separate wire magic and mono range");
            Check(Wire.Read64(raw,20)==100&&Wire.Read16(raw,32)==f.PacketFrames&&Wire.Read32(raw,28)==f.SampleRate&&Wire.Read64(raw,40)==99,"microphone identity/timeline/format");
            Check(CaptureCodec.Encode(d,0,0,[],0,true).Length==48,"independent source-port probe");
            try{CaptureCodec.Encode(d,0,ulong.MaxValue,new byte[f.BytesPerFrame],1);throw new Exception("overflow accepted");}catch(InvalidDataException){count++;}
        }
        foreach(var f in CaptureFormat.Supported)
        {
            await using var board=new CaptureDevice(f);await using var control=new ControlSession();await control.ConnectAsync("127.0.0.1",board.Port);
            using var outputSocket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            await using var sender=new MicrophoneSender(control,Math.Max(48000,f.SampleRate),24,"127.0.0.1",0);
            await sender.StartAsync();sender.Push(Enumerable.Repeat(.25f,Math.Max(48000,f.SampleRate)*3/100).ToArray());
            await Wait(()=>board.Packets.Count>=8);
            Check(sender.LocalPort!=((IPEndPoint)outputSocket.Client.LocalEndPoint!).Port&&board.Priority=="playback_first","microphone owns separate socket and defaults below playback");
            var snapshot=JsonSerializer.SerializeToElement(sender.Statistics());Check(snapshot.GetProperty("worker_thread").GetInt32()!=Environment.CurrentManagedThreadId&&snapshot.GetProperty("worker_alive").GetBoolean(),"dedicated microphone worker off caller/UI");
            var packets=board.Packets.ToArray().OrderBy(x=>Wire.Read32(x,16)).ToArray();ulong next=0;foreach(var p in packets){Check(Wire.Read64(p,20)==next&&p[34]==1&&Wire.Read16(p,36)==p.Length-48,"source-time continuity across fractional/rate conversion");next+=Wire.Read16(p,32);}
            sender.Mute=true;int before=board.Packets.Count;sender.Push(Enumerable.Repeat(.25f,4800).ToArray());await Wait(()=>board.Packets.Count>=before+12);
            Check(board.Packets.ToArray().TakeLast(3).All(p=>!p.AsSpan(48).ContainsAnyExcept((byte)0)),"send mute zeros only microphone samples");
            await sender.DisposeAsync();Check(board.Stopped,"independent capture stop leaves control session alive");
        }
        await using(var board=new CaptureDevice(new CaptureFormat(48000,16)){DelayKeepalive=true})
        {await using var control=new ControlSession();await control.ConnectAsync("127.0.0.1",board.Port);await using var sender=new MicrophoneSender(control,48000,16,"127.0.0.1",0);await sender.StartAsync();await board.KeepaliveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));await sender.DisposeAsync();await control.RequestAsync("GET_INFO");Check(control.IsConnected&&board.Stopped,"stop during in-flight microphone control request preserves shared playback control connection");}
        await using(var board=new CaptureDevice(new CaptureFormat(96000,24)))
        {await using var control=new ControlSession();await control.ConnectAsync("127.0.0.1",board.Port);await using var sender=new MicrophoneSender(control,48000,16,"127.0.0.1",0);try{await sender.StartAsync();throw new Exception("source limit bypassed");}catch(NotSupportedException){Check(board.Prepares==0,"overspec USB microphone rejected before prepare/send");}}
        Console.WriteLine(JsonSerializer.Serialize(new{stage="capture_checks",checks=count,scope="offline format/wire, real loopback UDP dedicated sender, fractional SRC, mute, source limit and independent stop"}));return count;
    }
    static async Task Wait(Func<bool> condition){var start=System.Diagnostics.Stopwatch.StartNew();while(!condition()){if(start.ElapsedMilliseconds>3000)throw new TimeoutException("Microphone loopback stalled");await Task.Delay(5);}}
    internal sealed class CaptureDevice:IAsyncDisposable
    {
        readonly TcpListener tcp=new(IPAddress.Loopback,0);readonly UdpClient udp=new(new IPEndPoint(IPAddress.Loopback,0));readonly CancellationTokenSource stop=new();readonly Task server,receive;readonly CaptureFormat format;
        public readonly ConcurrentQueue<byte[]> Packets=new();public string Priority="";public bool Stopped,DelayKeepalive;public TaskCompletionSource KeepaliveStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);public int Prepares,PrepareDelayMs;int generation=1;public uint Generation{get=>(uint)Volatile.Read(ref generation);set=>Volatile.Write(ref generation,(int)value);}public int Port=>((IPEndPoint)tcp.LocalEndpoint).Port;
        public CaptureDevice(CaptureFormat f){format=f;tcp.Start();server=Serve();receive=Receive();}
        async Task Receive(){try{while(!stop.IsCancellationRequested){var r=await udp.ReceiveAsync(stop.Token);if(Wire.Read16(r.Buffer,6)==8)await udp.SendAsync(r.Buffer,r.RemoteEndPoint);else Packets.Enqueue(r.Buffer);}}catch(OperationCanceledException){}catch(ObjectDisposedException){}}
        async Task Serve()
        {
            try{using var client=await tcp.AcceptTcpClientAsync(stop.Token);var stream=client.GetStream();ulong seq=0;while(!stop.IsCancellationRequested){var frame=await Wire.ReadAsync(stream,stop.Token);using var doc=JsonDocument.Parse(frame.Payload);var req=doc.RootElement;string op=req.GetProperty("op").GetString()!;object data;
                if(op=="HELLO")data=new{device_id="0123456789abcdef0123456789abcdef",boot_id="77",protocol=1,connection_mode="open",owner=true};
                else if(op=="GET_INFO")data=new{capture=new{active=true,generation=Generation,format=format.ToRequest()}};
                else if(op=="CAPTURE_PREPARE"){Prepares++;if(Prepares>1&&PrepareDelayMs>0)await Task.Delay(PrepareDelayMs);Priority=req.GetProperty("args").GetProperty("priority").GetString()!;data=new{stream_id=71,epoch=Generation,session_token="99",source_port=((IPEndPoint)udp.Client.LocalEndPoint!).Port,format=format.ToRequest(),host_active=true};}
                else if(op=="CAPTURE_KEEPALIVE"){KeepaliveStarted.TrySetResult();if(DelayKeepalive)await Task.Delay(180);data=new{active=true,generation=Generation};}
                else if(op=="CAPTURE_STOP"){Stopped=true;data=new{};}else data=new{active=true,generation=1};
                await stream.WriteAsync(Wire.Control(2,frame.RequestId,op=="HELLO"?0:++seq,77,JsonSerializer.SerializeToUtf8Bytes(new{ok=true,code="OK",message="fixture",data})),stop.Token);
            }}catch(OperationCanceledException){}catch(EndOfStreamException){}catch(ObjectDisposedException){}
        }
        public async ValueTask DisposeAsync(){stop.Cancel();tcp.Stop();udp.Dispose();await Task.WhenAll(server,receive);stop.Dispose();}
    }
}
