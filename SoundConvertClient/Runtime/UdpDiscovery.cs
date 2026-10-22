using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed record UdpPeer(Device Device, string AdapterName, string LocalIpv4, byte[] Challenge,
    ulong BootId, ulong ConfigRevision, DateTimeOffset SeenUtc, string TransactionId = "", byte TransactionStage = 0);
public sealed record UdpResult(ushort Status, string Reason, byte[]? TransactionId, UdpPeer Peer);

/// <summary>Driver-free UABD discovery and network transactions over UDP limited broadcast.</summary>
public static class UdpDiscovery
{
    public static bool SameAdapter(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    public static bool CanReuseForConfiguration(UdpPeer peer, Device selection, string adapterId, DateTimeOffset now) =>
        now >= peer.SeenUtc && now - peer.SeenUtc <= TimeSpan.FromSeconds(10)
        && peer.Device.DeviceId == selection.DeviceId && SameAdapter(peer.AdapterName, adapterId)
        && peer.LocalIpv4 == selection.LocalAddress && peer.Device.Address == selection.Address
        && peer.Device.ControlPort == selection.ControlPort
        && string.Equals(peer.Device.Mac, selection.Mac, StringComparison.OrdinalIgnoreCase) && selection.Mac.Length == 12;

    public static async Task<IReadOnlyList<UdpPeer>> DiscoverPeersAsync(CancellationToken ct = default, string? adapterId = null)
    {
        var adapters = NetworkAdapterInfo.GetAll().Where(a => a.IsUp && a.HasIpv4 && (adapterId is null || SameAdapter(a.Id, adapterId) || a.Address == adapterId)).ToArray();
        if (adapterId is not null && adapters.Length != 1) throw new IOException("原搜索网卡或 IPv4 已失效，请重新搜索。");
        return (await Task.WhenAll(adapters.Select(a => DiscoverAdapterAsync(a, ct)))).SelectMany(p => p).ToArray();
    }
    static async Task<IReadOnlyList<UdpPeer>> DiscoverAdapterAsync(NetworkAdapterInfo adapter, CancellationToken ct)
    {
        using var channel = new UdpBroadcastChannel(adapter.Address);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(1800);
        ulong id = RandomId(); byte[] request = channel.PrepareRequest(DiscoveryCodec.Request(id));
        var peers = new Dictionary<string, UdpPeer>();
        async Task Send() { for (int i = 0; i < 3; ++i) { await channel.SendAsync(request, stop.Token); await Task.Delay(500, stop.Token); } }
        var sending = Send();
        try
        {
            while (true)
            {
                byte[] data = await channel.ReceiveAsync(stop.Token);
                try { var peer = ParsePeer(data, id, adapter.Id, adapter.Address); peers[peer.Device.DeviceId] = peer; }
                catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or FormatException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally { stop.Cancel(); try { await sending; } catch (OperationCanceledException) { } }
        ct.ThrowIfCancellationRequested(); return peers.Values.ToArray();
    }
    internal static UdpPeer ParsePeer(byte[] data, ulong id, string adapterId, string local)
    {
        ValidateHeader(data, id, 2, 0); var device = DiscoveryCodec.Parse(data, id, local); var fields = ParseFields(data, 0);
        _ = Required(fields, 10, 6);
        return WithTransaction(new(device, adapterId, local, data.AsSpan(36,16).ToArray(), Read64(Required(fields,8,8)), Read64(Required(fields,9,8)), DateTimeOffset.UtcNow), fields);
    }
    public static Task<UdpResult> PrepareNetworkAsync(UdpPeer peer, bool dhcp, string? address = null, int prefix = 24, string? gateway = null, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        if (timeoutSeconds is < 30 or > 120) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        var fields = new SortedDictionary<ushort, byte[]> { [0x8012] = [(byte)(dhcp ? 1 : 2)] };
        if (!dhcp) { if (prefix is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(prefix)); fields[0x8013] = Ipv4(address!); fields[0x8014] = [(byte)prefix]; fields[0x8015] = Ipv4(gateway ?? "0.0.0.0"); }
        byte[] timeout = new byte[2]; Wire.Write16(timeout, 0, (ushort)timeoutSeconds); fields[0x8016] = timeout;
        return ExchangeAsync(peer, 3, fields, ct);
    }
    public static Task<UdpResult> ApplyAsync(UdpPeer p, byte[] t, CancellationToken ct = default) => TransactionAsync(p,t,4,ct);
    public static Task<UdpResult> ConfirmAsync(UdpPeer p, byte[] t, CancellationToken ct = default) => TransactionAsync(p,t,5,ct);
    public static Task<UdpResult> RollbackAsync(UdpPeer p, byte[] t, CancellationToken ct = default) => TransactionAsync(p,t,6,ct);
    static Task<UdpResult> TransactionAsync(UdpPeer p, byte[] t, byte operation, CancellationToken ct)
    {
        if(t.Length != 16) throw new ArgumentException("A 16-byte transaction is required.");
        return ExchangeAsync(p, operation, new() { [0x8011] = t.ToArray() }, ct);
    }
    static async Task<UdpResult> ExchangeAsync(UdpPeer peer, byte operation, SortedDictionary<ushort,byte[]> fields, CancellationToken ct)
    {
        if (!NetworkAdapterInfo.GetAll().Any(a => SameAdapter(a.Id,peer.AdapterName) && a.Address == peer.LocalIpv4 && a.IsUp)) throw new IOException("原搜索网卡或 IPv4 已改变。");
        if (DateTimeOffset.UtcNow - peer.SeenUtc > TimeSpan.FromSeconds(25)) throw new IOException("设备 challenge 已过期，请重新搜索。");
        using var channel = new UdpBroadcastChannel(peer.LocalIpv4);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(2500);
        ulong id = RandomId(); byte[] revision = new byte[8], boot = new byte[8]; Wire.Write64(revision,0,peer.ConfigRevision); Wire.Write64(boot,0,peer.BootId);
        fields[0x8010] = revision; fields[0x8017] = boot;
        byte[] request = channel.PrepareRequest(BuildRequest(peer,operation,id,fields));
        async Task Send() { for(int i=0;i<3;++i) { await channel.SendAsync(request,stop.Token); await Task.Delay(500,stop.Token); } }
        var sending=Send();
        try
        {
            while(true) { var response=await channel.ReceiveAsync(stop.Token); try { return ParseResult(response,id,peer); } catch(InvalidDataException) { } }
        }
        catch(OperationCanceledException error) when(!ct.IsCancellationRequested) { throw new TimeoutException("未收到 UDP 广播应答；请确认板卡固件已更新、双方位于同一广播域，并查询事务后再重试写入。",error); }
        finally { stop.Cancel(); try { await sending; } catch(OperationCanceledException) { } }
    }
    internal static byte[] BuildRequest(UdpPeer peer, byte operation, ulong requestId,
        SortedDictionary<ushort, byte[]> fields)
    {
        if (operation is < 3 or > 6 || peer.Challenge.Length != 16) throw new ArgumentException("Invalid UDP broadcast operation.");
        int length = 56 + fields.Sum(f => 4 + f.Value.Length);
        if (length > 1024) throw new ArgumentException("UDP broadcast message exceeds 1024 bytes.");
        byte[] data = new byte[length]; "UABD"u8.CopyTo(data);
        data[4] = 1; data[5] = operation; Wire.Write16(data, 6, 56); Wire.Write16(data, 8, (ushort)length);
        Wire.Write64(data, 12, requestId);
        byte[] id = Convert.FromHexString(peer.Device.DeviceId);
        if (id.Length != 16 || id.All(b => b == 0)) throw new ArgumentException("Exact device identity required.");
        id.CopyTo(data, 20); peer.Challenge.CopyTo(data, 36);
        int offset = 56;
        foreach (var (type, value) in fields)
        {
            Wire.Write16(data, offset, type); Wire.Write16(data, offset + 2, (ushort)value.Length);
            value.CopyTo(data, offset + 4); offset += value.Length + 4;
        }
        return data;
    }

    internal static UdpResult ParseResult(byte[] data, ulong requestId, UdpPeer peer)
    {
        ValidateHeader(data, requestId, 7, 0);
        if (!data.AsSpan(20, 16).SequenceEqual(Convert.FromHexString(peer.Device.DeviceId)))
            throw new InvalidDataException("UDP broadcast response device identity mismatch.");
        var fields = ParseFields(data, 0);
        ushort status = Wire.Read16(Required(fields, 11, 2), 0);
        string reason = fields.TryGetValue(12, out var text) ? Wire.Utf8.GetString(text) : "";
        if (text is { Length: > 128 }) throw new InvalidDataException("Invalid result reason.");
        ulong boot = fields.TryGetValue(8, out var b) ? Read64(b) : peer.BootId;
        if (boot != peer.BootId) throw new InvalidDataException("Device rebooted during transaction.");
        ulong revision = fields.TryGetValue(9, out var r) ? Read64(r) : peer.ConfigRevision;
        byte[]? transaction = fields.TryGetValue(0x8011, out var t) ? t : null;
        if (transaction is not null && transaction.Length != 16) throw new InvalidDataException("Invalid transaction ID.");
        var device = peer.Device;
        if (fields.TryGetValue(3, out var address))
        {
            if (address.Length != 4) throw new InvalidDataException("Invalid current IPv4 field.");
            device = device with { Address = new IPAddress(address).ToString() };
        }
        var updated = WithTransaction(peer with { Device = device, Challenge = data.AsSpan(36, 16).ToArray(), ConfigRevision = revision, SeenUtc = DateTimeOffset.UtcNow }, fields);
        return new(status, reason, transaction, updated);
    }

    private static UdpPeer WithTransaction(UdpPeer peer, Dictionary<ushort, byte[]> fields)
    {
        byte stage = 0;
        if (fields.TryGetValue(15, out var state))
        {
            if (state.Length != 1 || state[0] > 4) throw new InvalidDataException("Invalid transaction stage.");
            stage = state[0];
        }
        string id = fields.TryGetValue(0x8011, out var transaction) && transaction.Length == 16
            ? Convert.ToHexString(transaction).ToLowerInvariant() : "";
        if (fields.ContainsKey(0x8011) && id.Length == 0) throw new InvalidDataException("Invalid transaction identity.");
        return peer with { TransactionId = id, TransactionStage = stage };
    }

    private static void ValidateHeader(byte[] data, ulong id, byte type, ushort auth)
    {
        if (data.Length < 56 + auth || data.Length > 1024 || !data.AsSpan(0, 4).SequenceEqual("UABD"u8) ||
            data[4] != 1 || data[5] != type || Wire.Read16(data, 6) != 56 || Wire.Read16(data, 8) != data.Length ||
            Wire.Read16(data, 10) != 1 || Wire.Read64(data, 12) != id || Wire.Read16(data, 52) != auth || Wire.Read16(data, 54) != 0)
            throw new InvalidDataException("Invalid UDP broadcast response header.");
    }

    private static Dictionary<ushort, byte[]> ParseFields(byte[] data, int auth)
    {
        var fields = new Dictionary<ushort, byte[]>(); int offset = 56, end = data.Length - auth; ushort previous = 0;
        while (offset < end)
        {
            if (offset + 4 > end) throw new InvalidDataException("Truncated UDP broadcast TLV.");
            ushort type = Wire.Read16(data, offset), length = Wire.Read16(data, offset + 2); offset += 4;
            if (type <= previous || offset + length > end || ((type & 0x8000) != 0 && type is < 0x8010 or > 0x8017))
                throw new InvalidDataException("Duplicate, unordered or unknown critical UDP broadcast TLV.");
            previous = type; fields.Add(type, data.AsSpan(offset, length).ToArray()); offset += length;
        }
        return fields;
    }

    private static byte[] Required(Dictionary<ushort, byte[]> fields, ushort type, int size) =>
        fields.TryGetValue(type, out var value) && value.Length == size ? value : throw new InvalidDataException($"Missing or invalid TLV {type:x4}.");
    private static ulong Read64(byte[] bytes) => bytes.Length == 8 ? Wire.Read64(bytes, 0) : throw new InvalidDataException("Invalid uint64 field.");
    private static ulong RandomId() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    private static byte[] Ipv4(string value) => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
        ? ip.GetAddressBytes() : throw new ArgumentException("An IPv4 address is required.");
}
