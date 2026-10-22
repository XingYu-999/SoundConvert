namespace UsbAudioBridge;

// These are historical observations, not a simultaneous four-port policy.
// Ports from separate sessions may legitimately be equal.
public sealed record UdpPortHistory(int BoardPlayback=0,int SoftwarePlayback=0,int BoardCapture=0,int SoftwareCapture=0)
{
    static int Valid(int port)=>port is >=1024 and <=65535?port:0;
    static int Remember(int previous,int configured,int actual)=>configured==0&&Valid(actual)!=0?actual:previous;
    public UdpPortHistory Normalize()=>new(Valid(BoardPlayback),Valid(SoftwarePlayback),Valid(BoardCapture),Valid(SoftwareCapture));
    public UdpPortHistory RememberPlayback(UdpPortSettings policy,int board,int software)=>this with
    {BoardPlayback=Remember(BoardPlayback,policy.BoardPlayback,board),SoftwarePlayback=Remember(SoftwarePlayback,policy.SoftwarePlayback,software)};
    public UdpPortHistory RememberCapture(UdpPortSettings policy,int board,int software)=>this with
    {BoardCapture=Remember(BoardCapture,policy.BoardCapture,board),SoftwareCapture=Remember(SoftwareCapture,policy.SoftwareCapture,software)};
}
