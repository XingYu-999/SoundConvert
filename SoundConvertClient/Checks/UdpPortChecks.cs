using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace UsbAudioBridge.Checks;

static class UdpPortChecks
{
    public static async Task<int> RunAsync()
    {
        int count=0;void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
        Check(UdpPortSettings.Parse(" 自动协商 ")==0&&UdpPortSettings.Parse("65535")==65535,"automatic/explicit UDP policy");
        foreach(string text in new[]{"","0","1023","65536","-1","49162.5","4e4"})
        {try{UdpPortSettings.Parse(text);throw new Exception("invalid port accepted");}catch(ArgumentException){count++;}}
        foreach(var p in new[]{new UdpPortSettings(49160),new UdpPortSettings(BoardCapture:49161),new UdpPortSettings(51000,0,51000),new UdpPortSettings(0,51000,0,51000)})
        {try{p.Validate();throw new Exception("conflicting ports accepted");}catch(ArgumentException){count++;}}
        new UdpPortSettings(51000,51000,51001,51001).Validate();count++;
        var history=new UdpPortHistory(53000,53001,53002,53003);
        Check(history.RememberPlayback(new(54000,54001),54000,54001)==history,"manual negotiation never overwrites automatic history");
        Check(history.RememberPlayback(new(SoftwarePlayback:54001),55000,54001)==(history with{BoardPlayback=55000}),"mixed policy remembers only the automatically allocated endpoint");
        Check(new UdpPortHistory(-1,65536,0,65535).Normalize()==new UdpPortHistory(SoftwareCapture:65535),"corrupt historical port values are discarded individually");
        Check(new UdpPortHistory(53000,53001,53000,53001).Normalize()==new UdpPortHistory(53000,53001,53000,53001),"observations from separate sessions may share port numbers");
        using var blocker=Bind(0);int occupied=((IPEndPoint)blocker.Client.LocalEndPoint!).Port;
        await using(var control=new ControlSession())
        {
            try{await using var r=new AudioReceiver(control,"127.0.0.1",occupied);throw new Exception("occupied playback port reused");}catch(SocketException){count++;}
            try{await using var s=new MicrophoneSender(control,48000,16,"127.0.0.1",occupied);throw new Exception("occupied microphone port reused");}catch(SocketException){count++;}
        }
        foreach(bool capture in new[]{false,true})foreach(bool manual in new[]{false,true})
        {
            int boardPort=manual?FreePort():0,localPort=manual?FreePort():0;
            await using var board=new PortDevice();await using var control=new ControlSession();await control.ConnectAsync("127.0.0.1",board.Port);
            if(capture)
            {
                await using var sender=new MicrophoneSender(control,48000,16,"127.0.0.1",localPort,boardPort:boardPort);await sender.StartAsync();
                Check(sender.Descriptor!.DestinationPort==board.AudioPort&&sender.LocalPort==board.ClientPort&&board.ProbePort==sender.LocalPort,"capture honors selected ports and probes actual endpoint");
                Check(board.RequestedPort==boardPort&&(!manual||board.AudioPort==boardPort&&sender.LocalPort==localPort),"capture automatic/manual port request confirmed");
                var observed=history.RememberCapture(new(BoardCapture:boardPort,SoftwareCapture:localPort),sender.Descriptor.DestinationPort,sender.LocalPort);
                Check(manual?observed==history:observed.BoardCapture==board.AudioPort&&observed.SoftwareCapture==sender.LocalPort,"capture history uses successfully negotiated socket endpoints only in automatic mode");
            }
            else
            {
                await using var receiver=new AudioReceiver(control,"127.0.0.1",localPort,boardPort:boardPort);await receiver.StartAsync();
                Check(receiver.Descriptor!.SourcePort==board.AudioPort&&receiver.Descriptor.ReceivePort==board.ClientPort&&board.ProbePort==board.ClientPort,"playback honors selected ports and probes actual endpoint");
                Check(board.RequestedPort==boardPort&&(!manual||board.AudioPort==boardPort&&board.ClientPort==localPort),"playback automatic/manual port request confirmed");
                var observed=history.RememberPlayback(new(BoardPlayback:boardPort,SoftwarePlayback:localPort),receiver.Descriptor.SourcePort,receiver.Descriptor.ReceivePort);
                Check(manual?observed==history:observed.BoardPlayback==board.AudioPort&&observed.SoftwarePlayback==board.ClientPort,"playback history uses successfully negotiated socket endpoints only in automatic mode");
            }
        }
        await using(var board=new PortDevice{IgnoreManual=true})
        await using(var control=new ControlSession())
        {
            await control.ConnectAsync("127.0.0.1",board.Port);int requested=FreePort();
            await using var receiver=new AudioReceiver(control,"127.0.0.1",0,boardPort:requested);
            try{await receiver.StartAsync();throw new Exception("ignored manual port accepted");}catch(NotSupportedException){count++;}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{stage="udp_port_checks",checks=count,scope="Real loopback TCP/UDP automatic/manual endpoint negotiation, probe and occupied-port rejection; not W5500 acceptance"}));return count;
    }
    static UdpClient Bind(int port){var u=new UdpClient(AddressFamily.InterNetwork);u.Client.ExclusiveAddressUse=true;u.Client.Bind(new IPEndPoint(IPAddress.Loopback,port));return u;}
    static int FreePort(){using var u=Bind(0);return ((IPEndPoint)u.Client.LocalEndPoint!).Port;}
    sealed class PortDevice:IAsyncDisposable
    {
        readonly TcpListener tcp=new(IPAddress.Loopback,0);readonly CancellationTokenSource stop=new();readonly Task server;
        UdpClient? udp;Task? probe;public bool IgnoreManual;public int RequestedPort,ClientPort,ProbePort;
        public int Port=>((IPEndPoint)tcp.LocalEndpoint).Port;public int AudioPort=>((IPEndPoint)udp!.Client.LocalEndPoint!).Port;
        public PortDevice(){tcp.Start();server=Serve();}
        async Task EchoProbe(){var p=await udp!.ReceiveAsync(stop.Token);ProbePort=p.RemoteEndPoint.Port;await udp.SendAsync(p.Buffer,p.RemoteEndPoint,stop.Token);}
        async Task Serve()
        {
            try
            {
                using var client=await tcp.AcceptTcpClientAsync(stop.Token);var stream=client.GetStream();ulong sequence=0;
                while(!stop.IsCancellationRequested)
                {
                    var frame=await Wire.ReadAsync(stream,stop.Token);using var doc=JsonDocument.Parse(frame.Payload);var req=doc.RootElement;string op=req.GetProperty("op").GetString()!;object data;
                    if(op=="HELLO")data=new{device_id="0123456789abcdef0123456789abcdef",boot_id="77",protocol=1,connection_mode="open",owner=true};
                    else if(op=="GET_INFO")data=new{usb_emulation=new{state="RUNNING"},usb=new{generation=1,format=AudioFormat.Pcm16.ToRequest()},capture=new{active=true,generation=1,format=new CaptureFormat(48000,16).ToRequest()}};
                    else if(op is "STREAM_PREPARE" or "CAPTURE_PREPARE")
                    {
                        var args=req.GetProperty("args");RequestedPort=args.GetProperty("source_port").GetInt32();ClientPort=args.GetProperty("receive_port").GetInt32();
                        udp=Bind(IgnoreManual?0:RequestedPort);if(IgnoreManual&&AudioPort==RequestedPort){udp.Dispose();udp=Bind(0);}probe=EchoProbe();
                        data=new{stream_id=71,epoch=1,session_token="99",source_port=AudioPort,packet_frames=48,format=(object)(op=="STREAM_PREPARE"?AudioFormat.Pcm16.ToRequest():new CaptureFormat(48000,16).ToRequest()),host_active=true,waiting_usb=true,follow_usb=true,fec_mode="none",codec="pcm"};
                    }
                    else data=new{active=true,generation=1,stream_id=71,epoch=1};
                    await stream.WriteAsync(Wire.Control(2,frame.RequestId,op=="HELLO"?0:++sequence,77,JsonSerializer.SerializeToUtf8Bytes(new{ok=true,code="OK",message="fixture",data})),stop.Token);
                }
            }
            catch(OperationCanceledException){}catch(EndOfStreamException){}catch(ObjectDisposedException){}
        }
        public async ValueTask DisposeAsync(){stop.Cancel();tcp.Stop();udp?.Dispose();await server;try{if(probe is not null)await probe;}catch(OperationCanceledException){}catch(ObjectDisposedException){}stop.Dispose();}
    }
}
