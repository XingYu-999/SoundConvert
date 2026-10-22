namespace SoundConvertClient;
public interface IBridgeView
{
    bool AutoReconnectEnabled { get; set; }
    bool IsReconnecting { get; }
    string ReconnectStatus { get; }
    string[] IndicatorModeOptions { get; }
    int IndicatorModeIndex { get; set; }
    bool IndicatorConfigurationSupported { get; }
    bool CanEditIndicator { get; }
    bool CanIdentifyIndicator { get; }
    string IndicatorConfigurationState { get; }
    string IndicatorModeDescription { get; }
    System.Windows.Input.ICommand IdentifyIndicatorCommand { get; }
    string BoardPlaybackUdpPort {get;set;}
    string SoftwarePlaybackUdpPort {get;set;}
    string BoardCaptureUdpPort {get;set;}
    string SoftwareCaptureUdpPort {get;set;}
    bool BoardPlaybackUdpPortAutomatic {get;set;}
    bool SoftwarePlaybackUdpPortAutomatic {get;set;}
    bool BoardCaptureUdpPortAutomatic {get;set;}
    bool SoftwareCaptureUdpPortAutomatic {get;set;}
    string BoardPlaybackUdpPortNumber {get;set;}
    string SoftwarePlaybackUdpPortNumber {get;set;}
    string BoardCaptureUdpPortNumber {get;set;}
    string SoftwareCaptureUdpPortNumber {get;set;}
    int LastAutomaticBoardPlaybackUdpPort {get;}
    int LastAutomaticSoftwarePlaybackUdpPort {get;}
    int LastAutomaticBoardCaptureUdpPort {get;}
    int LastAutomaticSoftwareCaptureUdpPort {get;}
    string UdpPortSettingsStatus {get;}
    string UdpPortNegotiationStatus {get;}
    System.Windows.Input.ICommand SaveUdpPortsCommand {get;}
    int ClockModeIndex {get;set;}
    System.Collections.Generic.IReadOnlyList<string> ClockModes {get;}
    string ClockStatus {get;}
    int MonitorIntervalSeconds {get;set;}
    int[] MonitorIntervals {get;}
    System.Func<ManualConnectViewModel, System.Threading.Tasks.Task>? RequestManualConnectDialog { get; set; }
    System.Windows.Input.ICommand ManualConnectCommand { get; }
    System.Windows.Input.ICommand ConnectDeviceCommand { get; }
    System.Windows.Input.ICommand ConfigureDeviceCommand { get; }
    bool HasPendingNetworkTransaction { get; }
    System.Windows.Input.ICommand ApplyOutputSettingsCommand { get; }
    string OutputSettingsStatus { get; }
    string InputModeHint { get; }
    bool HasInputModeHint { get; }

    bool CanControlCaptureVolume{get;}
    double CaptureUsbVolumeDb{get;set;}
    bool CaptureUsbMuted{get;set;}
    string CaptureUsbControlSource{get;}

    bool AudioProcessingEnabled {get;set;}
    double AudioBalance {get;set;}
    bool AudioMono {get;set;}
    bool AudioEqEnabled {get;set;}
    double AudioEqLowDb {get;set;}
    double AudioEqMidDb {get;set;}
    double AudioEqHighDb {get;set;}
    bool AudioLimiterEnabled {get;set;}
    double AudioLimiterCeilingDb {get;set;}
    bool OutputLimitEnabled {get;set;}
    double OutputGainLimit {get;set;}
    int ResumeFadeMs {get;set;}
    double LevelLeftDb {get;}
    double LevelRightDb {get;}
    double LevelLeftRmsDb {get;}
    double LevelRightRmsDb {get;}
    string LevelStatus {get;}
    string LevelClipStatus {get;}
    string AudioProcessingStatus {get;}

    bool UsbHidConsumerCompat{get;set;}
    bool HidConsumerCompatSupported{get;}
    bool CanEditHidConsumerCompat{get;}
    string UsbHidCompatibilityState{get;}
    string UsbDeviceProfile {get;set;}
    string CommittedUsbDeviceProfile {get;}
    string ActiveUsbDeviceProfile {get;}
    string[] UsbDeviceProfiles {get;}
    bool CanEditUsbProfile {get;}
    string UsbProfileBlockReason {get;}

