using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoundConvertClient;

public sealed record EngineRequest(string Operation, string? Name = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] JsonElement Value = default,
    Dictionary<string, JsonElement>? Changes = null, string? OperationId = null, string? EngineInstanceId = null, long CreatedTimestamp = 0);
public sealed record EngineResponse(bool Ok, string? Error = null, EngineSnapshot? Snapshot = null, string? Result = null, OperationTicket? Ticket = null, OperationTicket[]? Tickets = null);
public enum OperationState { Queued, Executing, Accepted, Completed, Failed, Unknown, Canceled }
public sealed record OperationTicket(string OperationId, string EngineInstanceId, string Kind, string? Target,
    OperationState State, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt = null,
    long ReceivedTimestamp = 0, long ExecutingTimestamp = 0, long CompletedTimestamp = 0, string? Result = null)
{
    public bool IsTerminal => State is OperationState.Completed or OperationState.Failed or OperationState.Unknown or OperationState.Canceled;
}
public sealed record EngineIdentity(string InstanceId, int ProtocolMin, int ProtocolMax, long StartedUtcTicks, int SessionId);
public sealed record EngineSnapshotTiming(double TotalMs,double ScalarMs,double CommandMs,string? SlowestScalar,double SlowestScalarMs,string? SlowestCommand,double SlowestCommandMs);
public sealed record EngineReadTiming(long StartedTimestamp,long HeaderReadTimestamp,long PayloadReadTimestamp,long CompletedTimestamp,RuntimeExecutionSample StartedSample,RuntimeExecutionSample CompletedSample);
public sealed record FormatState(uint Bit, bool Selected, bool CanSelect, string Hint);
public sealed record EngineSnapshot(int Protocol, string Version, int ProcessId, Dictionary<string, JsonElement> Values,
    Dictionary<string, bool> Commands, DeviceRow[] Devices, OutputEndpoint[] Outputs, InputEndpoint[] Inputs,
    UsbAudioBridge.NetworkAdapterInfo[] Adapters, RememberedBoard[] Boards, UiLogEntry[] LogEntries,
    FormatState[] Formats, FormatState[] CaptureFormats, string? OutputId, string? InputId, string? AdapterKey, string? DeviceKey,
    EngineIdentity? Identity = null, OperationTicket[]? Operations = null, EngineSnapshotTiming? Timing = null);

public static class EngineProtocol
{
    public const int Version = 4;
    public static string Release => "V" + typeof(BridgeRuntime).Assembly.GetName().Version!.ToString(3);
    public static string FrontendExecutable
    {
        get
        {
            string nextToRuntime = Path.Combine(AppContext.BaseDirectory, "SoundConvertClient.exe");
            return File.Exists(nextToRuntime) ? nextToRuntime : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "SoundConvertClient.exe"));
        }
    }
    public static string Name
    {
        get
        {
            string scope = Environment.UserDomainName + "\\" + Environment.UserName + ":" + Process.GetCurrentProcess().SessionId;
            if (Environment.GetEnvironmentVariable("UAB_UI_PREVIEW") == "1") scope += ":" + LocalStore.Root;
            return "UsbAudioBridge-Engine-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
        }
    }
    public static string AdapterKey(UsbAudioBridge.NetworkAdapterInfo adapter) => adapter.Id + "|" + adapter.Address;
    public static string DeviceKey(DeviceRow row) => row.Device.DeviceId + "|" + row.Device.LocalAddress;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > 1024 * 1024) throw new InvalidDataException("IPC message too large");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct,Action<EngineReadTiming>? timing=null)
    {
        long started=timing is null?0:Stopwatch.GetTimestamp();var sample=timing is null?default:RuntimeExecutionSample.Capture();
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        long headerRead=timing is null?0:Stopwatch.GetTimestamp();
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 1024 * 1024) throw new InvalidDataException("Invalid IPC message size");
        byte[] payload = new byte[length]; await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        long payloadRead=timing is null?0:Stopwatch.GetTimestamp();
        var result=JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Missing IPC message");
        timing?.Invoke(new(started,headerRead,payloadRead,Stopwatch.GetTimestamp(),sample,RuntimeExecutionSample.Capture()));return result;
    }
    public static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}
