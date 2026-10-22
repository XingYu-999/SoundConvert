using UsbAudioBridge;
namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    AudioProcessingSettings processingSettings=new();
    public bool AudioProcessingEnabled{get=>processingSettings.Enabled;set=>UpdateProcessing(processingSettings with{Enabled=value});}
    public double AudioBalance{get=>processingSettings.Balance;set=>UpdateProcessing(processingSettings with{Balance=value});}
    public bool AudioMono{get=>processingSettings.Mono;set=>UpdateProcessing(processingSettings with{Mono=value});}
    public bool AudioEqEnabled{get=>processingSettings.EqEnabled;set=>UpdateProcessing(processingSettings with{EqEnabled=value});}
    public double AudioEqLowDb{get=>processingSettings.LowDb;set=>UpdateProcessing(processingSettings with{LowDb=value});}
    public double AudioEqMidDb{get=>processingSettings.MidDb;set=>UpdateProcessing(processingSettings with{MidDb=value});}
    public double AudioEqHighDb{get=>processingSettings.HighDb;set=>UpdateProcessing(processingSettings with{HighDb=value});}
    public bool AudioLimiterEnabled{get=>processingSettings.LimiterEnabled;set=>UpdateProcessing(processingSettings with{LimiterEnabled=value});}
    public double AudioLimiterCeilingDb{get=>processingSettings.CeilingDb;set=>UpdateProcessing(processingSettings with{CeilingDb=value});}
    public bool OutputLimitEnabled{get=>processingSettings.OutputLimitEnabled;set=>UpdateProcessing(processingSettings with{OutputLimitEnabled=value});}
    public double OutputGainLimit{get=>processingSettings.OutputGainLimit;set=>UpdateProcessing(processingSettings with{OutputGainLimit=value});}
    public int ResumeFadeMs{get=>processingSettings.ResumeFadeMs;set=>UpdateProcessing(processingSettings with{ResumeFadeMs=value});}
    public string AudioProcessingStatus=>processingSettings.Enabled||processingSettings.OutputLimitEnabled||processingSettings.ResumeFadeMs>0?"已处理 · 应用内部增益 / 无前视峰值限制 · 新增缓冲 0 帧":"旁路 · 保持原样本与延迟";
    void UpdateProcessing(AudioProcessingSettings value){value.Validate();processingSettings=value;audio?.PostProcessor.SetTargetSettings(value);foreach(string p in ProcessingProperties)Changed(p);Changed(nameof(AudioProcessingStatus));if(!initializing)LocalStore.SaveSettings(CurrentSettings());}
    static readonly string[] ProcessingProperties=["AudioProcessingEnabled","AudioBalance","AudioMono","AudioEqEnabled","AudioEqLowDb","AudioEqMidDb","AudioEqHighDb","AudioLimiterEnabled","AudioLimiterCeilingDb","OutputLimitEnabled","OutputGainLimit","ResumeFadeMs"];
}
