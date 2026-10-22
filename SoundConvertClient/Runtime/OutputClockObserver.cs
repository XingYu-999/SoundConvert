using System.Diagnostics;

namespace SoundConvertClient;

/// <summary>Unmodified IAudioClock values, with the querying QPC interval for audit.</summary>
public readonly record struct OutputClockRead(ulong Position, ulong Frequency, ulong Qpc100ns,
    long QueryBeginTimestamp, long QueryEndTimestamp, int HResult, int FrequencyHResult,
    RenderTimingSnapshot RenderBefore = default, RenderTimingSnapshot RenderAfter = default);

public readonly record struct RawOutputClockSample(ulong OutputGeneration, ulong Position, ulong Frequency,
    ulong Qpc100ns, long QueryBeginTimestamp, long QueryEndTimestamp, long QueryTimestampFrequency,
    int HResult, int FrequencyHResult, bool Running, bool Accepted, string Reason,
    RenderTimingSnapshot RenderBefore = default, RenderTimingSnapshot RenderAfter = default);

/// <summary>The reader is opened, queried, and disposed on one dedicated MTA thread.</summary>
public interface IOutputClockReader : IDisposable
{
    OutputClockRead Read();
}

public sealed record OutputClockObserverDiagnostics(bool Enabled, ulong OutputGeneration, string State,
    long Samples, long AcceptedSamples, long RejectedSamples, long DroppedSamples, int QueuedSamples,
    long QueryTimestampFrequency, long LastAcceptedTimestamp, double MaximumQueryDurationMs,
    string? LastRejection, string? Failure, bool WorkerExited);

/// <summary>Pure validation, shared by the observer and deterministic checks.</summary>
public sealed class OutputClockSampleValidator
{
    // Absolute-origin sanity bounds, not fit-quality or accuracy claims.
    // 1 ms permits conversion/driver software timestamp granularity. 100 ms is
    // one observation period: older paired timestamps cannot remain "fresh".
    public const ulong MaximumFutureQpcTolerance100ns = 10_000;
    public const ulong MaximumQueryAge100ns = 1_000_000;
    readonly ulong generation;
    readonly long queryTimestampFrequency;
    ulong previousPosition, previousQpc, previousFrequency;
    bool hasPrevious;
    public OutputClockSampleValidator(ulong generation, long? queryTimestampFrequency = null)
    {
        this.generation = generation;
        this.queryTimestampFrequency = queryTimestampFrequency ?? Stopwatch.Frequency;
        if (this.queryTimestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(queryTimestampFrequency));
    }
    public RawOutputClockSample Validate(OutputClockRead value, bool running)
    {
        string reason = !running ? "not_running" : value.FrequencyHResult != 0 ? "frequency_hresult" :
            value.HResult == 1 ? "position_s_false" : value.HResult != 0 ? "position_hresult" :
            value.Frequency == 0 ? "frequency_zero" : value.Position == 0 ? "position_zero" :
            value.Qpc100ns == 0 ? "qpc_zero" :
            value.QueryBeginTimestamp < 0 || value.QueryEndTimestamp < 0 ? "query_timestamp_negative" :
            value.QueryEndTimestamp < value.QueryBeginTimestamp ? "query_timestamp_backward" :
            hasPrevious && value.Frequency != previousFrequency ? "frequency_changed" :
            hasPrevious && value.Position < previousPosition ? "position_backward" :
            hasPrevious && value.Qpc100ns < previousQpc ? "qpc_backward" :
            hasPrevious && value.Position == previousPosition ? "position_stalled" :
            hasPrevious && value.Qpc100ns == previousQpc ? "qpc_stalled" : CheckAbsoluteQpc(value);
        bool accepted = reason == "accepted";
        if (accepted)
        {
            previousPosition = value.Position; previousQpc = value.Qpc100ns;
            previousFrequency = value.Frequency; hasPrevious = true;
        }
        return new(generation, value.Position, value.Frequency, value.Qpc100ns,
            value.QueryBeginTimestamp, value.QueryEndTimestamp, queryTimestampFrequency,
            value.HResult, value.FrequencyHResult, running, accepted, reason, value.RenderBefore, value.RenderAfter);
    }

    string CheckAbsoluteQpc(OutputClockRead value)
    {
        // Multiply before dividing without overflowing 64-bit QPC origins.
        UInt128 begin100ns = (UInt128)(ulong)value.QueryBeginTimestamp * 10_000_000 / (ulong)queryTimestampFrequency;
        UInt128 end100ns = (UInt128)(ulong)value.QueryEndTimestamp * 10_000_000 / (ulong)queryTimestampFrequency;
        UInt128 pairedQpc = value.Qpc100ns;
        if (end100ns < begin100ns) return "query_timestamp_backward";
        if (pairedQpc > end100ns + MaximumFutureQpcTolerance100ns) return "qpc_future";
        if (end100ns > pairedQpc && end100ns - pairedQpc > MaximumQueryAge100ns) return "qpc_too_old";
        return "accepted";
    }
}

/// <summary>
/// Low-frequency observation only. No observer method runs in the render callback;
/// the queue lock is private and never shared with audio, control or snapshots.
/// </summary>
public sealed class OutputClockObserver : IDisposable
{
    public const int FrequencyHz = 10;
    public const int QueueCapacity = 512;
    readonly Func<IOutputClockReader> openReader;
    readonly Func<bool> isRunning;
    readonly ManualResetEventSlim stop = new(false);
    readonly object queueGate = new();
    readonly Queue<RawOutputClockSample> queue = new(QueueCapacity);
    readonly Thread worker;
    readonly ulong generation;
    OutputClockObserverDiagnostics diagnostics;
    bool started;
    volatile bool exited;
    long samples, accepted, rejected, dropped, lastAccepted;
    double maximumQueryMs;
    string? lastRejection, failure;

