using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UsbAudioBridge;

namespace SoundConvertClient;

public sealed class IpAddressEditor : INotifyPropertyChanged
{
    readonly Func<bool, string, int, string, Task<string>> save;
    bool dhcp, busy, saved;
    readonly bool canWrite;
    string ip, gateway, status = "选择 DHCP 自动获取，或填写静态 IPv4 后保存。";
    int prefix;
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public string DeviceLabel { get; }
    public string Identity { get; }
    public bool Dhcp { get => dhcp; set { dhcp = value; Changed(); Changed(nameof(IsStatic)); } }
    public bool IsStatic => !Dhcp;
    public string Address { get => ip; set { ip = value; Changed(); } }
    public int Prefix { get => prefix; set { prefix = value; Changed(); } }
    public string Gateway { get => gateway; set { gateway = value; Changed(); } }
    public bool Busy { get => busy; private set { busy = value; Changed(); Changed(nameof(CanSave)); Changed(nameof(CanClose)); } }
    public bool CanSave => canWrite && !Busy && !saved;
    public bool CanClose => !Busy;
    public string Status { get => status; private set { status = value; Changed(); } }
    public ICommand SaveCommand { get; }

    public IpAddressEditor(Device device, Func<bool, string, int, string, Task<string>> save, bool canWrite = true)
    {
        this.save = save; this.canWrite = canWrite; Identity = $"ID {device.DeviceId} · MAC {device.Mac}";
        if (!canWrite) status = "当前为查看模式。保存前请先断开当前设备，并处理已有网络配置事务；此操作会中断音频。";
        DeviceLabel = $"{device.Model} · {device.Address} · 固件 {device.Firmware}";
        dhcp = device.AddressMode != "static"; ip = device.Address == "0.0.0.0" ? "" : device.Address;
        prefix = device.PrefixLength is >= 1 and <= 30 ? device.PrefixLength : 24; gateway = device.Gateway;
        SaveCommand = new UiCommand(_ => _ = SaveAsync());
    }

    public async Task SaveAsync()
    {
        if (!CanSave) return;
        Busy = true;
        try
        {
            Validate(Dhcp, Address, Prefix, Gateway);
            Status = "正在通过UDP 广播应用配置并验证新地址，请稍候…";
            Status = await save(Dhcp, Address.Trim(), Prefix, Gateway.Trim()); saved = true;
        }
        catch (Exception error) { Status = error.Message; }
        finally { Busy = false; }
    }

    public static void Validate(bool dhcp, string address, int prefix, string gateway)
    {
        if (dhcp) return;
        uint Ip(string value)
        {
            if (!IPAddress.TryParse(value.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("请输入有效的 IPv4 地址。");
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        }
        bool Usable(uint value) => value >> 24 is > 0 and < 224 && value >> 24 != 127 && value >> 16 != 0xa9fe;
        if (prefix is < 1 or > 30) throw new ArgumentException("前缀长度须为 1–30。");
        uint ip = Ip(address), gw = Ip(gateway), mask = uint.MaxValue << (32 - prefix), host = ~mask;
        if (!Usable(ip) || (ip & host) == 0 || (ip & host) == host)
            throw new ArgumentException("静态 IP 不能是网络地址、广播地址、回环或保留地址。");
        if (gw != 0 && (!Usable(gw) || (gw & mask) != (ip & mask) || (gw & host) == 0 || (gw & host) == host))
            throw new ArgumentException("网关须为同网段的有效主机地址；无网关请填 0.0.0.0。");
    }
}
