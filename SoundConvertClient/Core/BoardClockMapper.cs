namespace UsbAudioBridge;

public sealed record BoardClockSnapshot(ClockSessionIdentity Identity, ulong MappingId, ClockObserverState State,
    long Accepted, long Rejected, string LastRejectReason, int Samples, int LowRttExcluded, double SpanSeconds,
    double PcUsPerBoardUs, double InterceptSeconds, ulong BoardOriginUs, ulong PcOriginTicks, ulong PcFrequency,
    double ResidualP95Us, double WindowDifferencePpm, double AgeSeconds, double RttP50Us, double RttP95Us, double RttMaximumUs)
{
    public double PcSecondsSinceOrigin(ulong boardUs) => ClockMath.Difference(boardUs, BoardOriginUs) / 1_000_000 * PcUsPerBoardUs + InterceptSeconds;
    public bool Qualified => State == ClockObserverState.Qualified;
    public string AbsoluteOffsetAccuracy => "unknown: path asymmetry is not bounded by the four software timestamps";
}

/// <summary>Software four-timestamp mapping. A small residual does not prove an absolute one-way delay.</summary>
public sealed class BoardClockMapper
{
    readonly object gate = new();
    readonly ClockObserverSettings settings;
    readonly ClockFitWindow fit;
    readonly double[] rtts;
    ClockSessionIdentity identity;
    ulong mappingId, boardOrigin, pcOrigin, frequency, lastRequest, lastP1, lastB2, lastValid;
    int rttCount, rttNext;
    long accepted, rejected;
    bool initialized, forcedFallback;
    string lastReject = "";
    ClockFitResult result;
    public BoardClockMapper(ClockSessionIdentity identity, ClockObserverSettings? settings = null)
    { this.settings = settings ?? new(); this.settings.Validate(); fit = new(this.settings); rtts = new double[this.settings.Capacity]; Reset(identity); }
    public void Reset(ClockSessionIdentity newIdentity)
    {
        lock (gate) { identity = newIdentity; mappingId++; boardOrigin = pcOrigin = frequency = lastRequest = lastP1 = lastB2 = lastValid = 0;
            rttCount = rttNext = 0; accepted = rejected = 0; initialized = forcedFallback = false; lastReject = ""; result = default; fit.Clear(); }
    }
    public bool TryObserve(ClockExchange exchange)
    {
        lock (gate)
        {
            var m = exchange.Message;
            bool Reject(string reason, bool fatal = false)
            { rejected++; lastReject = reason; if (fatal) { forcedFallback = true; fit.Clear(); result = default; initialized = false; mappingId++; } return false; }
            if (m.Kind != ClockObservationKind.Reply) return Reject("kind");
            if (!identity.IsValid || m.Identity != identity) return Reject("identity");
            if (m.RequestId == 0 || lastRequest != 0 && m.RequestId <= lastRequest) return Reject("old_or_duplicate_request");
            if (exchange.PcFrequency == 0 || initialized && exchange.PcFrequency != frequency) return Reject("pc_frequency_changed", true);
            if (exchange.P4Ticks < exchange.P1Ticks || initialized && exchange.P1Ticks <= lastP1 || m.B3Us < m.B2Us || initialized && m.B2Us <= lastB2)
                return Reject("clock_reversed", true);
            double roundTrip = (exchange.P4Ticks - exchange.P1Ticks) / (double)exchange.PcFrequency;
            if (roundTrip > settings.MaximumExchangeSeconds) return Reject("exchange_timeout");
            double slope = result.Slope > 0 ? result.Slope : 1;
            double residualRtt = roundTrip - (m.B3Us - m.B2Us) / 1_000_000d * slope;
            if (residualRtt < -settings.NegativeRttToleranceUs / 1_000_000) return Reject("negative_residual_rtt");
            if (initialized && ClockMath.Difference(exchange.P1Ticks, lastValid) / frequency >= settings.FallbackAfterSeconds)
            { fit.Clear(); result = default; initialized = false; mappingId++; }
            if (!initialized) { boardOrigin = m.B2Us; pcOrigin = exchange.P1Ticks; frequency = exchange.PcFrequency; initialized = true; }
            double boardMid = (m.B2Us - boardOrigin) / 1_000_000d + (m.B3Us - m.B2Us) / 2_000_000d;
            double pcMid = (exchange.P1Ticks - pcOrigin) / (double)frequency + (exchange.P4Ticks - exchange.P1Ticks) / (2d * frequency);
            fit.Add(boardMid, pcMid, Math.Max(0, residualRtt)); result = fit.Fit(1, true);
            rtts[rttNext] = Math.Max(0, residualRtt) * 1_000_000; rttNext = (rttNext + 1) % rtts.Length; rttCount = Math.Min(rttCount + 1, rtts.Length);
            lastRequest = m.RequestId; lastP1 = exchange.P1Ticks; lastB2 = m.B2Us; lastValid = exchange.P4Ticks;
            accepted++; forcedFallback = false; return true;
        }
    }
    public BoardClockSnapshot Snapshot(ulong nowTicks)
    {
        lock (gate)
        {
            double age = initialized && frequency > 0 ? Math.Max(0, ClockMath.Difference(nowTicks, lastValid) / frequency) : 0;
            var values = rtts.Take(rttCount).Order().ToArray();
            double Q(double q) => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)(values.Length * q))];
            var state = !identity.IsValid ? ClockObserverState.Disabled : ClockMath.AgeState(initialized, result.Qualified, forcedFallback, age, settings);
            return new(identity, mappingId, state, accepted, rejected, lastReject, result.Samples, result.LowRttExcluded, result.SpanSeconds,
                result.Slope, result.Intercept, boardOrigin, pcOrigin, frequency, result.ResidualP95Us, result.WindowDifferencePpm, age, Q(.5), Q(.95), Q(1));
        }
    }
}
