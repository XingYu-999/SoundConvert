using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
namespace UsbAudioBridge;

public sealed record ControlFrame(byte Kind, uint RequestId, ulong Sequence, ulong Boot, byte[] Payload, byte[] Raw);
public static class Wire
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static uint JsonUInt32(JsonElement e) { if (e.ValueKind != JsonValueKind.Number || !e.TryGetDecimal(out decimal value) || value < 0 || value > uint.MaxValue || decimal.Truncate(value) != value) throw new InvalidDataException("Expected an integral uint32 JSON number"); return (uint)value; }
    public static byte[] Control(byte kind, uint id, ulong seq, ulong boot, byte[] payload)
    {
        if (payload.Length > 8192) throw new InvalidDataException("Control payload too large");
        byte[] b = new byte[32 + payload.Length];
        "UABC"u8.CopyTo(b); b[4] = 1; b[5] = kind; Write16(b, 6, 0); Write32(b, 8, (uint)payload.Length); Write32(b, 12, id); Write64(b, 16, seq); Write64(b, 24, boot); payload.CopyTo(b, 32);
        return b;
    }
    public static async Task<ControlFrame> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] h = new byte[32]; await stream.ReadExactlyAsync(h, ct).ConfigureAwait(false);
        if (!h.AsSpan(0, 4).SequenceEqual("UABC"u8) || h[4] != 1 || h[5] is < 1 or > 3 || Read16(h, 6) != 0 || Read32(h, 8) > 8192) throw new InvalidDataException("Invalid control header");
        int n = (int)Read32(h, 8); byte[] b = new byte[32 + n]; h.CopyTo(b, 0); await stream.ReadExactlyAsync(b.AsMemory(32), ct).ConfigureAwait(false);
        byte[] p = b.AsSpan(32, n).ToArray(); ValidateJson(p); return new(h[5], Read32(h, 12), Read64(h, 16), Read64(h, 24), p, b);
    }
    public static void ValidateJson(byte[] p) { _ = Utf8.GetString(p); using var d = JsonDocument.Parse(p, new JsonDocumentOptions { MaxDepth = 16 }); if (d.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("JSON object required"); Check(d.RootElement); }
    static void Check(JsonElement e) { if (e.ValueKind == JsonValueKind.Object) { var keys = new HashSet<string>(StringComparer.Ordinal); foreach (var x in e.EnumerateObject()) { if (!keys.Add(x.Name)) throw new InvalidDataException("Duplicate JSON key"); Check(x.Value); } } else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Check(x); }
    public static ushort Read16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
    public static uint Read32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    public static ulong Read64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b[o..]);
    public static void Write16(Span<byte> b, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b[o..], v);
    public static void Write32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b[o..], v);
    public static void Write64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(b[o..], v);
}
public sealed record Device(string DeviceId, string Model, string Firmware, string Address, int ControlPort, string LocalAddress, string Mac,
    int PrefixLength = 24, string Gateway = "0.0.0.0", string AddressMode = "", byte? NetworkState = null,
    ulong? ConfigurationBootId = null, ulong? ConfigurationRevision = null);
