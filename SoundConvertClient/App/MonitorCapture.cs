using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Buffers.Binary;
using System.Diagnostics;
using UsbAudioBridge;
namespace SoundConvertClient;

/// <summary>Measures the selected output mix only. No captured audio is persisted.</summary>
public sealed class MonitorCapture : IDisposable
{
    readonly MMDevice device;
    readonly WasapiLoopbackCapture capture;
    readonly object gate = new();
    long frames, nonzero, invalidSamples, lastCallback, largeSteps;
    double leftSquares, rightSquares, leftPeak, rightPeak;
    double l440s, l440c, l880s, l880c, r440s, r440c, r880s, r880c;
    double l440power, l880power, r440power, r880power;
    double previousLeft, previousRight, previousDl, previousDr, maximumStep, maximumSecondDifference, maximumCallbackGapMs;
    int windowFrames, windows;
    string? fault;
    public MonitorCapture(string endpoint)
    {
        using var enumerator = new MMDeviceEnumerator(); device = enumerator.GetDevice(endpoint);
        capture = new WasapiLoopbackCapture(device); capture.DataAvailable += Received;
        capture.RecordingStopped += (_, args) => { if (args.Exception is not null) lock (gate) fault = args.Exception.Message; };
    }
    public void Start() => capture.StartRecording();
    public void Stop() => capture.StopRecording();
    void Received(object? sender, WaveInEventArgs args)
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastCallback != 0) maximumCallbackGapMs = Math.Max(maximumCallbackGapMs, Stopwatch.GetElapsedTime(lastCallback, now).TotalMilliseconds);
            lastCallback = now;
            var format = capture.WaveFormat; int bytes = format.BitsPerSample / 8, channels = format.Channels;
            bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (bytes is not (2 or 3 or 4) || floating && bytes != 4) { fault = "Unsupported loopback sample encoding: " + format; return; }
            double Sample(int offset)
            {
                var sample = args.Buffer.AsSpan(offset, bytes);
                return floating ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)) :
                    bytes == 4 ? BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d : PcmCodec.ReadFloat(sample, bytes * 8);
            }
            for (int offset = 0; offset + format.BlockAlign <= args.BytesRecorded; offset += format.BlockAlign)
            {
                double left = Sample(offset), right = channels > 1 ? Sample(offset + bytes) : left;
                if (!double.IsFinite(left) || !double.IsFinite(right)) { invalidSamples++; continue; }
                if (frames > 0)
                {
                    double dl = left - previousLeft, dr = right - previousRight, step = Math.Max(Math.Abs(dl), Math.Abs(dr));
                    maximumStep = Math.Max(maximumStep, step);
                    maximumSecondDifference = Math.Max(maximumSecondDifference, Math.Max(Math.Abs(dl - previousDl), Math.Abs(dr - previousDr)));
                    if (step > .25) largeSteps++;
                    previousDl = dl; previousDr = dr;
                }
                previousLeft = left; previousRight = right; frames++;
                if (Math.Abs(left) > 1e-6) nonzero++; if (Math.Abs(right) > 1e-6) nonzero++;
                leftSquares += left * left; rightSquares += right * right;
                leftPeak = Math.Max(leftPeak, Math.Abs(left)); rightPeak = Math.Max(rightPeak, Math.Abs(right));
                double t = 2 * Math.PI * windowFrames / format.SampleRate;
                double s440 = Math.Sin(440 * t), c440 = Math.Cos(440 * t), s880 = Math.Sin(880 * t), c880 = Math.Cos(880 * t);
                l440s += left * s440; l440c += left * c440; l880s += left * s880; l880c += left * c880;
                r440s += right * s440; r440c += right * c440; r880s += right * s880; r880c += right * c880;
                if (++windowFrames >= format.SampleRate / 10)
                {
                    double scale = 2d / (windowFrames * windowFrames);
                    l440power += (l440s * l440s + l440c * l440c) * scale; l880power += (l880s * l880s + l880c * l880c) * scale;
                    r440power += (r440s * r440s + r440c * r440c) * scale; r880power += (r880s * r880s + r880c * r880c) * scale;
                    windows++; windowFrames = 0; l440s = l440c = l880s = l880c = r440s = r440c = r880s = r880c = 0;
                }
            }
        }
    }
    public bool ToneQualityAccepted { get { lock (gate) return fault is null && invalidSamples == 0 && frames > 0 && windows > 0 && leftSquares > 0 && rightSquares > 0 &&
        l440power * (capture.WaveFormat.SampleRate / 10) / leftSquares >= .95 && r880power * (capture.WaveFormat.SampleRate / 10) / rightSquares >= .95; } }
    public long NonzeroSamples { get { lock (gate) return nonzero; } }
    public object Statistics()
    {
        lock (gate) return new { endpoint = device.FriendlyName, tone_quality_accepted = ToneQualityAccepted,
            left_target_energy_fraction = leftSquares > 0 ? l440power * (capture.WaveFormat.SampleRate / 10) / leftSquares : 0,
            right_target_energy_fraction = rightSquares > 0 ? r880power * (capture.WaveFormat.SampleRate / 10) / rightSquares : 0,
            format = capture.WaveFormat.ToString(), sample_rate = capture.WaveFormat.SampleRate, captured_frames = frames, nonzero_samples = nonzero,
            invalid_samples = invalidSamples, maximum_sample_step = maximumStep, maximum_second_difference = maximumSecondDifference,
            sample_steps_over_quarter_scale = largeSteps, maximum_capture_callback_gap_ms = maximumCallbackGapMs,
            left_rms = frames > 0 ? Math.Sqrt(leftSquares / frames) : 0, right_rms = frames > 0 ? Math.Sqrt(rightSquares / frames) : 0,
            left_peak = leftPeak, right_peak = rightPeak, left_440_rms = frames > 0 ? Math.Sqrt(l440power * (capture.WaveFormat.SampleRate / 10) / frames) : 0,
            left_880_rms = frames > 0 ? Math.Sqrt(l880power * (capture.WaveFormat.SampleRate / 10) / frames) : 0,
            right_440_rms = frames > 0 ? Math.Sqrt(r440power * (capture.WaveFormat.SampleRate / 10) / frames) : 0,
            right_880_rms = frames > 0 ? Math.Sqrt(r880power * (capture.WaveFormat.SampleRate / 10) / frames) : 0, fault };
    }
    public void Dispose() { capture.StopRecording(); capture.Dispose(); device.Dispose(); }
}
