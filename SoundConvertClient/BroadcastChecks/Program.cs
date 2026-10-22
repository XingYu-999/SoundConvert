using SoundConvertClient;
using UsbAudioBridge;

int checks = 0;
void Check(bool value, string name) { ++checks; if (!value) throw new Exception(name); }
void Reject(Action action, string name)
{
    ++checks;
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception(name);
}
byte[] local = [2, 1, 2, 3, 4, 5], remote = [2, 6, 7, 8, 9, 10];
var device = new Device("0123456789abcdef0123456789abcdef", "board", "test", "0.0.0.0", 49161, "192.0.2.1", Convert.ToHexString(remote));
var peer = new UdpPeer(device, "test", "192.0.2.1", new byte[16], 42, 7, DateTimeOffset.UtcNow);
byte[] Number(ulong number) { byte[] bytes = new byte[8]; Wire.Write64(bytes, 0, number); return bytes; }
var fields = new SortedDictionary<ushort, byte[]> { [0x8010] = Number(7), [0x8012] = [2], [0x8013] = [192, 168, 1, 20], [0x8014] = [24], [0x8017] = Number(42) };
byte[] request = UdpDiscovery.BuildRequest(peer, 3, 123, fields);
Check(request.AsSpan(0, 4).SequenceEqual("UABD"u8) && request[5] == 3 && Wire.Read16(request, 52) == 0, "Direct connection header, no pairing material");
Check(Wire.Read16(request, 8) == request.Length && Wire.Read64(request, 12) == 123, "Final message length");
Check(Wire.Read16(request, 56) == 0x8010 && Wire.Read64(request, 60) == 7, "Ascending critical TLVs, network byte order");
var resultFields = new SortedDictionary<ushort, byte[]> { [8] = Number(42), [9] = Number(8), [11] = [0, 0], [12] = "COMMITTED"u8.ToArray(), [0x8011] = new byte[16] };
byte[] response = UdpDiscovery.BuildRequest(peer, 3, 123, resultFields);
response[5] = 7; Wire.Write16(response, 10, 1); response[36] = 1;
var result = UdpDiscovery.ParseResult(response, 123, peer);
Check(result.Status == 0 && result.Reason == "COMMITTED" && result.Peer.ConfigRevision == 8 && result.Peer.Challenge[0] == 1, "Direct response updates revision/challenge");
Check(result.TransactionId?.Length == 16, "Configuration transaction ID returned");
var discoveryFields = new SortedDictionary<ushort, byte[]>
{
    [1]="board"u8.ToArray(), [2]="V0.0.1"u8.ToArray(), [3]=[192,0,2,22], [4]=[24], [5]=[0,0,0,0],
    [6]=[3], [8]=Number(42), [9]=Number(8), [10]=remote, [13]=[5], [14]=[2], [15]=[3], [0x8011]=new byte[16]
};
byte[] discovery=UdpDiscovery.BuildRequest(peer,3,456,discoveryFields);discovery[5]=2;Wire.Write16(discovery,10,1);
var discovered=DiscoveryCodec.Parse(discovery,456,"192.0.2.1");
Check(discovered.Firmware=="V0.0.1"&&discovered.AddressMode=="static"&&discovered.PrefixLength==24&&discovered.NetworkState==5,"discovery reports firmware and static candidate metadata");
discoveryFields[6]=[0];discoveryFields[13]=[1];discoveryFields[14]=[1];
discovery=UdpDiscovery.BuildRequest(peer,3,456,discoveryFields);discovery[5]=2;Wire.Write16(discovery,10,1);
Check(DiscoveryCodec.Parse(discovery,456,"192.0.2.1").AddressMode=="dhcp","configured DHCP survives no-address state");
var stagedFields=new SortedDictionary<ushort,byte[]>(resultFields){[15]=[3]};
byte[] staged=UdpDiscovery.BuildRequest(peer,3,123,stagedFields);staged[5]=7;Wire.Write16(staged,10,1);
Check(UdpDiscovery.ParseResult(staged,123,peer).Peer.TransactionStage==3,"committed transaction exposed for lost acknowledgment recovery");
byte[] altered;
altered = response.ToArray(); Wire.Write16(altered, 52, 32);
Reject(() => UdpDiscovery.ParseResult(altered, 123, peer), "Reject legacy pairing-tag header");
Reject(() => UdpDiscovery.ParseResult(response, 124, peer), "Reject replayed request ID");
altered = response.ToArray(); altered[20] ^= 1;
Reject(() => UdpDiscovery.ParseResult(altered, 123, peer), "Reject wrong exact device");
altered = response.ToArray(); Wire.Write64(altered, 60, 43);
Reject(() => UdpDiscovery.ParseResult(altered, 123, peer), "Reject boot change");
altered = response.ToArray(); Wire.Write16(altered, 56, 0x9000);
Reject(() => UdpDiscovery.ParseResult(altered, 123, peer), "Reject unknown critical TLV");
altered = response.ToArray(); Wire.Write16(altered, 68, 8);
Reject(() => UdpDiscovery.ParseResult(altered, 123, peer), "Reject duplicate field");
var broadcastPeer=UdpDiscovery.ParsePeer(discovery,456,"test","192.0.2.1");
Check(broadcastPeer.Device.LocalAddress=="192.0.2.1" && broadcastPeer.BootId==42,"UDP reply uses selected NIC path and validates board identity");
await BroadcastSocketChecks.Run(Check);
Console.WriteLine($"PASS: {checks} UDP codec and live socket fixture checks; no physical board or source-IP 0.0.0.0 acceptance.");
