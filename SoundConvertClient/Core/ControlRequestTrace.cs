namespace UsbAudioBridge;

public sealed record ControlRequestTrace(string? OperationId, string Operation, uint RequestId,
    long EnqueuedTimestamp, long SentTimestamp, long AckTimestamp, long CompletedTimestamp, string State, string? Error);

/// <summary>Correlates an Engine operation with requests without carrying audio through IPC.</summary>
public sealed class ControlOperationContext : IDisposable
{
    static readonly AsyncLocal<string?> current = new();
    readonly string? previous;
    public static string? Current => current.Value;
    public ControlOperationContext(string operationId) { previous = current.Value; current.Value = operationId; }
    public void Dispose() => current.Value = previous;
}
