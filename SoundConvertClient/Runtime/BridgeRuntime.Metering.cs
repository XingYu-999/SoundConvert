using System.Diagnostics;
namespace SoundConvertClient;
public sealed partial class BridgeRuntime
{
    double levelLeftDb=-90,levelRightDb=-90,levelLeftRmsDb=-90,levelRightRmsDb=-90;long lastMeterTick=Stopwatch.GetTimestamp(),clipUntil;string levelStatus="无新数据",levelClipStatus="";
    uint meterGeneration;long previousClipCount;
    public double LevelLeftDb{get=>levelLeftDb;private set=>Set(ref levelLeftDb,value);}
    public double LevelRightDb{get=>levelRightDb;private set=>Set(ref levelRightDb,value);}
    public double LevelLeftRmsDb{get=>levelLeftRmsDb;private set=>Set(ref levelLeftRmsDb,value);}
    public double LevelRightRmsDb{get=>levelRightRmsDb;private set=>Set(ref levelRightRmsDb,value);}
    public string LevelStatus{get=>levelStatus;private set=>Set(ref levelStatus,value);}
    public string LevelClipStatus{get=>levelClipStatus;private set=>Set(ref levelClipStatus,value);}
    static double Db(double value)=>Math.Clamp(20*Math.Log10(Math.Max(value,1e-9)),-90,0);
    void UpdateLevelMeters()
    {
        long now=Stopwatch.GetTimestamp();double elapsed=Stopwatch.GetElapsedTime(lastMeterTick,now).TotalSeconds;lastMeterTick=now;
        var s=audio?.Buffer.LevelMeter.ReadSnapshot()??default;bool fresh=s.Valid&&Stopwatch.GetElapsedTime(s.Timestamp,now).TotalMilliseconds<=500;
        LevelLeftDb=Math.Max(fresh?Db(s.LeftPeak):-90,LevelLeftDb-24*elapsed);LevelRightDb=Math.Max(fresh?Db(s.RightPeak):-90,LevelRightDb-24*elapsed);
        LevelLeftRmsDb=fresh?Db(s.LeftRms):-90;LevelRightRmsDb=fresh?Db(s.RightRms):-90;
        LevelStatus=fresh?"有效接收音频 · 板端增益之后、电脑处理之前":"无新数据（不代表播放器已暂停）";
        if(s.Valid){if(s.Generation!=meterGeneration){meterGeneration=s.Generation;previousClipCount=0;}if(fresh&&s.FullScaleSamples>previousClipCount)clipUntil=now+2*Stopwatch.Frequency;previousClipCount=s.FullScaleSamples;}
        LevelClipStatus=now<clipUntil?"达到满量程／可能削波":"";
    }
    void ResetLevelMeters(){LevelLeftDb=LevelRightDb=LevelLeftRmsDb=LevelRightRmsDb=-90;LevelStatus="无新数据";clipUntil=0;meterGeneration=0;previousClipCount=0;LevelClipStatus="";}
}
