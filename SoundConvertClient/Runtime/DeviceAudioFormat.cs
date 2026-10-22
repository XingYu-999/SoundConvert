using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundConvertClient;

// PKEY_AudioEngine_DeviceFormat is the Windows-selected device output format.
// AudioClient.MixFormat instead describes the engine's internal processing.
public sealed record DeviceAudioFormat(int SampleRate, int Channels, int ContainerBits, int ValidBits, bool FloatingPoint)
{
    public int PrecisionBits => FloatingPoint ? ContainerBits switch { 32 => 24, 64 => 53, _ => 0 } : ValidBits;
    public string Label => $"{SampleRate / 1000d:0.###} kHz / {ValidBits}-bit{(FloatingPoint ? " 浮点" : "")} / {Channels} 声道"
        + (ValidBits != ContainerBits ? $"（{ContainerBits} 位容器）" : "");

    public static DeviceAudioFormat Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 18) throw new InvalidDataException("设备输出格式不完整。");
        // Keep parsing bounded even if a driver supplies an invalid property blob.
        return ParseCore(blob.ToArray());
    }

    private static DeviceAudioFormat ParseCore(byte[] blobCopy)
    {
        int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(blobCopy.AsSpan(offset));
        int tag = U16(0), channels = U16(2), bits = U16(14), valid = bits;
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(blobCopy.AsSpan(4));
        int extra = U16(16);
        bool floating = tag == 3;
        if (tag == 0xfffe)
        {
            if (extra < 22 || blobCopy.Length < 40) throw new InvalidDataException("设备扩展格式不完整。");
            valid = U16(18);
            Guid sub = new(blobCopy.AsSpan(24, 16));
            if (sub == new Guid("00000003-0000-0010-8000-00aa00389b71")) floating = true;
            else if (sub != new Guid("00000001-0000-0010-8000-00aa00389b71")) throw new InvalidDataException("未知设备输出编码。");
        }
        else if (tag is not (1 or 3)) throw new InvalidDataException("未知设备输出编码。");
        if (rate is < 1 or > 768000 || channels is < 1 or > 32 || bits is < 8 or > 64 || bits % 8 != 0 || valid < 1 || valid > bits)
            throw new InvalidDataException("设备输出格式无效。");
        return new((int)rate, channels, bits, valid, floating);
    }

    public static string MixLabel(WaveFormat mix) => FromWaveFormat(mix).Label;

    public static DeviceAudioFormat FromWaveFormat(WaveFormat mix)
    {
        IntPtr pointer = WaveFormat.MarshalToPtr(mix);
        try
        {
            byte[] bytes = new byte[mix is WaveFormatExtensible ? 40 : 18];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return Parse(bytes);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
