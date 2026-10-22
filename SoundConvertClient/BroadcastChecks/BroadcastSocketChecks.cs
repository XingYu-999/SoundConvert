using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SoundConvertClient;
using UsbAudioBridge;

static class BroadcastSocketChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var adapter=NetworkAdapterInfo.GetAll().First(a=>a.IsUp&&a.HasIpv4);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server=new UdpClient(new IPEndPoint(IPAddress.Any,UdpBroadcastChannel.DiscoveryPort));server.EnableBroadcast=true;
        int index=NetworkInterface.GetAllNetworkInterfaces().Single(n=>n.Id==adapter.Id).GetIPProperties().GetIPv4Properties().Index;
        if(OperatingSystem.IsWindows())server.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(index));
        const string identity="fedcba9876543210fedcba9876543210";
        byte[] challenge=new byte[16],transaction=Enumerable.Repeat((byte)42,16).ToArray();ulong revision=7;byte stage=0;int applies=0,requests=0;
        var cache=new Dictionary<ulong,byte[]>();
        byte[] Number(ulong n){byte[] b=new byte[8];Wire.Write64(b,0,n);return b;}
        async Task Serve()
        {
            try { while(true)
            {
                var packet=await server.ReceiveAsync(stop.Token);var data=packet.Buffer;
                if(data.Length<62 || !data.AsSpan(0,4).SequenceEqual("UABD"u8) || data[5] is not (1 or 3 or 4 or 5 or 6))continue;
                bool writing=data[5]!=1;
                if(writing&&!data.AsSpan(20,16).SequenceEqual(Convert.FromHexString(identity)))continue;
                check(packet.RemoteEndPoint.Address.ToString()==adapter.Address,"broadcast request uses exact selected source IPv4");
                check(Wire.Read16(data,56)==16 && Wire.Read16(data,58)==2,"request carries a separate broadcast reply port");
                ulong id=Wire.Read64(data,12);int port=Wire.Read16(data,60);++requests;
                if(cache.TryGetValue(id,out var cached)) { await server.SendAsync(cached,new IPEndPoint(IPAddress.Broadcast,port),stop.Token);continue; }
                if(writing)
                {
                    check(data.AsSpan(36,16).SequenceEqual(challenge),"configuration uses the latest challenge");
                    if(data[5]==3)stage=1;
                    if(data[5]==4){stage=2;++applies;}
                    if(data[5]==5){stage=3;++revision;}
                    if(data[5]==6)stage=4;
                }
                challenge[0]++;
                string address=stage>=2?"192.0.2.22":"0.0.0.0";
                var device=new Device(identity,"UDP socket fixture","test",address,49161,adapter.Address,"020102030405");
                var peer=new UdpPeer(device,adapter.Id,adapter.Address,challenge,42,revision,DateTimeOffset.UtcNow);
                var fields=new SortedDictionary<ushort,byte[]>{[1]="UDP socket fixture"u8.ToArray(),[2]="test"u8.ToArray(),[3]=IPAddress.Parse(address).GetAddressBytes(),[4]=[24],[5]=[0,0,0,0],[6]=[(byte)(stage>=2?3:0)],[7]=[0xbf,0xfd],[8]=Number(42),[9]=Number(revision),[10]=Convert.FromHexString(device.Mac),[11]=[0,0],[12]=System.Text.Encoding.UTF8.GetBytes(stage==3?"COMMITTED":"OK"),[13]=[(byte)(stage>=2?5:2)],[14]=[(byte)(stage>=2?2:1)],[15]=[stage]};
                if(stage!=0)fields[0x8011]=transaction;
                byte[] response=UdpDiscovery.BuildRequest(peer,3,id,fields);response[5]=(byte)(writing?7:2);Wire.Write16(response,10,1);
                // A malformed/wrong-device broadcast on the same NIC must not satisfy the exchange.
                byte[] wrong=response.ToArray();wrong[20]^=1;
                if(writing)await server.SendAsync(wrong,new IPEndPoint(IPAddress.Broadcast,port),stop.Token);
                if(writing)cache[id]=response;
                if(data[5]==4)continue; // lose the first APPLY ACK; retry the identical request from cache.
                await server.SendAsync(response,new IPEndPoint(IPAddress.Broadcast,port),stop.Token);
            } }
            catch(OperationCanceledException)when(stop.IsCancellationRequested) { }
        }
        var serving=Serve();
        try
        {
            using(var canceled=new CancellationTokenSource(50))
            {
                try { await UdpDiscovery.DiscoverPeersAsync(canceled.Token,adapter.Address);throw new Exception("cancellation was swallowed"); }
                catch(OperationCanceledException){check(true,"search cancellation propagates from the live broadcast socket");}
            }
            var peer=(await UdpDiscovery.DiscoverPeersAsync(stop.Token,adapter.Address)).Single(p=>p.Device.DeviceId==identity);
            check(peer.Device.Address=="0.0.0.0","broadcast discovers an advertised unconfigured device without TCP");
            var prepared=await UdpDiscovery.PrepareNetworkAsync(peer,false,"192.0.2.22",24,"0.0.0.0",ct:stop.Token);
            var applied=await UdpDiscovery.ApplyAsync(prepared.Peer,prepared.TransactionId!,stop.Token);
            check(applied.Status==0 && applies==1,"lost APPLY acknowledgment retries one request identity without repeating its write");
            var candidate=(await UdpDiscovery.DiscoverPeersAsync(stop.Token,adapter.Address)).Single(p=>p.Device.DeviceId==identity);
            check(candidate.Device.Address=="192.0.2.22" && candidate.TransactionStage==2,"candidate on another advertised subnet remains discoverable by broadcast");
            var confirmed=await UdpDiscovery.ConfirmAsync(candidate,transaction,stop.Token);
            check(confirmed.Reason=="COMMITTED" && confirmed.Peer.ConfigRevision==8,"broadcast confirmation validates transaction and committed revision");
            check(requests>=8,"real UDP broadcast requests and replies exercised");
        }
        finally { stop.Cancel();await serving; }
    }
}
