using System.ComponentModel;
using System.Runtime.CompilerServices;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed class FormatOption(AudioFormat format,uint bit,Action changed):INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    bool selected,canSelect;string hint="连接后读取设备能力";
    public AudioFormat Format{get;}=format;
    public uint Bit{get;}=bit;
    public string Label=>Format.Label;
    public bool Selected{get=>selected;set{if(selected==value)return;selected=value;Notify();changed();}}
    public bool CanSelect{get=>canSelect;set{if(canSelect==value)return;canSelect=value;Notify();}}
    public string Hint{get=>hint;set{if(hint==value)return;hint=value;Notify();}}
    void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}

public sealed partial class BridgeRuntime
{
    bool initializing=true,refreshingOutputs,outputAvailable,updatingOutput,outputWritePending,outputMuted,formatSelectionSupported,internalVolumeSupported;
    readonly IAudioOutputControl outputControl;
    double outputVolume=100;string outputFormat="未选择输出声卡",outputName="未选择输出声卡",usbProtocol="—";
    uint committedFormats=147,activeFormats=147,supportedOutputFormats;
    CancellationTokenSource? outputChangeCancellation;
    OutputEndpoint? lastValidOutputSelection;
    OutputSnapshot? lastValidOutputSnapshot;
    bool outputSelectionPending;
    long outputWriteGeneration;
    DateTimeOffset nextOutputRefresh;
    public System.Collections.ObjectModel.ObservableCollection<FormatOption> FormatOptions{get;}=[];
    public string OutputName{get=>outputName;private set=>Set(ref outputName,value);}
    public string OutputFormat{get=>outputFormat;private set=>Set(ref outputFormat,value);}
    public string UsbProtocol{get=>usbProtocol;private set=>Set(ref usbProtocol,value);}
    public string ConnectedAddress=>IsConnected?control!.Address:"—";
    public bool CanControlOutput=>outputAvailable&&SelectedOutput is not null&&!IsBusy&&!outputSelectionPending;
    public double OutputVolume{get=>outputVolume;set{value=Math.Clamp(value,0,100);if(outputVolume==value)return;Set(ref outputVolume,value);if(!updatingOutput&&CanControlOutput)QueueOutputWrite();}}
    public bool OutputMuted{get=>outputMuted;set{if(outputMuted==value)return;Set(ref outputMuted,value);if(!updatingOutput&&CanControlOutput)QueueOutputWrite();}}
    public bool CanConfigureFormats=>CanEditUsb&&formatSelectionSupported;
    public string FormatPolicyHint=>!formatSelectionSupported?"格式勾选和自动适配需要支持格式选择的固件。":supportedOutputFormats==0?"当前输出没有可用的双声道转换通路，请选择可用的输出声卡。":"输入自动转换到输出格式；允许升采样和位深扩展，不允许降采样或降低有效位深。灰色项保留选择，停止后可修改。";
    uint DraftFormats=>FormatOptions.Where(x=>x.Selected).Aggregate(0u,(mask,x)=>mask|x.Bit);
    void InitializeFormats()
    {
        for(int i=0;i<AudioFormat.Supported.Count;i++)FormatOptions.Add(new(AudioFormat.Supported[i],1u<<i,()=>Changed(nameof(UsbDraftState))));
        var ordered=FormatOptions.OrderBy(x=>x.Format.SampleRate).ThenBy(x=>x.Format.Bits).ToArray();FormatOptions.Clear();foreach(var option in ordered)FormatOptions.Add(option);
        SetFormatDraft(147);
    }
    void SetFormatDraft(uint mask){foreach(var option in FormatOptions)option.Selected=(mask&option.Bit)!=0;}
    void RefreshFormatOptions()
    {
        foreach(var option in FormatOptions)
        {
            bool available=(supportedOutputFormats&option.Bit)!=0;
            option.CanSelect=CanConfigureFormats&&(UsbState=="STOPPED"||available);
            option.Hint=!formatSelectionSupported?"请更新固件":available?"可直接播放或自动转换到当前输出格式":"当前输出不可用，或需要降低采样率/有效位深；模拟时排除";
        }
        Changed(nameof(CanConfigureFormats));Changed(nameof(FormatPolicyHint));
    }
    void OutputSelectionChanged()
    {
        outputChangeCancellation?.Cancel();outputChangeCancellation?.Dispose();outputChangeCancellation=new();
        ++outputWriteGeneration;outputWritePending=false;outputAvailable=false;supportedOutputFormats=0;
        outputSelectionPending=!initializing&&!refreshingOutputs;
        OutputFormat=outputSelectionPending?"正在切换输出设备…":"正在读取输出声卡…";
        if(lastValidOutputSnapshot is null||!outputSelectionPending)OutputName=SelectedOutput?.Name??"未选择输出声卡";
        Changed(nameof(CanControlOutput));RefreshToggleButtons();
        if(!initializing&&!refreshingOutputs)_=ChangeOutputLater(SelectedOutput?.Id,outputChangeCancellation.Token);
    }
    async Task ChangeOutputLater(string? id,CancellationToken ct)
    {
        try
        {
            await Task.Delay(150,ct);await commands.WaitAsync(ct);
            try
            {
                if(shuttingDown||id!=SelectedOutput?.Id)return;IsBusy=true;GlobalError="";
                var candidate=SelectedOutput;var previous=lastValidOutputSelection;var previousSnapshot=lastValidOutputSnapshot;
                bool resume=listeningIntent,stopped=false;
                try
                {
                    if(candidate?.DeviceId is not {} endpoint||IsBoardOutput(candidate))throw new InvalidOperationException("请选择可用的电脑输出设备。");
                    var snapshot=await outputControl.ReadAsync(endpoint,ct);
                    if(snapshot.SupportedMask==0)throw new InvalidOperationException("候选输出没有可用的音频格式。");
                    if(IsConnected&&CanManage&&FormatPolicy.Effective(committedFormats,snapshot.SupportedMask)==0)throw new InvalidOperationException("候选输出与已保存 USB 格式没有交集。");
                    ct.ThrowIfCancellationRequested();if(id!=SelectedOutput?.Id)return;
                    await StopPlaybackCore();stopped=true;ApplyOutputSnapshot(snapshot);
                    if(IsConnected&&CanManage&&UsbState=="RUNNING")await ApplyOutputFormats();
                    ct.ThrowIfCancellationRequested();if(id!=SelectedOutput?.Id)return;
                    if(resume&&IsConnected&&CanManage&&UsbState=="RUNNING")await Listen();
                    lastValidOutputSelection=candidate;lastValidOutputSnapshot=snapshot;
                    LocalStore.SaveSettings(CurrentSettings());
                }
                catch(Exception error)when(error is not OperationCanceledException)
                {
                    var restore=Outputs.FirstOrDefault(x=>x.Id==previous?.Id&&x.DeviceId==previous?.DeviceId);
                    Set(ref selectedOutput,restore,nameof(SelectedOutput));
                    if(restore is not null&&previousSnapshot is not null)ApplyOutputSnapshot(previousSnapshot);
                    else{outputAvailable=false;supportedOutputFormats=0;OutputName="未选择输出声卡";OutputFormat="没有可用输出声卡";}
                    if(stopped&&restore is not null&&resume&&IsConnected&&CanManage&&UsbState=="RUNNING")
                        try{await ApplyOutputFormats();await Listen();}catch(Exception recovery){Log("原输出恢复未完成："+recovery.Message);}
                    throw new IOException("输出切换失败，已保留最后有效选择："+error.Message,error);
                }
            }
            finally{outputSelectionPending=false;IsBusy=false;commands.Release();RefreshShell();}
        }
        catch(OperationCanceledException){}
        catch(Exception error){GlobalError=error.Message;Log("输出切换失败："+error.Message);}
    }
    async Task<bool> RefreshOutputState()
    {
        var cancellation=activeConnectionCancellation?.Token??lifetime.Token;
        uint previous=supportedOutputFormats;string previousFormat=OutputFormat;string? id=SelectedOutput?.DeviceId;
        if(id is null||IsBoardOutput(SelectedOutput!)){outputAvailable=false;supportedOutputFormats=0;OutputName=SelectedOutput?.Name??"未选择输出声卡";OutputFormat=id is null?"没有可用输出声卡":OutputEndpointBlockReason(SelectedOutput!);}
        else try
        {
            var snapshot=await outputControl.ReadAsync(id,cancellation).WaitAsync(cancellation);
            if(id!=SelectedOutput?.DeviceId)return false;
            ApplyOutputSnapshot(snapshot);lastValidOutputSelection=SelectedOutput;lastValidOutputSnapshot=snapshot;
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { throw; }
        catch(Exception error){outputAvailable=false;supportedOutputFormats=0;OutputFormat="输出不可用："+error.Message;}
        Changed(nameof(CanControlOutput));RefreshFormatOptions();
        return previous!=supportedOutputFormats||previousFormat!=OutputFormat;
    }
    void ApplyOutputSnapshot(OutputSnapshot snapshot)
    {
        outputAvailable=true;supportedOutputFormats=snapshot.SupportedMask;OutputName=snapshot.Name;OutputFormat=snapshot.Format;
        if(!outputWritePending){updatingOutput=true;try{OutputVolume=snapshot.Volume;OutputMuted=snapshot.Muted;}finally{updatingOutput=false;}}
        Changed(nameof(CanControlOutput));RefreshFormatOptions();
    }
    async Task PollOutputState()
    {
        if(shuttingDown||IsBusy||outputSelectionPending||inputSelectionPending||InputDetecting||DateTimeOffset.UtcNow<nextOutputRefresh)return;
        try
        {
            nextOutputRefresh=DateTimeOffset.UtcNow.AddSeconds(3);await RefreshAudioDevicesInBackgroundAsync();
        }
        catch(OperationCanceledException)when(shuttingDown){}
        catch(Exception error){if(!shuttingDown){GlobalError="音频设备适配失败："+error.Message;Log(GlobalError);}}
    }
    async Task ApplyOutputFormats()
    {
        if(!formatSelectionSupported)return;
        uint effective=FormatPolicy.Effective(committedFormats,supportedOutputFormats);
        if(effective==0)
        {
            await SetUsbState(false);await StopPlaybackCore();listeningIntent=false;
            throw new InvalidOperationException("已勾选的格式与当前输出声卡没有交集，已停止模拟。请停止状态下勾选合适格式并保存，或更换输出声卡。");
        }
        uint captureEffective=committedCaptureFormats;
        if(MicrophoneSupported&&inputSnapshot is not null){captureEffective=committedCaptureFormats&inputSnapshot.SupportedMask;if(captureEffective==0&&microphoneIntent)throw new InvalidOperationException("麦克风已勾选格式与电脑输入源不匹配，请选择较低录音格式并保存。");if(captureEffective==0)captureEffective=activeCaptureFormats;}
        if(UsbState=="RUNNING"&&activeFormats==effective&&(!MicrophoneSupported||activeCaptureFormats==captureEffective))return;
        bool running=UsbState=="RUNNING",resume=listeningIntent;
        if(running){await StopPlaybackCore();await SetUsbState(false);}
        var info=await Session.RequestAsync("GET_INFO");
        await Session.RequestAsync("USB_SET_FORMATS",new{formats_mask=effective,expected_emulation_revision=info.GetProperty("usb_emulation").GetProperty("revision").GetString()});
        if(MicrophoneSupported){await Session.RequestAsync("USB_SET_CAPTURE_FORMATS",new{formats_mask=captureEffective,expected_emulation_revision=info.GetProperty("usb_emulation").GetProperty("revision").GetString()});activeCaptureFormats=captureEffective;}
        activeFormats=effective;
        if(running){await SetUsbState(true);if(resume)await StartPlayback();Log("已按当前输出声卡调整 USB 可选格式，并重新开始模拟。");}
        RefreshFormatOptions();
    }
    async Task RestoreAfterRejectedSave(bool restart,bool resume)
    {
        if(!restart||!IsConnected||!string.IsNullOrEmpty(PendingTransaction)||UsbState!="STOPPED")return;
        try{await ApplyOutputFormats();await SetUsbState(true);if(resume)await Listen();}
        catch(Exception error){Log("保存失败后恢复模拟失败："+error.Message);}
    }
    readonly record struct OutputWrite(string Id,double Percent,bool Mute,long Generation);
    LatestValueWriter<OutputWrite>? outputWriter;
    void QueueOutputWrite()
    {
        outputWritePending=true;
        outputWriter??=new(WriteOutput,error=>{outputWritePending=false;GlobalError="输出声卡音量调整失败："+error.Message;Log(GlobalError);});
        outputWriter.Submit(new(SelectedOutput!.DeviceId!,OutputVolume,OutputMuted,++outputWriteGeneration));
    }
    async Task WriteOutput(OutputWrite value)
    {
        if(shuttingDown||value.Id!=SelectedOutput?.DeviceId||IsBoardOutput(SelectedOutput!))return;
        await outputControl.WriteAsync(value.Id,value.Percent,value.Mute);
        if(value.Generation==outputWriteGeneration)outputWritePending=false;
    }
}
