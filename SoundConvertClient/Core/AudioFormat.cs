using System.Buffers.Binary;
using System.Text.Json;

namespace UsbAudioBridge;

public sealed record AudioFormat(int SampleRate, int Bits, int Channels = 2)
{
    public static AudioFormat Pcm16 { get; } = new(48000, 16);
    public static AudioFormat Pcm24 { get; } = new(96000, 24);
    // Stable order matches usb_formats.h. A mask is a preference, not the current Host format.
    public static IReadOnlyList<AudioFormat> Supported { get; } = Array.AsReadOnly(new AudioFormat[] {
        new(48000,16),new(96000,24),new(44100,16),new(44100,24),new(48000,24),
        new(88200,16),new(88200,24),new(96000,16),new(176400,16),new(192000,16) });
    public const uint AllMask = 0x3ff;
    public string Label => $"{Bits}-bit / {SampleRate/1000d:0.###} kHz · 双声道";
    public int BytesPerSample => Bits / 8;
    public int BytesPerFrame => BytesPerSample * Channels;
    public int PacketFrames => (SampleRate + 999) / 1000;
    public byte Code => Bits == 24 ? (byte)2 : (byte)1;
    public string SampleFormat => Bits == 24 ? "S24LE_PACKED" : "S16LE";
    public void Validate()
    {
        if (!Supported.Contains(this))
            throw new InvalidDataException("Unsupported stereo USB Full-Speed audio format.");
    }
    public static AudioFormat FromJson(JsonElement json)
    {
        string? sample = json.GetProperty("sample_format").GetString();
        var result = new AudioFormat(json.GetProperty("sample_rate").GetInt32(), sample switch
        { "S16LE" => 16, "S24LE_PACKED" => 24, _ => throw new InvalidDataException("Unsupported PCM encoding.") }, json.GetProperty("channels").GetInt32());
        result.Validate(); return result;
    }
    public object ToRequest() => new { sample_rate = SampleRate, channels = Channels, sample_format = SampleFormat };
}

public static class PcmCodec
{
    public static int ReadSigned(ReadOnlySpan<byte> sample, int bits) => bits switch
    {
        16 => BinaryPrimitives.ReadInt16LittleEndian(sample),
        24 => ((sample[0] | (sample[1] << 8) | (sample[2] << 16)) << 8) >> 8,
        _ => throw new InvalidDataException("Unsupported PCM bit depth.")
    };
    public static float ReadFloat(ReadOnlySpan<byte> sample, int bits) => ReadSigned(sample, bits) / (bits == 24 ? 8388608f : 32768f);
}