    bool CanControlBoardGain { get; }
    double BoardVolumePercent { get; set; }
    bool BoardMuted { get; set; }
    string BoardGainReadout { get; }
    string HostKeyBlockReason { get; }
    string HostKeyFeedback { get; }
    string HostKeyTransport { get; }
    string HostMediaCapability { get; }
    System.Windows.Input.ICommand HostPlayPauseCommand { get; }
    System.Windows.Input.ICommand HostNextTrackCommand { get; }
    System.Windows.Input.ICommand HostPreviousTrackCommand { get; }
    System.Windows.Input.ICommand HostVolumeUpCommand { get; }
    System.Windows.Input.ICommand HostVolumeDownCommand { get; }
    System.Windows.Input.ICommand HostMuteCommand { get; }
    System.Boolean IsConnecting { get;  }
    System.String ConnectionButtonText { get;  }
    System.Boolean CanToggleConnection { get;  }
    System.String AutoConnectSummary { get;  }
    System.Boolean AutoConnectCurrent { get; set; }
    System.Boolean CanConfigureAutoConnect { get;  }
    System.Windows.Input.ICommand ToggleConnectionCommand { get;  }
    System.Windows.Input.ICommand ClearAutoConnectCommand { get;  }
    System.Collections.ObjectModel.ObservableCollection<SoundConvertClient.DeviceRow> Devices { get;  }
    System.Collections.ObjectModel.ObservableCollection<SoundConvertClient.OutputEndpoint> Outputs { get;  }
    System.Collections.ObjectModel.ObservableCollection<SoundConvertClient.UiLogEntry> LogEntries { get;  }
    System.Boolean IsConnected { get;  }
    System.Boolean IsConnectionReady { get; }
    System.Boolean IsObserver { get;  }
    System.Boolean IsListening { get;  }
    System.Boolean CanManage { get;  }
    System.Boolean CanEditUsb { get;  }
    System.Boolean CanEditHiRes { get;  }
    System.String UsbBlockReason { get;  }
    System.String Address { get; set; }
    System.String LocalAddress { get;  }
    System.Int32 ControlPort { get; set; }
    System.String Status { get;  }
    System.String Message { get; set; }
    System.String DeviceInfo { get; set; }
    System.String Logs { get; set; }
    System.String AudioStats { get; set; }
    System.String ConnectedName { get;  }
    System.String UsbSerial { get;  }
    System.String UsbState { get;  }
    System.String PlaybackLabel { get;  }
    System.String DeclaredFormats { get;  }
    System.String ActualFormat { get;  }
    System.String FirmwareVersion { get;  }
    System.String NetworkLabel { get;  }
    System.String DeviceIdentity { get;  }
    System.String PacketCount { get;  }
    double PlaybackBufferMs { get; }
    double PlaybackDriftPpm { get; }
    bool HasPlaybackMetrics { get; }
    long PlaybackMetricSequence { get; }
    long PlaybackMetricGeneration { get; }
    System.String BufferLevel { get;  }
    System.String DriftText { get;  }
    System.String IntegrityText { get;  }
    System.String OutputBackend { get;  }
    System.String ProductName { get; set; }
    System.Boolean HiResEnabled { get; set; }
    System.Boolean HiResSupported { get;  }
    System.String UsbDraftState { get;  }
    System.Double Volume { get; set; }
    System.Boolean Muted { get; set; }
    SoundConvertClient.DeviceRow? SelectedDevice { get; set; }
    SoundConvertClient.OutputEndpoint? SelectedOutput { get; set; }
    System.Collections.ObjectModel.ObservableCollection<SoundConvertClient.InputEndpoint> Inputs { get;  }
    System.Collections.ObjectModel.ObservableCollection<SoundConvertClient.CaptureFormatOption> CaptureFormatOptions { get;  }
    SoundConvertClient.InputEndpoint? SelectedInput { get; set; }
    System.Boolean MicrophoneSupported { get;  }
    System.String InputName { get;  }
    System.String InputFormat { get;  }
    System.String InputStatus { get;  }
    System.String InputDetection { get;  }
    System.Double InputPeak { get;  }
    System.Boolean InputDetecting { get;  }
    System.String BoardCaptureStatus { get;  }
    System.String BoardCaptureFormat { get;  }
    System.String MicrophoneDiagnostics { get;  }
    System.Boolean IsMicrophoneSending { get;  }
    System.Boolean CanStartMicrophone { get;  }
    System.Boolean MicrophonePlaybackFirst { get; set; }
    System.Double MicrophoneGain { get; set; }
    System.Boolean MicrophoneMuted { get; set; }
    System.String CapturePolicyHint { get;  }
    System.Windows.Input.ICommand RefreshInputsCommand { get;  }
    System.Windows.Input.ICommand DetectInputCommand { get;  }
    System.Windows.Input.ICommand StartMicrophoneCommand { get;  }
    System.Windows.Input.ICommand StopMicrophoneCommand { get;  }
    System.Boolean MonitorEnabled { get;  }
    System.String MonitorButtonText { get;  }
    System.String MonitorDetails { get;  }
    System.String MonitorStatus { get;  }
    System.String BoardMac { get;  }
    System.String BoardUsbVersion { get;  }
    System.String BoardUacVersion { get;  }
    System.String BoardLinkSpeed { get;  }
    System.String ControlPortLabel { get;  }
    System.String PlaybackPorts { get;  }
    System.String CapturePorts { get;  }
    System.Windows.Input.ICommand ToggleMonitorCommand { get;  }
    System.Collections.ObjectModel.ObservableCollection<UsbAudioBridge.NetworkAdapterInfo> SearchAdapters { get;  }
    UsbAudioBridge.NetworkAdapterInfo? SelectedSearchAdapter { get; set; }
    System.Boolean CanSelectSearchAdapter { get;  }
    System.Boolean CanDiscover { get;  }
    System.String SearchAdapterSummary { get;  }
    System.Windows.Input.ICommand RefreshSearchAdaptersCommand { get;  }
    System.Collections.ObjectModel.ObservableCollection<SoundConvertClient.FormatOption> FormatOptions { get;  }
    System.String OutputName { get;  }
    System.String OutputFormat { get;  }
    System.String UsbProtocol { get;  }
    System.String ConnectedAddress { get;  }
    System.Boolean CanControlOutput { get;  }
    System.Double OutputVolume { get; set; }
    System.Boolean OutputMuted { get; set; }
    System.Boolean CanConfigureFormats { get;  }
    System.String FormatPolicyHint { get;  }
    System.Boolean IsBusy { get;  }
    System.String GlobalError { get;  }
    System.Boolean HasGlobalError { get;  }
    System.String OperationStatus { get;  }
    System.String SelectedPage { get; set; }
    System.Boolean IsOverview { get;  }
    System.Boolean IsDevices { get;  }
    System.Boolean IsAudio { get;  }
    System.Boolean IsInput { get;  }
    System.Boolean IsUsb { get;  }
    System.Boolean IsNetwork { get;  }
    System.Boolean IsLogs { get;  }
    System.Boolean IsSettings { get;  }
    System.Boolean IsAbout { get;  }
    System.Boolean IsChangelog { get;  }
    System.Boolean IsNavigationExpanded { get; set; }
    System.Double NavigationWidth { get;  }
    System.Boolean MinimizeToTray { get; set; }
    System.Boolean ConnectionAnnouncementEnabled { get; set; }
    System.String UserDataDirectory { get;  }
    System.String AppVersion { get;  }
    System.String RuntimeVersion { get;  }
    System.Boolean HasRemembered { get;  }
    System.String RememberedSummary { get;  }
    System.String DiscoverySummary { get;  }
    System.String HeaderStatus { get;  }
    System.String OverviewHeading { get;  }
    System.String OverviewHint { get;  }
    System.Boolean CanConnect { get;  }
    System.Collections.ObjectModel.ObservableCollection<SoundConvertClient.RememberedBoardRow> RememberedBoards { get;  }
    System.Func<System.Threading.Tasks.Task>? RequestExit { get; set; }
    System.Action? RequestHide { get; set; }
    System.Windows.Input.ICommand NavigateCommand { get;  }
    System.Windows.Input.ICommand ToggleNavigationCommand { get;  }
    System.Windows.Input.ICommand DiscoverCommand { get;  }
    System.Windows.Input.ICommand DiscoverBroadcastCommand { get;  }
    System.Windows.Input.ICommand ConnectCommand { get;  }
    System.Windows.Input.ICommand DisconnectCommand { get;  }
    System.Windows.Input.ICommand QuickSearchCommand { get;  }
    System.Windows.Input.ICommand QuickConnectCommand { get;  }
    System.Windows.Input.ICommand ConnectRememberedCommand { get;  }
    System.Windows.Input.ICommand ForgetBoardCommand { get;  }
    System.Windows.Input.ICommand RefreshCommand { get;  }
    System.Windows.Input.ICommand CapabilitiesCommand { get;  }
    System.Windows.Input.ICommand ConfigCommand { get;  }
    System.Windows.Input.ICommand StatsCommand { get;  }
    System.Windows.Input.ICommand SaveUsbCommand { get;  }
    System.Windows.Input.ICommand DiscardUsbDraftCommand { get;  }
    System.Windows.Input.ICommand RefreshOutputsCommand { get;  }
    System.Windows.Input.ICommand BufferProfileCommand { get;  }
    System.Windows.Input.ICommand SaveSettingsCommand { get;  }
    System.Windows.Input.ICommand ExportCommand { get;  }
    System.Windows.Input.ICommand OpenDataCommand { get;  }
    System.Windows.Input.ICommand ClearLogsCommand { get;  }
    System.Windows.Input.ICommand ExitCommand { get;  }
    System.Windows.Input.ICommand ShutdownEngineCommand { get; }
    System.Windows.Input.ICommand HideCommand { get;  }
    System.Windows.Input.ICommand OpenWindowsSoundCommand { get;  }
    System.Windows.Input.ICommand OpenReferenceCommand { get;  }
    System.Windows.Input.ICommand ReadNetworkCommand { get;  }
    System.Windows.Input.ICommand ApplyNetworkCommand { get;  }
    System.Windows.Input.ICommand QueryTransactionCommand { get;  }
    System.Windows.Input.ICommand ConfirmNetworkCommand { get;  }
    System.Windows.Input.ICommand RollbackNetworkCommand { get;  }
    System.Windows.Input.ICommand ApplyBroadcastCommand { get;  }
    System.Windows.Input.ICommand ConfirmBroadcastCommand { get;  }
    System.Windows.Input.ICommand QueryBroadcastCommand { get;  }
    System.Windows.Input.ICommand RollbackBroadcastCommand { get;  }
    System.Boolean IsUsbEmulating { get;  }
    System.String UsbButtonText { get;  }
    System.Boolean CanToggleUsb { get;  }
    System.Boolean IsMonitoring { get;  }
    System.String ListeningButtonText { get;  }
    System.Boolean CanToggleListening { get;  }
    System.Windows.Input.ICommand ToggleUsbCommand { get;  }
    System.Windows.Input.ICommand ToggleListeningCommand { get;  }
    System.Int32 MaximumBufferMs { get; set; }
    System.Collections.Generic.IReadOnlyList<System.String> ResamplingQualities { get;  }
    UsbAudioBridge.ResamplerQuality ResamplingQuality { get; set; }
    System.Int32 ResamplingQualityIndex { get; set; }
    System.Boolean NetworkFecEnabled { get; set; }
    UsbAudioBridge.AudioTransportCodec TransportCodec { get; set; }
    System.Int32 TransportCodecIndex { get; set; }
    System.Boolean CompressionSupported { get;  }
    System.String BufferBudgetSummary { get;  }
    System.String[] NetworkModes { get;  }
    System.String NetworkMode { get; set; }
    System.String NetworkIp { get; set; }
    System.String NetworkGateway { get; set; }
    System.String NetworkDns { get; set; }
    System.Int32 NetworkPrefix { get; set; }
    System.String PendingTransaction { get; set; }
    System.String NetworkStatus { get; set; }
    System.Boolean CloseToTray { get; set; }
    System.Boolean StartInTray { get; set; }
    System.Boolean StartAtLogin { get; set; }
    System.Boolean CanModifyDiscoveredIp { get;  }
    System.Func<SoundConvertClient.IpAddressEditor,System.Threading.Tasks.Task>? RequestIpAddressDialog { get; set; }
    System.Windows.Input.ICommand ModifyDiscoveredIpCommand { get;  }
    System.Boolean CanDiscoverBroadcast { get;  }
    System.String UdpDiscoveryHint { get;  }
    System.String BroadcastAvailability { get;  }
    bool IsDiscovering { get; }
    bool CanCancelSearch { get; }
    bool CanCancelOperation { get; }
    bool CanCancelConnection { get; }
    System.Windows.Input.ICommand CancelSearchCommand { get; }
    System.Windows.Input.ICommand CancelConnectionCommand { get; }
}
