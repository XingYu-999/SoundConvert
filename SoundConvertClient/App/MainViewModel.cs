using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using UsbAudioBridge;

namespace SoundConvertClient;

/// <summary>UI state and IPC proxy. Owns no audio device, board socket, or playback lifetime.</summary>
public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable, IBridgeView
{
    readonly EngineClient client;
    readonly Dictionary<string, JsonElement> values = [];
    readonly Dictionary<string, bool> enabled = [];
    readonly Dictionary<string, UiCommand> commands = [];
    readonly Dictionary<string, long> localEdits = [];
    bool applying, started, backendAvailable;
    string? playbackMetricInstance;
    long playbackMetricEpoch;
    string selectedPage = "主页", localError = "";
    bool navigationExpanded = true;
    OutputEndpoint? selectedOutput; InputEndpoint? selectedInput; DeviceRow? selectedDevice; NetworkAdapterInfo? selectedAdapter;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Func<Task>? RequestExit { get; set; }
    public Action? RequestHide { get; set; }
    public Func<IpAddressEditor, Task>? RequestIpAddressDialog { get; set; }
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<OutputEndpoint> Outputs { get; } = [];
    public ObservableCollection<InputEndpoint> Inputs { get; } = [];
    public ObservableCollection<NetworkAdapterInfo> SearchAdapters { get; } = [];
    public ObservableCollection<RememberedBoardRow> RememberedBoards { get; } = [];
    public ObservableCollection<UiLogEntry> LogEntries { get; } = [];
    public ObservableCollection<FormatOption> FormatOptions { get; } = [];
    public ObservableCollection<CaptureFormatOption> CaptureFormatOptions { get; } = [];
    public int EngineProcessId => client.ProcessId;
    public string SelectedPage { get => selectedPage; set { if (selectedPage == value) return; selectedPage = value; Changed(); foreach (string n in new[] { nameof(IsOverview), nameof(IsDevices), nameof(IsAudio), nameof(IsInput), nameof(IsLogs), nameof(IsSettings), nameof(IsAbout), nameof(IsChangelog) }) Changed(n); } }
    public bool IsOverview => SelectedPage == "主页"; public bool IsDevices => SelectedPage == "虚拟声卡";
    public bool IsAudio => SelectedPage == "播放"; public bool IsInput => SelectedPage == "输入";
    public bool IsUsb => false; public bool IsNetwork => false; public bool IsLogs => SelectedPage == "日志";
    public bool IsSettings => SelectedPage == "设置"; public bool IsAbout => SelectedPage == "关于"; public bool IsChangelog => SelectedPage == "更新日志";
    public bool IsNavigationExpanded { get => navigationExpanded; set { if (navigationExpanded == value) return; navigationExpanded = value; Changed(); Changed(nameof(NavigationWidth)); SetRemote(nameof(IsNavigationExpanded), value); } }
    public double NavigationWidth => IsNavigationExpanded ? 216 : 72;
    public string GlobalError => localError.Length != 0 || deviceActionBusy ? localError : Get<string>(nameof(GlobalError));
    public bool HasGlobalError => GlobalError.Length != 0;
    public string AppVersion => EngineProtocol.Release;
    public string RuntimeVersion => $"Avalonia {typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version} · 独立后台 {EngineProcessId}";

    public OutputEndpoint? SelectedOutput { get => selectedOutput; set => Select(ref selectedOutput, value, value?.Id); }
    public InputEndpoint? SelectedInput { get => selectedInput; set => Select(ref selectedInput, value, value?.Id); }
    public NetworkAdapterInfo? SelectedSearchAdapter { get => selectedAdapter; set => Select(ref selectedAdapter, value, value is null ? null : EngineProtocol.AdapterKey(value)); }
    public DeviceRow? SelectedDevice { get => selectedDevice; set => Select(ref selectedDevice, value, value is null ? null : EngineProtocol.DeviceKey(value)); }

    public MainViewModel(EngineClient? client = null)
    {
        this.client = client ?? new EngineClient();
        var settings = LocalStore.LoadSettings(persistMigration: false); navigationExpanded = settings.NavigationExpanded;
        values[nameof(Status)] = EngineProtocol.Json("正在连接音频后台…");
        values[nameof(Address)] = EngineProtocol.Json(settings.Address);
        values[nameof(AutoReconnectEnabled)] = EngineProtocol.Json(settings.AutoReconnectEnabled);
        values[nameof(CloseToTray)] = EngineProtocol.Json(settings.CloseToTray);
        values[nameof(StartInTray)] = EngineProtocol.Json(settings.StartInTray);
        foreach (var (format, i) in AudioFormat.Supported.Select((f, i) => (f, i)))
        {
            FormatOption? option = null;
            option = new(format, 1u << i, () => { if (!applying) _ = SendAsync(new("format", "playback", EngineProtocol.Json(new { bit = option!.Bit, selected = option.Selected }))); });
            FormatOptions.Add(option);
        }
        var ordered = FormatOptions.OrderBy(x => x.Format.SampleRate).ThenBy(x => x.Format.Bits).ToArray();
        FormatOptions.Clear(); foreach (var option in ordered) FormatOptions.Add(option);
        foreach (var (format, i) in CaptureFormat.Supported.Select((f, i) => (f, i)))
        {
            CaptureFormatOption? option = null;
            option = new(format, 1u << i, () => { if (!applying) _ = SendAsync(new("format", "capture", EngineProtocol.Json(new { bit = option!.Bit, selected = option.Selected }))); });
            CaptureFormatOptions.Add(option);
        }
        this.client.SnapshotReceived += snapshot => Dispatcher.UIThread.Post(() => { SetBackendAvailable(true); ApplySnapshot(snapshot); }, DispatcherPriority.Background);
        this.client.ConnectionChanged += error => Dispatcher.UIThread.Post(() => { SetBackendAvailable(this.client.ProcessId != 0); localError = ConnectionFeedback.Normalize(error); Changed(nameof(GlobalError)); Changed(nameof(HasGlobalError)); Changed(nameof(EngineProcessId)); Changed(nameof(RuntimeVersion)); });
    }
    void SetBackendAvailable(bool available)
    {
        if (backendAvailable == available) return;
        backendAvailable = available;
        if(!available)
        {
            foreach(string name in new[]{nameof(IsBusy),nameof(IsConnecting),nameof(IsDiscovering)}) values[name]=EngineProtocol.Json(false);
            values[nameof(OperationStatus)]=EngineProtocol.Json("");
            deviceActionBusy=deviceActionConnecting=false;deviceActionProgress="";
            RefreshDeviceAction();
        }
        foreach (var entry in values.Where(v => v.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)) Changed(entry.Key);
        foreach (var command in commands.Values) command.Refresh();
    }
    void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new(name));
        var udpRelated=UdpPortPresentation.Related(name);
        if(udpRelated.Mode is {} mode)PropertyChanged?.Invoke(this,new(mode));
        if(udpRelated.Number is {} number)PropertyChanged?.Invoke(this,new(number));
        if (name is nameof(IsConnected) or nameof(IsConnecting)) PropertyChanged?.Invoke(this, new(nameof(IsConnectionReady)));
        if (name is nameof(CanCancelConnection) or nameof(CanCancelSearch)) PropertyChanged?.Invoke(this,new(nameof(CanCancelOperation)));
    }
    T Get<T>(string name)
    {
        if (!backendAvailable && typeof(T) == typeof(bool) && (name.StartsWith("Can", StringComparison.Ordinal) || name is nameof(IsConnected) or nameof(IsListening) or nameof(IsMicrophoneSending) or nameof(IsBusy) or nameof(IsConnecting) or nameof(IsDiscovering) or nameof(IsReconnecting))) return (T)(object)false;
        if (values.TryGetValue(name, out var value)) return value.Deserialize<T>()!;
        if (typeof(T) == typeof(string)) return (T)(object)"";
        if (typeof(T) == typeof(string[]) || typeof(T) == typeof(IReadOnlyList<string>)) return (T)(object)Array.Empty<string>();
        return default!;
    }
    void SetRemote<T>(string name, T value)
    {
        if (value is double number)
        {
            if (!double.IsFinite(number)) return;
            if (name is nameof(OutputVolume) or nameof(Volume) or nameof(MicrophoneGain)) value = (T)(object)Math.Clamp(number, 0, 100);
        }
        var json = EngineProtocol.Json(value);
        if (values.TryGetValue(name, out var previous) && previous.GetRawText() == json.GetRawText()) return;
        values[name] = json; Changed(name);
        if (name == nameof(BoardVolumePercent)) Changed(nameof(BoardGainReadout));
        if (applying) return;
        localEdits[name] = Environment.TickCount64; client.Set(name, json);
    }
    bool IsLocallyEditing(string name) => localEdits.TryGetValue(name, out long changed) && (client.HasPending(name) || Environment.TickCount64 - changed < 250);
    void Select<T>(ref T? field, T? value, string? key, [CallerMemberName] string? name = null) where T : class
    { if (Equals(field, value)) return; field = value; Changed(name); if (!applying) { localEdits[name!] = Environment.TickCount64; client.Set(name!, EngineProtocol.Json(key)); } }

    static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source)) return;
        for (int i = 0; i < source.Count; i++) { if (i == target.Count) target.Add(source[i]); else if (!Equals(target[i], source[i])) target[i] = source[i]; }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
    public void ApplySnapshot(EngineSnapshot snapshot)
    {
        if(snapshot.Identity?.InstanceId is { } instance && playbackMetricInstance!=instance)
        {
            playbackMetricInstance=instance; ++playbackMetricEpoch; Changed(nameof(PlaybackMetricGeneration));
        }
        applying = true;
        try
        {
            foreach (var value in snapshot.Values)
            {
                if (value.Key is nameof(SelectedPage) or nameof(IsNavigationExpanded) or nameof(NavigationWidth) or nameof(AppVersion) or nameof(RuntimeVersion) || value.Key.StartsWith("Is") && value.Key is "IsOverview" or "IsDevices" or "IsAudio" or "IsInput" or "IsUsb" or "IsNetwork" or "IsLogs" or "IsSettings" or "IsAbout" or "IsChangelog") continue;
                if (IsLocallyEditing(value.Key)) continue;
                if (!values.TryGetValue(value.Key, out var previous) || previous.GetRawText() != value.Value.GetRawText()) { values[value.Key] = value.Value; Changed(value.Key); if (value.Key == nameof(GlobalError)) Changed(nameof(HasGlobalError)); if(value.Key==nameof(BoardVolumePercent))Changed(nameof(BoardGainReadout)); if(value.Key==nameof(PendingTransaction))Changed(nameof(HasPendingNetworkTransaction)); }
            }
            Reconcile(Devices, snapshot.Devices); Reconcile(Outputs, snapshot.Outputs); Reconcile(Inputs, snapshot.Inputs); Reconcile(SearchAdapters, snapshot.Adapters); Reconcile(LogEntries, snapshot.LogEntries);
            if (!RememberedBoards.Select(x => x.Board).SequenceEqual(snapshot.Boards)) Reconcile(RememberedBoards, snapshot.Boards.Select(b => new RememberedBoardRow(b, ConnectRememberedCommand, ForgetBoardCommand)).ToArray());
            if (!IsLocallyEditing(nameof(SelectedOutput))) SelectedOutput = Outputs.FirstOrDefault(x => x.Id == snapshot.OutputId);
            if (!IsLocallyEditing(nameof(SelectedInput))) SelectedInput = Inputs.FirstOrDefault(x => x.Id == snapshot.InputId);
            if (!IsLocallyEditing(nameof(SelectedSearchAdapter))) SelectedSearchAdapter = SearchAdapters.FirstOrDefault(x => EngineProtocol.AdapterKey(x) == snapshot.AdapterKey);
            if (!IsLocallyEditing(nameof(SelectedDevice))) SelectedDevice = Devices.FirstOrDefault(x => EngineProtocol.DeviceKey(x) == snapshot.DeviceKey);
            foreach (var state in snapshot.Formats) { var option = FormatOptions.Single(x => x.Bit == state.Bit); option.Selected = state.Selected; option.CanSelect = state.CanSelect; option.Hint = state.Hint; }
            foreach (var state in snapshot.CaptureFormats) { var option = CaptureFormatOptions.Single(x => x.Bit == state.Bit); option.Selected = state.Selected; option.CanSelect = state.CanSelect; option.Hint = state.Hint; }
            foreach (var command in snapshot.Commands) if (!enabled.TryGetValue(command.Key, out bool previous) || previous != command.Value) { enabled[command.Key] = command.Value; if (commands.TryGetValue(command.Key, out var existing)) existing.Refresh(); }
        }
        finally { applying = false; }
    }
    ICommand Command(string name)
    {
        if (commands.TryGetValue(name, out var existing)) return existing;
        return commands[name] = new UiCommand(parameter =>
        {
            if(BridgeRuntime.KeyForCommand(name) is not null){_=SendHostKeyCommand(name);return;}
            switch (name)
            {
                case "NavigateCommand": Navigate(parameter?.ToString() ?? "主页"); return;
                case "ToggleNavigationCommand": IsNavigationExpanded = !IsNavigationExpanded; return;
                case "ExitCommand": if (RequestExit is not null) _ = RequestExit(); return;
                case "ShutdownEngineCommand": if (RequestShutdownExit is not null) _ = RequestShutdownExit(); return;
                case "ManualConnectCommand": OpenManualConnection(); return;
                case "CancelConnectionCommand": _ = CancelConnectionUiAsync(); return;
                case "CancelSearchCommand": _ = SendAsync(new("command",name)); return;
                case "ConnectDeviceCommand": if (parameter is DeviceRow connectRow) _ = ConnectDeviceRowAsync(connectRow); return;
                case "ConfigureDeviceCommand": if (parameter is DeviceRow configureRow) _ = ConfigureDeviceRowAsync(configureRow); return;
                case "HideCommand": RequestHide?.Invoke(); return;
                case "SaveUdpPortsCommand": _ = SaveUdpPortDraftAsync(); return;
                case "OpenDataCommand": Process.Start(new ProcessStartInfo(LocalStore.Root) { UseShellExecute = true }); return;
                case "OpenWindowsSoundCommand": Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); return;
                case "OpenReferenceCommand": Process.Start(new ProcessStartInfo("https://github.com/XingYu-999/ControllerForwardingTool") { UseShellExecute = true }); return;
                case "ModifyDiscoveredIpCommand": if (RequestIpAddressDialog is not null && SelectedDevice is { } row) _ = RequestIpAddressDialog(new(row.Device, async (dhcp, ip, prefix, gateway) => (await SendAsync(new("changeIp", Value: EngineProtocol.Json(new { device = row.Device, dhcp, ip, prefix, gateway })))).Result ?? "")); return;
            }
            string? argument = parameter is RememberedBoardRow board ? board.Board.DeviceId : parameter?.ToString();
            if (TracksUiOperation(name)) { _ = SendTrackedCommandAsync(name, argument); return; }
            _ = SendAsync(new("command", name, EngineProtocol.Json(argument)));
        }, () => name=="CancelConnectionCommand" ? CanCancelConnection : name=="CancelSearchCommand" ? CanCancelSearch : name is "NavigateCommand" or "ToggleNavigationCommand" or "ExitCommand" or "ShutdownEngineCommand" or "HideCommand" or "OpenDataCommand" or "OpenWindowsSoundCommand" or "OpenReferenceCommand" || backendAvailable && !deviceActionInFlight && !pendingUiCommands.Contains(name) && (!(name is "ToggleListeningCommand" or "ApplyOutputSettingsCommand") || !OutputCommandPending) && enabled.GetValueOrDefault(name));
    }
    async Task<EngineResponse> SendAsync(EngineRequest request)
    {
        localError = ""; Changed(nameof(GlobalError)); Changed(nameof(HasGlobalError));
        try { var response = await client.RequestAsync(request); if (!response.Ok) Log(response.Error ?? "后台操作失败"); return response; }
        catch (Exception error) { string text=ConnectionFeedback.Message(error);Log(text);return new(false,text); }
    }
    public Task InitializeAsync() { if (!started) { started = true; client.Start(); } return Task.CompletedTask; }
    public void Navigate(string page) => SelectedPage = page switch { "概览" => "主页", "虚拟声卡连接" or "USB 声卡" or "网络" => "虚拟声卡", "声音" => "播放", _ => page };
    public void Log(string message) { localError = ConnectionFeedback.Normalize(message); Changed(nameof(GlobalError)); Changed(nameof(HasGlobalError)); }
    public Task Execute(Func<Task> action, string progress = "") => action();
    public async Task Listen() => await SendAsync(new("listen"));
    public async Task StopListening() => await SendAsync(new("stopListening"));
    public async ValueTask DisposeAsync() => await client.StopForExitAsync();
}
