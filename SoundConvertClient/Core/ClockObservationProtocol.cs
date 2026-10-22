using System.Buffers.Binary;

namespace UsbAudioBridge;

public enum ClockObservationMode { Legacy, ObserveOnly, ClockAssistExperimental }
public enum ClockObserverState { Disabled, Acquiring, Qualified, Holdover, Fallback }
public enum ClockObservationKind : byte { Request = 1, Reply = 2 }

public readonly record struct ClockSessionIdentity(ulong Boot, uint StreamId, uint Epoch, ulong Token)
{
    public bool IsValid => Boot != 0 && StreamId != 0 && Epoch != 0 && Token != 0;
}

public readonly record struct ClockObservationMessage(ClockSessionIdentity Identity, ClockObservationKind Kind,
    ulong RequestId, ulong P1Echo, ulong B2Us = 0, ulong B3Us = 0, ulong AnchorSampleIndex = 0,
    ulong AnchorExtractUs = 0, uint AnchorGeneration = 0, uint SampleRate = 0,
    ushort AnchorFrames = 0, bool AnchorValid = false);

/// <summary>UABT v1 is a separate, strictly bounded sideband. It never changes the UABA/FEC byte domain.</summary>
public static class ClockObservationProtocol
{
    public const int Length = 104;
    public const byte Version = 1;
    public static bool IsClockPacket(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[..4].SequenceEqual("UABT"u8);

    public static void Encode(ClockObservationMessage message, Span<byte> destination)
    {
        if (destination.Length != Length) throw new ArgumentException("UABT requires exactly 104 bytes.", nameof(destination));
        Validate(message, out string reason);
        if (reason.Length != 0) throw new ArgumentException(reason, nameof(message));
        destination.Clear(); "UABT"u8.CopyTo(destination); destination[4] = Version; destination[5] = (byte)message.Kind;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], Length);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], message.Identity.Boot);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], message.Identity.StreamId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], message.Identity.Epoch);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], message.Identity.Token);
        BinaryPrimitives.WriteUInt64BigEndian(destination[32..], message.RequestId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[40..], message.P1Echo);
        BinaryPrimitives.WriteUInt64BigEndian(destination[48..], message.B2Us);
        BinaryPrimitives.WriteUInt64BigEndian(destination[56..], message.B3Us);
        BinaryPrimitives.WriteUInt64BigEndian(destination[64..], message.AnchorSampleIndex);
        BinaryPrimitives.WriteUInt64BigEndian(destination[72..], message.AnchorExtractUs);
        BinaryPrimitives.WriteUInt32BigEndian(destination[80..], message.AnchorGeneration);
        BinaryPrimitives.WriteUInt32BigEndian(destination[84..], message.SampleRate);
        BinaryPrimitives.WriteUInt16BigEndian(destination[88..], message.AnchorFrames);
        BinaryPrimitives.WriteUInt16BigEndian(destination[90..], message.AnchorValid ? (ushort)1 : (ushort)0);
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out ClockObservationMessage message, out string reason)
    {
        message = default;
        if (bytes.Length != Length) { reason = "length"; return false; }
        if (!IsClockPacket(bytes)) { reason = "magic"; return false; }
        if (bytes[4] != Version) { reason = "version"; return false; }
        if (BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]) != Length) { reason = "declared_length"; return false; }
        if (bytes[5] is not (1 or 2)) { reason = "kind"; return false; }
        if ((BinaryPrimitives.ReadUInt16BigEndian(bytes[90..]) & ~1) != 0) { reason = "flags"; return false; }
        foreach (byte value in bytes[92..]) if (value != 0) { reason = "reserved"; return false; }
        message = new(new(BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]), BinaryPrimitives.ReadUInt64BigEndian(bytes[24..])),
            (ClockObservationKind)bytes[5], BinaryPrimitives.ReadUInt64BigEndian(bytes[32..]), BinaryPrimitives.ReadUInt64BigEndian(bytes[40..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[48..]), BinaryPrimitives.ReadUInt64BigEndian(bytes[56..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[64..]), BinaryPrimitives.ReadUInt64BigEndian(bytes[72..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[80..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[84..]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[88..]), bytes[91] == 1);
        return Validate(message, out reason);
    }

    static bool Validate(ClockObservationMessage message, out string reason)
    {
        reason = "";
        if (!message.Identity.IsValid) reason = "identity_zero";
        else if (message.RequestId == 0) reason = "request_zero";
        else if (message.Kind is not (ClockObservationKind.Request or ClockObservationKind.Reply)) reason = "kind";
        else if (message.Kind == ClockObservationKind.Request && (message.B2Us != 0 || message.B3Us != 0 ||
            message.AnchorSampleIndex != 0 || message.AnchorExtractUs != 0 || message.AnchorGeneration != 0 ||
            message.SampleRate != 0 || message.AnchorFrames != 0 || message.AnchorValid)) reason = "request_unused";
        else if (message.Kind == ClockObservationKind.Reply && message.B3Us < message.B2Us) reason = "board_time_reversed";
        else if (!message.AnchorValid && (message.AnchorSampleIndex != 0 || message.AnchorExtractUs != 0 ||
            message.AnchorGeneration != 0 || message.SampleRate != 0 || message.AnchorFrames != 0)) reason = "invalid_anchor_nonzero";
        else if (message.AnchorValid && (message.Kind != ClockObservationKind.Reply || message.AnchorGeneration != message.Identity.Epoch ||
            message.SampleRate == 0 || message.AnchorFrames == 0)) reason = "anchor_metadata";
        return reason.Length == 0;
    }
}

/// <summary>P1/P4 are raw local QPC ticks, while P1Echo is the original opaque microsecond wire echo.</summary>
public readonly record struct ClockExchange(ClockObservationMessage Message, ulong P1Ticks, ulong P4Ticks, ulong PcFrequency);
