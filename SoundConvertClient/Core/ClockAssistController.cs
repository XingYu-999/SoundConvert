namespace UsbAudioBridge;

public sealed record ClockAssistSnapshot(ClockObservationMode Mode, bool Qualified, bool Active, double RequestedFeedforwardPpm,
    double AppliedFeedforwardPpm, double HandoffBiasPpm, double ResidualIntegralPpm, double TotalPpm,
    long FallbackCount, string Reason, bool InputLimited);

/// <summary>Optional controller around the original PI; Legacy and ObserveOnly call the identical old Update method.</summary>
public sealed class ClockAssistController
{
    readonly DriftController drift;
    ClockObservationMode mode;
    bool qualified, active;
    double requested, rawRequested, applied, handoffBias;
    long fallbacks;
    string reason = "legacy_default";
    public ClockAssistController(DriftController drift) { this.drift = drift; }
    public void SetInput(ClockObservationMode newMode, bool allQualified, double feedforwardPpm, string? fallbackReason = null)
    {
        mode = Enum.IsDefined(newMode) ? newMode : ClockObservationMode.Legacy;
        qualified = allQualified && double.IsFinite(feedforwardPpm);
        rawRequested = double.IsFinite(feedforwardPpm) ? feedforwardPpm : 0;
        requested = double.IsFinite(feedforwardPpm) ? Math.Clamp(feedforwardPpm, -500, 500) : 0;
        reason = fallbackReason ?? (Math.Abs(rawRequested) > 500 ? "feedforward_limited_to_500ppm" : qualified ? "three_observers_qualified" : "observer_not_qualified");
    }
    public double Update(double errorFrames, int outputFrames, int sourceRate = 48000, int outputRate = 48000, bool healthy = true)
    {
        bool desired = mode == ClockObservationMode.ClockAssistExperimental && qualified && healthy;
        if (desired && !active)
        {
            // A preload accounts for the entire new FF before its visible ramp begins.
            // Bias and FF exchange equal amounts; the original PI output remains continuous.
            applied = 0; handoffBias = requested; drift.TrackOutput(drift.Ppm, requested, 1); active = true;
        }
        else if (!desired && active)
        {
            drift.TrackOutput(drift.Ppm); active = false; applied = handoffBias = 0; fallbacks++;
            if (!healthy) reason = "audio_unhealthy";
        }
        if (!active) return drift.Update(errorFrames, outputFrames, sourceRate, outputRate);
        double dt = outputFrames / (double)outputRate;
        if (dt is <= 0 or > .2) return drift.Ppm;
        double delta = Math.Clamp(requested - applied, -100 * dt, 100 * dt);
        applied += delta;
        // Only the initial transfer bias is faded. Later estimate changes reach the rate controller normally.
        double release = Math.CopySign(Math.Min(Math.Abs(handoffBias), Math.Abs(delta)), handoffBias);
        handoffBias -= release;
        return drift.UpdateWithFeedforward(errorFrames, outputFrames, applied + handoffBias, sourceRate, outputRate);
    }
    public void Reset() { active = false; applied = handoffBias = 0; }
    public void Invalidate(string fallbackReason) { qualified = false; reason = fallbackReason; }
    public ClockAssistSnapshot Snapshot() => new(mode, qualified, active, rawRequested, applied, handoffBias,
        drift.CaptureState().Integral, drift.Ppm, fallbacks, reason, Math.Abs(rawRequested) > 500);

    public static bool TryCalculateFeedforward(BoardClockSnapshot board, SourceSampleClockSnapshot source,
        OutputClockSnapshot output, int sourceNominalRate, int outputNominalRate, out double feedforwardPpm)
    {
        feedforwardPpm = 0;
        if (!board.Qualified || !source.Qualified || !output.Qualified || board.Identity != source.Identity ||
            sourceNominalRate <= 0 || outputNominalRate <= 0 || source.SampleRate != sourceNominalRate ||
            board.PcUsPerBoardUs <= 0 || source.SamplesPerBoardSecond <= 0 || output.DeviceSecondsPerPcSecond <= 0) return false;
        double step = (source.SamplesPerBoardSecond / board.PcUsPerBoardUs) / (output.DeviceSecondsPerPcSecond * outputNominalRate);
        feedforwardPpm = (step / (sourceNominalRate / (double)outputNominalRate) - 1) * 1_000_000;
        return double.IsFinite(feedforwardPpm);
    }
}
