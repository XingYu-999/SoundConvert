using NAudio.CoreAudioApi;
using NAudio.Wave;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed record InputEndpoint(string Id,string Name,string? DefaultDeviceId=null)
{
    public bool IsSystemDefault=>Id==WindowsDefaultAudio.Id;
    public string? DeviceId=>IsSystemDefault?DefaultDeviceId:Id;
    public override string ToString()=>Name;
}
public sealed record InputSnapshot(string Id,string Name,string Format,uint SupportedMask,int SampleRate,int PrecisionBits,int Channels);
public interface IInputCapture:IDisposable{event Action<float[]>? Samples;string? Failure{get;}long Frames{get;}double Peak{get;}double Rms{get;}int SampleRate{get;}void Start();}
/// <summary>The sample span is borrowed only for the synchronous event call.</summary>
public delegate void InputSamplesHandler(ReadOnlySpan<float> samples);
public interface ISpanInputCapture:IInputCapture{event InputSamplesHandler? SpanSamples;}
public interface IAudioInputControl{IReadOnlyList<InputEndpoint> Endpoints();Task<InputSnapshot> ReadAsync(string id,CancellationToken ct=default);IInputCapture CreateCapture(string id);}
public sealed class WindowsInputAudioControl:IAudioInputControl
{
    public IInputCapture CreateCapture(string id)=>new MicrophoneCapture(id);
    public IReadOnlyList<InputEndpoint> Endpoints()
    {
        var current=WindowsDefaultAudio.Get(DataFlow.Capture);
        using var e=new MMDeviceEnumerator();var result=new List<InputEndpoint>();
        foreach(var d in e.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active)){using(d){UsbEndpointIdentity.CacheEndpoint(d);result.Add(new(d.ID,d.FriendlyName));}}
        return [new(WindowsDefaultAudio.Id,WindowsDefaultAudio.Label(current.Name),current.Id),..result.OrderByDescending(x=>x.Id==current.Id).ThenBy(x=>x.Name.Contains("Virtual",StringComparison.OrdinalIgnoreCase)||x.Name.Contains("Streaming",StringComparison.OrdinalIgnoreCase)||x.Name.Contains("ToDesk",StringComparison.OrdinalIgnoreCase))];
    }
    public Task<InputSnapshot> ReadAsync(string id,CancellationToken ct=default)=>Task.Run(()=>{ct.ThrowIfCancellationRequested();using var e=new MMDeviceEnumerator();using var d=e.GetDevice(id);if(d.State!=DeviceState.Active)throw new IOException("声音输入已离线");using var client=d.AudioClient;var mix=DeviceAudioFormat.FromWaveFormat(client.MixFormat);DeviceAudioFormat? selected=null;try{if(d.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat]?.Value is byte[] b)selected=DeviceAudioFormat.Parse(b);}catch{}
        uint mask=selected is null?0:CaptureFormat.FromSource(selected.SampleRate,selected.PrecisionBits,selected.Channels)&CaptureFormat.FromSource(mix.SampleRate,mix.PrecisionBits,mix.Channels);
        return new InputSnapshot(id,d.FriendlyName,$"设备：{selected?.Label??"格式未知"}\n采集：{mix.Label}",mask,mix.SampleRate,Math.Min(selected?.PrecisionBits??0,mix.PrecisionBits),mix.Channels);},ct);
}
/// <summary>Capture only; no recording or local speaker monitoring.</summary>
public sealed class MicrophoneCapture:ISpanInputCapture
{
    readonly MMDevice device;readonly WasapiCapture capture;int disposed;readonly int sampleBytes,channels,validBits;readonly bool floating;readonly float[] mono;float[] legacyMono=[];
    long callbackAllocatedBytes,callbacks;int maximumCallbackFrames,callbackThreadId,callbackThreadPriority;
    readonly UsbAudioBridge.AudioCaptureTiming callbackTiming=new();
    public event Action<float[]>? Samples;public event InputSamplesHandler? SpanSamples;
    public string? Failure{get;private set;}public long Frames{get;private set;}public double Peak{get;private set;}public double Rms{get;private set;}
    public int SampleRate=>capture.WaveFormat.SampleRate;public int Channels=>channels;
    public MicrophoneCapture(string endpoint)
    {
        using var e=new MMDeviceEnumerator();device=e.GetDevice(endpoint);try{capture=new WasapiCapture(device,true,10);}catch{device.Dispose();throw;}var format=DeviceAudioFormat.FromWaveFormat(capture.WaveFormat);sampleBytes=format.ContainerBits/8;channels=format.Channels;validBits=format.ValidBits;floating=format.FloatingPoint;
        if(format.ContainerBits is not(16 or 24 or 32)||floating&&format.ContainerBits!=32||SampleRate is <8000 or >384000){capture.Dispose();device.Dispose();throw new NotSupportedException("暂不支持该麦克风采集编码或采样率，请在 Windows 中选择8–384kHz、16/24/32位PCM或32位浮点。");}
        // One second is a fixed upper bound, well above the requested 10 ms
        // WASAPI period. A slow driver cannot grow the callback workspace.
        mono=new float[SampleRate];
        capture.DataAvailable+=OnDataAvailable;capture.RecordingStopped+=(_,a)=>{if(a.Exception is not null)Failure=a.Exception.Message;};
    }
    void OnDataAvailable(object? sender,WaveInEventArgs a)
    {
        if(Volatile.Read(ref disposed)!=0||Failure is not null)return;
        long began=System.Diagnostics.Stopwatch.GetTimestamp(),allocationStart=GC.GetAllocatedBytesForCurrentThread();
        try
        {
            if(Volatile.Read(ref callbackThreadId)==0){callbackThreadPriority=(int)Thread.CurrentThread.Priority;Volatile.Write(ref callbackThreadId,Environment.CurrentManagedThreadId);}
            int frames=a.BytesRecorded/(sampleBytes*channels);Interlocked.Increment(ref callbacks);maximumCallbackFrames=Math.Max(maximumCallbackFrames,frames);
            callbackTiming.Begin(began,frames,GC.GetTotalPauseDuration().Ticks,GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2));
            if(frames>mono.Length){Failure=$"麦克风单次采集超过固定工作区（{frames} > {mono.Length} 帧），输入已停止处理";return;}
            double peak=0,energy=0;
            for(int f=0;f<frames;f++){double sum=0;for(int ch=0;ch<channels;ch++){int at=(f*channels+ch)*sampleBytes;double v=floating?BitConverter.ToSingle(a.Buffer,at):sampleBytes==2?System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(a.Buffer.AsSpan(at))/32768d:sampleBytes==3?PcmCodec.ReadSigned(a.Buffer.AsSpan(at),24)/8388608d:System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(a.Buffer.AsSpan(at))/2147483648d;sum+=double.IsFinite(v)?v:0;}float value=(float)Math.Clamp(sum/channels,-1,1);mono[f]=value;peak=Math.Max(peak,Math.Abs(value));energy+=value*value;}
            Peak=peak;Rms=frames==0?0:Math.Sqrt(energy/frames);Frames+=frames;
            SpanSamples?.Invoke(mono.AsSpan(0,frames));
            // Existing recording tools require exact-length arrays. Their
            // compatibility path allocates only when they actually subscribe.
            var legacy=Samples;if(legacy is not null){if(legacyMono.Length!=frames)legacyMono=new float[frames];mono.AsSpan(0,frames).CopyTo(legacyMono);legacy(legacyMono);}
        }
        finally{callbackTiming.Complete(System.Diagnostics.Stopwatch.GetTimestamp());Interlocked.Add(ref callbackAllocatedBytes,GC.GetAllocatedBytesForCurrentThread()-allocationStart);}
    }
    public object Statistics()=>new{callbacks=Interlocked.Read(ref callbacks),callback_allocated_bytes=Interlocked.Read(ref callbackAllocatedBytes),maximum_callback_frames=Volatile.Read(ref maximumCallbackFrames),workspace_frames=mono.Length,callback_thread_id=Volatile.Read(ref callbackThreadId),callback_thread_priority=((ThreadPriority)Volatile.Read(ref callbackThreadPriority)).ToString(),callback_thread_priority_cached=Volatile.Read(ref callbackThreadId)!=0,span_subscribers=SpanSamples is not null,legacy_array_subscribers=Samples is not null,timing=callbackTiming.Snapshot(),allocation_scope="application callback and synchronous subscribers; excludes NAudio before callback"};
    public void Start()=>capture.StartRecording();
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;try{capture.StopRecording();}finally{try{capture.Dispose();}finally{device.Dispose();}}}
}
