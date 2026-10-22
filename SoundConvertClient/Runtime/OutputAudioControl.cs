using NAudio.CoreAudioApi;
using NAudio.Wave;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed record OutputSnapshot(string Id, string Name, string Format, double Volume, bool Muted, uint SupportedMask,
    DeviceAudioFormat? DeviceFormat = null, string MixFormat = "");
public interface IAudioOutputControl
{
    IReadOnlyList<OutputEndpoint> Endpoints();
    Task<OutputSnapshot> ReadAsync(string id,CancellationToken ct=default);
    Task WriteAsync(string id,double percent,bool muted,CancellationToken ct=default);
}
public sealed class WindowsOutputAudioControl:IAudioOutputControl
{
    public IReadOnlyList<OutputEndpoint> Endpoints()
    {
        var current = WindowsDefaultAudio.Get(DataFlow.Render);
        return [new(WindowsDefaultAudio.Id, WindowsDefaultAudio.Label(current.Name), current.Id), .. WindowsAudio.Endpoints()];
    }
    public Task<OutputSnapshot> ReadAsync(string id,CancellationToken ct=default)=>OutputAudioControl.ReadAsync(id,ct);
    public Task WriteAsync(string id,double percent,bool muted,CancellationToken ct=default)=>OutputAudioControl.WriteAsync(id,percent,muted,ct);
}
public static class OutputAudioControl
{
    public static uint ConvertibleInputs(DeviceAudioFormat? deviceFormat, DeviceAudioFormat mixFormat) => deviceFormat is null ? 0 :
        FormatPolicy.ConvertibleInputs(deviceFormat.SampleRate, deviceFormat.PrecisionBits, deviceFormat.Channels) &
        FormatPolicy.ConvertibleInputs(mixFormat.SampleRate, mixFormat.PrecisionBits, mixFormat.Channels);

    public static Task<OutputSnapshot> ReadAsync(string id, CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(id);
        if(device.State!=DeviceState.Active)throw new IOException("输出声卡已离线。");
        using var client=device.AudioClient;var mix=client.MixFormat;
        DeviceAudioFormat? deviceFormat = null;
        string formatStatus;
        try
        {
            var property = device.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat];
            if (property?.Value is not byte[] blob) throw new InvalidDataException("设备未提供输出格式。");
            deviceFormat = DeviceAudioFormat.Parse(blob);
            formatStatus = deviceFormat.Label + " · 设备输出（Windows 设置）";
        }
        catch (Exception error) when (error is InvalidDataException or System.Runtime.InteropServices.COMException)
        { formatStatus = "设备输出位深未知：" + error.Message; }
        var mixFormat = DeviceAudioFormat.FromWaveFormat(mix);
        string mixLabel = mixFormat.Label;
        // Check the two stages of shared rendering, not exclusive source-format
        // support. Neither stage may reduce the source rate or PCM precision.
        uint mask = ConvertibleInputs(deviceFormat, mixFormat);
        var volume=device.AudioEndpointVolume;
        return new OutputSnapshot(id,device.FriendlyName,formatStatus+"\n混音处理："+mixLabel+"（WASAPI）",volume.MasterVolumeLevelScalar*100d,volume.Mute,mask,deviceFormat,mixLabel);
    },ct);
    public static Task WriteAsync(string id,double percent,bool muted,CancellationToken ct=default) => Task.Run(() =>
    {
        if(!double.IsFinite(percent)||percent is <0 or >100)throw new ArgumentOutOfRangeException(nameof(percent));
        ct.ThrowIfCancellationRequested();using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(id);
        if(device.State!=DeviceState.Active)throw new IOException("输出声卡已离线。");
        var volume=device.AudioEndpointVolume;
        if(Math.Abs(volume.MasterVolumeLevelScalar-percent/100)>.00001)volume.MasterVolumeLevelScalar=(float)(percent/100);
        if(volume.Mute!=muted)volume.Mute=muted;
    },ct);
}
