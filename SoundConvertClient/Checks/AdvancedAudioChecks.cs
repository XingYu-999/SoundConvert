using System.Buffers.Binary;
using System.Text.Json;
namespace UsbAudioBridge.Checks;

static class AdvancedAudioChecks
{
    sealed class Clock : TimeProvider
    {
        long now = 1;
        public override long TimestampFrequency => 1000000;
        public override long GetTimestamp() => now;
        public void At(int ms) => now = 1 + ms * 1000L;
    }
    static AudioPacket Packet(AudioFormat format, ulong index, double hz = 1000, double amplitude = .5, ulong timeline = 1, int? frameCount = null)
    {
        int frames=frameCount??format.PacketFrames;
        byte[] pcm = new byte[frames * format.BytesPerFrame];
        for (int frame = 0; frame < frames; frame++)
        {
            double sample = hz == 0 ? amplitude : amplitude * Math.Sin(2 * Math.PI * hz * (index + (ulong)frame) / format.SampleRate);
            int scale = format.Bits == 24 ? 8388607 : 32767;
            for (int channel = 0; channel < 2; channel++)
            {
                int value = (int)(sample * scale) * (channel == 0 ? 1 : -1), offset = frame * format.BytesPerFrame + channel * format.BytesPerSample;
                pcm[offset] = (byte)value; pcm[offset + 1] = (byte)(value >> 8);
                if (format.Bits == 24) pcm[offset + 2] = (byte)(value >> 16);
            }
        }
        return new(0, (uint)(index / (uint)format.PacketFrames), index, (ushort)frames, pcm, format, timeline);
    }
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string name) { count++; if (!value) throw new Exception(name); }
        foreach (int value in new[] { -8388608, -1, 0, 1, 8388607 })
        {
            byte[] bytes = [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];
            Check(PcmCodec.ReadSigned(bytes, 24) == value, "S24 sign extension " + value);
            Check(Math.Abs(PcmCodec.ReadFloat(bytes, 24) - value / 8388608d) < 1e-7, "S24 normalization " + value);
        }
        var descriptor = new StreamDescriptor(1, 2, 3, 49162, 49162, AudioFormat.Pcm24, 96);
        var raw = Packet(AudioFormat.Pcm24, 0);
        byte[] wire = new byte[48 + raw.Pcm.Length]; "UABA"u8.CopyTo(wire); wire[4] = 1; wire[5] = 48;
        Wire.Write32(wire, 8, 1); Wire.Write32(wire, 12, 2); Wire.Write32(wire, 28, 96000); Wire.Write16(wire, 32, 96);
        wire[34] = 2; wire[35] = 2; Wire.Write16(wire, 36, 576); Wire.Write64(wire, 40, 3); raw.Pcm.CopyTo(wire, 48);
        Check(AudioCodec.Parse(wire, descriptor).Format == AudioFormat.Pcm24, "96k packet negotiated");
        try { AudioCodec.Parse(wire, descriptor with { NegotiatedFormat = AudioFormat.Pcm16, PacketFrames = 48 }); throw new Exception("Old-format packet accepted"); }
        catch (InvalidDataException) { count++; }

        var following = descriptor with { FollowUsb = true, Epoch = 1, NegotiatedFormat = AudioFormat.Pcm16, PacketFrames = 48 };
        Check(AudioCodec.Parse(wire, following).Format == AudioFormat.Pcm24, "new USB generation carries its actual format without control polling");
        Check(AudioCodec.Parse(wire, following).TimelineId != AudioCodec.Parse(wire, following with { FollowUsb = false, Epoch = 2, NegotiatedFormat = AudioFormat.Pcm24, PacketFrames = 96 }).TimelineId, "persistent source timeline includes generation");
        foreach(var invalid in new[]{following with{Token=4},following with{StreamId=2},following with{Epoch=3},following with{Epoch=2}})
        {
            try{AudioCodec.Parse(wire,invalid);throw new Exception("Invalid persistent source packet accepted");}
            catch(InvalidDataException){count++;}
        }
        Wire.Write32(wire,12,0);
        Check(AudioCodec.Parse(wire,following with{Epoch=uint.MaxValue}).Format==AudioFormat.Pcm24,"USB generation wrap advances safely");
        Wire.Write32(wire,28,uint.MaxValue);
        try{AudioCodec.Parse(wire,following with{Epoch=uint.MaxValue});throw new Exception("Invalid persistent rate accepted");}
        catch(InvalidDataException){count++;}
        Wire.Write32(wire,28,96000);Wire.Write32(wire,12,2);

        var results = new List<object>();
        foreach (var format in new[] { AudioFormat.Pcm16, AudioFormat.Pcm24 })
        foreach (int outputRate in new[] { 44100, 48000, 96000 })
        {
            var clock = new Clock(); var buffer = new AudioBuffer(40, clock, outputRate);
            float[] output = new float[outputRate / 100 * 2]; double energy = 0; long rendered = 0, crossings = 0; float previous = 0;
            for (int ms = 0; ms < 1200; ms++)
            {
                clock.At(ms); buffer.Add(Packet(format, (ulong)(ms * format.PacketFrames)));
                if (ms % 10 != 0) continue;
                buffer.Read(output, 0, output.Length);
                if (ms < 100) continue;
                for (int frame = 0; frame < output.Length / 2; frame++)
                { float value = output[frame * 2]; energy += value * value; if (previous <= 0 && value > 0) crossings++; previous = value; rendered++; }
            }
            double rms = Math.Sqrt(energy / rendered), measuredHz = crossings * outputRate / (double)rendered;
            Check(buffer.OutputUnderruns == 0 && buffer.ConcealedSourceFrames == 0, "steady rate conversion has no missing frames");
            Check(Math.Abs(rms - .5 / Math.Sqrt(2)) < .01 && Math.Abs(measuredHz - 1000) < 3, "real source/output ASRC ratio");
            Check(buffer.BufferedMs <= 40 && buffer.SourceFormat == format, "96k capacity remains milliseconds bounded");
            results.Add(new { source = format, output_rate = outputRate, rms, measured_hz = measuredHz, max_step = buffer.MaximumOutputStep });
        }
        // Exercise integer and fractional upsampling with real floor/ceil USB packet sizes.
        foreach(int outputRate in new[]{44100,48000,96000,192000})
        foreach(var format in AudioFormat.Supported.Where(f=>f.SampleRate<=outputRate))
        {
            var clock=new Clock();var buffer=new AudioBuffer(40,clock,outputRate);
            float[] output=new float[outputRate/100*2];double energy=0,stereoError=0;long framesRendered=0,crossings=0;float previous=0;ulong index=0;
            for(int ms=0;ms<1200;ms++)
            {
                int frames=(ms+1)*format.SampleRate/1000-ms*format.SampleRate/1000;
                clock.At(ms);buffer.Add(Packet(format,index,frameCount:frames));index+=(uint)frames;
                if(ms%10!=0)continue;buffer.Read(output,0,output.Length);if(ms<100)continue;
                for(int frame=0;frame<output.Length/2;frame++)
                {
                    float value=output[frame*2];energy+=value*value;stereoError+=Math.Abs(value+output[frame*2+1]);
                    if(previous<=0&&value>0)crossings++;previous=value;framesRendered++;
                }
            }
            double rms=Math.Sqrt(energy/framesRendered),hz=crossings*outputRate/(double)framesRendered;
            Check(buffer.OutputUnderruns==0&&buffer.MissingFrames==0,$"continuous conversion {format.Label} -> 24/{outputRate}");
            Check(Math.Abs(rms-.5/Math.Sqrt(2))<.01&&Math.Abs(hz-1000)<3&&stereoError/framesRendered<.0001,$"frequency, level and stereo preserved {format.Label} -> 24/{outputRate}");
            results.Add(new{conversion_source=format,output_rate=outputRate,rms,measured_hz=hz});
        }
        // Downsampling remains an engine capability, separate from the UI's no-reduction policy.
        {
            var clock = new Clock(); var buffer = new AudioBuffer(40, clock, 48000); var output = new float[960]; double energy = 0; long samples = 0;
            for (int ms = 0; ms < 300; ms++)
            { clock.At(ms); buffer.Add(Packet(AudioFormat.Pcm24, (ulong)(ms * 96), 35000)); if (ms % 10 != 0) continue; buffer.Read(output, 0, output.Length); if (ms >= 100) foreach (float sample in output) { energy += sample * sample; samples++; } }
            Check(Math.Sqrt(energy / samples) < .002, "96-to-48 anti-alias rejection at 35 kHz");
        }
        foreach (var format in new[] { AudioFormat.Pcm16, AudioFormat.Pcm24 })
        {
            var clock = new Clock(); var buffer = new AudioBuffer(20, clock, 48000); var output = new float[960]; AudioPacket? held = null;
            for (int ms = 0; ms < 400; ms++)
            {
                clock.At(ms);
                var packet = Packet(format, (ulong)(ms * format.PacketFrames), 0);
                if (ms == 240) held = packet;
                else if (ms != 100 && ms is not (>= 180 and < 183)) { buffer.Add(packet); if (ms == 241) buffer.Add(held!); }
                if (ms % 10 == 0) buffer.Read(output, 0, output.Length);
            }
            Check(buffer.ConcealedSourceFrames == 4 * format.PacketFrames, "Only the injected 1ms and 3ms holes are concealed");
            Check(buffer.PermanentGapSourceFrames==4*format.PacketFrames,"committed missing source frames retain source-rate units");
            Check(buffer.MaximumOutputStep < .03 && buffer.OutputStepsOverQuarterScale == 0, "PLC removes the former half-scale zero-fill click");
            Check(buffer.ConflictingOverlapFrames == 0 && buffer.MissingFrames > 0, "Timely reordered packet remains eligible");
            results.Add(new { injected_gap_format = format, plc_frames = buffer.ConcealedSourceFrames, max_step = buffer.MaximumOutputStep, max_second_difference = buffer.MaximumOutputSecondDifference });
        }
        {
            var clock = new Clock(); var buffer = new AudioBuffer(20, clock); var output = new float[960];
            for (int ms = 0; ms < 700; ms++)
            {
                clock.At(ms);
                if (ms is not (>= 200 and < 270))
                {
                    bool hi = ms >= 400; var format = hi ? AudioFormat.Pcm24 : AudioFormat.Pcm16;
                    buffer.Add(Packet(format, (ulong)((hi ? ms - 400 : ms) * format.PacketFrames), 0, hi ? -.5 : .5, hi ? 2u : 1u));
                }
                if (ms == 500) buffer.Mute = true; if (ms == 550) buffer.Mute = false; if (ms == 600) buffer.Gain = .2f;
                if (ms % 10 == 0) buffer.Read(output, 0, output.Length);
            }
            Check(buffer.OutputUnderruns > 0 && buffer.FormatChanges == 1, "Timeout and format changes exercised");
            Check(buffer.MaximumOutputStep < .03 && buffer.BoundaryFadeFrames > 0, "Underrun/format/mute/gain boundaries fade without a hard edge");
            Check(Math.Abs(output[^2] + .1) < .002 && Math.Abs(output[^1] - .1) < .002, "24-bit stream recovers with current monitor gain");
            results.Add(new { transitions_max_step = buffer.MaximumOutputStep, transitions_max_second_difference = buffer.MaximumOutputSecondDifference, format_changes = buffer.FormatChanges });
        }
        foreach (var format in new[] { AudioFormat.Pcm16, AudioFormat.Pcm24 })
        {
            var clock = new Clock(); var buffer = new AudioBuffer(100, clock, 48000); var output = new float[960];
            clock.At(0);
            for (int packet = 0; packet < 80; packet++) buffer.Add(Packet(format, (ulong)(packet * format.PacketFrames), 0));
            // No new packets arrive for 60 ms, but seven 10 ms callbacks are
            // covered by the 76ms target plus one-packet startup allowance.
            for (int ms = 0; ms <= 60; ms += 10) { clock.At(ms); buffer.Read(output, 0, output.Length); }
            Check(buffer.OutputUnderruns == 0 && buffer.RebufferCount == 0 && buffer.RenderedFrames == 3360,
                "60ms delivery silence must not mute sufficiently cached audio");
            // Startup may explicitly reserve one delivery batch, but all
            // seven covered callbacks must still render audible samples.
            double trimmedMs=buffer.PrefillTrimmedSourceFrames*1000d/format.SampleRate;
            Check(output[0] > .49 && output[1] < -.49 && buffer.MaximumBufferMs == 100 && buffer.BufferedMs+trimmedMs > 9.9 && trimmedMs<=3.01,
                "cached playback stays audible without increasing its configured capacity");
            clock.At(70); buffer.Read(output, 0, output.Length);
            Check(buffer.OutputUnderruns == 1 && buffer.UnderrunEvents.Single().Reason == "insufficient_frontier",
                "only the exhausted real source frontier starts rebuffering");
            results.Add(new { cached_gap_format = format, no_packet_ms = 60, capacity_ms = buffer.MaximumBufferMs,
                rendered_before_frontier = 3360, underrun_reason = buffer.UnderrunEvents.Single().Reason });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "offline_audio_quality", results }));
        return count;
    }
}
