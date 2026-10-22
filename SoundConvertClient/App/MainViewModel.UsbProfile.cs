namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    public bool UsbHidConsumerCompat{get=>Get<bool>(nameof(UsbHidConsumerCompat));set=>SetRemote(nameof(UsbHidConsumerCompat),value);}
    public bool HidConsumerCompatSupported=>Get<bool>(nameof(HidConsumerCompatSupported));
    public string UsbHidCompatibilityState=>Get<string>(nameof(UsbHidCompatibilityState));
    public bool CanEditHidConsumerCompat=>Get<bool>(nameof(CanEditHidConsumerCompat));
    public string UsbDeviceProfile{get=>Get<string>(nameof(UsbDeviceProfile));set=>SetRemote(nameof(UsbDeviceProfile),value);}
    public string CommittedUsbDeviceProfile=>Get<string>(nameof(CommittedUsbDeviceProfile));
    public string ActiveUsbDeviceProfile=>Get<string>(nameof(ActiveUsbDeviceProfile));
    public string[] UsbDeviceProfiles=>Get<string[]>(nameof(UsbDeviceProfiles))??[];
    public bool CanEditUsbProfile=>Get<bool>(nameof(CanEditUsbProfile));
    public string UsbProfileBlockReason=>Get<string>(nameof(UsbProfileBlockReason));
}
