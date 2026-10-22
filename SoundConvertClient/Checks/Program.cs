using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UsbAudioBridge;
if(args.FirstOrDefault()=="--audio-extension-benchmark") { UsbAudioBridge.Checks.AudioExtensionBenchmark.Run(args[1]); return; }
int count = 0; void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; }
void Reject(Action action, string name) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException) { count++; return; } throw new Exception("Expected rejection: " + name); }
var descriptor = new StreamDescriptor(0x12345678, 0x87654321, 0xFEDCBA9876543210, 49162, 49162);
byte[] Audio(ulong index = 0, uint sequence = 0) { byte[] b = new byte[240]; "UABA"u8.CopyTo(b); b[4] = 1; b[5] = 48; Wire.Write32(b, 8, descriptor.StreamId); Wire.Write32(b, 12, descriptor.Epoch); Wire.Write32(b, 16, sequence); Wire.Write64(b, 20, index); Wire.Write32(b, 28, 48000); Wire.Write16(b, 32, 48); b[34] = 2; b[35] = 1; Wire.Write16(b, 36, 192); Wire.Write64(b, 40, descriptor.Token); for (int i = 48; i < 240; i += 4) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i), short.MaxValue); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i + 2), short.MinValue); } return b; }
using (var decimalId = System.Text.Json.JsonDocument.Parse("2.066593195e9")) Check(Wire.JsonUInt32(decimalId.RootElement) == 2066593195, "scientific uint32"); using (var fractionalId = System.Text.Json.JsonDocument.Parse("1.5")) Reject(() => Wire.JsonUInt32(fractionalId.RootElement), "fractional uint32"); using (var overflowId = System.Text.Json.JsonDocument.Parse("4294967296")) Reject(() => Wire.JsonUInt32(overflowId.RootElement), "overflow uint32");
var good = Audio(); var p = AudioCodec.Parse(good, descriptor); Check(p.Frames == 48 && p.Pcm.Length == 192, "audio length"); Check(AudioCodec.Parse(Audio(1234567890123, 0xffffffff), descriptor).SampleIndex == 1234567890123, "64 bit index"); Check(AudioCodec.Parse(Audio(48, 0), descriptor).Sequence == 0, "sequence wrap"); Check(AudioCodec.Parse(AudioCodec.Probe(descriptor), descriptor).Flags == 8, "probe"); Reject(() => AudioCodec.Parse(good[..^1], descriptor), "truncated"); Reject(() => AudioCodec.Parse([.. good, 0], descriptor), "padding"); Reject(() => AudioCodec.Parse(good, descriptor with { Epoch = 1 }), "stale epoch"); Reject(() => AudioCodec.Parse(good, descriptor with { Token = 1 }), "stale token"); var x = Audio(); x[35] = 2; Reject(() => AudioCodec.Parse(x, descriptor), "format"); x = Audio(); x[38] = 1; Reject(() => AudioCodec.Parse(x, descriptor), "reserved"); x = Audio(); x[7] = 16; Reject(() => AudioCodec.Parse(x, descriptor), "unknown flags"); x = Audio(); x[7] = 2; Reject(() => AudioCodec.Parse(x, descriptor), "silence nonzero"); Reject(() => AudioCodec.Parse(Audio(ulong.MaxValue - 1), descriptor), "sample overflow"); x = AudioCodec.Probe(descriptor); x[7] = 12; Reject(() => AudioCodec.Parse(x, descriptor), "probe end");
Wire.ValidateJson("{\"中文\":\"开发板\"}"u8.ToArray()); count++; Reject(() => Wire.ValidateJson("{\"a\":1,\"a\":2}"u8.ToArray()), "duplicate key"); Reject(() => Wire.ValidateJson("[]"u8.ToArray()), "object"); Reject(() => Wire.ValidateJson([0xff]), "utf8");
var req = Wire.Control(1, 10, 1, 123, "{\"op\":\"GET_INFO\",\"args\":{}}"u8.ToArray());
Check(Wire.Read64(req, 16) == 1 && Wire.Read32(req, 12) == 10 && Wire.Read16(req, 6) == 0, "Direct control frame fields");
var response = Wire.Control(2, 10, 1, 123, "{\"ok\":true,\"data\":{}}"u8.ToArray());
using (var stream = new FragmentStream(response)) { var r = await Wire.ReadAsync(stream, default); Check(r.RequestId == 10 && r.Sequence == 1, "Fragmented direct control stream"); }
try { var legacy = response.ToArray(); Wire.Write16(legacy, 6, 1); await Wire.ReadAsync(new MemoryStream(legacy), default); throw new Exception("Expected legacy tagged frame rejection"); } catch (InvalidDataException) { count++; }
try { await Wire.ReadAsync(new MemoryStream(response[..^1]), default); throw new Exception("Expected partial EOF rejection"); } catch (EndOfStreamException) { count++; }
var buffer = new AudioBuffer(20,UsbAudioBridge.Checks.PlaybackTimingChecks.FrozenTime); for (int i = 0; i < 6; i++) buffer.Add(AudioCodec.Parse(Audio((ulong)(10000 + i * 48), (uint)i), descriptor)); float[] output = new float[96]; buffer.Read(output, 0, 96); Check(output[94] > 0 && output[95] < 0, "stereo signs"); Check(output.All(v => Math.Abs(v) <= 1), "amplitude bounded"); buffer.Add(AudioCodec.Parse(Audio(10000), descriptor)); Check(buffer.Late > 0, "late rejected"); buffer.Add(AudioCodec.Parse(Audio(10240), descriptor)); Check(buffer.Duplicates > 0, "overlap deduplicated"); buffer.Mute = true; for (int i = 0; i < 8; i++) buffer.Read(output, 0, 96); Check(output.All(v => v == 0), "mute ramp"); buffer.Add(AudioCodec.Parse(Audio(999999), descriptor)); Check(buffer.Overflows == 1 && buffer.BufferedMs <= 1, "future bounded reset");
var discover = DiscoveryCodec.Request(0x123456789abcdef0); Check(discover.Length == 56 && Wire.Read64(discover, 12) == 0x123456789abcdef0, "discovery endian"); Reject(() => DiscoveryCodec.Parse(discover, 0x123456789abcdef0, "127.0.0.1"), "reject request as response");
foreach(double phase in new[]{0d,.25,.5,.75}){var coeff=SincFilter.Phase(phase).ToArray();Check(Math.Abs(coeff.Sum(x=>(double)x)-1)<1e-6,"FIR DC unity");foreach(double hz in new[]{440d,880d,1000d,10000d,18000d}){double real=0,imag=0;for(int tap=0;tap<coeff.Length;tap++){real+=coeff[tap]*Math.Cos(2*Math.PI*hz*tap/48000);imag+=coeff[tap]*Math.Sin(2*Math.PI*hz*tap/48000);}Check(Math.Abs(Math.Sqrt(real*real+imag*imag)-1)<.01,"FIR passband "+hz);}double nyquist=0;for(int tap=0;tap<coeff.Length;tap++)nyquist+=coeff[tap]*(tap%2==0?1:-1);Check(Math.Abs(nyquist)<.15,"FIR Nyquist attenuation");}
var bounded=new AudioBuffer(20,UsbAudioBridge.Checks.PlaybackTimingChecks.FrozenTime);for(int i=0;i<4;i++)bounded.Add(AudioCodec.Parse(Audio((ulong)(i*48)),descriptor));bounded.Read(new float[2000],0,2000);Check(bounded.OutputDemandTooLarge,"output demand limit is explicit");Check(bounded.OversizedReadCallbacks==1,"oversized callback counted");bounded.Read(new float[96],0,96);Check(!bounded.OutputDemandTooLarge&&bounded.CurrentReadFrames==48&&bounded.LargestReadFrames==1000,"oversized callback is not a permanent alarm");
foreach(double sourcePpm in new[]{-300d,-100d,100d,300d}){var controller=new DriftController();double errorFrames=0,maxError=0;for(int step=0;step<120000;step++){double adjustment=controller.Update(errorFrames,480);errorFrames+=(sourcePpm-adjustment)*480/1000000;maxError=Math.Max(maxError,Math.Abs(errorFrames));}Check(Math.Abs(controller.Ppm-sourcePpm)<1,"drift model converges "+sourcePpm);Check(Math.Abs(errorFrames)<.25&&maxError<150,"drift model bounded "+sourcePpm);}
count+=await UsbAudioBridge.Checks.MicrophoneHotPathChecks.RunAsync();count+=await UsbAudioBridge.Checks.FormatAndAutoConnectChecks.RunAsync();count+=await UsbAudioBridge.Checks.ControlConnectionChecks.RunAsync();count+=await UsbAudioBridge.Checks.AudioThreadingChecks.RunAsync();count+=UsbAudioBridge.Checks.PlaybackTimingChecks.Run();count+=UsbAudioBridge.Checks.PlaybackMarginChecks.Run();count+=UsbAudioBridge.Checks.AdvancedAudioChecks.Run();
count+=UsbAudioBridge.Checks.AudioExtensionChecks.Run();count+=UsbAudioBridge.Checks.AudioUpgradeChecks.Run();
count+=UsbAudioBridge.Checks.AudioFecChecks.Run();
count+=UsbAudioBridge.Checks.ImaAdpcmChecks.Run();
count+=UsbAudioBridge.Checks.RtpPcmChecks.Run();
count+=await UsbAudioBridge.Checks.CaptureChecks.RunAsync();
count+=await UsbAudioBridge.Checks.FirmwareUf2Checks.RunAsync();
count+=await UsbAudioBridge.Checks.UdpPortChecks.RunAsync();
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ok = true, checks = count, scope = "offline protocol and audio; not hardware acceptance", threading_regression=UsbAudioBridge.Checks.AudioThreadingChecks.LastResult }));
sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes) { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], ct); }










