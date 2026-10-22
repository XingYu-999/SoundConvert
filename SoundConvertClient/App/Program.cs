using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using UsbAudioBridge;
namespace SoundConvertClient;

internal static class Program
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(uint processId);

    [STAThread] public static int Main(string[] args)
    {
        if (args.Length > 0 && !args.Contains("--tray"))
        {
            // Reuse an existing terminal for CLI output; never allocate a console for GUI startup.
            // Windows preserves redirected standard handles supplied by the parent process.
            if (OperatingSystem.IsWindows()) AttachConsole(uint.MaxValue);
            return Cli.RunAsync(args).GetAwaiter().GetResult();
        }
        using var instance = new SingleInstance();
        if (!instance.IsPrimary)
        {
            bool activated = instance.ActivatePrimary();
            if (!activated) Console.Error.WriteLine("Existing instance could not be activated; no second engine started.");
            return activated ? 0 : 3;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
public static class Cli
{
    static bool commandIsAcceptance(string[] args) => args.Contains("--acceptance");
    static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    public static async Task<int> RunAsync(string[] args)
    {
        string? Value(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int Seconds() => Math.Clamp(int.Parse(Value("--seconds") ?? "5"), 1, 86400);
        try
        {
            if(args.Contains("--version")){Print(new{ok=true,software_version="V"+typeof(App).Assembly.GetName().Version?.ToString(3)});return 0;}
            if(args.Any(x=>x is "--udp-apply" or "--udp-confirm" or "--udp-rollback"))return await CliBroadcast.RunAsync(args,Print);
            if (args.Contains("--audio-devices")) { Print(new { ok = true, endpoints = WindowsAudio.Endpoints() }); return 0; }
            if (Value("--usb-test-tone") is string endpoint) { AudioFormat? source = Value("--source-format") switch { null => null, "16/48" => AudioFormat.Pcm16, "24/96" => AudioFormat.Pcm24, _ => throw new ArgumentException("--source-format must be 16/48 or 24/96; specifying it requests exclusive WASAPI.") }; Print(new { ok = true, stage = "tone_starting", endpoint, seconds = Seconds(), left_hz = 440, right_hz = 880, requested_source_format = source, mode = source is null ? "shared" : "exclusive" }); await WindowsAudio.ToneAsync(endpoint, Seconds(), source, Print); Print(new { ok = true, stage = "tone_completed" }); return 0; }
            if(args.Contains("--discover-udp")){Print(new{ok=true,transport="udp_broadcast",devices=(await UdpDiscovery.DiscoverPeersAsync(adapterId:Value("--local"))).Select(p=>new{p.Device,p.AdapterName,p.LocalIpv4,boot_id=p.BootId.ToString(),config_revision=p.ConfigRevision.ToString()})});return 0;}
            if (args.Contains("discover")) { Print(new { ok = true, devices = await DiscoveryService.DiscoverAsync(Value("--local")) }); return 0; }
            if (args.Contains("--help")) { Console.WriteLine("--version\nfirmware --host IPv4\ndiscover [--local IPv4]\n--discover-l2\n--udp-apply --device-id HEX --mode dhcp|static [--ip IPv4 --prefix 24 --gateway IPv4] [--adapter NAME]\n--udp-confirm|--udp-rollback --device-id HEX --transaction-id HEX [--adapter NAME]\n--acceptance --host IPv4 (temporary name test; restores initial name/state)\ninfo|connect|start|stop|stats|config|receive --host IPv4 [--port 49161] [--seconds 5] [--local IPv4] [--receive-port 49162] [--play] [--output ENDPOINT]\n--monitor-check --host IPv4 --output ENDPOINT --seconds 10 (WASAPI loopback metrics; no audio saved)\n--audio-devices\n--usb-test-tone ENDPOINT --seconds 5\nstart/stop control USB emulation; receive prepares/probes/starts/stops network audio. Devices connect directly without a pairing code or file."); return 0; }
            string host = Value("--host") ?? throw new ArgumentException("--host required");
            if (commandIsAcceptance(args)) { await Acceptance.RunAsync(host, int.Parse(Value("--port") ?? "49161"), Print); return 0; }
            await using var c = new ControlSession(); await c.ConnectAsync(host, int.Parse(Value("--port") ?? "49161"));
            Print(new { ok = true, stage = "connected", device_id = c.DeviceId, boot_id = c.Boot.ToString() });
            string command = args[0]; if (command is "connect" or "info") { Print(new { ok = true, op = "GET_INFO", data = await c.RequestAsync("GET_INFO") }); Print(new { ok = true, op = "GET_CAPABILITIES", data = await c.RequestAsync("GET_CAPABILITIES") }); }
            else if (command is "start" or "stop") { var info = await c.RequestAsync("GET_INFO"); string revision = info.GetProperty("usb_emulation").GetProperty("revision").GetString()!; Print(new { ok = true, op = command, data = await c.RequestAsync(command == "start" ? "USB_EMULATION_START" : "USB_EMULATION_STOP", new { expected_emulation_revision = revision }) }); }
            else if(command=="firmware") Print(new{ok=true,op="GET_FIRMWARE_VERSION",data=await c.RequestAsync("GET_FIRMWARE_VERSION")});
            else if (command is "stats" or "config") Print(new { ok = true, data = await c.RequestAsync(command == "stats" ? "GET_STATS" : "GET_CONFIG") });
            else if (command is "receive" or "--monitor-check")
            {
                bool monitor=command=="--monitor-check";if(monitor&&Value("--output") is null)throw new ArgumentException("--monitor-check requires an explicit --output endpoint");using var audio = args.Contains("--play")||monitor ? new WindowsAudio(Value("--output"),int.Parse(Value("--max-buffer-ms")??"20")) : null;using var capture=monitor?new MonitorCapture(Value("--output")!):null;capture?.Start(); await using var r = new AudioReceiver(c, Value("--local") ?? "0.0.0.0", int.Parse(Value("--receive-port") ?? "49162")); if (audio is not null) r.Packet += audio.Buffer.Add;
                if(audio is not null)await audio.StartAndWarmUpAsync();await r.StartAsync(); Print(new { ok = true, stage = "stream_started", stream_id = r.Descriptor!.StreamId, epoch = r.Descriptor.Epoch }); await Task.Delay(TimeSpan.FromSeconds(Seconds())); Print(new { ok = r.Accepted > 0 && r.Fault is null && (capture is null||capture.NonzeroSamples>0) && (!monitor||audio!.ContinuityAccepted&&capture!.ToneQualityAccepted), stage = "received", stats = r.Statistics(), output_measurement=capture?.Statistics(),output_backend=audio?.OutputDiagnostics(), device_stats = await c.RequestAsync("GET_STATS") }); if (r.Accepted == 0 || r.Fault is not null || capture is not null && capture.NonzeroSamples==0 || monitor&&(!audio!.ContinuityAccepted||!capture!.ToneQualityAccepted)) return 4;
            }
            else throw new ArgumentException("Unknown command; use --help"); return 0;
        }
        catch (Exception ex) { Print(new { ok = false, error = ex is DeviceException de ? de.Code : ex.GetType().Name, message = ex.Message }); return 1; }
    }
}