public static class DiscoveryCodec
{
    public static byte[] Request(ulong id) { byte[] b = new byte[56]; "UABD"u8.CopyTo(b); b[4] = 1; b[5] = 1; Wire.Write16(b, 6, 56); Wire.Write16(b, 8, 56); Wire.Write64(b, 12, id); RandomNumberGenerator.Fill(b.AsSpan(36, 16)); return b; }
    public static Device Parse(byte[] b, ulong id, string local)
    {
        if (b.Length < 56 || b.Length > 1024 || !b.AsSpan(0, 4).SequenceEqual("UABD"u8) || b[4] != 1 || b[5] != 2 || Wire.Read16(b, 6) != 56 || Wire.Read16(b, 8) != b.Length || Wire.Read16(b, 10) != 1 || Wire.Read64(b, 12) != id || Wire.Read16(b, 52) != 0 || Wire.Read16(b, 54) != 0) throw new InvalidDataException("Invalid discovery response");
        string model = "", firmware = "", ip = "0.0.0.0", mac = "", gateway = "0.0.0.0", mode = ""; int prefix = 24; byte? state = null; int port = 49161; int offset = 56; ushort prev = 0;
        while (offset < b.Length) { if (offset + 4 > b.Length) throw new InvalidDataException("Truncated TLV"); ushort type = Wire.Read16(b, offset), n = Wire.Read16(b, offset + 2); offset += 4; if (type <= prev || offset + n > b.Length) throw new InvalidDataException("Invalid or duplicate TLV"); prev = type; var v = b.AsSpan(offset, n); switch (type) { case 1: if (n > 64) throw new InvalidDataException(); model = Wire.Utf8.GetString(v); break; case 2: if (n > 64) throw new InvalidDataException(); firmware = Wire.Utf8.GetString(v); break; case 3: if (n != 4) throw new InvalidDataException(); ip = new IPAddress(v).ToString(); break; case 4: if (n != 1 || v[0] > 30) throw new InvalidDataException(); prefix = v[0]; break; case 5: if (n != 4) throw new InvalidDataException(); gateway = new IPAddress(v).ToString(); break; case 6: if (n != 1 || v[0] > 3) throw new InvalidDataException(); mode = v[0] == 1 ? "dhcp" : v[0] is 2 or 3 ? "static" : ""; break; case 13: if (n != 1 || v[0] > 7) throw new InvalidDataException(); state = v[0]; break; case 14: if (n != 1 || v[0] is < 1 or > 2) throw new InvalidDataException(); mode = v[0] == 1 ? "dhcp" : "static"; break; case 7: if (n != 2) throw new InvalidDataException(); port = Wire.Read16(v, 0); break; case 0x8011: if (n != 16) throw new InvalidDataException(); break; case 10: if (n != 6) throw new InvalidDataException(); mac = Convert.ToHexString(v); break; default: if ((type & 0x8000) != 0) throw new InvalidDataException("Unknown critical TLV"); break; } offset += n; }
        return new(Convert.ToHexString(b.AsSpan(20, 16)).ToLowerInvariant(), model, firmware, ip, port, local, mac, prefix, gateway, mode, state);
    }
}
public sealed record StreamDescriptor(uint StreamId, uint Epoch, ulong Token, int SourcePort, int ReceivePort,
    AudioFormat? NegotiatedFormat = null, int PacketFrames = 48, bool FollowUsb = false,bool FecEnabled=false,AudioTransportCodec Codec=AudioTransportCodec.Pcm)
{ public AudioFormat Format => NegotiatedFormat ?? AudioFormat.Pcm16; }
public sealed record AudioPacket(ushort Flags, uint Sequence, ulong SampleIndex, ushort Frames, byte[] Pcm,
    AudioFormat? NegotiatedFormat = null, ulong TimelineId = 0)
{ public AudioFormat Format => NegotiatedFormat ?? AudioFormat.Pcm16; }
public static class AudioCodec
{
    public static byte[] Probe(StreamDescriptor d) { byte[] b = new byte[48]; "UABA"u8.CopyTo(b); b[4] = 1; b[5] = 48; Wire.Write16(b, 6, 8); Wire.Write32(b, 8, d.StreamId); Wire.Write32(b, 12, d.Epoch); Wire.Write64(b, 40, d.Token); return b; }
    public static AudioPacket Parse(byte[] b, StreamDescriptor d)
    {
        if (b.Length < 48 || b.Length > 1200 || !b.AsSpan(0, 4).SequenceEqual("UABA"u8) || b[5] != 48 || Wire.Read16(b, 36) != b.Length - 48) throw new InvalidDataException("Malformed audio packet");
        bool compressed=b[4]==2;
        if(compressed?(d.Codec!=AudioTransportCodec.ImaAdpcm||b[38]!=1||b[39]!=0||Wire.Read16(b,6)!=0):(b[4]!=1||Wire.Read16(b,38)!=0||d.Codec!=AudioTransportCodec.Pcm&&Wire.Read16(b,6) is not (4 or 8)))throw new InvalidDataException("Unexpected transport codec");
        uint epoch = Wire.Read32(b, 12);
        bool nextTimeline = d.FollowUsb && unchecked((int)(epoch - d.Epoch)) > 0;
        if (Wire.Read32(b, 8) != d.StreamId || (epoch != d.Epoch && !nextTimeline) || Wire.Read64(b, 40) != d.Token) throw new InvalidDataException("Stale stream");
        AudioFormat format = d.Format;
        if (nextTimeline && (Wire.Read16(b, 6) & 8) == 0)
        {
            format = new AudioFormat(unchecked((int)Wire.Read32(b, 28)), b[35] switch { 1 => 16, 2 => 24, _ => throw new InvalidDataException("Unsupported PCM encoding") }, b[34]);
            format.Validate();
        }
        ushort flags = Wire.Read16(b, 6), frames = Wire.Read16(b, 32); if ((flags & ~15) != 0 || (flags & 12) == 12) throw new InvalidDataException("Audio flags");
        if ((flags & 8) != 0) { if (flags != 8 || b.Length != 48 || frames != 0 || Wire.Read32(b, 28) != 0 || b[34] != 0 || b[35] != 0 || Wire.Read32(b, 16) != 0 || Wire.Read64(b, 20) != 0) throw new InvalidDataException("Invalid PROBE"); }
        else { format.Validate(); if (Wire.Read32(b, 28) != format.SampleRate || b[34] != format.Channels || b[35] != format.Code || b.Length != 48 + (compressed?frames+7:frames*format.BytesPerFrame) || (frames == 0 && (flags & 4) == 0) || frames > (d.FollowUsb ? format.PacketFrames : d.PacketFrames)) throw new InvalidDataException("Unexpected negotiated audio format"); if ((flags & 2) != 0 && b.AsSpan(48).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Nonzero SILENCE payload"); }
        if (Wire.Read64(b, 20) > ulong.MaxValue - frames) throw new InvalidDataException("Sample index overflow"); return new(flags, Wire.Read32(b, 16), Wire.Read64(b, 20), frames, compressed?ImaAdpcm.Decode(b.AsSpan(48),frames,format):b.AsSpan(48).ToArray(), format, d.FollowUsb ? unchecked(d.Token + epoch) : d.Token);
    }
}



