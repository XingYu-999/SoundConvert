using System.Text.Json;
using System.Windows.Input;

namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    static readonly string[] IndicatorModeNames = ["auto", "off", "on", "breathe", "blink"];
    public string[] IndicatorModeOptions { get; } = ["自动（推荐）", "常灭", "常亮", "持续呼吸", "持续闪烁"];
    string indicatorMode = "auto", committedIndicatorMode = "auto";
    string[] supportedIndicatorModes = [];
    public bool IndicatorConfigurationSupported { get; private set; }
    public int IndicatorModeIndex
    {
        get => IsConnected && configLoaded ? Array.IndexOf(IndicatorModeNames, indicatorMode) : -1;
        set
        {
            if (value < 0 || value >= IndicatorModeNames.Length) return;
            Set(ref indicatorMode, IndicatorModeNames[value]);
            RefreshIndicatorState(); Changed(nameof(UsbDraftState));
        }
    }
    public bool CanEditIndicator => CanEditUsb && configLoaded && IndicatorConfigurationSupported;
    public bool CanIdentifyIndicator => IsConnected && CanManage && !IsBusy;
    public string IndicatorConfigurationState => !IsConnected || !configLoaded ? "连接设备后读取已保存的指示灯配置。"
        : !IndicatorConfigurationSupported ? "当前固件不支持指示灯配置，请更新配套固件。"
        : $"已保存：{IndicatorLabel(committedIndicatorMode)}" + (indicatorMode != committedIndicatorMode ? " · 存在未保存修改" : "");
    public string IndicatorModeDescription => indicatorMode switch
    {
        "auto" => "USB 声卡已连接但空闲时缓慢呼吸；播放或麦克风有音频数据时约每秒闪烁 3 次。未连接、停止模拟或挂起时熄灭。",
        "off" => "关闭 LD2 指示灯，适合夜间或不需要灯光提示的场景。",
        "on" => "设备供电且固件运行时，LD2 保持点亮。",
        "breathe" => "设备运行时持续缓慢呼吸，约 2.4 秒一个周期。",
        "blink" => "设备运行时持续闪烁，每秒一次。",
        _ => "设备保存了当前软件不认识的模式；请选择支持的模式后保存。"
    };
    string IndicatorLabel(string mode)
    {
        int index = Array.IndexOf(IndicatorModeNames, mode);
        return index >= 0 ? IndicatorModeOptions[index] : $"未知模式（{mode}）";
    }
    void ReadIndicatorCapabilities(JsonElement data)
    {
        supportedIndicatorModes = data.TryGetProperty("indicator_modes", out var modes) && modes.ValueKind == JsonValueKind.Array
            ? modes.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(IndicatorModeNames.Contains).ToArray() : [];
        IndicatorConfigurationSupported = data.TryGetProperty("indicator_configuration_supported", out var supported)
            && supported.ValueKind == JsonValueKind.True && supportedIndicatorModes.Length > 0;
        RefreshIndicatorState();
    }
    void ReadIndicatorConfiguration(JsonElement usb, bool preserveDraft)
    {
        committedIndicatorMode = String(usb, "indicator_mode", "auto");
        if (!preserveDraft) indicatorMode = committedIndicatorMode;
        RefreshIndicatorState();
    }
    void ValidateIndicatorDraft()
    {
        if (IndicatorConfigurationSupported && !supportedIndicatorModes.Contains(indicatorMode))
            throw new ArgumentException("当前固件不支持所选指示灯模式，请选择支持的模式。");
    }
    void DiscardIndicatorDraft() { indicatorMode = committedIndicatorMode; RefreshIndicatorState(); }
    void ResetIndicatorState()
    {
        IndicatorConfigurationSupported = false; supportedIndicatorModes = [];
        indicatorMode = committedIndicatorMode = "auto"; RefreshIndicatorState();
    }
    void RefreshIndicatorState()
    {
        foreach (string name in new[] { nameof(IndicatorConfigurationSupported), nameof(IndicatorModeIndex), nameof(CanEditIndicator),
            nameof(CanIdentifyIndicator), nameof(IndicatorConfigurationState), nameof(IndicatorModeDescription) }) Changed(name);
    }
    public ICommand IdentifyIndicatorCommand => AsyncCommand(nameof(IdentifyIndicatorCommand), async () =>
    {
        EnsureOwner(); await Session.RequestAsync("IDENTIFY", new { seconds = 5 });
        Log("LD2 正在快速闪烁识别，5 秒后恢复原指示灯模式。");
    });
}
