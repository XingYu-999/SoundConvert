using System.Buffers.Binary;
using System.Security.Cryptography;
using UsbAudioBridge;

namespace UsbAudioBridge.Checks;

static class FirmwareUf2Checks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool good, string name) { if (!good) throw new Exception("UF2: " + name); count++; }
        void Reject(byte[] bytes, string name)
        {
            try { FirmwareUf2.Parse(bytes, "bad.uf2"); throw new Exception("Accepted " + name); }
            catch (InvalidDataException) { count++; }
        }
        byte[] valid = Image();
        var parsed = FirmwareUf2.Parse(valid, "fixture.uf2");
        Check(parsed.FileBytes == 1024 && parsed.PayloadBytes == 512 && parsed.AddressEnd == 0x10000200, "standard flash image");
        Check(parsed.Sha256 == Convert.ToHexString(SHA256.HashData(valid)), "hash exact selected bytes");
        Reject([], "empty"); Reject(valid[..^1], "partial block"); Reject(new byte[512], "wrong magic");
        foreach (int field in new[] { 0, 4, 508 }) { var b = valid.ToArray(); b[field] ^= 1; Reject(b, "damaged magic"); }
        foreach (uint family in new uint[] { 0xe48bff56, 0xe48bff57, 0xe48bff5a, 0xe48bff5b, 0 })
        { var b = valid.ToArray(); Put(b, 28, family); Reject(b, "wrong chip or unsupported family"); }
        foreach (uint flags in new uint[] { 0, 0x2001, 0x3000, 0x6000, 0xa000 })
        { var b = valid.ToArray(); Put(b, 8, flags); Reject(b, "unsupported flags"); }
        foreach (uint address in new uint[] { 0x0fffff00, 0x101fe000, 0x101fff00, 0x10200000, 0xffffff00, 0x10000001 })
        { var b = valid.ToArray(); Put(b, 12, address); Reject(b, "range, config or alignment"); }
        { var b = valid.ToArray(); Put(b, 16, 512); Reject(b, "oversized payload"); }
        { var b = valid.ToArray(); Put(b, 16, 0); Reject(b, "empty payload"); }
        { var b = valid.ToArray(); Put(b, 512 + 20, 0); Reject(b, "duplicate block number"); }
        { var b = valid.ToArray(); Put(b, 512 + 12, 0x10000000); Reject(b, "duplicate target address"); }
        { var b = valid.ToArray(); Put(b, 512 + 12, 0x10000200); Reject(b, "block number and target page mismatch"); }
        { var b = valid.ToArray(); Put(b, 24, 3); Reject(b, "missing block"); }
        { var b = valid.ToArray(); Put(b, 512 + 24, 1); Reject(b, "inconsistent block count"); }
        { var b = valid.ToArray(); Put(b, 20, 2); Reject(b, "out of range block number"); }
        { var b = valid.ToArray(); Put(b, 12, 0x10000200); Reject(b, "missing flash start"); }
        var compat = CompatibilityBlock().Concat(valid).ToArray();
        Check(FirmwareUf2.Parse(compat, "sdk.uf2").HasCompatibilityBlock, "Pico RP2350 ignored compatibility block with separate count");
        foreach (int field in new[] { 8, 12, 16, 20, 24, 32, 288, 292 })
        { var b = compat.ToArray(); b[field] ^= 1; Reject(b, "malformed compatibility block"); }
        Reject(valid.Concat(CompatibilityBlock()).ToArray(), "compatibility block misplaced");
        Reject(CompatibilityBlock(), "no actual firmware");
        Reject(CompatibilityBlock().Concat(compat).ToArray(), "duplicate compatibility block");

        var target = new BootselDrive("X:\\", "fixture-volume", "RP2350", "RP2350");
        var access = new MemoryDrive(); var progress = new SynchronousProgress();
        // Parse must freeze the bytes; mutating the caller's array cannot alter what is sent.
        valid.AsSpan().Clear();
        await new BootselFirmwareWriter(access).TransferAsync(parsed, target, progress);
        Check(access.Output!.Data.SequenceEqual(Image()), "send immutable validated bytes");
        Check(access.Output.Flushed && access.Output.Closed && progress.Values.Last() == 100, "finish only after flush and close");
        Check(progress.Values.SequenceEqual(progress.Values.Order()), "monotone progress");
        foreach (string failure in new[] { "open", "write", "flush", "close" })
        {
            access = new MemoryDrive { Failure = failure }; progress = new();
            try { await new BootselFirmwareWriter(access).TransferAsync(parsed, target, progress); throw new Exception("Accepted " + failure); }
            catch (IOException) { count++; }
            Check(!progress.Values.Contains(100), "no completion on " + failure + " failure");
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { stage = "firmware_uf2_checks", checks = count, scope = "UF2 validation and in-memory transfer fault injection; no physical drive or board writes" }));
        return count;
    }

    static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    static byte[] Image()
    {
        var bytes = new byte[1024];
        for (int i = 0; i < 2; i++)
        {
            int at = i * 512;
            Put(bytes, at, 0x0a324655); Put(bytes, at + 4, 0x9e5d5157); Put(bytes, at + 8, 0x2000);
            Put(bytes, at + 12, 0x10000000 + (uint)i * 256); Put(bytes, at + 16, 256);
            Put(bytes, at + 20, (uint)i); Put(bytes, at + 24, 2); Put(bytes, at + 28, FirmwareUf2.ArmSecureFamily);
            bytes.AsSpan(at + 32, 256).Fill((byte)(i + 1)); Put(bytes, at + 508, 0x0ab16f30);
        }
        return bytes;
    }
    static byte[] CompatibilityBlock()
    {
        byte[] bytes = Image()[..512]; Put(bytes, 8, 0xa000); Put(bytes, 12, 0x10ffff00); Put(bytes, 28, 0xe48bff57);
        bytes.AsSpan(32, 256).Fill(0xef); Put(bytes, 288, 0x9957e304); return bytes;
    }
    sealed class SynchronousProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }
    sealed class MemoryDrive : IBootselDriveAccess
    {
        public string Failure = "";
        public RecordingStream? Output;
        public Task<IReadOnlyList<BootselDrive>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BootselDrive>>([]);
        public Task<Stream> OpenFirmwareAsync(BootselDrive target) => Failure == "open"
            ? Task.FromException<Stream>(new IOException("detached fixture"))
            : Task.FromResult<Stream>(Output = new(Failure));
    }
    sealed class RecordingStream(string failure) : MemoryStream
    {
        public byte[] Data = []; public bool Flushed, Closed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => failure == "write"
            ? ValueTask.FromException(new IOException("fixture write failure")) : base.WriteAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken)
        { if (failure == "flush") throw new IOException("fixture flush failure"); Flushed = true; return Task.CompletedTask; }
        public override ValueTask DisposeAsync()
        {
            Data = ToArray(); Closed = true; base.Dispose();
            return failure == "close" ? ValueTask.FromException(new IOException("fixture close failure")) : ValueTask.CompletedTask;
        }
    }
}
