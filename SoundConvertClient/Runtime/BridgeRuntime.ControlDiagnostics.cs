using System.Text.Json;

namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    public long EndpointIdentityProbeCount=>UsbEndpointIdentity.NativeProbeCount;
    public string ControlDiagnostics => control is null ? "{}" : JsonSerializer.Serialize(control.ControlStatistics);
    public string PollingDiagnostics=>JsonSerializer.Serialize(new{command_available=commands.CurrentCount,polling,
        output_poll=outputPoll.Status.ToString(),recovery_poll=recoveryPoll.Status.ToString(),status_poll=statusPoll.Status.ToString(),monitor_poll=monitorPoll.Status.ToString(),
        nextRecoveryPoll,nextStatusPoll,nextMonitor,nextOutputRefresh,outputSelectionPending,inputSelectionPending,lastAccepted});
}
