using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UsbAudioBridge;

public sealed record NetworkAdapterInfo(string Id, string Name, string Description, string Address,
    string Mask, bool IsUp, bool SupportsBroadcast)
{
    public bool HasIpv4 => IPAddress.TryParse(Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any);
    public string Status => IsUp ? "已连接" : "未连接";
    public string Label => $"{Name} · {(HasIpv4 ? Address : "无 IPv4 地址")} · {Status}";

    public static IReadOnlyList<NetworkAdapterInfo> GetAll() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.Supports(NetworkInterfaceComponent.IPv4))
        .SelectMany(n =>
        {
            var addresses = n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)).ToArray();
            bool up = n.OperationalStatus == OperationalStatus.Up, broadcast = n.GetPhysicalAddress().GetAddressBytes().Length == 6;
            return addresses.Length == 0
                ? [new NetworkAdapterInfo(n.Id, n.Name, n.Description, "0.0.0.0", "0.0.0.0", up, broadcast)]
                : addresses.Select(a => new NetworkAdapterInfo(n.Id, n.Name, n.Description, a.Address.ToString(), a.IPv4Mask.ToString(), up, broadcast));
        }).OrderByDescending(n => n.IsUp).ThenByDescending(n => n.SupportsBroadcast).ThenBy(n => n.Name).ThenBy(n => n.Address).ToArray();
}
