namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    readonly HashSet<string> pendingUiCommands = [];
    public Func<Task>? PendingFrameAsync { get; set; }
    string localOutputAction = "", localUiOperation = "";
    static bool TracksUiOperation(string name) => name is "ToggleListeningCommand" or "ApplyOutputSettingsCommand" or "StartMicrophoneCommand" or "StopMicrophoneCommand" or "DiscoverCommand" or "QuickSearchCommand" or "DiscoverBroadcastCommand";
    bool OutputCommandPending => pendingUiCommands.Contains("ToggleListeningCommand") || pendingUiCommands.Contains("ApplyOutputSettingsCommand");
    async Task SendTrackedCommandAsync(string name, string? argument)
    {
        if (!pendingUiCommands.Add(name)) return;
        if (name is "ToggleListeningCommand" or "ApplyOutputSettingsCommand") localOutputAction = name == "ApplyOutputSettingsCommand" ? "正在应用输出设置…" : IsMonitoring ? "正在停止输出…" : "正在开始输出…";
        localUiOperation = name switch { "DiscoverCommand" or "QuickSearchCommand" or "DiscoverBroadcastCommand" => "正在使用 UDP 广播搜索设备…", "StartMicrophoneCommand" => "正在启用麦克风传输…", "StopMicrophoneCommand" => "正在停止麦克风传输…", _ => localOutputAction };
        RefreshLocalOperationFeedback();
        Task rendered = PendingFrameAsync?.Invoke() ?? Task.CompletedTask;
        try
        {
            await SendAsync(new("command", name, EngineProtocol.Json(argument)));
            // A terminal ticket can arrive before the next periodic snapshot.
            // Refresh actual state before re-enabling a toggle with an old label/intent.
            if (client.ProcessId != 0)
            {
                try
                {
                    var actual = await client.RequestAsync(new("snapshot")).WaitAsync(TimeSpan.FromSeconds(3));
                    if (actual.Ok && actual.Snapshot is { } snapshot) ApplySnapshot(snapshot);
                    else Log(actual.Error ?? "操作已结束，但当前状态尚未确认。");
                }
                catch (Exception error) { Log("操作后状态未确认：" + error.Message); }
            }
        }
        finally
        {
            try { await rendered; } catch (Exception error) { Log("首帧反馈未确认：" + error.Message); }
            pendingUiCommands.Remove(name);
            if (!OutputCommandPending) localOutputAction = "";
            if (pendingUiCommands.Count == 0) localUiOperation = "";
            RefreshLocalOperationFeedback();
        }
    }
    void RefreshLocalOperationFeedback()
    {
        foreach (string name in new[] { nameof(IsBusy), nameof(OperationStatus), nameof(CanToggleListening), nameof(ListeningButtonText), nameof(CanStartMicrophone), nameof(IsDiscovering), nameof(CanCancelSearch), nameof(CanCancelOperation), nameof(CanCancelConnection) }) Changed(name);
        foreach (var command in commands.Values) command.Refresh();
    }
}
