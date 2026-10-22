namespace UsbAudioBridge;

/// <summary>Bounded source-index buffer, ratio-aware sinc ASRC and explicit short loss concealment.</summary>
public sealed class AudioBuffer
{
    // 192 kHz * 100 ms + worst supported FIR history/lookahead and a packet must fit physically.
    const int Capacity = 32768;
    readonly object gate = new();
    public AudioLevelMeter LevelMeter{get;}=new();uint meterGeneration;
    readonly TimeProvider clock;
    readonly float[] samples = new float[Capacity * 2];
    readonly ulong[] tags = new ulong[Capacity];
    readonly bool[] valid = new bool[Capacity], concealed = new bool[Capacity];
    readonly DriftController drift = new();
    readonly ClockAssistController clockAssist;
    readonly ArrivalTiming arrival = new();
    readonly SourceClockObserver sourceClock = new();
    readonly AudioTiming readTiming=new(),addTiming=new(),readWait=new(),addWait=new();
    readonly float[] workLeft=new float[Capacity],workRight=new float[Capacity];
    readonly int[] workIncomplete=new int[Capacity+1];
    readonly List<AudioUnderrunEvent> underrunEvents = new();
    AudioFormat sourceFormat = AudioFormat.Pcm16;
    SincFilter.Bank filter;
    bool initialized, playing, overflowSinceLastRead;
    double position, ppm, gainCurrent,driftReferenceFrames;
    double lastLeft, lastRight, previousDeltaLeft, previousDeltaRight, tailLeft, tailRight;
    int tailRemaining, fadeInRemaining;
    long currentBurstFrames, lastPacket, firstPacketTimestamp, firstPlaybackTimestamp;
    long maximumReadTicks, readDeadlineMisses;
    ulong origin, maxEnd, segmentStart, timeline;
    int targetFrames = 192;
    float monitorGain = 1;
    bool monitorMute;
    int MaximumFrames => MaximumBufferMs * sourceFormat.SampleRate / 1000;
    double FramesPerMs => sourceFormat.SampleRate / 1000d;
    double NominalStep => sourceFormat.SampleRate / (double)OutputSampleRate;
    int FadeFrames => Math.Max(1, OutputSampleRate / 500);

