namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    // These properties describe a currently verified session, not remembered
    // targets. Recovery checks the session's permanent ID independently of UI.
    void InvalidateCurrentBoardState()
    {
        FirmwareVersion=DeviceIdentity=BoardMac=BoardUsbVersion=BoardUacVersion=BoardLinkSpeed=NetworkLabel=UsbProtocol="—";
        ConnectedName="开发板";UsbSerial="";UsbState="未知";Status="未连接";DeviceInfo="未连接，尚无当前设备信息。";
        DeclaredFormats="未读取当前设备格式声明";ActualFormat="USB 主机尚未选择音频格式";lastHostFormat=null;
        BoardCaptureStatus="未连接";BoardCaptureFormat="—";
        activeUsbDeviceProfile="unknown";activeHidConsumerCompat=false;usbDeviceProfiles=[];hidConsumerCompatSupported=false;
        udpPortNegotiationSupported=false;RefreshUdpPortStatus();configLoaded=false;ResetIndicatorState();usbHostStreaming=usbHostFormatSelected=false;
        HiResSupported=CompressionSupported=MicrophoneSupported=false;
        formatSelectionSupported=internalVolumeSupported=runtimeStatusSupported=captureVolumeSupported=false;
        lastMonitorSample=default;MonitorDetails="未连接，尚无当前设备状态快照。";
        foreach(string name in new[]{nameof(CommittedUsbDeviceProfile),nameof(ActiveUsbDeviceProfile),nameof(UsbDeviceProfiles),nameof(HidConsumerCompatSupported),nameof(CanEditUsbProfile),nameof(CanEditHidConsumerCompat),nameof(CanControlCaptureVolume),nameof(UsbProfileBlockReason),nameof(UsbHidCompatibilityState),nameof(UsbDraftState)})Changed(name);
        RefreshFormatOptions();RefreshInputOptions();
    }
}
