using System.Text.Json;
using UsbAudioBridge;

namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    AudioFormat? lastHostFormat;

    void ReadHostFormat(JsonElement usb, bool selected)
    {
        var remembered=RememberedBoards.FirstOrDefault(x=>x.Board.DeviceId.Equals(DeviceIdentity,StringComparison.OrdinalIgnoreCase));
        lastHostFormat??=remembered?.Board.LastHostFormat;
        if(selected&&usb.TryGetProperty("format",out var format))
        {
            var actual=AudioFormat.FromJson(format);
            lastHostFormat=actual;
            ActualFormat=actual.Label;
            if(remembered is not null&&remembered.Board.LastHostFormat!=actual)
            {
                int index=RememberedBoards.IndexOf(remembered);
                RememberedBoards[index]=new(remembered.Board with { LastHostFormat=actual },ConnectRememberedCommand,ForgetBoardCommand);
                PersistBoards();
            }
            return;
        }
        ActualFormat=selected?"USB Host 已选择格式，格式信息暂不可用":lastHostFormat is { } last
            ?$"当前空闲（alt 0） · 上次 Host 格式：{last.Label}"
            :"当前空闲（alt 0） · 暂无上次 Host 格式记录";
    }
}