    public AudioBuffer(int maximumBufferMs = 20, TimeProvider? timeProvider = null, int outputSampleRate = 48000,ResamplerQuality quality=ResamplerQuality.Balanced)
    {
        if (maximumBufferMs is < 4 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumBufferMs));
        if (outputSampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        clock = timeProvider ?? TimeProvider.System; MaximumBufferMs = maximumBufferMs; OutputSampleRate = outputSampleRate;Quality=quality;
        clockAssist = new(drift);
        SincFilter.Prewarm(outputSampleRate,quality);
        filter = SincFilter.ForRates(sourceFormat.SampleRate, OutputSampleRate,Quality);
    }
    public AudioFormat SourceFormat { get { lock (gate) return sourceFormat; } }
    public int OutputSampleRate { get; }
    public int MaximumBufferMs { get; }
    public ResamplerQuality Quality{get;}
    public int FilterTaps=>filter.Taps;
    public double LookAheadMs=>filter.LookAhead/FramesPerMs;
    public double DriftReferenceMs=>driftReferenceFrames/FramesPerMs;
    public double ArrivalJitterMs=>arrival.JitterMs;
    public double ShadowClockPpm=>sourceClock.Ppm;
    public bool ShadowClockConfident=>sourceClock.Confident;
    public object TimingSnapshot()=>new{read=readTiming.Snapshot(),add=addTiming.Snapshot(),read_lock_wait=readWait.Snapshot(),add_lock_wait=addWait.Snapshot(),window="latest 2048 observations; read starts after one rendered second"};
    public int TargetFrames => targetFrames;
    public double ConservativePrefillMs
    {
        get
        {
            if(MaximumBufferMs<40)return 0;
            // An 80 ms cap previously prefilled only ~41 ms, insufficient for
            // the measured 42.8 ms delivery pause. Use the selected margin,
            // retaining room for a whole Windows refill (observed 22 ms),
            // FIR lookahead and a packet. This never changes the hard cap.
            double refillMs=LargestReadFrames*1000d/OutputSampleRate;
            double headroomMs=Math.Max(24,refillMs+LookAheadMs+1);
            return Math.Min(MaximumBufferMs,Math.Max(MaximumBufferMs/2d+1.5,MaximumBufferMs-headroomMs));
        }
    }
    public int LargestReadFrames { get; private set; }
    public int CurrentReadFrames { get; private set; }
    public long OversizedReadCallbacks { get; private set; }
    public bool OutputDemandTooLarge { get; private set; }
    public double MaximumArrivalGapMs { get; private set; }
    public long MaximumArrivalBurstFrames { get; private set; }
    public double RequiredPrefillMs { get; private set; }
    public double RecommendedBufferMs { get; private set; }
    public bool BufferConfigurationInsufficient => RequiredPrefillMs > MaximumBufferMs || OutputDemandTooLarge;
    public bool CapacityBelowRecommendation => RecommendedBufferMs > MaximumBufferMs;
    public float Gain { get => Volatile.Read(ref monitorGain); set => Volatile.Write(ref monitorGain, value); }
    public bool Mute { get => Volatile.Read(ref monitorMute); set => Volatile.Write(ref monitorMute, value); }
    public long MissingFrames, Duplicates, Late, Overflows, Resyncs;
    public long Initializations, OutputCallbacks, OutputUnderruns, OutputSilenceFrames;
    public long BufferingCallbacks, RebufferCount, InterpolationIncompleteFrames;
    public long OutputFramesRequested, RenderedFrames, StartupUnderruns, SteadyUnderruns, OverflowDroppedFrames;
    public long ConcealedSourceFrames, ConcealedOutputFrames, BoundaryFadeFrames, FormatChanges, ConflictingOverlapFrames;
    public long RepairedBeforePlaybackFrames;
    public long PrefillTrimmedSourceFrames;
    public long LateSourceFrames,PermanentGapSourceFrames,CommittedSourceFrames;
    public double MaximumOutputStep { get; private set; }
    public double MaximumOutputSecondDifference { get; private set; }
    public long OutputStepsOverQuarterScale { get; private set; }
    public double MaximumReadDurationUs => Interlocked.Read(ref maximumReadTicks) * 1_000_000d / System.Diagnostics.Stopwatch.Frequency;
    public long ReadDeadlineMisses => Interlocked.Read(ref readDeadlineMisses);
    public AudioUnderrunEvent[] UnderrunEvents { get { lock (gate) return underrunEvents.ToArray(); } }
    public double DriftPpm => ppm;
    public void SetClockAssistInput(ClockObservationMode mode, bool qualified, double feedforwardPpm, string? reason = null)
    { lock (gate) clockAssist.SetInput(mode, qualified, feedforwardPpm, reason); }
    public ClockAssistSnapshot ClockAssistSnapshot { get { lock (gate) return clockAssist.Snapshot(); } }
    public double BufferedMs { get { lock (gate) return initialized ? Math.Max(0, (maxEnd - origin) - position) / FramesPerMs : 0; } }

