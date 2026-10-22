using System.Collections.ObjectModel;

namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    long audioSelectionGeneration;
    bool audioPollTracking;
    string outputPollingStage="idle";
    long outputPollingStarted;
    public string OutputPollingStage=>outputPollingStage;
    public double OutputPollingAgeMs=>outputPollingStage=="idle"?0:System.Diagnostics.Stopwatch.GetElapsedTime(outputPollingStarted).TotalMilliseconds;
    bool BoardEndpointName(string name)=>name.Contains("W5500-EVB-Pico2",StringComparison.OrdinalIgnoreCase)||
        !string.IsNullOrWhiteSpace(committedProductName)&&committedProductName.Length>=4&&name.Contains(committedProductName,StringComparison.OrdinalIgnoreCase);
    string OutputEndpointBlockReason(OutputEndpoint endpoint)=>endpoint.DeviceId is not {} id?"没有可用输出设备。":BoardEndpointName(endpoint.Name)?"这是开发板自身的 USB 声卡，请选择电脑耳机或扬声器。":
        outputControl is WindowsOutputAudioControl?UsbEndpointIdentity.CachedSafetyReason(id,UsbSerial):"";
    string InputEndpointBlockReason(InputEndpoint endpoint)=>endpoint.DeviceId is not {} id?"没有可用输入设备。":BoardEndpointName(endpoint.Name)?"这是开发板自身的 USB 录音端点，请选择电脑麦克风。":
        inputControl is WindowsInputAudioControl?UsbEndpointIdentity.CachedSafetyReason(id,UsbSerial):"";
    void EnsureAudioPollTracking()
    {
        if(audioPollTracking)return;audioPollTracking=true;
        PropertyChanged+=(_,e)=>{if(e.PropertyName is nameof(SelectedOutput) or nameof(SelectedInput))audioSelectionGeneration++;};
    }
    sealed record AudioDevicePollResult(OutputEndpoint[] Outputs,InputEndpoint[] Inputs,OutputEndpoint? Output,InputEndpoint? Input,
        OutputSnapshot? OutputState,InputSnapshot? InputState,string? OutputError,string? InputError,bool SafeOutput,bool SafeInput,bool ReadInput);
    static bool IsBoardPollEndpoint(string? id,string name,string product,string serial,bool windows)=>id is not null&&
        (name.Contains("W5500-EVB-Pico2",StringComparison.OrdinalIgnoreCase)||
        !string.IsNullOrWhiteSpace(product)&&product.Length>=4&&name.Contains(product,StringComparison.OrdinalIgnoreCase)||
        windows&&UsbEndpointIdentity.IsBoardOrUnverified(id,serial));
    async Task RefreshAudioDevicesInBackgroundAsync()
    {
        EnsureAudioPollTracking();
        long generation=audioSelectionGeneration;var selectedOut=SelectedOutput;var selectedIn=SelectedInput;var session=control;
        string product=committedProductName,serial=UsbSerial;
        bool needInput=inputSnapshot is null;
        outputPollingStarted=System.Diagnostics.Stopwatch.GetTimestamp();outputPollingStage="enumerating-outside-command-lock";
        try
        {
            // Endpoint discovery and driver metadata may block in native COM. They
            // neither run on the state dispatcher nor own the board command gate.
            var result=await Task.Run(async()=>
            {
                var outputs=outputControl.Endpoints().Where(x=>x.IsSystemDefault||!IsBoardPollEndpoint(x.DeviceId,x.Name,product,serial,outputControl is WindowsOutputAudioControl)).ToArray();
                var inputs=inputControl.Endpoints().Where(x=>x.IsSystemDefault||!IsBoardPollEndpoint(x.DeviceId,x.Name,product,serial,inputControl is WindowsInputAudioControl)).ToArray();
                var nextOut=outputs.FirstOrDefault(x=>x.Id==selectedOut?.Id);var nextIn=inputs.FirstOrDefault(x=>x.Id==selectedIn?.Id);
                bool safeOut=nextOut?.DeviceId is not null&&!IsBoardPollEndpoint(nextOut.DeviceId,nextOut.Name,product,serial,outputControl is WindowsOutputAudioControl);
                bool safeIn=nextIn?.DeviceId is not null&&!IsBoardPollEndpoint(nextIn.DeviceId,nextIn.Name,product,serial,inputControl is WindowsInputAudioControl);
                OutputSnapshot? outputState=null;InputSnapshot? inputState=null;string? outputError=null,inputError=null;
                if(safeOut)
                {
                    outputPollingStage="reading-output-outside-command-lock";
                    try{outputState=await outputControl.ReadAsync(nextOut!.DeviceId!,lifetime.Token).ConfigureAwait(false);}catch(Exception error){outputError=error.Message;}
                }
                bool readInput=needInput||nextIn?.DeviceId!=selectedIn?.DeviceId;
                if(safeIn&&readInput)
                {
                    outputPollingStage="reading-input-outside-command-lock";
                    try{inputState=await inputControl.ReadAsync(nextIn!.DeviceId!,lifetime.Token).ConfigureAwait(false);}catch(Exception error){inputError=error.Message;}
                }
                return new AudioDevicePollResult(outputs,inputs,nextOut,nextIn,outputState,inputState,outputError,inputError,safeOut,safeIn,readInput);
            },lifetime.Token);
            outputPollingStage="waiting-to-apply-cached-result";
            if(shuttingDown||IsBusy||outputSelectionPending||inputSelectionPending||generation!=audioSelectionGeneration||session!=control||
                selectedOut?.Id!=SelectedOutput?.Id||selectedOut?.DeviceId!=SelectedOutput?.DeviceId||
                selectedIn?.Id!=SelectedInput?.Id||selectedIn?.DeviceId!=SelectedInput?.DeviceId||!await commands.WaitAsync(0))return;
            bool pending=false;
            try
            {
                outputPollingStage="applying-cached-result";
                string? oldOutput=SelectedOutput?.DeviceId,oldInput=SelectedInput?.DeviceId;
                bool outputChanged=oldOutput!=result.Output?.DeviceId,inputChanged=oldInput!=result.Input?.DeviceId;
                bool restartOutput=outputChanged||result.Output?.IsSystemDefault==true&&listeningIntent&&(audio is null||audio.Failure is not null);
                bool restartInput=inputChanged||result.Input?.IsSystemDefault==true&&microphoneIntent&&microphoneCapture is null;
                uint oldMask=supportedOutputFormats;string oldFormat=OutputFormat;
                refreshingOutputs=inputRefreshing=true;
                try
                {
                    ReplaceEndpoints(Outputs,result.Outputs);ReplaceEndpoints(Inputs,result.Inputs);
                    SelectedOutput=result.Output;SelectedInput=result.Input;
                }
                finally{refreshingOutputs=inputRefreshing=false;}
                if(result.OutputState is { } outputState)
                {ApplyOutputSnapshot(outputState);lastValidOutputSelection=SelectedOutput;lastValidOutputSnapshot=outputState;}
                else
                {
                    outputAvailable=false;supportedOutputFormats=0;OutputName=SelectedOutput?.Name??"未选择输出声卡";
                    OutputFormat=result.OutputError is not null?"输出不可用："+result.OutputError:result.Output?.DeviceId is null?"没有可用输出声卡":OutputEndpointBlockReason(result.Output);
                    Changed(nameof(CanControlOutput));RefreshFormatOptions();
                }
                if(result.ReadInput||!result.SafeInput)
                {
                    inputSnapshot=result.InputState;
                    if(result.InputState is { } inputState)
                    {lastValidInputSelection=SelectedInput;lastValidInputSnapshot=inputState;InputFormat=inputState.Format;InputStatus="输入源已就绪";}
                    else{InputFormat=result.InputError??(result.Input?.DeviceId is null?"没有可用输入源":InputEndpointBlockReason(result.Input));InputStatus="输入源不可用";}
                    Changed(nameof(InputName));RefreshInputOptions();
                }
                bool formatChanged=oldMask!=supportedOutputFormats||oldFormat!=OutputFormat;
                bool updateFormats=(formatChanged||inputChanged)&&outputAvailable&&IsConnected&&CanManage&&UsbState=="RUNNING"&&string.IsNullOrEmpty(PendingTransaction);
                bool stopOutput=restartOutput||!result.SafeOutput&&audio is not null;
                bool stopInput=inputChanged||!result.SafeInput&&microphoneCapture is not null;
                if(stopOutput||stopInput||updateFormats||restartInput)
                {
                    long appliedGeneration=audioSelectionGeneration;
                    bool StillCurrent()=>!shuttingDown&&appliedGeneration==audioSelectionGeneration&&session==control;
                    pending=true;SetUserCommandPending(true);outputPollingStage="rebuilding-changed-endpoints";
                    if(stopOutput){await StopPlaybackCore();if(!StillCurrent())return;}
                    if(stopInput){await StopMicrophoneCore();if(!StillCurrent())return;}
                    if(updateFormats){await ApplyOutputFormats();if(!StillCurrent())return;}
                    if(restartOutput&&result.SafeOutput&&outputAvailable&&listeningIntent&&audio is null&&IsConnected&&CanManage&&UsbState=="RUNNING")await StartPlayback();
                    if(restartInput&&result.SafeInput&&inputSnapshot is not null&&microphoneIntent&&MicrophoneSupported&&IsConnected&&CanManage&&UsbState=="RUNNING")await StartMicrophoneCore();
                }
                RefreshToggleButtons();
            }
            finally{if(pending)SetUserCommandPending(false);commands.Release();}
        }
        finally{outputPollingStage="idle";}
    }
    static void ReplaceEndpoints<T>(ObservableCollection<T> target, IEnumerable<T> endpoints)
    {
        var next = endpoints.ToArray();
        if (target.SequenceEqual(next)) return;
        for (int i = 0; i < next.Length; i++)
        {
            if (i == target.Count) target.Add(next[i]);
            else if (!Equals(target[i], next[i])) target[i] = next[i];
        }
        while (target.Count > next.Length) target.RemoveAt(target.Count - 1);
    }

    // Explicit refresh buttons use the command domain; background polling uses
    // the gather/apply path above so driver reads never own that domain.
    // A default option keeps its ID while the bound Windows device changes.
    async Task RefreshAudioDeviceState()
    {
        if (outputSelectionPending || inputSelectionPending) return;
        string? oldOutput = SelectedOutput?.DeviceId, oldInput = SelectedInput?.DeviceId;
        RefreshOutputs();
        RefreshInputs();
        bool outputChanged = oldOutput != SelectedOutput?.DeviceId;
        bool inputChanged = oldInput != SelectedInput?.DeviceId;
        bool safeOutput = SelectedOutput?.DeviceId is not null && !IsBoardOutput(SelectedOutput);
        bool safeInput = SelectedInput?.DeviceId is not null && !IsBoardInput(SelectedInput);
        bool restartOutput = outputChanged || SelectedOutput?.IsSystemDefault == true && listeningIntent && (audio is null || audio.Failure is not null);
        bool restartInput = inputChanged || SelectedInput?.IsSystemDefault == true && microphoneIntent && microphoneCapture is null;
        if (restartOutput || !safeOutput && audio is not null) await StopPlaybackCore();
        if (inputChanged || !safeInput && microphoneCapture is not null) await StopMicrophoneCore();
        bool formatChanged = await RefreshOutputState();
        if (restartInput || inputSnapshot is null) await RefreshInputState();
        if ((formatChanged || inputChanged) && outputAvailable && IsConnected && CanManage && UsbState == "RUNNING" && string.IsNullOrEmpty(PendingTransaction))
            await ApplyOutputFormats();
        if (restartOutput && safeOutput && outputAvailable && listeningIntent && audio is null && IsConnected && CanManage && UsbState == "RUNNING")
            await StartPlayback();
        if (restartInput && safeInput && inputSnapshot is not null && microphoneIntent && MicrophoneSupported && IsConnected && CanManage && UsbState == "RUNNING")
            await StartMicrophoneCore();
        RefreshToggleButtons();
    }
}