    public OutputClockObserver(ulong outputGeneration, Func<IOutputClockReader> openReader, Func<bool> isRunning)
    {
        generation = outputGeneration; this.openReader = openReader; this.isRunning = isRunning;
        diagnostics = new(true, generation, "created", 0, 0, 0, 0, 0, Stopwatch.Frequency, 0, 0, null, null, false);
        worker = new Thread(Run) { IsBackground = true, Name = "UAB output clock observer", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        if (started || exited) throw new InvalidOperationException("An output clock observer can only be started once.");
        started = true; worker.Start();
    }

    public bool TryDequeue(out RawOutputClockSample sample)
    {
        lock (queueGate)
        {
            if (queue.Count == 0) { sample = default; return false; }
            sample = queue.Dequeue();
            Volatile.Write(ref diagnostics, Volatile.Read(ref diagnostics) with { QueuedSamples = queue.Count });
            return true;
        }
    }

    // Pure cached data: this does not call IAudioClock or any endpoint COM API.
    public OutputClockObserverDiagnostics Diagnostics => Volatile.Read(ref diagnostics);
    public bool WorkerExited => exited;

    public bool Stop(TimeSpan timeout)
    {
        if(exited)return true;
        stop.Set();
        if (!started) { exited = true; Publish("stopped"); return true; }
        if (Thread.CurrentThread == worker) return false;
        bool completed = worker.Join(timeout);
        if (!completed)
        {
            failure = "output_clock_query_did_not_exit_within_timeout";
            Publish("stop_pending");
        }
        return completed;
    }

    internal void WaitForExit() { if (started && Thread.CurrentThread != worker) worker.Join(); }

    void Run()
    {
        IOutputClockReader? reader = null;
        try
        {
            reader = openReader();
            var validator = new OutputClockSampleValidator(generation);
            Publish("observing");
            // Wait before the first query so a new render stream may advance
            // beyond its documented initial zero position. No catch-up bursts.
            while (!stop.Wait(TimeSpan.FromMilliseconds(1000d / FrequencyHz)))
            {
                OutputClockRead read = reader.Read();
                var sample = validator.Validate(read, !stop.IsSet && isRunning());
                samples++;
                if (sample.Accepted) { accepted++; lastAccepted = read.QueryEndTimestamp; }
                else { rejected++; lastRejection = sample.Reason; }
                maximumQueryMs = Math.Max(maximumQueryMs,
                    Math.Max(0, (read.QueryEndTimestamp - read.QueryBeginTimestamp) * 1000d / Stopwatch.Frequency));
                lock (queueGate)
                {
                    if (queue.Count == QueueCapacity) { queue.Dequeue(); dropped++; }
                    queue.Enqueue(sample);
                }
                Publish(sample.Accepted ? "observing" : "sample_rejected");
            }
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            Publish("unsupported_or_failed");
        }
        finally
        {
            // GetService and ReleaseComObject occur on this same thread.
            try { reader?.Dispose(); }
            catch (Exception ex) { failure = $"clock_release: {ex.GetType().Name}: {ex.Message}"; }
            exited = true;
            Publish(failure is null ? "stopped" : "unsupported_or_failed");
        }
    }

    void Publish(string state)
    {
        int queued; lock (queueGate) queued = queue.Count;
        Volatile.Write(ref diagnostics, new(true, generation, state, samples, accepted, rejected, dropped,
            queued, Stopwatch.Frequency, lastAccepted, maximumQueryMs, lastRejection, failure, exited));
    }

    public void Dispose()
    {
        if (Stop(TimeSpan.FromSeconds(2))) stop.Dispose();
        // A blocked native query retains this observer and its service until
        // it exits. Its render client must be retained as well (WindowsAudio).
    }
}

// Kept independent of WASAPI so blocked-driver teardown can be checked without
// activating an endpoint or allowing a native call to retain freed COM state.
internal static class OutputClockShutdown
{
    internal static void StopObserverAndAudio(OutputClockObserver? observer, Action stopAudio,
        TimeSpan timeout, Action<TimeoutException> reportTimeout)
    {
        TimeoutException? clockTimeout = null;
        if (observer is not null)
        {
            if (observer.Stop(timeout)) observer.Dispose();
            else
            {
                clockTimeout = new TimeoutException(
                    $"Output clock observer did not exit within {timeout.TotalMilliseconds:F0} ms; native query/release remains pending.");
                reportTimeout(clockTimeout);
            }
        }
        // Stop rendering even after observation failed to exit. The owner must
        // keep the client alive until ReleaseAfterObserver's safe release.
        try { stopAudio(); }
        catch (Exception audioError)
        {
            if (clockTimeout is not null)
                throw new AggregateException("Audio stop and output clock shutdown both failed.", clockTimeout, audioError);
            throw;
        }
        if (clockTimeout is not null) throw clockTimeout;
    }

    internal static void ReleaseAfterObserver(OutputClockObserver? observer, Action release,
        Action<Exception> reportDeferredFailure)
    {
        if (observer is null || observer.WorkerExited) { release(); return; }
        ThreadPool.QueueUserWorkItem(_=>
        {
            try { observer.WaitForExit(); observer.Dispose(); release(); }
            catch (Exception ex) { reportDeferredFailure(ex); }
        });
    }
}
