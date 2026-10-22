namespace UsbAudioBridge;

/// <summary>Start-to-start request cadence. Timeout consumes its period; an overrun never produces catch-up sends.</summary>
public static class ClockRequestCadence
{
    public static TimeSpan DelayAfterExchange(ulong p1Ticks, ulong nowTicks, ulong frequency, bool qualified)
    {
        if (frequency == 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        long period = qualified ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerSecond / 2;
        if (nowTicks < p1Ticks) return TimeSpan.FromTicks(period);
        // Subtract integer origins before conversion; UInt128 also handles a
        // diagnostic uint64 origin/frequency without overflow or float loss.
        UInt128 elapsed = (UInt128)(nowTicks - p1Ticks) * (ulong)TimeSpan.TicksPerSecond / frequency;
        // A full-period overrun begins a new period from now, without a burst.
        return TimeSpan.FromTicks(elapsed >= (ulong)period ? period : period - (long)elapsed);
    }
}