    public void Add(AudioPacket packet)
    {
        packet.Format.Validate();
        if (packet.Pcm.Length != packet.Frames * packet.Format.BytesPerFrame || packet.SampleIndex > ulong.MaxValue - packet.Frames)
            throw new InvalidDataException("Invalid PCM range");
        if (packet.Frames == 0) return;
        long addStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        lock (gate)
        {
            addWait.Record(System.Diagnostics.Stopwatch.GetTimestamp()-addStarted);
            long now = clock.GetTimestamp();
            bool changed = sourceFormat != packet.Format || initialized && timeline != packet.TimelineId;
            if (changed)
            {
                sourceFormat = packet.Format; filter = SincFilter.ForRates(sourceFormat.SampleRate, OutputSampleRate,Quality);
                if(MaximumFrames+filter.History+filter.LookAhead+sourceFormat.PacketFrames>Capacity)throw new InvalidOperationException("Physical audio ring cannot cover configured timeline and FIR history.");
                arrival.Reset();sourceClock.Reset();currentBurstFrames=0;
                targetFrames = sourceFormat.SampleRate / 250;
                if (initialized) { FormatChanges++; Resyncs++; } else Initializations++;
                Reset(packet.SampleIndex);
            }
            timeline = packet.TimelineId;
            if (lastPacket != 0 && !changed)
            {
                double gapMs = clock.GetElapsedTime(lastPacket, now).TotalMilliseconds;
                MaximumArrivalGapMs = Math.Max(MaximumArrivalGapMs, gapMs);
                currentBurstFrames = gapMs > .25 ? packet.Frames : currentBurstFrames + packet.Frames;
            }
            else currentBurstFrames = packet.Frames;
            MaximumArrivalBurstFrames = Math.Max(MaximumArrivalBurstFrames, currentBurstFrames);
            arrival.Observe(packet.SampleIndex,packet.Frames,now,clock,sourceFormat.SampleRate);
            sourceClock.Observe(packet.SampleIndex,now,clock,sourceFormat.SampleRate);
            if (firstPacketTimestamp == 0) firstPacketTimestamp = now;
            if (!initialized) { Reset(packet.SampleIndex); Initializations++; }
            // Arrival timing alone cannot invalidate a known sample-index timeline.
            // Delayed/reordered bursts may still fit the configured buffer and repair it.
            else if (!changed && (packet.Flags & 1) != 0)
            { Reset(packet.SampleIndex); Resyncs++; }
            lastPacket = now;
            ulong end = packet.SampleIndex + packet.Frames, cursor = origin + (ulong)position;
            if (end <= cursor) { Late++;LateSourceFrames+=packet.Frames; return; }
            if (packet.Frames > MaximumFrames) { Overflows++; return; }
            if (end - cursor > (ulong)MaximumFrames)
            {
                if (!overflowSinceLastRead) { Overflows++; Resyncs++; }
                overflowSinceLastRead = true;
                if (packet.SampleIndex > maxEnd && packet.SampleIndex - maxEnd > (ulong)MaximumFrames) Reset(packet.SampleIndex);
                else
                {
                    ulong nextOrigin = end - (ulong)MaximumFrames;
                    OverflowDroppedFrames += (long)(nextOrigin - cursor); origin = segmentStart = nextOrigin;
                    position = 0; maxEnd = Math.Max(maxEnd, origin); playing = false;
                    BeginTransition(); drift.Reset(); clockAssist.Reset(); ppm = 0;
                }
            }
            cursor = origin + (ulong)position;
            bool partiallyLate=false;
            for (int frame = 0; frame < packet.Frames; frame++)
            {
                ulong index = packet.SampleIndex + (ulong)frame;
                if (index < cursor) { LateSourceFrames++;partiallyLate=true; continue; }
                int slot = (int)(index % Capacity), offset = frame * sourceFormat.BytesPerFrame;
                float left = PcmCodec.ReadFloat(packet.Pcm.AsSpan(offset), sourceFormat.Bits);
                float right = PcmCodec.ReadFloat(packet.Pcm.AsSpan(offset + sourceFormat.BytesPerSample), sourceFormat.Bits);
                if (Has(index) && !concealed[slot])
                {
                    if (samples[slot * 2] != left || samples[slot * 2 + 1] != right) ConflictingOverlapFrames++;
                    else Duplicates++;
                    continue;
                }
                if(Has(index)&&concealed[slot])RepairedBeforePlaybackFrames++;
                tags[slot] = index; valid[slot] = true; concealed[slot] = false;
                samples[slot * 2] = left; samples[slot * 2 + 1] = right;LevelMeter.ObserveStereoFrame(left,right);
            }
            maxEnd = Math.Max(maxEnd, end);
            if(partiallyLate)Late++;
            addTiming.Record(System.Diagnostics.Stopwatch.GetTimestamp()-addStarted);
        }
    }

    void Reset(ulong start)
    {
        LevelMeter.Reset(++meterGeneration,sourceFormat.SampleRate,sourceFormat.Bits);
        Array.Clear(valid); Array.Clear(concealed); origin = maxEnd = segmentStart = start; position = 0;
        initialized = true; playing = false; ppm = 0;driftReferenceFrames=0; drift.Reset(); clockAssist.Reset(); BeginTransition();
    }
    void BeginTransition()
    { tailLeft = lastLeft; tailRight = lastRight; tailRemaining = FadeFrames; fadeInRemaining = FadeFrames; }
    bool Has(ulong index) => valid[(int)(index % Capacity)] && tags[(int)(index % Capacity)] == index;
    double Source(ulong index, int channel) => Has(index) ? samples[(int)(index % Capacity) * 2 + channel] : 0;

