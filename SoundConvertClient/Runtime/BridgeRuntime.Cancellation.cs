using System.Windows.Input;
namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    CancellationTokenSource? searchCancellation, activeConnectionCancellation;
    bool connectionCancelRequested;
    public bool IsDiscovering => searchCancellation is not null;
    public bool CanCancelSearch => searchCancellation is { IsCancellationRequested:false };
    public bool CanCancelConnection => IsReconnecting || activeConnectionCancellation is { IsCancellationRequested:false } || autoConnectCancellation is { IsCancellationRequested:false };
    public bool CanCancelOperation => CanCancelConnection || CanCancelSearch;
    public ICommand CancelSearchCommand => SyncCommand(nameof(CancelSearchCommand), _ => CancelSearch(), () => CanCancelSearch);
    public ICommand CancelConnectionCommand => SyncCommand(nameof(CancelConnectionCommand), _ => CancelConnection(), () => CanCancelConnection);
    public void CancelSearch() { searchCancellation?.Cancel(); RefreshCancellationState(); }
    public void CancelConnection()
    {
        connectionCancelRequested=true; StopAutomaticReconnect(clearTarget:true,message:"自动重连已取消。");activeConnectionCancellation?.Cancel(); autoConnectCancellation?.Cancel();
        RefreshCancellationState();
    }
    void RefreshCancellationState()
    {
        foreach(string name in new[] { nameof(IsDiscovering), nameof(CanCancelSearch), nameof(CanCancelConnection), nameof(CanCancelOperation) }) Changed(name);
        RefreshCommandStates();
    }
}
