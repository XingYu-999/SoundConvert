using System.Text;
using System.Text.Json;
using System.Diagnostics;
namespace SoundConvertClient;

public static class LocalStore
{
    public static string Root => Environment.GetEnvironmentVariable("UAB_UI_PREVIEW")=="1"&&Environment.GetEnvironmentVariable("UAB_PREVIEW_DATA") is string preview?Path.GetFullPath(preview):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbAudioBridge");
    static readonly object settingsGate = new();
    static readonly LogWriteTrace?[] logWrites=new LogWriteTrace[64];static long logSequence;
    public static object LogDiagnostics=>new{clock_frequency=Stopwatch.Frequency,writes=Enumerable.Range(0,logWrites.Length).Select(i=>Volatile.Read(ref logWrites[i])).Where(x=>x is not null).OrderBy(x=>x!.StartedTimestamp).ToArray()};
    public static void SaveSettings(Settings settings)
    {
        lock(settingsGate)
        {
            Directory.CreateDirectory(Root);
            string path=Path.Combine(Root,"settings.json"), temporary=path+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(settings),new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temporary,path,path+".bak",true);
            else File.Move(temporary,path);
        }
    }
    public static Settings LoadSettings(bool persistMigration=true)
    {
        string path=Path.Combine(Root,"settings.json");
        if(!File.Exists(path))return new(SchemaVersion:2);
        try
        {
            var settings=JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))??throw new JsonException("Empty settings");
            if(settings.SchemaVersion>=2)return settings with {MonitorIntervalSeconds=ValidMonitorInterval(settings.MonitorIntervalSeconds)};
            var target=settings.AutoConnectBoard??LoadBoards().Where(x=>x.LastConnected is not null).OrderByDescending(x=>x.LastConnected).FirstOrDefault();
            if(target is null&&!string.IsNullOrWhiteSpace(settings.DeviceId)&&!string.IsNullOrWhiteSpace(settings.Address))
                target=new(settings.DeviceId,"W5500-EVB-Pico2",settings.Address,settings.LocalAddress);
            var migrated=settings with {SchemaVersion=2,AutoConnectLastDevice=true,LastSuccessfulDevice=target,AutoConnectBoard=target,MonitorIntervalSeconds=3,AutoConnectMigrationNotice=true};
            if(persistMigration)
            {
                string backup=Path.Combine(Root,"settings.schema1.bak");if(!File.Exists(backup))File.Copy(path,backup);
                SaveSettings(migrated);
            }
            return migrated;
        }
        catch(JsonException){return new(SchemaVersion:2,AutoConnectMigrationNotice:true);}
        catch(IOException){return new(SchemaVersion:2,AutoConnectMigrationNotice:true);}
    }
    public static int ValidMonitorInterval(int seconds)=>seconds is 1 or 3 or 5 or 10?seconds:3;
    public static IReadOnlyList<RememberedBoard> LoadBoards()
    {
        try { return JsonSerializer.Deserialize<List<RememberedBoard>>(File.ReadAllText(Path.Combine(Root,"devices.json"))) ?? []; }
        catch { return []; }
    }
    public static void SaveBoards(IEnumerable<RememberedBoard> boards)
    {
        Directory.CreateDirectory(Root); string path=Path.Combine(Root,"devices.json"), temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(boards));File.Move(temporary,path,true);
    }
    public static void Log(string message)
    {
        bool diagnostics=Environment.GetEnvironmentVariable("UAB_ENGINE_DIAGNOSTICS")=="1";
        long started=diagnostics?Stopwatch.GetTimestamp():0;var sample=diagnostics?RuntimeExecutionSample.Capture():default;string? failure=null;
        try { Directory.CreateDirectory(Root); string p = Path.Combine(Root, "events.jsonl"); if (File.Exists(p) && new FileInfo(p).Length > 2_000_000) File.Move(p, p + ".1", true); File.AppendAllText(p, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, message }) + Environment.NewLine); }
        catch(IOException error){failure=error.Message;}
        finally
        {
            if(diagnostics)
            {
                long slot=Interlocked.Increment(ref logSequence)-1;
                Volatile.Write(ref logWrites[(int)(slot%logWrites.Length)],new(started,Stopwatch.GetTimestamp(),Environment.CurrentManagedThreadId,message.Length,sample,RuntimeExecutionSample.Capture(),failure));
            }
        }
    }
}
public sealed record LogWriteTrace(long StartedTimestamp,long CompletedTimestamp,int ThreadId,int MessageLength,RuntimeExecutionSample StartedSample,RuntimeExecutionSample CompletedSample,string? Error);
public sealed record Settings(string Address = "", string DeviceId = "", string LocalAddress = "0.0.0.0", string? OutputId = null, bool CloseToTray = true, bool StartInTray = false, bool StartAtLogin = false, double Volume = 100, bool Muted = false, string? PendingTransaction = null, string? PendingDevice = null,int MaximumBufferMs=20,string? PendingUsbRevision=null,[property:System.Text.Json.Serialization.JsonPropertyName("PendingL2Adapter")]string? PendingBroadcastAdapter=null,bool NavigationExpanded=true,bool MinimizeToTray=false,bool ConnectionAnnouncementEnabled=true,RememberedBoard? AutoConnectBoard=null,bool PendingRestartUsb=false,string? SearchAdapterId=null,string? SearchAdapterAddress=null,UsbAudioBridge.ResamplerQuality ResamplingQuality=UsbAudioBridge.ResamplerQuality.Balanced,bool NetworkFecEnabled=false,UsbAudioBridge.AudioTransportCodec TransportCodec=UsbAudioBridge.AudioTransportCodec.Pcm,string? InputId=null,bool MicrophonePlaybackFirst=true,double MicrophoneGain=100,bool MicrophoneMuted=false,UsbAudioBridge.AudioProcessingSettings? Processing=null,bool PendingResumeListening=false,bool PendingResumeMicrophone=false,int SchemaVersion=0,bool AutoConnectLastDevice=true,RememberedBoard? LastSuccessfulDevice=null,int MonitorIntervalSeconds=3,bool AutoConnectMigrationNotice=false,int ClockModeIndex=0,UsbAudioBridge.UdpPortSettings? UdpPorts=null,UsbAudioBridge.UdpPortHistory? LastAutomaticUdpPorts=null,bool AutoReconnectEnabled=true);




