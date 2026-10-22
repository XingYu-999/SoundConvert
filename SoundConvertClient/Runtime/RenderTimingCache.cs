using System.Diagnostics;
using System.Threading;

namespace SoundConvertClient;

/// <summary>Primitive-only cached render facts. Unknown means no coherent read was obtained.</summary>
public readonly record struct RenderTimingSnapshot(bool Known, long Version, long TimestampFrequency,
    int BufferFrames, int SampleRate, bool StreamStarted, long StreamStartedTimestamp,
    long PaddingQueryBeginTimestamp, long PaddingQueryEndTimestamp, int PaddingFrames,
    long SuccessfulReleaseFrames, long SuccessfulReleaseCount, long LastReleaseBeginTimestamp,
    long LastReleaseEndTimestamp, long LastReleaseGapTicks, long MaximumReleaseGapTicks,
    long MaximumSteadyReleaseGapTicks, long SteadyZeroPaddingCount, long LastSteadyZeroPaddingTimestamp,
    long LastSteadyZeroPaddingReleaseAgeTicks, long LastSteadyZeroPaddingReleaseCount);

/// <summary>
/// One render-thread writer, bounded readers. Writes allocate nothing, do not
/// lock, and never call COM. Readers try at most three version checks, then
/// report Unknown rather than wait for the render thread.
/// </summary>
internal sealed class RenderTimingCache
{
    internal const int MaximumReadAttempts = 3;
    internal const int SteadyWarmupSeconds = 1;
    long version;
    int bufferFrames, sampleRate, paddingFrames;
    bool streamStarted;
    long streamStartedAt, paddingBegin, paddingEnd, releasedFrames, releaseCount;
    long releaseBegin, releaseEnd, releaseGap, maximumReleaseGap, maximumSteadyReleaseGap;
    long zeroPaddingCount, lastZeroPaddingAt, lastZeroPaddingReleaseAge, lastZeroPaddingReleaseCount;

    // Interlocked's full fence prevents payload stores preceding the odd
    // version. The final release-store publishes all payload fields together.
    void BeginWrite() => Interlocked.Increment(ref version);
    void EndWrite() => Volatile.Write(ref version, version + 1);

    internal void Reset(int actualBufferFrames, int outputSampleRate)
    {
        BeginWrite();
        bufferFrames = actualBufferFrames; sampleRate = outputSampleRate;
        streamStarted = false; streamStartedAt = paddingBegin = paddingEnd = 0; paddingFrames = 0;
        releasedFrames = releaseCount = releaseBegin = releaseEnd = releaseGap = maximumReleaseGap = maximumSteadyReleaseGap = 0;
        zeroPaddingCount = lastZeroPaddingAt = lastZeroPaddingReleaseAge = lastZeroPaddingReleaseCount = 0;
        EndWrite();
    }

    internal void MarkStarted(long timestamp)
    {
        BeginWrite(); streamStartedAt = timestamp; streamStarted = true; EndWrite();
    }

    internal void MarkStopped()
    {
        BeginWrite(); streamStarted = false; EndWrite();
    }

    bool IsSteady(long timestamp) => streamStarted && timestamp >= streamStartedAt &&
        timestamp - streamStartedAt >= Stopwatch.Frequency * SteadyWarmupSeconds;

    internal void RecordPadding(int frames, long queryBegin, long queryEnd)
    {
        BeginWrite();
        paddingFrames = frames; paddingBegin = queryBegin; paddingEnd = queryEnd;
        if (frames == 0 && IsSteady(queryEnd))
        {
            zeroPaddingCount++; lastZeroPaddingAt = queryEnd;
            lastZeroPaddingReleaseAge = releaseCount != 0 && queryEnd >= releaseEnd ? queryEnd - releaseEnd : 0;
            lastZeroPaddingReleaseCount = releaseCount;
        }
        EndWrite();
    }

    internal void RecordSuccessfulRelease(int frames, long queryBegin, long queryEnd)
    {
        BeginWrite();
        releaseGap = releaseCount != 0 && queryEnd >= releaseEnd ? queryEnd - releaseEnd : 0;
        maximumReleaseGap = Math.Max(maximumReleaseGap, releaseGap);
        if (IsSteady(queryEnd)) maximumSteadyReleaseGap = Math.Max(maximumSteadyReleaseGap, releaseGap);
        releasedFrames += frames; releaseCount++;
        releaseBegin = queryBegin; releaseEnd = queryEnd;
        EndWrite();
    }

    internal RenderTimingSnapshot Read()
    {
        for (int attempt = 0; attempt < MaximumReadAttempts; attempt++)
        {
            long before = Volatile.Read(ref version);
            if (before == 0 || (before & 1) != 0) continue;
            var snapshot = new RenderTimingSnapshot(true, before, Stopwatch.Frequency, bufferFrames, sampleRate,
                streamStarted, streamStartedAt, paddingBegin, paddingEnd, paddingFrames, releasedFrames, releaseCount,
                releaseBegin, releaseEnd, releaseGap, maximumReleaseGap, maximumSteadyReleaseGap,
                zeroPaddingCount, lastZeroPaddingAt, lastZeroPaddingReleaseAge, lastZeroPaddingReleaseCount);
            // Do not let payload loads float after the second version read.
            Thread.MemoryBarrier();
            if (before == Volatile.Read(ref version)) return snapshot;
        }
        return default;
    }
}
