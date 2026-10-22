using UsbAudioBridge;
namespace SoundConvertClient;
public enum ClockObservationStage { SidebandOnly, BoardMapping, Full }

public sealed partial class BridgeRuntime
{
    ClockObservationMode clockMode;
    readonly ClockObservationStage clockStage=Enum.TryParse<ClockObservationStage>(Environment.GetEnvironmentVariable("UAB_CLOCK_STAGE"),true,out var level)&&Enum.IsDefined(level)?level:ClockObservationStage.Full;
    ClockObservationSession? clockObservation;
    public IReadOnlyList<string> ClockModes{get;}=new[]{"原有控制（默认）","仅观测时间与频差","时钟辅助（实验）"};
    public string ClockStage=>clockStage.ToString();
    public int ClockModeIndex
    {
        get=>(int)clockMode;
        set
        {
            if(value is <0 or >2||value==(int)clockMode)return;
            Set(ref clockMode,(ClockObservationMode)value);Changed();Changed(nameof(OutputSettingsStatus));Changed(nameof(ClockStatus));
            if(!initializing)LocalStore.SaveSettings(CurrentSettings());
        }
    }
    public string ClockStatus=>clockObservation?.Summary??(clockMode==ClockObservationMode.Legacy?"Legacy · 时间侧带与输出时钟观测关闭":"尚未启动观测；请应用并重新开始输出。");
    internal string ReadClockEvidence()=>clockObservation?.Diagnostics??"{}";
    async Task StopClockObservation()
    {
        var current=clockObservation;clockObservation=null;if(current is not null)await current.DisposeAsync();
    }
}
