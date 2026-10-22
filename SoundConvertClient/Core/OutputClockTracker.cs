namespace UsbAudioBridge;

/// <summary>IAudioClock position units are Frequency units/s. Its paired QPC is already in 100 ns units.</summary>
public readonly record struct OutputClockSample(ulong OutputGeneration, ulong Position, ulong Frequency,
    ulong Qpc100ns, int HResult, bool Running);

public sealed record OutputClockSnapshot(ulong OutputGeneration, ClockObserverState State, long Accepted, long Rejected,
    string LastRejectReason, ulong DeviceFrequency, int Samples, double SpanSeconds, double DeviceSecondsPerPcSecond,
    double ResidualP95Us, double WindowDifferencePpm, double AgeSeconds)
{
    public bool Qualified => State == ClockObserverState.Qualified;
}

/// <summary>Low frequency, non-render-thread tracker for the actual rendering client's raw clock pair.</summary>
public sealed class OutputClockTracker
{
    readonly object gate = new();
    readonly ClockObserverSettings settings;
    readonly ClockFitWindow fit;
    ulong generation, frequency, positionOrigin, qpcOrigin, lastPosition, lastQpc;
    bool initialized, forcedFallback;
    long accepted, rejected;
    string lastReject = "";
    ClockFitResult result;
    public OutputClockTracker(ClockObserverSettings? settings = null)
    { this.settings = settings ?? new(); this.settings.Validate(); fit = new(this.settings); }
    public void Reset(ulong outputGeneration = 0)
    { lock (gate) { generation = outputGeneration; frequency = positionOrigin = qpcOrigin = lastPosition = lastQpc = 0;
        initialized = forcedFallback = false; accepted = rejected = 0; lastReject = ""; result = default; fit.Clear(); } }
    public bool TryObserve(OutputClockSample sample)
    {
        lock (gate)
        {
            bool Reject(string reason, bool invalidate = false)
            { rejected++; lastReject = reason; if (invalidate) { fit.Clear(); result = default; initialized = false; forcedFallback = true; } return false; }
            if (sample.OutputGeneration == 0) return Reject("output_generation_zero", true);
            if (generation != sample.OutputGeneration)
            { generation = sample.OutputGeneration; initialized = forcedFallback = false; result = default; fit.Clear(); }
            if (!sample.Running) return Reject("output_stopped", true);
            if (sample.HResult != 0) return Reject(sample.HResult == 1 ? "S_FALSE_low_quality" : "clock_hresult", sample.HResult < 0);
            if (sample.Frequency == 0 || sample.Qpc100ns == 0) return Reject("clock_units_invalid", true);
            if (sample.Position == 0) return Reject("startup_zero_position");
            if (initialized && sample.Frequency != frequency) return Reject("device_frequency_changed", true);
            if (initialized && (sample.Position <= lastPosition || sample.Qpc100ns <= lastQpc)) return Reject("position_or_qpc_not_advancing", true);
            if (initialized && sample.Qpc100ns - lastQpc < 10000) return Reject("output_interval_too_short");
            if (initialized && (sample.Qpc100ns - lastQpc) / 10_000_000d >= settings.FallbackAfterSeconds)
            { fit.Clear(); result = default; initialized = false; }
            if (!initialized)
            { frequency = sample.Frequency; positionOrigin = sample.Position; qpcOrigin = sample.Qpc100ns; initialized = true; }
            double x = (sample.Qpc100ns - qpcOrigin) / 10_000_000d;
            double y = (sample.Position - positionOrigin) / (double)frequency;
            // A wrong position unit or discontinuous driver pair must never become a huge feedforward.
            if (x > .1 && (y / x is < .99 or > 1.01)) return Reject("clock_ratio_or_units_invalid", true);
            fit.Add(x, y); result = fit.Fit(1, false); lastPosition = sample.Position; lastQpc = sample.Qpc100ns;
            accepted++; forcedFallback = false; return true;
        }
    }
    public OutputClockSnapshot Snapshot(ulong nowQpc100ns)
    {
        lock (gate)
        {
            double age = initialized ? Math.Max(0, ClockMath.Difference(nowQpc100ns, lastQpc) / 10_000_000) : 0;
            var state = generation == 0 ? ClockObserverState.Disabled : ClockMath.AgeState(initialized, result.Qualified, forcedFallback, age, settings);
            return new(generation, state, accepted, rejected, lastReject, frequency, result.Samples, result.SpanSeconds,
                result.Slope, result.ResidualP95Us, result.WindowDifferencePpm, age);
        }
    }
}
