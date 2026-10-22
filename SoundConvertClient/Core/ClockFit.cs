namespace UsbAudioBridge;

/// <summary>Pre-registered experiment limits, deliberately shared by all three observers.</summary>
public sealed record ClockObserverSettings
{
    public int Capacity { get; init; } = 1024;
    public double WindowSeconds { get; init; } = 60;
    public int MinimumSamples { get; init; } = 32;
    public double MinimumSpanSeconds { get; init; } = 30;
    public double MaximumResidualP95Us { get; init; } = 500;
    public double MaximumWindowDifferencePpm { get; init; } = 20;
    public double HoldoverAfterSeconds { get; init; } = 3;
    public double FallbackAfterSeconds { get; init; } = 8;
    public double MaximumExchangeSeconds { get; init; } = .250;
    public double NegativeRttToleranceUs { get; init; } = 50;
    public double LowRttAllowanceUs { get; init; } = 500;
    internal void Validate()
    {
        if (Capacity is < 32 or > 4096 || MinimumSamples < 32 || MinimumSamples > Capacity ||
            WindowSeconds < 30 || MinimumSpanSeconds < 30 || MinimumSpanSeconds > WindowSeconds ||
            MaximumResidualP95Us is <= 0 or > 500 || MaximumWindowDifferencePpm is <= 0 or > 20 ||
            HoldoverAfterSeconds != 3 || FallbackAfterSeconds != 8 || MaximumExchangeSeconds is <= 0 or > .250 ||
            NegativeRttToleranceUs < 0 || LowRttAllowanceUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(ClockObserverSettings));
    }
}

internal readonly record struct ClockFitPoint(double X, double Y, double Rtt = 0);
internal readonly record struct ClockFitResult(int Samples, double SpanSeconds, double Slope, double Intercept,
    double ResidualP95Us, double WindowDifferencePpm, bool Qualified, int LowRttExcluded);

/// <summary>All coordinates are small differences from integer origins. No absolute uint64 is cast before subtraction.</summary>
internal sealed class ClockFitWindow
{
    readonly ClockFitPoint[] points;
    readonly ClockObserverSettings settings;
    int count, next;
    public ClockFitWindow(ClockObserverSettings settings) { this.settings = settings; points = new ClockFitPoint[settings.Capacity]; }
    public void Clear() { count = next = 0; }
    public void Add(double x, double y, double rtt = 0) { points[next] = new(x, y, rtt); next = (next + 1) % points.Length; count = Math.Min(count + 1, points.Length); }
    public ClockFitResult Fit(double nominalSlope, bool filterRtt)
    {
        if (count == 0) return default;
        double end = points[(next + points.Length - 1) % points.Length].X;
        var recent = new List<ClockFitPoint>(count);
        for (int i = 0; i < count; i++) if (end - points[i].X <= settings.WindowSeconds) recent.Add(points[i]);
        double minimumRtt = filterRtt ? recent.Min(p => p.Rtt) : 0;
        var selected = filterRtt ? recent.Where(p => p.Rtt <= minimumRtt + settings.LowRttAllowanceUs / 1_000_000).ToArray() : recent.ToArray();
        if (selected.Length < 2) return new(selected.Length, 0, 0, 0, 0, 0, false, recent.Count - selected.Length);
        (double slope, double intercept) = Linear(selected);
        double span = selected.Max(p => p.X) - selected.Min(p => p.X);
        var residuals = selected.Select(p => Math.Abs(p.Y - (slope * p.X + intercept)) / nominalSlope * 1_000_000).Order().ToArray();
        double p95 = residuals[Math.Min(residuals.Length - 1, (int)Math.Ceiling(residuals.Length * .95) - 1)];
        double consistency = 0;
        bool windows = true;
        foreach (double seconds in new[] { 10d, 30d, 60d })
        {
            var subset = selected.Where(p => end - p.X <= seconds).ToArray();
            double requiredSpan = Math.Min(seconds, settings.MinimumSpanSeconds) * .8;
            if (subset.Length < 3 || subset.Max(p => p.X) - subset.Min(p => p.X) < requiredSpan) { windows = false; continue; }
            double localSlope = Linear(subset).Slope;
            consistency = Math.Max(consistency, Math.Abs(localSlope / slope - 1) * 1_000_000);
        }
        bool qualified = selected.Length >= settings.MinimumSamples && span >= settings.MinimumSpanSeconds && windows &&
            double.IsFinite(slope) && slope / nominalSlope is >= .99 and <= 1.01 && p95 <= settings.MaximumResidualP95Us && consistency <= settings.MaximumWindowDifferencePpm;
        return new(selected.Length, span, slope, intercept, p95, consistency, qualified, recent.Count - selected.Length);
    }
    static (double Slope, double Intercept) Linear(ClockFitPoint[] values)
    {
        double meanX = values.Average(p => p.X), meanY = values.Average(p => p.Y), covariance = 0, variance = 0;
        foreach (var point in values) { double x = point.X - meanX; covariance += x * (point.Y - meanY); variance += x * x; }
        double slope = variance > 1e-20 ? covariance / variance : double.NaN;
        return (slope, meanY - slope * meanX);
    }
}

internal static class ClockMath
{
    public static double Difference(ulong value, ulong origin) => value >= origin ? (double)(value - origin) : -(double)(origin - value);
    public static ClockObserverState AgeState(bool initialized, bool qualified, bool fallback, double age, ClockObserverSettings settings)
    {
        if (fallback || age >= settings.FallbackAfterSeconds) return ClockObserverState.Fallback;
        if (qualified && age >= settings.HoldoverAfterSeconds) return ClockObserverState.Holdover;
        return initialized && qualified ? ClockObserverState.Qualified : ClockObserverState.Acquiring;
    }
}
