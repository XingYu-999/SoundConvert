namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    double playbackBufferMs,playbackDriftPpm;
    bool hasPlaybackMetrics;
    long playbackMetricSequence,playbackMetricGeneration;
    public double PlaybackBufferMs { get=>playbackBufferMs; private set=>Set(ref playbackBufferMs,value); }
    public double PlaybackDriftPpm { get=>playbackDriftPpm; private set=>Set(ref playbackDriftPpm,value); }
    public bool HasPlaybackMetrics { get=>hasPlaybackMetrics; private set=>Set(ref hasPlaybackMetrics,value); }
    public long PlaybackMetricSequence => playbackMetricSequence;
    public long PlaybackMetricGeneration => playbackMetricGeneration;
    void ResetPlaybackMetrics()
    {
        HasPlaybackMetrics=false; PlaybackBufferMs=0; PlaybackDriftPpm=0;
        ++playbackMetricGeneration; Changed(nameof(PlaybackMetricGeneration));
    }
    void PublishPlaybackMetrics(double bufferMs,double driftPpm)
    {
        HasPlaybackMetrics=IsListening&&double.IsFinite(bufferMs)&&double.IsFinite(driftPpm);
        PlaybackBufferMs=double.IsFinite(bufferMs)?bufferMs:0;
        PlaybackDriftPpm=double.IsFinite(driftPpm)?driftPpm:0;
        ++playbackMetricSequence; Changed(nameof(PlaybackMetricSequence));
    }
}
