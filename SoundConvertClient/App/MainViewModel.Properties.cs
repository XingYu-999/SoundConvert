// Typed binding surface for the IPC-backed UI. Runtime state lives in SoundConvertEngine.
using System.Windows.Input;
namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    public bool AutoReconnectEnabled { get=>Get<bool>(nameof(AutoReconnectEnabled)); set=>SetRemote(nameof(AutoReconnectEnabled),value); }
    public bool IsReconnecting => Get<bool>(nameof(IsReconnecting));
    public string ReconnectStatus => Get<string>(nameof(ReconnectStatus));
    public System.Boolean IsConnecting { get => backendAvailable && (deviceActionConnecting || Get<System.Boolean>(nameof(IsConnecting))); }
    public System.String ConnectionButtonText { get => Get<System.String>(nameof(ConnectionButtonText)); }
    public System.Boolean CanToggleConnection { get => !deviceActionInFlight && Get<System.Boolean>(nameof(CanToggleConnection)); }
    public System.String AutoConnectSummary { get => Get<System.String>(nameof(AutoConnectSummary)); }
    public System.Boolean AutoConnectCurrent { get => Get<System.Boolean>(nameof(AutoConnectCurrent)); set => SetRemote(nameof(AutoConnectCurrent), value); }
    public System.Boolean CanConfigureAutoConnect { get => Get<System.Boolean>(nameof(CanConfigureAutoConnect)); }
    public ICommand ToggleConnectionCommand => Command(nameof(ToggleConnectionCommand));
    public ICommand ClearAutoConnectCommand => Command(nameof(ClearAutoConnectCommand));
    public System.Boolean IsConnected { get => Get<System.Boolean>(nameof(IsConnected)); }
    public bool IsConnectionReady => IsConnected && !IsConnecting;
    public System.Boolean IsObserver { get => Get<System.Boolean>(nameof(IsObserver)); }
    public System.Boolean IsListening { get => Get<System.Boolean>(nameof(IsListening)); }
    public System.Boolean CanManage { get => Get<System.Boolean>(nameof(CanManage)); }
    public System.Boolean CanEditUsb { get => Get<System.Boolean>(nameof(CanEditUsb)); }
    public System.Boolean CanEditHiRes { get => Get<System.Boolean>(nameof(CanEditHiRes)); }
    public System.String UsbBlockReason { get => Get<System.String>(nameof(UsbBlockReason)); }
    public System.String Address { get => Get<System.String>(nameof(Address)); set => SetRemote(nameof(Address), value); }
    public System.String LocalAddress { get => Get<System.String>(nameof(LocalAddress)); }
    public System.Int32 ControlPort { get => Get<System.Int32>(nameof(ControlPort)); set => SetRemote(nameof(ControlPort), value); }
    public System.String Status { get => deviceActionProgress.Length != 0 ? deviceActionProgress : Get<System.String>(nameof(Status)); }
    public System.String Message { get => Get<System.String>(nameof(Message)); set => SetRemote(nameof(Message), value); }
    public System.String DeviceInfo { get => Get<System.String>(nameof(DeviceInfo)); set => SetRemote(nameof(DeviceInfo), value); }
    public System.String Logs { get => Get<System.String>(nameof(Logs)); set => SetRemote(nameof(Logs), value); }
    public System.String AudioStats { get => Get<System.String>(nameof(AudioStats)); set => SetRemote(nameof(AudioStats), value); }
    public System.String ConnectedName { get => Get<System.String>(nameof(ConnectedName)); }
    public System.String UsbSerial { get => Get<System.String>(nameof(UsbSerial)); }
    public System.String UsbState { get => Get<System.String>(nameof(UsbState)); }
    public System.String PlaybackLabel { get => Get<System.String>(nameof(PlaybackLabel)); }
    public System.String DeclaredFormats { get => Get<System.String>(nameof(DeclaredFormats)); }
    public System.String ActualFormat { get => Get<System.String>(nameof(ActualFormat)); }
    public System.String FirmwareVersion { get => Get<System.String>(nameof(FirmwareVersion)); }
    public System.String NetworkLabel { get => Get<System.String>(nameof(NetworkLabel)); }
    public System.String DeviceIdentity { get => Get<System.String>(nameof(DeviceIdentity)); }
    public System.String PacketCount { get => Get<System.String>(nameof(PacketCount)); }
    public double PlaybackBufferMs => Get<double>(nameof(PlaybackBufferMs));
    public double PlaybackDriftPpm => Get<double>(nameof(PlaybackDriftPpm));
    public bool HasPlaybackMetrics => backendAvailable && Get<bool>(nameof(HasPlaybackMetrics));
    public long PlaybackMetricSequence => Get<long>(nameof(PlaybackMetricSequence));
    public long PlaybackMetricGeneration => playbackMetricEpoch * (1L<<32) + Get<long>(nameof(PlaybackMetricGeneration));
    public System.String BufferLevel { get => Get<System.String>(nameof(BufferLevel)); }
    public System.String DriftText { get => Get<System.String>(nameof(DriftText)); }
    public System.String IntegrityText { get => Get<System.String>(nameof(IntegrityText)); }
    public System.String OutputBackend { get => Get<System.String>(nameof(OutputBackend)); }
    public System.String ProductName { get => Get<System.String>(nameof(ProductName)); set => SetRemote(nameof(ProductName), value); }
    public System.Boolean HiResEnabled { get => Get<System.Boolean>(nameof(HiResEnabled)); set => SetRemote(nameof(HiResEnabled), value); }
    public System.Boolean HiResSupported { get => Get<System.Boolean>(nameof(HiResSupported)); }
    public System.String UsbDraftState { get => Get<System.String>(nameof(UsbDraftState)); }
    public System.Double Volume { get => Get<System.Double>(nameof(Volume)); set => SetRemote(nameof(Volume), value); }
    public System.Boolean Muted { get => Get<System.Boolean>(nameof(Muted)); set => SetRemote(nameof(Muted), value); }
    public System.Boolean MicrophoneSupported { get => Get<System.Boolean>(nameof(MicrophoneSupported)); }
    public System.String InputName { get => Get<System.String>(nameof(InputName)); }
    public System.String InputFormat { get => Get<System.String>(nameof(InputFormat)); }
    public System.String InputStatus { get => Get<System.String>(nameof(InputStatus)); }
    public System.String InputDetection { get => Get<System.String>(nameof(InputDetection)); }
    public System.Double InputPeak { get => Get<System.Double>(nameof(InputPeak)); }
    public System.Boolean InputDetecting { get => Get<System.Boolean>(nameof(InputDetecting)); }
    public System.String BoardCaptureStatus { get => Get<System.String>(nameof(BoardCaptureStatus)); }
    public System.String BoardCaptureFormat { get => Get<System.String>(nameof(BoardCaptureFormat)); }
    public System.String MicrophoneDiagnostics { get => Get<System.String>(nameof(MicrophoneDiagnostics)); }
    public System.Boolean IsMicrophoneSending { get => Get<System.Boolean>(nameof(IsMicrophoneSending)); }
    public System.Boolean CanStartMicrophone { get => !pendingUiCommands.Contains("StartMicrophoneCommand") && Get<System.Boolean>(nameof(CanStartMicrophone)); }
    public System.Boolean MicrophonePlaybackFirst { get => Get<System.Boolean>(nameof(MicrophonePlaybackFirst)); set => SetRemote(nameof(MicrophonePlaybackFirst), value); }
    public System.Double MicrophoneGain { get => Get<System.Double>(nameof(MicrophoneGain)); set => SetRemote(nameof(MicrophoneGain), value); }
    public System.Boolean MicrophoneMuted { get => Get<System.Boolean>(nameof(MicrophoneMuted)); set => SetRemote(nameof(MicrophoneMuted), value); }
    public System.String CapturePolicyHint { get => Get<System.String>(nameof(CapturePolicyHint)); }
    public ICommand RefreshInputsCommand => Command(nameof(RefreshInputsCommand));
    public ICommand DetectInputCommand => Command(nameof(DetectInputCommand));
    public ICommand StartMicrophoneCommand => Command(nameof(StartMicrophoneCommand));
    public ICommand StopMicrophoneCommand => Command(nameof(StopMicrophoneCommand));
    public System.Boolean MonitorEnabled { get => Get<System.Boolean>(nameof(MonitorEnabled)); }
    public System.String MonitorButtonText { get => Get<System.String>(nameof(MonitorButtonText)); }
    public System.String MonitorDetails { get => Get<System.String>(nameof(MonitorDetails)); }
    public System.String MonitorStatus { get => Get<System.String>(nameof(MonitorStatus)); }
    public int MonitorIntervalSeconds { get => Get<int>(nameof(MonitorIntervalSeconds)); set => SetRemote(nameof(MonitorIntervalSeconds),value); }
    public int[] MonitorIntervals => [1,3,5,10];
    public System.String BoardMac { get => Get<System.String>(nameof(BoardMac)); }
    public System.String BoardUsbVersion { get => Get<System.String>(nameof(BoardUsbVersion)); }
    public System.String BoardUacVersion { get => Get<System.String>(nameof(BoardUacVersion)); }
    public System.String BoardLinkSpeed { get => Get<System.String>(nameof(BoardLinkSpeed)); }
    public System.String ControlPortLabel { get => Get<System.String>(nameof(ControlPortLabel)); }
    public System.String PlaybackPorts { get => Get<System.String>(nameof(PlaybackPorts)); }
    public System.String CapturePorts { get => Get<System.String>(nameof(CapturePorts)); }
    public ICommand ToggleMonitorCommand => Command(nameof(ToggleMonitorCommand));
    public System.Boolean CanSelectSearchAdapter { get => !deviceActionInFlight && Get<System.Boolean>(nameof(CanSelectSearchAdapter)); }
    public System.Boolean CanDiscover { get => !deviceActionInFlight && Get<System.Boolean>(nameof(CanDiscover)); }
    public System.String SearchAdapterSummary { get => Get<System.String>(nameof(SearchAdapterSummary)); }
    public ICommand RefreshSearchAdaptersCommand => Command(nameof(RefreshSearchAdaptersCommand));
    public System.String OutputName { get => Get<System.String>(nameof(OutputName)); }
    public System.String OutputFormat { get => Get<System.String>(nameof(OutputFormat)); }
    public System.String UsbProtocol { get => Get<System.String>(nameof(UsbProtocol)); }
    public System.String ConnectedAddress { get => Get<System.String>(nameof(ConnectedAddress)); }
    public System.Boolean CanControlOutput { get => Get<System.Boolean>(nameof(CanControlOutput)); }
    public System.Double OutputVolume { get => Get<System.Double>(nameof(OutputVolume)); set => SetRemote(nameof(OutputVolume), value); }
    public System.Boolean OutputMuted { get => Get<System.Boolean>(nameof(OutputMuted)); set => SetRemote(nameof(OutputMuted), value); }
    public System.Boolean CanConfigureFormats { get => Get<System.Boolean>(nameof(CanConfigureFormats)); }
    public System.String FormatPolicyHint { get => Get<System.String>(nameof(FormatPolicyHint)); }
    public System.Boolean IsBusy { get => backendAvailable && (deviceActionBusy || pendingUiCommands.Count != 0 || Get<System.Boolean>(nameof(IsBusy))); }
    public System.String OperationStatus { get => deviceActionProgress.Length != 0 ? deviceActionProgress : localUiOperation.Length != 0 ? localUiOperation : backendAvailable ? Get<System.String>(nameof(OperationStatus)) : ""; }
    public System.Boolean MinimizeToTray { get => Get<System.Boolean>(nameof(MinimizeToTray)); set => SetRemote(nameof(MinimizeToTray), value); }
    public System.Boolean ConnectionAnnouncementEnabled { get => Get<System.Boolean>(nameof(ConnectionAnnouncementEnabled)); set => SetRemote(nameof(ConnectionAnnouncementEnabled), value); }
    public System.String UserDataDirectory { get => Get<System.String>(nameof(UserDataDirectory)); }
    public System.Boolean HasRemembered { get => Get<System.Boolean>(nameof(HasRemembered)); }
    public System.String RememberedSummary { get => Get<System.String>(nameof(RememberedSummary)); }
    public System.String DiscoverySummary { get => Get<System.String>(nameof(DiscoverySummary)); }
    public System.String HeaderStatus { get => Get<System.String>(nameof(HeaderStatus)); }
    public System.String OverviewHeading { get => Get<System.String>(nameof(OverviewHeading)); }
    public System.String OverviewHint { get => Get<System.String>(nameof(OverviewHint)); }
    public System.Boolean CanConnect { get => Get<System.Boolean>(nameof(CanConnect)); }
    public ICommand NavigateCommand => Command(nameof(NavigateCommand));
    public ICommand ToggleNavigationCommand => Command(nameof(ToggleNavigationCommand));
    public ICommand DiscoverCommand => Command(nameof(DiscoverCommand));
    public ICommand DiscoverBroadcastCommand => Command(nameof(DiscoverBroadcastCommand));
    public ICommand ConnectCommand => Command(nameof(ConnectCommand));
    public ICommand DisconnectCommand => Command(nameof(DisconnectCommand));
    public ICommand QuickSearchCommand => Command(nameof(QuickSearchCommand));
    public ICommand QuickConnectCommand => Command(nameof(QuickConnectCommand));
    public ICommand ConnectRememberedCommand => Command(nameof(ConnectRememberedCommand));
    public ICommand ForgetBoardCommand => Command(nameof(ForgetBoardCommand));
    public ICommand RefreshCommand => Command(nameof(RefreshCommand));
    public ICommand CapabilitiesCommand => Command(nameof(CapabilitiesCommand));
    public ICommand ConfigCommand => Command(nameof(ConfigCommand));
    public ICommand StatsCommand => Command(nameof(StatsCommand));
    public ICommand SaveUsbCommand => Command(nameof(SaveUsbCommand));
    public ICommand DiscardUsbDraftCommand => Command(nameof(DiscardUsbDraftCommand));
    public ICommand RefreshOutputsCommand => Command(nameof(RefreshOutputsCommand));
    public ICommand BufferProfileCommand => Command(nameof(BufferProfileCommand));
    public ICommand SaveSettingsCommand => Command(nameof(SaveSettingsCommand));
    public ICommand ExportCommand => Command(nameof(ExportCommand));
    public ICommand OpenDataCommand => Command(nameof(OpenDataCommand));
    public ICommand ClearLogsCommand => Command(nameof(ClearLogsCommand));
    public ICommand ExitCommand => Command(nameof(ExitCommand));
    public ICommand HideCommand => Command(nameof(HideCommand));
    public ICommand OpenWindowsSoundCommand => Command(nameof(OpenWindowsSoundCommand));
    public ICommand OpenReferenceCommand => Command(nameof(OpenReferenceCommand));
    public ICommand ReadNetworkCommand => Command(nameof(ReadNetworkCommand));
    public ICommand ApplyNetworkCommand => Command(nameof(ApplyNetworkCommand));
    public ICommand QueryTransactionCommand => Command(nameof(QueryTransactionCommand));
    public ICommand ConfirmNetworkCommand => Command(nameof(ConfirmNetworkCommand));
    public ICommand RollbackNetworkCommand => Command(nameof(RollbackNetworkCommand));
    public ICommand ApplyBroadcastCommand => Command(nameof(ApplyBroadcastCommand));
    public ICommand ConfirmBroadcastCommand => Command(nameof(ConfirmBroadcastCommand));
    public ICommand QueryBroadcastCommand => Command(nameof(QueryBroadcastCommand));
    public ICommand RollbackBroadcastCommand => Command(nameof(RollbackBroadcastCommand));
    public System.Boolean IsUsbEmulating { get => Get<System.Boolean>(nameof(IsUsbEmulating)); }
    public System.String UsbButtonText { get => Get<System.String>(nameof(UsbButtonText)); }
    public System.Boolean CanToggleUsb { get => Get<System.Boolean>(nameof(CanToggleUsb)); }
    public System.Boolean IsMonitoring { get => Get<System.Boolean>(nameof(IsMonitoring)); }
    public System.String ListeningButtonText { get => localOutputAction.Length != 0 ? localOutputAction : Get<System.String>(nameof(ListeningButtonText)); }
    public System.Boolean CanToggleListening { get => !OutputCommandPending && Get<System.Boolean>(nameof(CanToggleListening)); }
    public ICommand ToggleUsbCommand => Command(nameof(ToggleUsbCommand));
    public ICommand ToggleListeningCommand => Command(nameof(ToggleListeningCommand));
    public System.Int32 MaximumBufferMs { get => Get<System.Int32>(nameof(MaximumBufferMs)); set => SetRemote(nameof(MaximumBufferMs), value); }
    public System.Collections.Generic.IReadOnlyList<string> ResamplingQualities { get => Get<System.Collections.Generic.IReadOnlyList<string>>(nameof(ResamplingQualities)); }
    public UsbAudioBridge.ResamplerQuality ResamplingQuality { get => Get<UsbAudioBridge.ResamplerQuality>(nameof(ResamplingQuality)); set => SetRemote(nameof(ResamplingQuality), value); }
    public System.Int32 ResamplingQualityIndex { get => Get<System.Int32>(nameof(ResamplingQualityIndex)); set => SetRemote(nameof(ResamplingQualityIndex), value); }
    public System.Boolean NetworkFecEnabled { get => Get<System.Boolean>(nameof(NetworkFecEnabled)); set => SetRemote(nameof(NetworkFecEnabled), value); }
    public UsbAudioBridge.AudioTransportCodec TransportCodec { get => Get<UsbAudioBridge.AudioTransportCodec>(nameof(TransportCodec)); set => SetRemote(nameof(TransportCodec), value); }
    public System.Int32 TransportCodecIndex { get => Get<System.Int32>(nameof(TransportCodecIndex)); set => SetRemote(nameof(TransportCodecIndex), value); }
    public System.Boolean CompressionSupported { get => Get<System.Boolean>(nameof(CompressionSupported)); }
    public System.String BufferBudgetSummary { get => Get<System.String>(nameof(BufferBudgetSummary)); }
    public System.String[] NetworkModes { get => Get<System.String[]>(nameof(NetworkModes)); }
    public System.String NetworkMode { get => Get<System.String>(nameof(NetworkMode)); set => SetRemote(nameof(NetworkMode), value); }
    public System.String NetworkIp { get => Get<System.String>(nameof(NetworkIp)); set => SetRemote(nameof(NetworkIp), value); }
    public System.String NetworkGateway { get => Get<System.String>(nameof(NetworkGateway)); set => SetRemote(nameof(NetworkGateway), value); }
    public System.String NetworkDns { get => Get<System.String>(nameof(NetworkDns)); set => SetRemote(nameof(NetworkDns), value); }
    public System.Int32 NetworkPrefix { get => Get<System.Int32>(nameof(NetworkPrefix)); set => SetRemote(nameof(NetworkPrefix), value); }
    public System.String PendingTransaction { get => Get<System.String>(nameof(PendingTransaction)); set => SetRemote(nameof(PendingTransaction), value); }
    public System.String NetworkStatus { get => Get<System.String>(nameof(NetworkStatus)); set => SetRemote(nameof(NetworkStatus), value); }
    public System.Boolean CloseToTray { get => Get<System.Boolean>(nameof(CloseToTray)); set => SetRemote(nameof(CloseToTray), value); }
    public System.Boolean StartInTray { get => Get<System.Boolean>(nameof(StartInTray)); set => SetRemote(nameof(StartInTray), value); }
    public System.Boolean StartAtLogin { get => Get<System.Boolean>(nameof(StartAtLogin)); set => SetRemote(nameof(StartAtLogin), value); }
    public System.Boolean CanModifyDiscoveredIp { get => Get<System.Boolean>(nameof(CanModifyDiscoveredIp)); }
    public ICommand ModifyDiscoveredIpCommand => Command(nameof(ModifyDiscoveredIpCommand));
    public System.Boolean CanDiscoverBroadcast { get => Get<System.Boolean>(nameof(CanDiscoverBroadcast)); }
    public System.String UdpDiscoveryHint { get => Get<System.String>(nameof(UdpDiscoveryHint)); }
    public System.String BroadcastAvailability { get => Get<System.String>(nameof(BroadcastAvailability)); }
}
