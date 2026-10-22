using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using UsbAudioBridge;

namespace SoundConvertClient;

public sealed class CaptureFormatOption(CaptureFormat format,uint bit,Action changed):INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;bool selected,canSelect;string hint="请选择电脑输入源";
    public CaptureFormat Format{get;}=format;public uint Bit{get;}=bit;public string Label=>Format.Label;
    public bool Selected{get=>selected;set{if(selected==value)return;selected=value;Notify();changed();}}
    public bool CanSelect{get=>canSelect;set{if(canSelect==value)return;canSelect=value;Notify();}}public string Hint{get=>hint;set{if(hint==value)return;hint=value;Notify();}}
    void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
public sealed partial class BridgeRuntime
{
    readonly IAudioInputControl inputControl;InputEndpoint? selectedInput;InputSnapshot? inputSnapshot;IInputCapture? microphoneCapture,inputProbe;MicrophoneSender? microphoneSender;
    bool microphoneIntent,microphoneSupported,inputDetecting,inputRefreshing,captureReconfiguring,microphonePlaybackFirst=true,microphoneMuted,inputFaultStopping;
    uint committedCaptureFormats=1,activeCaptureFormats=1;double microphoneGain=100,inputPeak;string inputFormat="未选择输入源",inputStatus="尚未启用输入",inputDetection="未检测",boardCaptureStatus="未连接",boardCaptureFormat="—",microphoneDiagnostics="尚未传输";
    CancellationTokenSource? inputChangeCancellation;readonly RuntimeTimer inputMeterTimer=new(){Interval=TimeSpan.FromMilliseconds(100)};
    InputEndpoint? lastValidInputSelection;InputSnapshot? lastValidInputSnapshot;bool inputSelectionPending;
    static readonly JsonSerializerOptions MicrophoneDiagnosticOptions=new(){WriteIndented=true};
    public ObservableCollection<InputEndpoint> Inputs{get;}=[];public ObservableCollection<CaptureFormatOption> CaptureFormatOptions{get;}=[];
    public InputEndpoint? SelectedInput{get=>selectedInput;set{if(selectedInput==value)return;bool changed=selectedInput?.Id!=value?.Id||selectedInput?.DeviceId!=value?.DeviceId;Set(ref selectedInput,value);Changed(nameof(InputName));if(changed)InputSelectionChanged();}}
    public bool MicrophoneSupported{get=>microphoneSupported;private set{Set(ref microphoneSupported,value);RefreshInputOptions();}}
    public string InputName
    {
        get
        {
            var endpoint=inputSelectionPending&&lastValidInputSelection is not null?lastValidInputSelection:SelectedInput;
            var snapshot=inputSelectionPending&&lastValidInputSelection is not null?lastValidInputSnapshot:inputSnapshot;
            return endpoint?.IsSystemDefault==true?endpoint.Name:snapshot?.Name??endpoint?.Name??"未选择电脑输入源";
        }
    }
    public string InputFormat{get=>inputFormat;private set=>Set(ref inputFormat,value);}
    public string InputStatus{get=>inputStatus;private set=>Set(ref inputStatus,value);}public string InputDetection{get=>inputDetection;private set=>Set(ref inputDetection,value);}
    public double InputPeak{get=>inputPeak;private set=>Set(ref inputPeak,value);}public bool InputDetecting{get=>inputDetecting;private set{Set(ref inputDetecting,value);RefreshCommandStates();}}
    public string BoardCaptureStatus{get=>boardCaptureStatus;private set=>Set(ref boardCaptureStatus,value);}public string BoardCaptureFormat{get=>boardCaptureFormat;private set=>Set(ref boardCaptureFormat,value);}
    public string MicrophoneDiagnostics{get=>microphoneDiagnostics;private set=>Set(ref microphoneDiagnostics,value);}
    public bool IsMicrophoneSending=>microphoneSender is not null&&microphoneSender.Fault is null;
    public bool CanStartMicrophone=>IsConnected&&CanManage&&MicrophoneSupported&&UsbState=="RUNNING"&&inputSnapshot?.SupportedMask>0&&!InputDetecting&&!IsBusy&&!inputSelectionPending;
    public bool MicrophonePlaybackFirst{get=>microphonePlaybackFirst;set=>Set(ref microphonePlaybackFirst,value);}
    public double MicrophoneGain{get=>microphoneGain;set{Set(ref microphoneGain,Math.Clamp(value,0,100));if(microphoneSender is {}s)s.Gain=(float)microphoneGain/100;}}
    public bool MicrophoneMuted{get=>microphoneMuted;set{Set(ref microphoneMuted,value);if(microphoneSender is {}s)s.Mute=value;}}
    public string CapturePolicyHint=>inputSnapshot is null?"先选择电脑麦克风并读取实际格式，再开放相应录音格式。":"开发板麦克风格式不得高于电脑输入源的当前采样率和精度；高于上限的选项不可启用。";
    uint DraftCaptureFormats=>CaptureFormatOptions.Where(x=>x.Selected).Aggregate(0u,(m,x)=>m|x.Bit);
    public ICommand RefreshInputsCommand=>AsyncCommand(nameof(RefreshInputsCommand),RefreshAudioDeviceState);
    public ICommand DetectInputCommand=>AsyncCommand(nameof(DetectInputCommand),DetectInput);
    public ICommand StartMicrophoneCommand=>SyncCommand(nameof(StartMicrophoneCommand),_=>_=Execute(StartMicrophone,"正在启用麦克风传输…"),()=>CanStartMicrophone&&!IsMicrophoneSending);
    public ICommand StopMicrophoneCommand=>AsyncCommand(nameof(StopMicrophoneCommand),StopMicrophone);
    void InitializeInputs(string? id)
    {
        for(int i=0;i<CaptureFormat.Supported.Count;i++)CaptureFormatOptions.Add(new(CaptureFormat.Supported[i],1u<<i,()=>{Changed(nameof(UsbDraftState));RefreshInputOptions();}));SetCaptureDraft(1);
        try{RefreshInputs();SelectedInput=id is null?Inputs.FirstOrDefault():Inputs.FirstOrDefault(x=>x.Id==id);}catch(Exception e){InputStatus=e.Message;}
        inputMeterTimer.Tick+=(_,_)=>{UpdateInputPeak();UpdateLevelMeters();};inputMeterTimer.Start();
    }
    void SetCaptureDraft(uint mask){foreach(var x in CaptureFormatOptions)x.Selected=(mask&x.Bit)!=0;}
    public void RefreshInputs(){inputRefreshing=true;try{string? previous=SelectedInput?.Id;ReplaceEndpoints(Inputs,inputControl.Endpoints().Where(x=>x.IsSystemDefault||!IsBoardInput(x)));SelectedInput=Inputs.FirstOrDefault(x=>x.Id==previous)??(initializing&&previous is null?Inputs.FirstOrDefault():null);}finally{inputRefreshing=false;}}
    bool IsBoardInput(InputEndpoint x)=>x.DeviceId is {} id&&(x.Name.Contains("W5500-EVB-Pico2",StringComparison.OrdinalIgnoreCase)||(!string.IsNullOrWhiteSpace(committedProductName)&&committedProductName.Length>=4&&x.Name.Contains(committedProductName,StringComparison.OrdinalIgnoreCase))||(inputControl is WindowsInputAudioControl&&UsbEndpointIdentity.IsBoardOrUnverified(id,UsbSerial)));
    void RefreshInputOptions(){foreach(var x in CaptureFormatOptions){bool allowed=inputSnapshot is not null&&(inputSnapshot.SupportedMask&x.Bit)!=0;x.CanSelect=CanEditUsb&&MicrophoneSupported&&(allowed||x.Selected);x.Hint=allowed?"可由当前电脑输入源发送":"高于当前输入源格式，或源格式尚未检测";}Changed(nameof(CapturePolicyHint));Changed(nameof(CanStartMicrophone));Changed(nameof(IsMicrophoneSending));RefreshCommandStates();}
    void InputSelectionChanged(){inputSelectionPending=!initializing&&!inputRefreshing;if(!inputSelectionPending)inputSnapshot=null;InputFormat=inputSelectionPending?"正在切换输入设备…":"正在读取输入源…";Changed(nameof(InputName));RefreshInputOptions();if(!inputSelectionPending)return;inputChangeCancellation?.Cancel();inputChangeCancellation?.Dispose();inputChangeCancellation=new();_=ChangeInputLater(inputChangeCancellation.Token);}
    async Task ChangeInputLater(CancellationToken ct)
    {
        try
        {
            await Task.Delay(150,ct);await commands.WaitAsync(ct);
            try
            {
                var candidate=SelectedInput;var previous=lastValidInputSelection;var previousSnapshot=lastValidInputSnapshot;bool stopped=false;
                try
                {
                    if(candidate?.DeviceId is not {} endpoint||IsBoardInput(candidate))throw new InvalidOperationException("请选择可用的电脑麦克风。");
                    var snapshot=await inputControl.ReadAsync(endpoint,ct);
                    if(snapshot.SupportedMask==0||microphoneIntent&&(snapshot.SupportedMask&committedCaptureFormats)==0)throw new InvalidOperationException("候选输入与 USB 麦克风格式不兼容。");
                    ct.ThrowIfCancellationRequested();if(candidate!=SelectedInput)return;
                    await StopMicrophoneCore();stopped=true;inputSnapshot=snapshot;InputFormat=snapshot.Format;InputStatus="输入源已就绪";
                    if(IsConnected&&CanManage&&UsbState=="RUNNING")await ApplyOutputFormats();ct.ThrowIfCancellationRequested();if(candidate!=SelectedInput)return;if(microphoneIntent)await StartMicrophoneCore();
                    lastValidInputSelection=candidate;lastValidInputSnapshot=snapshot;LocalStore.SaveSettings(CurrentSettings());
                }
                catch(Exception error)when(error is not OperationCanceledException)
                {
                    var restore=Inputs.FirstOrDefault(x=>x.Id==previous?.Id&&x.DeviceId==previous?.DeviceId);
                    Set(ref selectedInput,restore,nameof(SelectedInput));inputSnapshot=restore is null?null:previousSnapshot;InputFormat=inputSnapshot?.Format??"没有可用输入源";
                    if(stopped&&restore is not null&&microphoneIntent)try{await StartMicrophoneCore();}catch(Exception recovery){Log("原输入恢复未完成："+recovery.Message);}
                    InputStatus="输入切换失败，已保留最后有效选择："+error.Message;GlobalError=InputStatus;
                }
            }
            finally{inputSelectionPending=false;Changed(nameof(InputName));RefreshInputOptions();commands.Release();}
        }
        catch(OperationCanceledException){}catch(Exception e){InputStatus="输入切换失败："+e.Message;}
    }
    async Task RefreshInputState()
    {
        var cancellation=activeConnectionCancellation?.Token??lifetime.Token;
        if(SelectedInput?.DeviceId is not {} id||IsBoardInput(SelectedInput)){inputSnapshot=null;InputFormat=SelectedInput?.DeviceId is null?"没有可用输入源":InputEndpointBlockReason(SelectedInput);InputStatus="输入源不可用";RefreshInputOptions();return;}
        try{var s=await inputControl.ReadAsync(id,cancellation).WaitAsync(cancellation);if(id==SelectedInput?.DeviceId){inputSnapshot=s;lastValidInputSelection=SelectedInput;lastValidInputSnapshot=s;InputFormat=s.Format;InputStatus="输入源已就绪";Changed(nameof(InputName));}}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}catch(Exception e){inputSnapshot=null;InputFormat=e.Message;}RefreshInputOptions();
    }
    async Task DetectInput()
    {
        await RefreshInputState();if(SelectedInput is null||inputSnapshot is null)throw new InvalidOperationException("请选择可用麦克风输入源");InputDetecting=true;
        try{if(microphoneCapture is not null){InputDetection=$"正在传输，已采集{microphoneCapture.Frames:N0}帧；峰值{microphoneCapture.Peak:F3}";return;}inputProbe=inputControl.CreateCapture(SelectedInput.DeviceId!);inputProbe.Start();InputDetection="正在检测输入电平（不保存录音）…";await Task.Delay(3000,lifetime.Token);if(inputProbe.Failure is {}f)throw new IOException(f);InputDetection=$"采集{inputProbe.Frames:N0}帧 · {(inputProbe.Frames==0?"未收到数据":inputProbe.Peak>.001?"检测到声音":"已收到静音数据")}";}
        finally{inputProbe?.Dispose();inputProbe=null;InputDetecting=false;}
    }
    async Task StartMicrophone(){EnsureOwner();microphoneIntent=true;try{captureReconfiguring=true;await RefreshInputState();await ApplyOutputFormats();captureReconfiguring=false;await StartMicrophoneCore();}catch{microphoneIntent=false;throw;}finally{captureReconfiguring=false;}}
    async Task StartMicrophoneCore()
    {
        if(microphoneSender is not null||captureReconfiguring)return;if(!MicrophoneSupported||SelectedInput?.DeviceId is not {} endpoint||inputSnapshot is null||IsBoardInput(SelectedInput))throw new InvalidOperationException("请选择可用的电脑麦克风，不能使用开发板自己的录音端点。");
        MicrophoneSender? sender=null;IInputCapture? capture=null;
        try{var ports=udpPorts;EnsureBoardUdpPort(ports.BoardCapture,49163);capture=inputControl.CreateCapture(endpoint);sender=new MicrophoneSender(Session,capture.SampleRate,inputSnapshot.PrecisionBits,LocalAddress,ports.SoftwareCapture,MicrophonePlaybackFirst,ports.BoardCapture){Gain=(float)MicrophoneGain/100,Mute=MicrophoneMuted};if(capture is ISpanInputCapture spanCapture)spanCapture.SpanSamples+=sender.Push;else capture.Samples+=sender.PushArray;await sender.StartAsync();capture.Start();microphoneSender=sender;microphoneCapture=capture;activeCaptureUdpPorts=ports;RefreshUdpPortStatus();InputStatus="电脑输入源采集中";RefreshInputOptions();}
        catch{capture?.Dispose();if(sender is not null)await sender.DisposeAsync();throw;}
    }
    async Task StopMicrophoneCore(){var c=microphoneCapture;microphoneCapture=null;var s=microphoneSender;microphoneSender=null;activeCaptureUdpPorts=null;RefreshUdpPortStatus();try{c?.Dispose();}finally{if(s is not null)await s.DisposeAsync();}InputStatus="已停止麦克风传输";RefreshInputOptions();}
    async Task StopMicrophone(){microphoneIntent=false;await StopMicrophoneCore();}
    void UpdateInputPeak()=>InputPeak=(microphoneCapture??inputProbe)?.Peak??0;
    void UpdateInputMetrics(){var c=microphoneCapture??inputProbe;UpdateInputPeak();string? fault=c?.Failure??microphoneSender?.Fault;if(fault is not null){InputStatus="输入异常："+fault;if(microphoneSender is not null&&!inputFaultStopping){inputFaultStopping=true;_=StopFaultedInput(fault);}return;}if(microphoneSender is {}s){MicrophoneDiagnostics=JsonSerializer.Serialize(s.Statistics((c as MicrophoneCapture)?.Statistics()),MicrophoneDiagnosticOptions);InputStatus=s.State=="WAITING_HOST"?"电脑采集中，等待 USB Host 启用麦克风":"电脑采集中，麦克风数据正在发送";}Changed(nameof(IsMicrophoneSending));}
    async Task StopFaultedInput(string message){try{await commands.WaitAsync(lifetime.Token);try{if(SelectedInput?.IsSystemDefault!=true&&control?.IsConnected==true)microphoneIntent=false;await StopMicrophoneCore();InputStatus=(microphoneIntent?"等待系统默认麦克风恢复：":"输入已停止：")+message;}finally{commands.Release();}}catch(OperationCanceledException){}catch(Exception e){InputStatus="输入已停止："+message+"；"+e.Message;}finally{inputFaultStopping=false;}}
    void ReadCaptureStatus(JsonElement data){if(!data.TryGetProperty("capture",out var m))return;ReadCaptureVolume(m);activeCaptureFormats=m.TryGetProperty("formats_mask",out var mask)?mask.GetUInt32():activeCaptureFormats;bool enabled=m.TryGetProperty("active",out var active)&&active.GetBoolean();BoardCaptureStatus=enabled?m.TryGetProperty("primed",out var p)&&p.GetBoolean()?"USB Host 正在录音，数据已就绪":"USB Host 正在录音，等待麦克风数据":"USB Host 未启用录音";if(m.TryGetProperty("format",out var f))BoardCaptureFormat=(enabled?"":"待启用的默认格式：")+CaptureFormat.FromJson(f).Label;}
}
