using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UsbAudioBridge;

/// <summary>Selected-NIC broadcast sender and wildcard receiver, including replies from other IP subnets.</summary>
public sealed class UdpBroadcastChannel : IDisposable
{
    readonly Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly Socket receiver = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly int interfaceIndex;
    public const int DiscoveryPort = 49160;
    public int ReplyPort => ((IPEndPoint)receiver.LocalEndPoint!).Port;

    public UdpBroadcastChannel(string local)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == local))
                ?? throw new IOException("原搜索网卡 IPv4 已失效，请刷新后重新选择。");
            interfaceIndex = nic.GetIPProperties().GetIPv4Properties().Index;
            sender.EnableBroadcast = true;
            sender.Bind(new IPEndPoint(IPAddress.Parse(local), 0));
            if (OperatingSystem.IsWindows()) sender.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* Windows IP_UNICAST_IF */, IPAddress.HostToNetworkOrder(interfaceIndex));
            receiver.ExclusiveAddressUse = true;
            receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            receiver.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch { Dispose(); throw; }
    }

    // Reply-port TLV permits a receiver bound to ANY (required for broadcasts),
    // while every transmitted packet remains bound to the exact selected IPv4.
    public byte[] PrepareRequest(byte[] request)
    {
        if (request.Length is < 56 or > 1018) throw new InvalidDataException("Invalid discovery request length.");
        byte[] result = new byte[request.Length + 6];
        request.AsSpan(0, 56).CopyTo(result);
        Wire.Write16(result, 56, 0x0010); Wire.Write16(result, 58, 2); Wire.Write16(result, 60, (ushort)ReplyPort);
        request.AsSpan(56).CopyTo(result.AsSpan(62)); Wire.Write16(result, 8, (ushort)result.Length);
        return result;
    }
    public async Task SendAsync(byte[] request, CancellationToken ct) =>
        _ = await sender.SendToAsync(request, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), ct).ConfigureAwait(false);

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[1025];
        while (true)
        {
            var result = await receiver.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            if (result.PacketInformation.Interface != interfaceIndex || result.ReceivedBytes > 1024 ||
                (result.SocketFlags & SocketFlags.Truncated) != 0 || ((IPEndPoint)result.RemoteEndPoint).Port != DiscoveryPort) continue;
            return buffer.AsSpan(0, result.ReceivedBytes).ToArray();
        }
    }
    public void Dispose() { sender.Dispose(); receiver.Dispose(); }
}
