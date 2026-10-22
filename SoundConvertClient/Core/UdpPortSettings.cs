using System.Globalization;

namespace UsbAudioBridge;

public sealed record UdpPortSettings(int BoardPlayback = 0, int SoftwarePlayback = 0, int BoardCapture = 0, int SoftwareCapture = 0)
{
    public const string Automatic = "自动协商";
    public static int Parse(string? text)
    {
        text = text?.Trim();
        if (text == Automatic) return 0;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1024 and <= 65535) return port;
        throw new ArgumentException("手动 UDP 端口必须是 1024–65535 的整数。");
    }
    public static string Label(int port) => port == 0 ? Automatic : port.ToString(CultureInfo.InvariantCulture);
    public static void ValidatePort(int port)
    {
        if (port != 0 && (port < 1024 || port > 65535)) throw new ArgumentOutOfRangeException(nameof(port), "UDP 端口必须为自动协商或 1024–65535。");
    }
    public void Validate()
    {
        foreach (int port in new[] { BoardPlayback, SoftwarePlayback, BoardCapture, SoftwareCapture }) ValidatePort(port);
        if (BoardPlayback is 49160 or 49161 || BoardCapture is 49160 or 49161) throw new ArgumentException("虚拟声卡端口不能占用设备发现 49160 或控制 49161。");
        if (BoardPlayback != 0 && BoardPlayback == BoardCapture || SoftwarePlayback != 0 && SoftwarePlayback == SoftwareCapture)
            throw new ArgumentException("同一端的播放与麦克风 UDP 端口不能相同。");
    }
}
