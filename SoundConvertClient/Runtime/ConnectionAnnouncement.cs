using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundConvertClient;

/// <summary>Original locally synthesized Chinese prompt, played only to the selected PC output.</summary>
public static class ConnectionAnnouncement
{
    private static readonly SemaphoreSlim Gate = new(1);

    public static Task PlayAsync(string? outputEndpointId, CancellationToken ct = default)
        => PlayResourceAsync(outputEndpointId, "connected-zh.wav", ct);

    public static Task PlayDisconnectedAsync(string? outputEndpointId, CancellationToken ct = default)
        => PlayResourceAsync(outputEndpointId, "disconnected-zh.wav", ct);

    private static async Task PlayResourceAsync(string? outputEndpointId, string resourceName, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            await Task.Run(async () =>
            {
                using var enumerator = new MMDeviceEnumerator();
                using var endpoint = string.IsNullOrEmpty(outputEndpointId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : enumerator.GetDevice(outputEndpointId);
                using var stream = typeof(ConnectionAnnouncement).Assembly.GetManifestResourceStream("SoundConvertClient.Assets." + resourceName)
                    ?? throw new FileNotFoundException("声卡提示音资源缺失。");
                using var reader = new WaveFileReader(stream);
                using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 30);
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                output.PlaybackStopped += (_, e) => { if (e.Exception is null) stopped.TrySetResult(); else stopped.TrySetException(e.Exception); };
                output.Init(reader);
                output.Play();
                try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
                finally { output.Stop(); }
            }, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
}
