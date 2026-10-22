using System.Text.Json;
namespace SoundConvertClient;

public sealed partial class BridgeRuntime
{
    bool updatingBoardSnapshot,boardWritePending,boardMuted;
    double boardVolumePercent=80;
    long boardWriteGeneration;
    LatestValueWriter<BoardWrite>? boardWriter;
    public bool CanControlBoardGain=>CanManage&&IsConnected&&internalVolumeSupported&&!IsBusy;
    public string BoardGainReadout=>$"{BoardVolumePercent:0}%";
    public double BoardVolumePercent{get=>boardVolumePercent;set{if(!double.IsFinite(value))return;value=Math.Round(Math.Clamp(value,0,100));if(boardVolumePercent==value)return;Set(ref boardVolumePercent,value);Changed(nameof(BoardGainReadout));if(!updatingBoardSnapshot&&CanControlBoardGain)QueueBoardWrite();}}
    public bool BoardMuted{get=>boardMuted;set{if(boardMuted==value)return;Set(ref boardMuted,value);if(!updatingBoardSnapshot&&CanControlBoardGain)QueueBoardWrite();}}
    void ResetBoardVolume()
    {
        ++boardWriteGeneration;boardWritePending=false;
        updatingBoardSnapshot=true;try{BoardVolumePercent=80;BoardMuted=false;}finally{updatingBoardSnapshot=false;}
        ResetHostKeys();RefreshVolumeControls();
    }
    void RefreshVolumeControls()=>Changed(nameof(CanControlBoardGain));
    void ReadUsbHostState(JsonElement info)
    {
        ReadHostKeyState(info);
        if(boardWritePending||!info.TryGetProperty("internal_volume",out var state))return;
        updatingBoardSnapshot=true;
        try{BoardVolumePercent=Number(state,"percent");BoardMuted=state.TryGetProperty("mute",out var mute)&&mute.GetBoolean();}
        finally{updatingBoardSnapshot=false;}
    }
    readonly record struct BoardWrite(UsbAudioBridge.ControlSession Session,int Percent,bool Mute,long Generation);
    void QueueBoardWrite()
    {
        boardWritePending=true;
        boardWriter??=new(WriteBoard,error=>{boardWritePending=false;GlobalError="声卡内部音量调整失败："+error.Message;Log(GlobalError);});
        boardWriter.Submit(new(Session,(int)BoardVolumePercent,BoardMuted,++boardWriteGeneration));
    }
    async Task WriteBoard(BoardWrite value)
    {
        await commands.WaitAsync(lifetime.Token);
        try
        {
            if(shuttingDown||value.Session!=control||!CanManage||!IsConnected||!internalVolumeSupported)return;
            await value.Session.RequestAsync("SET_INTERNAL_VOLUME",new{volume_percent=value.Percent,mute=value.Mute});
            if(value.Generation==boardWriteGeneration)boardWritePending=false;
        }
        finally{commands.Release();}
    }
}
