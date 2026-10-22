using System.Net.Sockets;
using UsbAudioBridge;

namespace SoundConvertClient;

public static class ConnectionFeedback
{
    public static string Message(Exception error)=>error is OperationCanceledException?"操作已取消。":Normalize(error.Message);
    public static string Normalize(string text)=>text.Replace("The operation was canceled.","操作已取消。",StringComparison.OrdinalIgnoreCase)
        .Replace("A task was canceled.","操作已取消。",StringComparison.OrdinalIgnoreCase)
        .Replace("The operation was cancelled.","操作已取消。",StringComparison.OrdinalIgnoreCase);
    public static string Target(string? name,string identity)=>!string.IsNullOrWhiteSpace(name)?name:!string.IsNullOrWhiteSpace(identity)?identity:"已保存的开发板";
    static IEnumerable<Exception> Causes(Exception error)
    {
        yield return error;
        if(error is AggregateException aggregate)
        { foreach(var inner in aggregate.InnerExceptions)foreach(var cause in Causes(inner))yield return cause; }
        else if(error.InnerException is {} inner)foreach(var cause in Causes(inner))yield return cause;
    }
    public static string BoardFailure(string target,Exception error,int port)
    {
        var causes=Causes(error).ToArray();
        var flow=causes.OfType<AutoConnectException>().FirstOrDefault();
        string reason,check;
        if(flow?.Failure==AutoConnectFailure.Ambiguous)
        { reason="发现多条相同设备身份的地址";check="请重新搜索并核对设备唯一标识，明确选择正确设备。"; }
        else if(causes.Any(e=>e.Message.Contains("唯一标识",StringComparison.Ordinal)))
        { reason="目标地址上的设备唯一标识不匹配，已拒绝连接";check="请检查设备唯一标识及 DHCP 地址是否变化，重新搜索后选择正确设备。"; }
        else if(flow?.Failure==AutoConnectFailure.DiscoveryFailed)
        { reason="原网卡设备搜索失败";check="请刷新并检查原网卡是否连接、IPv4 是否变化、是否选对设备所在网段。"; }
        else if(causes.Any(e=>e is TimeoutException or OperationCanceledException))
        { reason="连接或读取开发板响应超时";check=$"请检查开发板供电、网线、所选网卡/IP 网段及防火墙是否允许 TCP {port} 和设备发现 UDP 49160。"; }
        else if(causes.OfType<SocketException>().FirstOrDefault() is {} socket)
        {
            reason=socket.SocketErrorCode switch { SocketError.ConnectionRefused=>"目标拒绝 TCP 连接",SocketError.NetworkUnreachable=>"设备网络不可达",SocketError.HostUnreachable=>"目标设备不可达",SocketError.AddressNotAvailable=>"原网卡 IPv4 地址不可用",SocketError.AccessDenied=>"网络连接被系统或安全软件拒绝",SocketError.TimedOut=>"目标设备连接超时",SocketError.ConnectionReset=>"开发板中断了连接",_=>"网络连接失败" };
            check=$"请检查开发板供电、网线、IP/所选网卡和 TCP {port} 服务，并检查防火墙；可重新搜索确认当前设备地址。";
        }
        else if(causes.OfType<DeviceException>().FirstOrDefault() is {} rejected)
        { reason=$"开发板拒绝连接（{rejected.Code}）";check="请检查固件版本、是否已有其他管理端及配置事务状态，重新搜索后重试。"; }
        else if(causes.Any(e=>e is InvalidDataException))
        { reason="开发板身份或协议应答无效";check="请检查目标是否为本项目开发板、固件是否匹配，重新搜索并核对设备唯一标识。"; }
        else if(flow?.Failure==AutoConnectFailure.NotFound)
        { reason="未在原网卡同一网段发现匹配设备";check="请检查开发板供电、网线和原网卡/IP 网段，重新搜索或手动连接。"; }
        else
        { reason=ContainsChinese(error.Message)?Message(error):"开发板连接未完成";check="请检查开发板供电、网络和原网卡设置，重新搜索或查看日志。"; }
        if(flow?.Failure==AutoConnectFailure.NotFound&&!reason.Contains("未在原网卡",StringComparison.Ordinal))reason+="，且未发现匹配设备";
        return $"连接失败（{target}）：{reason}。{check}";
    }
    public static string BackendFailure(Exception error)
    {
        if(error is OperationCanceledException or TimeoutException)return "连接音频后台超时。请检查 Engine 目录是否完整、旧版前后台是否已退出，以及安全软件是否拦截。";
        if(error is FileNotFoundException)return "音频后台文件缺失。请完整解压新版并保留 Engine 目录。";
        return "音频后台连接失败："+(ContainsChinese(error.Message)?Message(error):"后台通信中断。请检查 Engine 程序、退出旧版前后台后重试或查看日志。");
    }
    static bool ContainsChinese(string text)=>text.Any(c=>c is >= '\u4e00' and <= '\u9fff');
}
