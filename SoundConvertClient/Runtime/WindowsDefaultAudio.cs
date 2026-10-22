using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace SoundConvertClient;

public static class WindowsDefaultAudio
{
    // Persist the user's routing choice separately from the current endpoint ID.
    public const string Id = "windows-system-default";
    public static string Label(string name) => $"Windows 系统默认（{name}）";

    public static (string? Id, string Name) Get(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return (device.ID, device.FriendlyName);
        }
        catch (COMException)
        {
            return (null, flow == DataFlow.Render ? "无可用输出设备" : "无可用麦克风");
        }
    }
}
