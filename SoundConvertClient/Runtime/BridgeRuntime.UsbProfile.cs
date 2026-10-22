using System.Text.Json;
namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    static readonly string[] KnownUsbProfiles=["desktop_speaker","speaker","headphones","headset"];
    string usbDeviceProfile="desktop_speaker",committedUsbDeviceProfile="desktop_speaker",activeUsbDeviceProfile="unknown";
    string[] usbDeviceProfiles=[];bool usbHidConsumerCompat,committedHidConsumerCompat,hidConsumerCompatSupported;
    public bool UsbHidConsumerCompat{get=>usbHidConsumerCompat;set{Set(ref usbHidConsumerCompat,value);Changed(nameof(UsbDraftState));}}
    public bool HidConsumerCompatSupported=>hidConsumerCompatSupported;
    public bool CanEditHidConsumerCompat=>CanEditUsb&&HidConsumerCompatSupported;
    bool activeHidConsumerCompat;
    public string UsbHidCompatibilityState=>!IsConnected||!configLoaded?"未读取当前设备 HID 配置":!HidConsumerCompatSupported?"固件使用默认 HID 集合":$"已保存：{(committedHidConsumerCompat?"Consumer Control":"按设备类型")} · 当前枚举：{(activeHidConsumerCompat?"Consumer Control":"按设备类型")}";
    void ReadHidCompatibilityStatus(JsonElement usb){activeHidConsumerCompat=usb.TryGetProperty("active_hid_consumer_compat",out var h)&&h.GetBoolean();Changed(nameof(UsbHidCompatibilityState));}
    public string UsbDeviceProfile{get=>usbDeviceProfile;set{if(!KnownUsbProfiles.Contains(value)&&value!=committedUsbDeviceProfile)throw new ArgumentException("未知 USB 类型");Set(ref usbDeviceProfile,value);Changed(nameof(UsbDraftState));Changed(nameof(UsbProfileBlockReason));}}
    public string CommittedUsbDeviceProfile=>IsConnected&&configLoaded?committedUsbDeviceProfile:"unknown";
    public string ActiveUsbDeviceProfile=>activeUsbDeviceProfile;
    public string[] UsbDeviceProfiles=>usbDeviceProfiles;
    public bool CanEditUsbProfile=>CanEditUsb&&usbDeviceProfiles.Length>0;
    public string UsbProfileBlockReason=>!IsConnected?"请连接设备后读取当前类型预设。":!KnownUsbProfiles.Contains(UsbDeviceProfile)?$"保留未知模式原值 {UsbDeviceProfile}；保存前请明确选择固件支持的模式。":usbDeviceProfiles.Length==0?"当前固件不支持类型预设":"选择仅修改草稿；保存后 USB 会短暂断开并重新枚举。";
    void ReadUsbProfileCapabilities(JsonElement data){hidConsumerCompatSupported=data.TryGetProperty("hid_consumer_compat_supported",out var hc)&&hc.GetBoolean();Changed(nameof(HidConsumerCompatSupported));Changed(nameof(CanEditHidConsumerCompat));usbDeviceProfiles=data.TryGetProperty("usb_device_profiles",out var p)?p.EnumerateArray().Select(x=>x.GetString()??"").Where(KnownUsbProfiles.Contains).ToArray():[];Changed(nameof(UsbDeviceProfiles));Changed(nameof(CanEditUsbProfile));Changed(nameof(UsbProfileBlockReason));}
    bool HasUsbConfigurationDraftChanges()=>ProductName.Trim()!=committedProductName||HiResEnabled!=committedHiRes||DraftFormats!=committedFormats||DraftCaptureFormats!=committedCaptureFormats||UsbDeviceProfile!=committedUsbDeviceProfile||UsbHidConsumerCompat!=committedHidConsumerCompat||indicatorMode!=committedIndicatorMode;
    void ReadUsbProfileConfiguration(JsonElement usb,bool preserveDraft){committedHidConsumerCompat=usb.TryGetProperty("hid_consumer_compat",out var hc)&&hc.GetBoolean();if(!preserveDraft)UsbHidConsumerCompat=committedHidConsumerCompat;committedUsbDeviceProfile=String(usb,"device_profile","desktop_speaker");if(!preserveDraft)UsbDeviceProfile=committedUsbDeviceProfile;Changed(nameof(CommittedUsbDeviceProfile));Changed(nameof(UsbHidCompatibilityState));Changed(nameof(UsbProfileBlockReason));}
    void ValidateUsbProfileDraft(){if(!KnownUsbProfiles.Contains(UsbDeviceProfile))throw new ArgumentException("未知 USB 类型不能保存，请先选择支持的模式。");if(usbDeviceProfiles.Length>0&&!usbDeviceProfiles.Contains(UsbDeviceProfile))throw new ArgumentException("固件不支持所选 USB 类型");}
    bool pendingResumeListening,pendingResumeMicrophone;
    sealed record UsbRuntimeIntent(bool Restart,bool Listen,bool Microphone);
    UsbRuntimeIntent CaptureUsbRuntimeIntent()=>new(UsbState=="RUNNING",listeningIntent,microphoneIntent);
    async Task RestoreUsbRuntimeIntentAsync(UsbRuntimeIntent intent){if(!intent.Restart)return;microphoneIntent=false;await ApplyOutputFormats();await SetUsbState(true);if(intent.Listen)await Listen();if(intent.Microphone){if(MicrophoneSupported){microphoneIntent=true;await StartMicrophoneCore();}else{microphoneIntent=false;Log("耳机预设未开放 USB 麦克风，已停止麦克风发送。");}}}
}
