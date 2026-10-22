namespace UsbAudioBridge;
/// <summary>Bounded PI clock tracker. Positive ppm consumes input faster. Updates use actual output frame duration.</summary>
public sealed class DriftController
{
    double filteredError, integral, ppm;
    public double Ppm => ppm;
    public DriftControllerState CaptureState() => new(filteredError, integral, ppm);
    public void RestoreState(DriftControllerState state)
    {
        if (!double.IsFinite(state.FilteredError) || !double.IsFinite(state.Integral) || !double.IsFinite(state.Ppm) ||
            Math.Abs(state.Integral) > 400 || Math.Abs(state.Ppm) > 500) throw new ArgumentOutOfRangeException(nameof(state));
        filteredError = state.FilteredError; integral = state.Integral; ppm = state.Ppm;
    }
    /// <summary>Presets the residual PI at a mode boundary. The current output is never reset.</summary>
    public void TrackOutput(double totalPpm, double feedforwardPpm = 0, double proportionalGain = 4)
    {
        if (!double.IsFinite(totalPpm) || !double.IsFinite(feedforwardPpm) || !double.IsFinite(proportionalGain))
            throw new ArgumentOutOfRangeException(nameof(totalPpm));
        ppm = Math.Clamp(totalPpm, -500, 500);
        integral = Math.Clamp(ppm - feedforwardPpm - filteredError * proportionalGain, -400, 400);
    }
    public void Reset() { filteredError = integral = ppm = 0; }
    public double Update(double bufferErrorFrames, int outputFrames, int sourceRate = 48000, int outputRate = 48000)
    {
        double dt = outputFrames / (double)outputRate;
        bufferErrorFrames *= 48000d / sourceRate;
        if (dt <= 0 || dt > .2) return ppm;
        filteredError += (bufferErrorFrames - filteredError) * (1 - Math.Exp(-dt));
        double proposedIntegral = Math.Clamp(integral + filteredError * dt * .25, -400, 400);
        double proposedOutput = filteredError * 4 + proposedIntegral;
        // A startup buffer offset can saturate the proportional term for many seconds.
        // Integrating further into that saturation used to drain the buffer below its
        // callback/lookahead floor even when the source and output clocks were identical.
        if (proposedOutput is >= -500 and <= 500 ||
            proposedOutput > 500 && filteredError < 0 ||
            proposedOutput < -500 && filteredError > 0)
            integral = proposedIntegral;
        double target = Math.Clamp(filteredError * 4 + integral, -500, 500);
        ppm += Math.Clamp(target - ppm, -100 * dt, 100 * dt);
        return ppm;
    }

    /// <summary>Experimental residual PI with one total saturation and one total slew limiter.</summary>
    public double UpdateWithFeedforward(double bufferErrorFrames, int outputFrames, double feedforwardPpm,
        int sourceRate = 48000, int outputRate = 48000)
    {
        double dt = outputFrames / (double)outputRate;
        if (dt <= 0 || dt > .2 || !double.IsFinite(feedforwardPpm)) return ppm;
        bufferErrorFrames *= 48000d / sourceRate;
        filteredError += (bufferErrorFrames - filteredError) * (1 - Math.Exp(-dt));
        const double proportionalGain = 1, integralGain = .1;
        double proposedIntegral = Math.Clamp(integral + filteredError * dt * integralGain, -400, 400);
        double proposedOutput = feedforwardPpm + filteredError * proportionalGain + proposedIntegral;
        if (proposedOutput is >= -500 and <= 500 || proposedOutput > 500 && filteredError < 0 || proposedOutput < -500 && filteredError > 0)
            integral = proposedIntegral;
        double target = Math.Clamp(feedforwardPpm + filteredError * proportionalGain + integral, -500, 500);
        ppm += Math.Clamp(target - ppm, -100 * dt, 100 * dt);
        return ppm;
    }
}

public readonly record struct DriftControllerState(double FilteredError, double Integral, double Ppm);
