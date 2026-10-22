using System.Text.Json;
using UsbAudioBridge;
namespace SoundConvertClient;
/// <summary>Opt-in hardware acceptance: temporary rename is restored; failed restoration is reported explicitly.</summary>
public static class Acceptance
{
    public static async Task RunAsync(string host, int port, Action<object> report)
    {
        ControlSession? c = null; string? original = null, originalPlayback = null; bool renamed = false, connected = false; bool initialRunning = false;
        async Task Open() { c = new(); await c.ConnectAsync(host, port); connected = true; }
        async Task<JsonElement> Info() => await c!.RequestAsync("GET_INFO");
        async Task State(bool start) { var i = await Info(); await c!.RequestAsync(start ? "USB_EMULATION_START" : "USB_EMULATION_STOP", new { expected_emulation_revision = i.GetProperty("usb_emulation").GetProperty("revision").GetString() }); for (int n = 0; n < 40; n++) { i = await Info(); if (i.GetProperty("usb_emulation").GetProperty("state").GetString() == (start ? "RUNNING" : "STOPPED")) return; await Task.Delay(100); } throw new TimeoutException("USB transition not completed"); }
        async Task Save(string name, string? playback = null) { var info = await Info(); string er = info.GetProperty("usb_emulation").GetProperty("revision").GetString()!; var cfg = await c!.RequestAsync("GET_CONFIG"); string rev = cfg.GetProperty("config_revision").GetString()!; var candidate = new { usb = new { product_name = name, playback_name = playback ?? name } }; await c.RequestAsync("VALIDATE_CONFIG", new { candidate, expected_emulation_revision = er }, rev); var prepared = await c.RequestAsync("PREPARE_CONFIG", new { candidate, expected_emulation_revision = er }, rev); string tid = prepared.GetProperty("transaction_id").GetString()!; await c.RequestAsync("APPLY_CONFIG", new { transaction_id = tid, expected_emulation_revision = er }); await c.RequestAsync("CONFIRM_CONFIG", new { transaction_id = tid, expected_emulation_revision = er }); }
        try
        {
            await Open(); var info = await Info(); initialRunning = info.GetProperty("usb_emulation").GetProperty("state").GetString() == "RUNNING"; await State(true); var cfg = await c!.RequestAsync("GET_CONFIG"); original = cfg.GetProperty("usb").GetProperty("product_name").GetString()!; originalPlayback = cfg.GetProperty("usb").GetProperty("playback_name").GetString()!; string oldRevision = cfg.GetProperty("config_revision").GetString()!;
            try { await Save("UAB-Acceptance"); throw new InvalidOperationException("RUNNING USB configuration was unexpectedly accepted"); } catch (DeviceException e) when (e.Code == "USB_EMULATION_NOT_STOPPED") { report(new { ok = true, check = "running_rejects_usb_config" }); }
            cfg = await c.RequestAsync("GET_CONFIG"); if (cfg.GetProperty("config_revision").GetString() != oldRevision) throw new InvalidOperationException("Rejected write changed revision");
            await State(false); report(new { ok = true, check = "usb_stop_completed" }); await c.DisposeAsync(); await Open(); info = await Info(); if (info.GetProperty("usb_emulation").GetProperty("state").GetString() != "STOPPED") throw new InvalidOperationException("Reconnect restarted USB"); report(new { ok = true, check = "reconnect_remains_stopped" });
            await Save("UAB-Acceptance"); renamed = true; cfg = await c!.RequestAsync("GET_CONFIG"); if (cfg.GetProperty("usb").GetProperty("product_name").GetString() != "UAB-Acceptance") throw new InvalidOperationException("Name commit readback mismatch"); info = await Info(); if (info.GetProperty("usb_emulation").GetProperty("state").GetString() != "STOPPED") throw new InvalidOperationException("Commit implicitly started USB"); report(new { ok = true, check = "stopped_name_transaction_readback" }); await State(true); report(new { ok = true, check = "explicit_start_completed", note = "Host descriptor refresh must be verified separately" });
        }
        finally
        {
            if (c is not null && connected) { try { if (renamed && original is not null) { await State(false); await Save(original, originalPlayback); report(new { ok = true, check = "original_name_restored" }); } await State(initialRunning); report(new { ok = true, check = "initial_usb_state_restored" }); } catch (Exception e) { report(new { ok = false, check = "restoration_failed", message = e.Message }); throw; } finally { await c.DisposeAsync(); } }
        }
    }
}

