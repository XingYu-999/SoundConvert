namespace UsbAudioBridge;
/// <summary>Shared float/packed output path. Starts the configured fade when rendered source audio resumes.</summary>
public sealed class AudioOutputPipeline(AudioBuffer source,AudioPostProcessor processor)
{
    bool rendering;
    public int SampleRate=>source.OutputSampleRate;
    public int Read(float[] samples,int offset,int count)
    {
        long before=source.RenderedFrames;int read=source.Read(samples,offset,count);bool active=source.RenderedFrames>before;
        if(active&&!rendering)processor.BeginResumeFade();rendering=active;processor.Process(samples.AsSpan(offset,read));return read;
    }
}
