namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    bool inputModeHintTracking;
    public string InputModeHint
    {
        get
        {
            if (!inputModeHintTracking)
            {
                inputModeHintTracking = true;
                PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(IsConnected) or nameof(MicrophoneSupported))
                    {
                        Changed(nameof(InputModeHint)); Changed(nameof(HasInputModeHint));
                    }
                };
            }
            return !IsConnected ? "连接开发板后读取麦克风能力。" : !MicrophoneSupported ? "当前模式不提供麦克风。" : "";
        }
    }
    public bool HasInputModeHint => InputModeHint.Length != 0;
}