    // Conceal at playback deadline, not packet arrival, so timely reordering can repair holes.
    // Short gaps interpolate adjacent samples; longer gaps fade to/from silence over 1 ms.
    void ConcealWindow(ulong start, ulong end)
    {
        ulong at = Math.Max(start, segmentStart);
        while (at < maxEnd && at <= end)
        {
            if (Has(at)) { ++at; continue; }
            ulong first = at;
            while (at < maxEnd && !Has(at)) ++at;
            int length = checked((int)(at - first));
            bool before = first > 0 && Has(first - 1), after = at < maxEnd && Has(at);
            double left0 = before ? Source(first - 1, 0) : 0, right0 = before ? Source(first - 1, 1) : 0;
            double left1 = after ? Source(at, 0) : 0, right1 = after ? Source(at, 1) : 0;
            int ramp = Math.Max(1, sourceFormat.SampleRate / 1000);
            for (int n = 0; n < length; n++)
            {
                double left, right;
                if (before && after && length <= sourceFormat.SampleRate / 500)
                { double t = (n + 1d) / (length + 1); left = left0 + (left1 - left0) * t; right = right0 + (right1 - right0) * t; }
                else
                {
                    double fadeOut = Math.Max(0, 1 - (n + 1d) / ramp), fadeIn = Math.Max(0, 1 - (length - n) / (double)ramp);
                    left = left0 * fadeOut + left1 * fadeIn; right = right0 * fadeOut + right1 * fadeIn;
                }
                ulong index = first + (ulong)n; int slot = (int)(index % Capacity);
                valid[slot] = concealed[slot] = true; tags[slot] = index;
                samples[slot * 2] = (float)left; samples[slot * 2 + 1] = (float)right;
            }
            ConcealedSourceFrames += length;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return ReadCore(buffer, offset, count); }
        finally
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started, observed;
            if(RenderedFrames>OutputSampleRate)readTiming.Record(elapsed);
            do { observed = Interlocked.Read(ref maximumReadTicks); if (elapsed <= observed) break; }
            while (Interlocked.CompareExchange(ref maximumReadTicks, elapsed, observed) != observed);
            if (count > 0 && elapsed / (double)System.Diagnostics.Stopwatch.Frequency > count / (2d * OutputSampleRate)) Interlocked.Increment(ref readDeadlineMisses);
        }
    }
    int ReadCore(float[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > buffer.Length - count || (count & 1) != 0) throw new ArgumentOutOfRangeException(nameof(count));
        long waiting=System.Diagnostics.Stopwatch.GetTimestamp();
        lock (gate)
        {
            readWait.Record(System.Diagnostics.Stopwatch.GetTimestamp()-waiting);
            Array.Clear(buffer, offset, count); OutputCallbacks++; CurrentReadFrames = count / 2;
            OutputFramesRequested += CurrentReadFrames; LargestReadFrames = Math.Max(LargestReadFrames, CurrentReadFrames);
            int demand = (int)Math.Ceiling(CurrentReadFrames * NominalStep);
            OutputDemandTooLarge = demand + filter.LookAhead > MaximumFrames;
            if (OutputDemandTooLarge) { OversizedReadCallbacks++; clockAssist.Invalidate("output_demand_too_large"); if (playing) { playing = false; BeginTransition(); } Silence(buffer, offset); return count; }
            double schedulingFrames = Math.Max(arrival.JitterMs,arrival.BurstMs)*FramesPerMs;
            int requiredPrefill = Math.Max((int)Math.Ceiling(ConservativePrefillMs * FramesPerMs),
                Math.Max((int)(4 * FramesPerMs), Math.Max(demand + (int)(2 * FramesPerMs),
                demand + (int)Math.Ceiling(schedulingFrames) + filter.LookAhead + (int)FramesPerMs)));
            RequiredPrefillMs = requiredPrefill / FramesPerMs;
            RecommendedBufferMs = Math.Ceiling((requiredPrefill + Math.Min(demand, schedulingFrames) + FramesPerMs) / FramesPerMs);
            // Every capacity needs room for the next delivery batch when
            // starting/rebuffering. A long pause can otherwise leave even
            // an 80 ms stream repeatedly restarting at its hard limit.
            int minimum=demand+filter.LookAhead+(int)Math.Ceiling(FramesPerMs);
            int reserve=(int)Math.Ceiling(Math.Max(FramesPerMs,Math.Min(demand,schedulingFrames)));
            int prefillCeiling=Math.Clamp(MaximumFrames-reserve,Math.Min(minimum,MaximumFrames),MaximumFrames);
            targetFrames = Math.Min(prefillCeiling, requiredPrefill);
            if (OutputCallbacks == 1 && CurrentReadFrames > OutputSampleRate / 100) { Silence(buffer, offset); return count; }
            // Packet age is diagnostic, not a playback deadline. An explicit
            // 80 ms buffer must remain usable during a 50-60 ms delivery gap.
            // The source frontier check below includes the real ASRC demand
            // and FIR lookahead and decides when fading/rebuffering is needed.
            if (!initialized) { Silence(buffer, offset); return count; }
            double level = (maxEnd - origin) - position;
            if (!playing)
            {
                if (level < targetFrames) { BufferingCallbacks++; Silence(buffer, offset); return count; }
                // Conservative capacities retain up to one actual callback of
                // extra arrived PCM. Trimming a 25ms refill to 22.5ms discarded
                // useful first-gap coverage at a 40ms cap, but keeping all old
                // backlog at a larger cap would add persistent startup latency.
                // Both policies retain the existing reserve/cap ceiling.
                int extraStartupFrames=MaximumBufferMs>=40?Math.Max(sourceFormat.PacketFrames,demand):sourceFormat.PacketFrames;
                int startupCeiling=Math.Min(prefillCeiling,targetFrames+extraStartupFrames);
                if(level>startupCeiling)
                {
                    // Select a fresh starting point only while not playing.
                    // This is explicit startup/rebuffer trimming, never PLC
                    // or an unreported discard from an active timeline.
                    long trim=(long)Math.Floor(level-startupCeiling);
                    position+=trim;level-=trim;PrefillTrimmedSourceFrames+=trim;
                }
                playing = true; fadeInRemaining = FadeFrames;driftReferenceFrames=level;
            }
            // A changing prefill recommendation is not a new oscillator error. Apply it at the next real rebuffer.
            double sincePacket=lastPacket==0?double.PositiveInfinity:clock.GetElapsedTime(lastPacket,clock.GetTimestamp()).TotalMilliseconds;
            if(sincePacket<3)ppm = clockAssist.Update(level - driftReferenceFrames, CurrentReadFrames, sourceFormat.SampleRate, OutputSampleRate,
                !overflowSinceLastRead);
            double step = NominalStep * (1 + ppm / 1_000_000);
            if (level < Math.Ceiling(CurrentReadFrames * step) + filter.LookAhead)
            {
                clockAssist.Invalidate("rebuffer");
                playing = false; RebufferCount++; OutputUnderruns++; RecordUnderrun("insufficient_frontier", level);
                BeginTransition(); Silence(buffer, offset); return count;
            }
            if (firstPlaybackTimestamp == 0) firstPlaybackTimestamp = clock.GetTimestamp();
            ulong firstIndex = origin + (ulong)position;
            ulong lastIndex = origin + (ulong)(position + CurrentReadFrames * step) + (uint)filter.LookAhead;
            long concealedBefore = ConcealedSourceFrames;
            ConcealWindow(firstIndex >= (ulong)filter.History ? firstIndex - (uint)filter.History : 0, lastIndex);
            if (ConcealedSourceFrames != concealedBefore) clockAssist.Invalidate("source_gap_concealed");
            int workFrames=checked((int)(lastIndex-firstIndex)+filter.History+1);
            if(workFrames>Capacity)throw new InvalidOperationException("ASRC work window exceeds physical storage.");
            workIncomplete[0]=0;
            // Validate/copy each source sample once, not once per output-frame FIR tap. The gate preserves ownership.
            for(int n=0;n<workFrames;n++)
            {
                int relative=n-filter.History;
                ulong si=relative<0&&firstIndex<(ulong)-relative?segmentStart:relative<0?firstIndex-(ulong)-relative:firstIndex+(ulong)relative;
                if(si<segmentStart&&!Has(si))si=segmentStart;
                int slot=(int)(si%Capacity);bool exists=Has(si);
                workLeft[n]=exists?samples[slot*2]:0;workRight[n]=exists?samples[slot*2+1]:0;
                workIncomplete[n+1]=workIncomplete[n]+(!exists||concealed[slot]?1:0);
            }
            float targetGain = Mute ? 0 : Math.Clamp(float.IsFinite(Gain) ? Gain : 0, 0, 1);
            for (int frame = 0; frame < CurrentReadFrames; frame++)
            {
                ulong index = origin + (ulong)position; double fraction = position - Math.Floor(position);
                gainCurrent += Math.Clamp(targetGain - gainCurrent, -1d / (OutputSampleRate * .005), 1d / (OutputSampleRate * .005));
                int workAt=checked((int)(index-firstIndex));
                filter.Convolve(workLeft.AsSpan(workAt,filter.Taps),workRight.AsSpan(workAt,filter.Taps),fraction,out double left,out double right);
                bool incomplete=workIncomplete[workAt+filter.Taps]!=workIncomplete[workAt];
                if (incomplete) InterpolationIncompleteFrames++;
                if (concealed[(int)(index % Capacity)]) { MissingFrames++; ConcealedOutputFrames++; }
                double fade = fadeInRemaining > 0 ? 1 - --fadeInRemaining / (double)FadeFrames : 1;
                left *= gainCurrent * fade; right *= gainCurrent * fade;
                if (tailRemaining > 0) { double tail = --tailRemaining / (double)FadeFrames; left += tailLeft * tail; right += tailRight * tail; BoundaryFadeFrames++; }
                Emit(buffer, offset + frame * 2, left, right); position += step;
            }
            ulong committedEnd=origin+(ulong)position;
            for(ulong si=firstIndex;si<committedEnd;si++){CommittedSourceFrames++;if(!Has(si)||concealed[(int)(si%Capacity)])PermanentGapSourceFrames++;}
            RenderedFrames += CurrentReadFrames; overflowSinceLastRead = false; return count;
        }
    }
    void Silence(float[] buffer, int offset)
    {
        for (int frame = 0; frame < CurrentReadFrames; frame++)
        {
            double left = 0, right = 0;
            if (tailRemaining > 0) { double tail = --tailRemaining / (double)FadeFrames; left = tailLeft * tail; right = tailRight * tail; BoundaryFadeFrames++; }
            Emit(buffer, offset + frame * 2, left, right);
        }
        OutputSilenceFrames += CurrentReadFrames;
    }
    void Emit(float[] buffer, int offset, double left, double right)
    {
        left = Math.Clamp(left, -1, 1); right = Math.Clamp(right, -1, 1);
        double dl = left - lastLeft, dr = right - lastRight, step = Math.Max(Math.Abs(dl), Math.Abs(dr));
        MaximumOutputStep = Math.Max(MaximumOutputStep, step);
        MaximumOutputSecondDifference = Math.Max(MaximumOutputSecondDifference, Math.Max(Math.Abs(dl - previousDeltaLeft), Math.Abs(dr - previousDeltaRight)));
        if (step > .25) OutputStepsOverQuarterScale++;
        buffer[offset] = (float)left; buffer[offset + 1] = (float)right;
        lastLeft = left; lastRight = right; previousDeltaLeft = dl; previousDeltaRight = dr;
    }
    void RecordUnderrun(string reason, double level)
    {
        long now = clock.GetTimestamp();
        double sincePlayback = firstPlaybackTimestamp == 0 ? 0 : clock.GetElapsedTime(firstPlaybackTimestamp, now).TotalMilliseconds;
        if (sincePlayback < 1000) StartupUnderruns++; else SteadyUnderruns++;
        if (underrunEvents.Count < 16) underrunEvents.Add(new(reason,
            firstPacketTimestamp == 0 ? 0 : clock.GetElapsedTime(firstPacketTimestamp, now).TotalMilliseconds,
            sincePlayback, level / FramesPerMs, CurrentReadFrames * 1000d / OutputSampleRate,
            targetFrames / FramesPerMs, lastPacket == 0 ? 0 : clock.GetElapsedTime(lastPacket, now).TotalMilliseconds));
    }
}

public sealed record AudioUnderrunEvent(string Reason, double SinceFirstPacketMs,
    double SinceFirstPlaybackMs, double BufferMs, double DemandMs, double TargetMs, double LastPacketAgeMs);
