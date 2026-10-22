using System.Diagnostics;
using System.Text.Json;
namespace UsbAudioBridge.Checks;

public static class AudioExtensionBenchmark
{
    public static void Run(string output)
    {
        var results = new List<object>();
        foreach (int rate in new[] { 48000, 96000, 192000 })
        foreach (bool enabled in new[] { false, true })
        {
            var processor = new AudioPostProcessor();
            processor.Reset(rate, 1);
            processor.SetTargetSettings(new(Enabled: enabled, Balance: .2, Mono: true,
                EqEnabled: true, LowDb: 3, MidDb: -3, HighDb: 3, LimiterEnabled: true));
            float[] block = new float[rate / 100 * 2];
            for (int i = 0; i < block.Length; i++) block[i] = (float)Math.Sin(i * .13) * .5f;
            var meter = new AudioLevelMeter(); meter.Reset(1, rate, 24);
            for (int i = 0; i < 1000; i++) { processor.Process(block); meter.ObserveInterleaved(block, 2); }
            const int iterations = 3000;
            long bytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) { processor.Process(block); meter.ObserveInterleaved(block, 2); }
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            if (allocated != 0) throw new InvalidOperationException($"Steady audio processing allocated {allocated} bytes at {rate} Hz, enabled={enabled}");
            results.Add(new { rate, processing_enabled = enabled, iterations, block_frames = block.Length / 2,
                wall_ms = elapsed, audio_ms = iterations * 10, realtime_percent = elapsed / (iterations * 10) * 100,
                allocated_bytes = allocated, added_buffer_frames = processor.LatencyFrames });
        }
        string path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { ok = true,
            scope = "Offline DSP plus source meter, warmed thread, 10 ms blocks; wall compute cost, not whole process CPU or physical audio latency",
            runtime = Environment.Version.ToString(), tiered_compilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), cpu_count = Environment.ProcessorCount, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Audio extension benchmark: " + path);
    }
}
