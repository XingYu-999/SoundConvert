using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using NAudio.Wave;
using System.Runtime.InteropServices;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed record OutputEndpoint(string Id, string Name, string? DefaultDeviceId = null)
{
    public bool IsSystemDefault => Id == WindowsDefaultAudio.Id;
    public string? DeviceId => IsSystemDefault ? DefaultDeviceId : Id;
    public override string ToString() => Name;
}
public sealed class WindowsAudio : IDisposable
{
    readonly MMDevice device; readonly IWavePlayer output;readonly string name;readonly double endpointNameInitialReadMs;
    readonly ClockedWasapiOut? clockedOutput;
    OutputClockObserver? outputClockObserver;
    OutputClockObserverDiagnostics disabledClockDiagnostics=new(false,0,"disabled",0,0,0,0,0,System.Diagnostics.Stopwatch.Frequency,0,0,null,null,true);
    static long outputGenerationCounter;
    bool disposed, clockObservationDisabled;
    public ulong OutputGeneration { get; private set; }
    public string? Failure{get;private set;} public string Name => name; public AudioBuffer Buffer { get; }
    public AudioPostProcessor PostProcessor{get;}=new();
    public object DeviceOutputFormat=>DescribeWaveFormat(output.OutputWaveFormat);
    public bool ExclusiveOutput{get;}
    public int? ActualBufferFrames{get;private set;}public double? ActualBufferMs{get;private set;}public double? ReportedStreamLatencyMs{get;private set;}public bool ContinuityAccepted=>Failure is null&&Buffer.MissingFrames==0&&Buffer.OutputUnderruns==0&&Buffer.Resyncs<=1;
    public object OutputDiagnostics()=>new{endpoint=Name,endpoint_name_cached=true,endpoint_name_initial_read_ms=endpointNameInitialReadMs,source_format=Buffer.SourceFormat,output_sample_rate=Buffer.OutputSampleRate,nominal_asrc_ratio=Buffer.SourceFormat.SampleRate/(double)Buffer.OutputSampleRate,format_changes=Buffer.FormatChanges,concealed_source_frames=Buffer.ConcealedSourceFrames,concealed_output_frames=Buffer.ConcealedOutputFrames,boundary_fade_frames=Buffer.BoundaryFadeFrames,maximum_output_step=Buffer.MaximumOutputStep,maximum_output_second_difference=Buffer.MaximumOutputSecondDifference,output_steps_over_quarter_scale=Buffer.OutputStepsOverQuarterScale,conflicting_overlap_frames=Buffer.ConflictingOverlapFrames,maximum_read_duration_us=Buffer.MaximumReadDurationUs,read_deadline_misses=Buffer.ReadDeadlineMisses,output_callbacks=Buffer.OutputCallbacks,output_frames_requested=Buffer.OutputFramesRequested,rendered_frames=Buffer.RenderedFrames,startup_underrun_callbacks=Buffer.StartupUnderruns,steady_underrun_callbacks=Buffer.SteadyUnderruns,underrun_events=Buffer.UnderrunEvents,overflow_dropped_frames=Buffer.OverflowDroppedFrames,maximum_arrival_gap_ms=Buffer.MaximumArrivalGapMs,maximum_arrival_burst_ms=Buffer.MaximumArrivalBurstFrames*1000d/Buffer.SourceFormat.SampleRate,conservative_prefill_ms=Buffer.ConservativePrefillMs,required_prefill_ms=Buffer.RequiredPrefillMs,recommended_buffer_ms=Buffer.RecommendedBufferMs,buffer_configuration_insufficient=Buffer.BufferConfigurationInsufficient,capacity_below_conservative_recommendation=Buffer.CapacityBelowRecommendation,continuity_accepted=ContinuityAccepted,missing_frames=Buffer.MissingFrames,late_drops=Buffer.Late,late_dropped_source_frames=Buffer.LateSourceFrames,permanent_gap_source_frames=Buffer.PermanentGapSourceFrames,prefill_trimmed_source_frames=Buffer.PrefillTrimmedSourceFrames,committed_source_frames=Buffer.CommittedSourceFrames,duplicate_source_frames=Buffer.Duplicates,overflows=Buffer.Overflows,resync_count=Buffer.Resyncs,buffer_ms=Buffer.BufferedMs,estimated_drift_ppm=Buffer.DriftPpm,output_underrun_callbacks=Buffer.OutputUnderruns,output_silence_frames=Buffer.OutputSilenceFrames,buffering_callbacks=Buffer.BufferingCallbacks,rebuffer_count=Buffer.RebufferCount,interpolation_incomplete_frames=Buffer.InterpolationIncompleteFrames,initializations=Buffer.Initializations,asrc=$"{Buffer.FilterTaps}-tap ratio-aware Blackman sinc / 1024 interpolated phases / ±500 ppm",resampler_quality=Buffer.Quality.ToString(),asrc_lookahead_ms=Buffer.LookAheadMs,drift_reference_ms=Buffer.DriftReferenceMs,arrival_jitter_ms=Buffer.ArrivalJitterMs,shadow_clock_ppm=Buffer.ShadowClockPpm,shadow_clock_confident=Buffer.ShadowClockConfident,repaired_before_playback_frames=Buffer.RepairedBeforePlaybackFrames,realtime_timing=Buffer.TimingSnapshot(),output_clock=OutputClockDiagnostics(),requested_latency_ms=10,actual_buffer_frames=ActualBufferFrames,actual_buffer_ms=ActualBufferMs,reported_stream_latency_ms=ReportedStreamLatencyMs,jitter_limit_ms=Buffer.MaximumBufferMs,prefill_ms=Buffer.TargetFrames*1000d/Buffer.SourceFormat.SampleRate,largest_input_read_ms=Buffer.LargestReadFrames*1000d/Buffer.OutputSampleRate,current_input_read_ms=Buffer.CurrentReadFrames*1000d/Buffer.OutputSampleRate,oversized_read_callbacks=Buffer.OversizedReadCallbacks,output_demand_exceeds_limit=Buffer.OutputDemandTooLarge};
    public static List<OutputEndpoint> Endpoints() { using var e = new MMDeviceEnumerator(); var result = new List<OutputEndpoint>(); foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) { using (d) {UsbEndpointIdentity.CacheEndpoint(d);result.Add(new(d.ID, d.FriendlyName));} } return result; }
    public WindowsAudio(string? id = null,int maximumBufferMs=20,ResamplerQuality quality=ResamplerQuality.Balanced,AudioFormat? exclusiveFormat=null,bool observeClock=false)
    {
        using var e=new MMDeviceEnumerator();device=id is null?e.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia):e.GetDevice(id);
        // Endpoint metadata is stable for this output instance. Reading
        // MMDevice.FriendlyName performs synchronous COM and must stay off the
        // repeated state-dispatcher diagnostics path.
        long nameStarted=System.Diagnostics.Stopwatch.GetTimestamp();name=device.FriendlyName;endpointNameInitialReadMs=System.Diagnostics.Stopwatch.GetElapsedTime(nameStarted).TotalMilliseconds;
        ExclusiveOutput=exclusiveFormat is not null;exclusiveFormat?.Validate();
        int outputRate=exclusiveFormat?.SampleRate??device.AudioClient.MixFormat.SampleRate;
        Buffer=new AudioBuffer(maximumBufferMs,outputSampleRate:outputRate,quality:quality);
        PostProcessor.Reset(outputRate,0);
        var share=ExclusiveOutput?AudioClientShareMode.Exclusive:AudioClientShareMode.Shared;
        var pipeline=new AudioOutputPipeline(Buffer,PostProcessor);IWaveProvider provider=exclusiveFormat is null?new SampleToWaveProvider(new BufferProvider(pipeline)):new PackedOutputProvider(pipeline,exclusiveFormat);
        if(ExclusiveOutput&&!device.AudioClient.IsFormatSupported(share,provider.WaveFormat))
        {device.Dispose();throw new NotSupportedException("The selected headphone does not support the requested exclusive PCM format.");}
        if(observeClock){clockedOutput=new ClockedWasapiOut(device,share,true,10);output=clockedOutput;}
        else output=new WasapiOut(device,share,true,10);
        output.PlaybackStopped+=(_,args)=>{if(args.Exception is not null)Failure=args.Exception.Message;};
        output.Init(provider);
        if(exclusiveFormat is not null&&(output.OutputWaveFormat.SampleRate!=exclusiveFormat.SampleRate||output.OutputWaveFormat.BitsPerSample!=exclusiveFormat.Bits||output.OutputWaveFormat.BlockAlign!=exclusiveFormat.BytesPerFrame))
        {output.Dispose();device.Dispose();throw new InvalidOperationException("The actual exclusive output format differs from the request.");}
        try
        {
            if(clockedOutput is not null)
            {ActualBufferFrames=clockedOutput.ActualBufferFrames;ActualBufferMs=ActualBufferFrames*1000d/output.OutputWaveFormat.SampleRate;ReportedStreamLatencyMs=clockedOutput.ReportedStreamLatency/10000d;}
            else if(typeof(WasapiOut).GetField("audioClient",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(output) is AudioClient client)
            {ActualBufferFrames=client.BufferSize;ActualBufferMs=client.BufferSize*1000d/output.OutputWaveFormat.SampleRate;ReportedStreamLatencyMs=client.StreamLatency/10000d;}
        }catch{ /* Explicitly unavailable, never estimated. */ }
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(output.PlaybackState==PlaybackState.Playing)return;
        OutputGeneration=unchecked((ulong)Interlocked.Increment(ref outputGenerationCounter));
        disabledClockDiagnostics=disabledClockDiagnostics with { OutputGeneration=OutputGeneration };
        output.Play();
        if(clockedOutput is not null&&!clockObservationDisabled)
        {
            outputClockObserver=new OutputClockObserver(OutputGeneration,clockedOutput.OpenClockReader,
                ()=>output.PlaybackState==PlaybackState.Playing);
            outputClockObserver.Start();
        }
    }
    public bool TryDequeueOutputClockSample(out RawOutputClockSample sample)
    {
        var observer=outputClockObserver;
        if(observer is not null)return observer.TryDequeue(out sample);
        sample=default;return false;
    }
    public OutputClockObserverDiagnostics OutputClockDiagnostics()=>outputClockObserver?.Diagnostics??disabledClockDiagnostics;
    public static Task PrewarmAsync(string endpoint,ResamplerQuality quality)=>Task.Run(()=>{using var e=new MMDeviceEnumerator();using var d=e.GetDevice(endpoint);SincFilter.Prewarm(d.AudioClient.MixFormat.SampleRate,quality);});
    public async Task StartAndWarmUpAsync(CancellationToken cancellationToken=default){Start();var wait=System.Diagnostics.Stopwatch.StartNew();while(Buffer.OutputCallbacks<3){cancellationToken.ThrowIfCancellationRequested();if(Failure is not null)throw new IOException("Audio output initialization failed: "+Failure);if(wait.ElapsedMilliseconds>3000)throw new TimeoutException("Audio output did not produce three callbacks during initialization");await Task.Delay(5,cancellationToken).ConfigureAwait(false);}if(Buffer.OutputDemandTooLarge)throw new InvalidOperationException($"Actual audio callback requires {Buffer.CurrentReadFrames*1000d/Buffer.OutputSampleRate+.5:F1} ms; increase the explicit buffer limit before listening.");}
    public void Stop()
    {
        OutputClockShutdown.StopObserverAndAudio(outputClockObserver,output.Stop,TimeSpan.FromSeconds(2),exception=>
        {
            clockObservationDisabled=true;
            Failure=Failure is null?exception.Message:Failure+"; "+exception.Message;
        });
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{Stop();}
        finally
        {
            // This also runs when Stop throws. Native release cannot race a
            // pending clock query, and its timeout cannot look like clean exit.
            OutputClockShutdown.ReleaseAfterObserver(outputClockObserver,()=>
            {
                try{output.Dispose();}finally{device.Dispose();}
            },ex=>Failure=Failure is null?"Deferred output clock cleanup failed: "+ex.Message:
                Failure+"; Deferred output clock cleanup failed: "+ex.Message);
        }
    }
    sealed class BufferProvider(AudioOutputPipeline source) : ISampleProvider { public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate,2);public int Read(float[] b,int o,int n)=>source.Read(b,o,n); }
    // Explicit-format diagnostic output. The GUI keeps its existing shared
    // endpoint mix format; requesting this path never changes Windows defaults.
    sealed class PackedOutputProvider(AudioOutputPipeline source,AudioFormat format):IWaveProvider
    {
        float[] scratch=Array.Empty<float>();
        public WaveFormat WaveFormat{get;}=SourceWaveFormat(format);
        public int Read(byte[] buffer,int offset,int count)
        {
            int samples=count/format.BytesPerFrame*2;
            if(scratch.Length<samples)scratch=new float[samples];
            source.Read(scratch,0,samples);int scale=format.Bits==24?8388608:32768;
            for(int i=0;i<samples;i++)
            {
                int value=(int)Math.Clamp(Math.Round(scratch[i]*scale),-scale,scale-1);
                int at=offset+i*format.BytesPerSample;buffer[at]=(byte)value;buffer[at+1]=(byte)(value>>8);
                if(format.Bits==24)buffer[at+2]=(byte)(value>>16);
            }
            return samples*format.BytesPerSample;
        }
    }
    public static async Task ToneAsync(string id, int seconds, AudioFormat? sourceFormat = null, Action<object>? report = null,CancellationToken cancellationToken=default)
    {
        sourceFormat?.Validate();
        void Stage(string name, object? detail = null) => report?.Invoke(new { stage = name, utc = DateTimeOffset.UtcNow, detail });
        Stage("source_endpoint_open_begin");
        using var e = new MMDeviceEnumerator(); using var d = e.GetDevice(id);
        Stage("source_endpoint_opened", new { endpoint = d.FriendlyName });
        var mode = sourceFormat is null ? AudioClientShareMode.Shared : AudioClientShareMode.Exclusive;
        IWaveProvider provider = sourceFormat is null ? new SampleToWaveProvider(new ToneProvider()) : new PcmToneProvider(sourceFormat);
        Stage("source_format_support_begin", new { requested = provider.WaveFormat.ToString(), wave_format = DescribeWaveFormat(provider.WaveFormat), share_mode = mode.ToString() });
        if (sourceFormat is not null)
        {
            bool supported = d.AudioClient.IsFormatSupported(mode, provider.WaveFormat);
            Stage("source_format_support_result", new { supported });
            if (!supported) throw new NotSupportedException($"USB endpoint does not support exclusive {sourceFormat.SampleRate}/{sourceFormat.Bits}/stereo.");
        }
        using var o = new WasapiOut(d, mode, true, 20);
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        o.PlaybackStopped += (_, args) => { Stage("source_playback_stopped", new { error = args.Exception?.ToString() }); stopped.TrySetResult(args.Exception); };
        Stage("source_init_begin"); o.Init(provider);
        Stage("source_init_complete", new { actual_format = o.OutputWaveFormat.ToString(), wave_format = DescribeWaveFormat(o.OutputWaveFormat) });
        if (sourceFormat is not null && (o.OutputWaveFormat.SampleRate != sourceFormat.SampleRate || o.OutputWaveFormat.BitsPerSample != sourceFormat.Bits ||
            o.OutputWaveFormat.Channels != sourceFormat.Channels || o.OutputWaveFormat.BlockAlign != sourceFormat.BytesPerFrame))
            throw new InvalidOperationException("WASAPI selected a different format; refusing to mislabel the source test.");
        Stage("source_play_begin"); o.Play(); Stage("source_play_returned");
        var duration = Task.Delay(TimeSpan.FromSeconds(seconds),cancellationToken);
        if (await Task.WhenAny(duration, stopped.Task).ConfigureAwait(false) == stopped.Task)
            throw new IOException("WASAPI source stopped before the requested duration.", await stopped.Task.ConfigureAwait(false));
        Stage("source_stop_begin"); o.Stop(); Stage("source_stop_complete");
    }
    public static WaveFormat SourceWaveFormat(AudioFormat format)
    {
        format.Validate();
        // Modern usbaudio.sys expects WAVEFORMATEXTENSIBLE for 24-bit PCM.
        // This changes the Windows API structure, not USB's packed three-byte samples.
        return format.Bits == 24 ? new WaveFormatExtensible(format.SampleRate, 24, 2) : new WaveFormat(format.SampleRate, 16, 2);
    }
    public static object DescribeWaveFormat(WaveFormat format)
    {
        int validBits = format.BitsPerSample; uint? channelMask = null; Guid? subFormat = null;
        if (format is WaveFormatExtensible extensible)
        {
            // NAudio 2.2.1 exposes SubFormat but not the mask/valid-bit fields.
            // Read the exact WAVEFORMATEXTENSIBLE structure passed to Windows.
            IntPtr pointer = WaveFormat.MarshalToPtr(format);
            try { validBits = (ushort)Marshal.ReadInt16(pointer, 18); channelMask = (uint)Marshal.ReadInt32(pointer, 20); }
            finally { Marshal.FreeHGlobal(pointer); }
            subFormat = extensible.SubFormat;
        }
        return new { encoding = format.Encoding.ToString(), format_tag = unchecked((ushort)format.Encoding),
            sample_rate = format.SampleRate, container_bits = format.BitsPerSample, valid_bits = validBits,
            channels = format.Channels, block_align = format.BlockAlign, channel_mask = channelMask, sub_format = subFormat };
    }
    sealed class PcmToneProvider(AudioFormat format) : IWaveProvider
    {
        long frame;
        public WaveFormat WaveFormat { get; } = SourceWaveFormat(format);
        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / format.BytesPerFrame;
            for (int i = 0; i < frames; i++, frame++) for (int channel = 0; channel < 2; channel++)
            {
                double value = (channel == 0 ? .08 : .05) * Math.Sin(2 * Math.PI * (channel == 0 ? 440 : 880) * frame / format.SampleRate);
                int sample = (int)(value * (format.Bits == 24 ? 8388607 : 32767));
                int at = offset + i * format.BytesPerFrame + channel * format.BytesPerSample;
                buffer[at] = (byte)sample; buffer[at + 1] = (byte)(sample >> 8);
                if (format.Bits == 24) buffer[at + 2] = (byte)(sample >> 16);
            }
            return frames * format.BytesPerFrame;
        }
    }
    sealed class ToneProvider : ISampleProvider { long frame; public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); public int Read(float[] b, int o, int n) { for (int i = 0; i < n / 2; i++, frame++) { b[o + i * 2] = (float)(.08 * Math.Sin(2 * Math.PI * 440 * frame / 48000)); b[o + i * 2 + 1] = (float)(.05 * Math.Sin(2 * Math.PI * 880 * frame / 48000)); } return n; } }
}














