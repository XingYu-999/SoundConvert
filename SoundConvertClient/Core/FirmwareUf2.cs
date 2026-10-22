using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UsbAudioBridge;

/// <summary>A frozen, range-checked image for this board's 2 MiB flash layout.</summary>
public sealed class FirmwareUf2
{
    public const uint FlashStart = 0x10000000;
    public const uint ConfigStart = 0x101fe000;
    public const uint ArmSecureFamily = 0xe48bff59;
    public const int MaxFileBytes = 8 * 1024 * 1024;
    readonly byte[] bytes;
    public string FileName { get; }
    public int FileBytes => bytes.Length;
    public int PayloadBytes { get; }
    public uint AddressEnd { get; }
    public string Sha256 { get; }
    public bool HasCompatibilityBlock { get; }

    FirmwareUf2(string fileName, byte[] bytes, int blocks, uint end, bool compatibility)
    {
        FileName = fileName; this.bytes = bytes; PayloadBytes = blocks * 256;
        AddressEnd = end; HasCompatibilityBlock = compatibility;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static async Task<FirmwareUf2> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(path), ".uf2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .uf2 固件文件。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length is < 512 or > MaxFileBytes || input.Length % 512 != 0)
            throw new InvalidDataException("UF2 文件长度无效，文件可能不完整。");
        byte[] data = new byte[(int)input.Length];
        await input.ReadExactlyAsync(data, cancellationToken);
        return Parse(data, Path.GetFileName(path));
    }

    public static FirmwareUf2 Parse(ReadOnlySpan<byte> source, string fileName)
    {
        if (source.Length is < 512 or > MaxFileBytes || source.Length % 512 != 0)
            throw new InvalidDataException("UF2 文件长度无效，文件可能不完整。");
        // Keep the exact bytes that passed validation, even if the source file later changes.
        byte[] data = source.ToArray();
        var numbers = new HashSet<uint>(); var addresses = new HashSet<uint>();
        uint? expected = null; uint end = FlashStart; bool compatibility = false;
        for (int offset = 0; offset < data.Length; offset += 512)
        {
            var block = data.AsSpan(offset, 512);
            uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + at, 4));
            if (U(0) != 0x0a324655 || U(4) != 0x9e5d5157 || U(508) != 0x0ab16f30)
                throw new InvalidDataException($"第 {offset / 512 + 1} 个 UF2 块损坏。");
            uint flags = U(8), address = U(12), payload = U(16), number = U(20), count = U(24), family = U(28);
            // picotool's RP2350-E10 workaround: an ignored absolute-family block.
            // It has a separate block count and deliberately lies outside physical flash.
            if (family == 0xe48bff57 && offset == 0 && flags == 0xa000 && address == 0x10ffff00 &&
                payload == 256 && number == 0 && count == 2 &&
                block.Slice(32, 256).IndexOfAnyExcept((byte)0xef) < 0 &&
                U(288) == 0x9957e304 && block.Slice(292, 216).IndexOfAnyExcept((byte)0) < 0)
            { compatibility = true; continue; }
            if (family != ArmSecureFamily || flags != 0x2000)
                throw new InvalidDataException("仅支持本项目使用的 RP2350 Arm-S Flash UF2；不支持此芯片类型或 UF2 标志。");
            if (payload != 256 || address % 256 != 0 || address < FlashStart || (ulong)address + payload > ConfigStart)
                throw new InvalidDataException("镜像写入范围无效，可能超出 2 MiB Flash 或覆盖末尾 8 KiB 配置区。");
            if (count == 0 || count > data.Length / 512 || number >= count || !numbers.Add(number) || !addresses.Add(address))
                throw new InvalidDataException("UF2 块编号、总数或地址存在错误或重复。");
            // This project's flash image is contiguous from XIP base. Keep the UF2
            // block number aligned with the physical page used by ROM sector erasure.
            if ((ulong)address != FlashStart + (ulong)number * 256)
                throw new InvalidDataException("UF2 块编号与 Flash 页地址不匹配，不是本项目支持的连续固件镜像。");
            expected ??= count;
            if (expected != count) throw new InvalidDataException("UF2 各块的总数不一致。");
            end = Math.Max(end, address + payload);
        }
        if (expected is null || numbers.Count != expected || !addresses.Contains(FlashStart))
            throw new InvalidDataException("UF2 镜像缺少固件块或 Flash 起始块，文件可能不完整。");
        return new(fileName, data, numbers.Count, end, compatibility);
    }

    internal async Task CopyToAsync(Stream destination, IProgress<int>? progress)
    {
        for (int offset = 0; offset < bytes.Length; offset += 16384)
        {
            int size = Math.Min(16384, bytes.Length - offset);
            // Once transfer starts, finish it rather than offering a partial-flash cancellation.
            await destination.WriteAsync(bytes.AsMemory(offset, size));
            progress?.Report(Math.Min(99, (int)((offset + size) * 100L / bytes.Length)));
        }
        await destination.FlushAsync();
    }
}

public sealed record BootselDrive(string RootPath, string VolumeIdentity, string Label, string BoardId)
{
    public string DisplayName => $"{RootPath}  {Label} · {BoardId}";
}

public interface IBootselDriveAccess
{
    Task<IReadOnlyList<BootselDrive>> ScanAsync(CancellationToken cancellationToken = default);
    // Implementations must revalidate the chosen volume before creating a file.
    Task<Stream> OpenFirmwareAsync(BootselDrive target);
}

public sealed class BootselFirmwareWriter(IBootselDriveAccess drives)
{
    public async Task TransferAsync(FirmwareUf2 image, BootselDrive target, IProgress<int>? progress = null)
    {
        await using (var destination = await drives.OpenFirmwareAsync(target))
            await image.CopyToAsync(destination, progress);
        // 100 means all bytes, flush and handle close completed; it is not flash readback verification.
        progress?.Report(100);
    }
}
