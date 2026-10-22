using System.Text.Json;
namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    bool captureVolumeSupported,captureUsbMuted,captureUsbReading,captureUsbPending;double captureUsbVolumeDb;long captureWriteGeneration;
    string captureUsbControlSource="尚未读取 USB 麦克风控制";
    LatestValueWriter<CaptureVolumeWrite>? captureVolumeWriter;
    sealed record CaptureVolumeWrite(UsbAudioBridge.ControlSession Session,double Db,bool Mute,long Generation);
    public bool CanControlCaptureVolume=>IsConnected&&CanManage&&MicrophoneSupported&&captureVolumeSupported&&!IsBusy;
    public double CaptureUsbVolumeDb{get=>captureUsbVolumeDb;set{if(!double.IsFinite(value))throw new ArgumentException("音量必须为有限值");Set(ref captureUsbVolumeDb,Math.Clamp(value,-60,0));if(!captureUsbReading&&CanControlCaptureVolume)QueueCaptureVolume();}}
    public bool CaptureUsbMuted{get=>captureUsbMuted;set{Set(ref captureUsbMuted,value);if(!captureUsbReading&&CanControlCaptureVolume)QueueCaptureVolume();}}
    public string CaptureUsbControlSource{get=>captureUsbControlSource;private set=>Set(ref captureUsbControlSource,value);}
    void ReadCaptureVolume(JsonElement capture)
    {
        if(!capture.TryGetProperty("usb_control",out var c))return;
        CaptureUsbControlSource=$"USB 麦克风 Feature Unit · {String(c,"source","未知")} · {(c.GetProperty("volume_q8").GetInt32()==short.MinValue?"−∞":(c.GetProperty("volume_q8").GetDouble()/256).ToString("0.0"))} dB";
        if(captureUsbPending)return;captureUsbReading=true;try{CaptureUsbVolumeDb=c.GetProperty("volume_q8").GetDouble()/256;CaptureUsbMuted=c.GetProperty("mute").GetBoolean();}finally{captureUsbReading=false;}
    }
    void QueueCaptureVolume()
    {
        captureUsbPending=true;captureVolumeWriter??=new(WriteCaptureVolume,e=>{captureUsbPending=false;GlobalError="USB 麦克风音量写入失败："+e.Message;});
        captureVolumeWriter.Submit(new(Session,CaptureUsbVolumeDb,CaptureUsbMuted,++captureWriteGeneration));
    }
    async Task WriteCaptureVolume(CaptureVolumeWrite value)
    {
        await commands.WaitAsync(lifetime.Token);try{if(value.Generation!=captureWriteGeneration||value.Session!=control||!CanManage||!IsConnected||!MicrophoneSupported)return;await value.Session.RequestAsync("USB_SET_CAPTURE_VOLUME",new{volume_q8=(int)Math.Round(value.Db*256),mute=value.Mute});}finally{if(value.Generation==captureWriteGeneration)captureUsbPending=false;commands.Release();}
    }
}
