namespace UsbAudioBridge;

public sealed record SourceSampleClockSnapshot(ClockSessionIdentity Identity, ClockObserverState State, long Accepted,
    long Rejected, string LastRejectReason, uint SampleRate, uint Generation, int Samples, double SpanSeconds,
    double SamplesPerBoardSecond, double ResidualP95Us, double WindowDifferencePpm, double AgeSeconds)
{
    public bool Qualified => State == ClockObserverState.Qualified;
}

/// <summary>Uses unique USB extraction anchors, never the PC packet arrival time, as the source clock ordinate.</summary>
public sealed class SourceSampleClockTracker
{
    readonly object gate = new();
    readonly ClockObserverSettings settings;
    readonly ClockFitWindow fit;
    ClockSessionIdentity identity;
    ulong sourceOrigin, boardOrigin, lastSource, lastBoard, lastPc, frequency;
    uint rate, generation;
    bool initialized, forcedFallback;
    long accepted, rejected;
    string lastReject = "";
    ClockFitResult result;
    public SourceSampleClockTracker(ClockSessionIdentity identity, ClockObserverSettings? settings = null)
    { this.settings = settings ?? new(); this.settings.Validate(); fit = new(this.settings); Reset(identity); }
    public void Reset(ClockSessionIdentity newIdentity)
    { lock (gate) { identity = newIdentity; sourceOrigin = boardOrigin = lastSource = lastBoard = lastPc = frequency = 0;
        rate = generation = 0; initialized = forcedFallback = false; accepted = rejected = 0; lastReject = ""; result = default; fit.Clear(); } }
    public bool TryObserve(ClockObservationMessage message, ulong pcReceivedTicks, ulong pcFrequency, bool sourceHealthy = true)
    {
        lock (gate)
        {
            bool Reject(string reason, bool invalidate = false)
            {
                rejected++; lastReject = reason;
                if (invalidate) { fit.Clear(); result = default; initialized = false; forcedFallback = true; }
                return false;
            }
            if (message.Identity != identity || !identity.IsValid) return Reject("identity");
            if (!sourceHealthy) return Reject("source_unhealthy", true);
            if (message.Kind != ClockObservationKind.Reply || !message.AnchorValid) return Reject("anchor_invalid");
            if (message.AnchorGeneration != identity.Epoch || message.SampleRate is < 8000 or > 384000 || message.AnchorFrames == 0)
                return Reject("anchor_metadata", true);
            if (message.AnchorExtractUs > message.B3Us || message.B3Us - message.AnchorExtractUs > 4000) return Reject("anchor_stale");
            if (pcFrequency == 0 || initialized && frequency != pcFrequency) return Reject("pc_frequency_changed", true);
            if (initialized && (message.SampleRate != rate || message.AnchorGeneration != generation)) return Reject("format_or_generation_changed", true);
            if (initialized && message.AnchorSampleIndex == lastSource && message.AnchorExtractUs == lastBoard) return Reject("duplicate_anchor");
            if (initialized && (message.AnchorSampleIndex <= lastSource || message.AnchorExtractUs <= lastBoard || pcReceivedTicks <= lastPc))
                return Reject("anchor_or_pc_reversed", true);
            if (initialized && (pcReceivedTicks - lastPc) / (double)frequency >= settings.FallbackAfterSeconds)
            { fit.Clear(); result = default; initialized = false; }
            if (!initialized)
            { sourceOrigin = message.AnchorSampleIndex; boardOrigin = message.AnchorExtractUs; rate = message.SampleRate;
                generation = message.AnchorGeneration; frequency = pcFrequency; initialized = true; }
            fit.Add((message.AnchorExtractUs - boardOrigin) / 1_000_000d, (double)(message.AnchorSampleIndex - sourceOrigin));
            result = fit.Fit(rate, false); lastSource = message.AnchorSampleIndex; lastBoard = message.AnchorExtractUs;
            lastPc = pcReceivedTicks; accepted++; forcedFallback = false; return true;
        }
    }
    public SourceSampleClockSnapshot Snapshot(ulong nowTicks)
    {
        lock (gate)
        {
            double age = initialized && frequency > 0 ? Math.Max(0, ClockMath.Difference(nowTicks, lastPc) / frequency) : 0;
            var state = !identity.IsValid ? ClockObserverState.Disabled : ClockMath.AgeState(initialized, result.Qualified, forcedFallback, age, settings);
            return new(identity, state, accepted, rejected, lastReject, rate, generation, result.Samples, result.SpanSeconds,
                result.Slope, result.ResidualP95Us, result.WindowDifferencePpm, age);
        }
    }
}
