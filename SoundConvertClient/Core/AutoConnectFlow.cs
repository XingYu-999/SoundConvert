namespace UsbAudioBridge;

public enum AutoConnectFailure { NotFound, Ambiguous, DiscoveryFailed, ConnectionFailed }
public sealed class AutoConnectException(AutoConnectFailure failure,string message,Exception? inner=null) : IOException(message,inner)
{
    public AutoConnectFailure Failure { get; } = failure;
}

public static class AutoConnectFlow
{
    // Exactly one remembered-address attempt and one discovery pass. Every dial
    // must verify the expected permanent identity before publishing a connection.
    public static async Task RunAsync(string identity,string address,int port,
        Func<string,int,string,Task> connect,Func<Task<IReadOnlyList<Device>>> discover,Func<string,bool> sameSubnet)
    {
        Exception? firstFailure=null;
        if(sameSubnet(address))
        {
            try{await connect(address,port,identity);return;}
            catch(Exception error)when(error is not OperationCanceledException){ firstFailure=error; }
        }
        IReadOnlyList<Device> devices;
        try { devices=await discover(); }
        catch(Exception error)when(error is not OperationCanceledException)
        { throw new AutoConnectException(AutoConnectFailure.DiscoveryFailed,"原网卡上的设备搜索失败。",firstFailure is null?error:new AggregateException(firstFailure,error)); }
        var candidates=devices.Where(x=>x.DeviceId.Equals(identity,StringComparison.OrdinalIgnoreCase)&&sameSubnet(x.Address)).DistinctBy(x=>(x.Address,x.ControlPort)).ToArray();
        if(candidates.Length==0)throw new AutoConnectException(AutoConnectFailure.NotFound,"未在原网卡的同一网段发现匹配设备。",firstFailure);
        if(candidates.Length>1)throw new AutoConnectException(AutoConnectFailure.Ambiguous,"发现多条相同设备身份的地址，不能安全选择。",firstFailure);
        var candidate=candidates[0];
        try{await connect(candidate.Address,candidate.ControlPort,identity);}
        catch(Exception error)when(error is not OperationCanceledException){throw new AutoConnectException(AutoConnectFailure.ConnectionFailed,"目标设备连接失败。",error);}
    }
}
