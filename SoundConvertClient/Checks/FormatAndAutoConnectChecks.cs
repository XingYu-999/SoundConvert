using System.Buffers.Binary;
using System.Text.Json;
namespace UsbAudioBridge.Checks;

static class FormatAndAutoConnectChecks
{
    public static async Task<int> RunAsync()
    {
        int count=0;void Check(bool value,string name){count++;if(!value)throw new Exception(name);}
        foreach(var format in AudioFormat.Supported)
        {
            format.Validate();Check(format.PacketFrames*format.BytesPerFrame+48<=1200,"UDP size budget");
            Check((format.PacketFrames+1)*format.BytesPerFrame<=1023,"USB FS packet budget");
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(format.ToRequest()));Check(AudioFormat.FromJson(json.RootElement)==format,"format JSON roundtrip");
            foreach(int frames in new[]{format.SampleRate/1000,format.PacketFrames}.Distinct())
            {
                var descriptor=new StreamDescriptor(1,2,3,49162,49162,format,format.PacketFrames);byte[] packet=new byte[48+frames*format.BytesPerFrame];
                "UABA"u8.CopyTo(packet);packet[4]=1;packet[5]=48;Wire.Write32(packet,8,1);Wire.Write32(packet,12,2);Wire.Write32(packet,28,(uint)format.SampleRate);Wire.Write16(packet,32,(ushort)frames);packet[34]=2;packet[35]=format.Code;Wire.Write16(packet,36,(ushort)(packet.Length-48));Wire.Write64(packet,40,3);
                Check(AudioCodec.Parse(packet,descriptor).Frames==frames,"floor/ceil packet frames");
            }
            var buffer=new AudioBuffer(80,PlaybackTimingChecks.FrozenTime,48000);ulong index=0;
            for(int ms=0;ms<60;ms++)
            {
                int frames=(ms+1)*format.SampleRate/1000-ms*format.SampleRate/1000;byte[] pcm=new byte[frames*format.BytesPerFrame];
                for(int frame=0;frame<frames;frame++)for(int ch=0;ch<2;ch++)
                {
                    int value=(ch==0?1:-1)*(format.Bits==16?16384:4194304),offset=frame*format.BytesPerFrame+ch*format.BytesPerSample;
                    pcm[offset]=(byte)value;pcm[offset+1]=(byte)(value>>8);if(format.Bits==24)pcm[offset+2]=(byte)(value>>16);
                }
                buffer.Add(new AudioPacket(0,(uint)ms,index,(ushort)frames,pcm,format,1));index+=(uint)frames;
            }
            float[] rendered=new float[960];buffer.Read(rendered,0,rendered.Length);buffer.Read(rendered,0,rendered.Length);
            Check(rendered.Where((_,i)=>i%2==0).Average() is >.45f and <.55f,"all-format ASRC left DC level");
            Check(rendered.Where((_,i)=>i%2==1).Average() is >-.55f and <-.45f,"all-format ASRC right DC level");
        }
        Check(AudioFormat.Supported.All(x=>x.Bits is 16 or 24),"only 16/24-bit PCM");
        try{new AudioFormat(48000,32).Validate();throw new Exception("32-bit PCM accepted");}catch(InvalidDataException){count++;}
        Check(FormatPolicy.Effective(3,1)==1&&FormatPolicy.Effective(2,1)==0,"no unselected fallback");
        Check(FormatPolicy.ConvertibleInputs(96000,24)==0xff,"24/96 output accepts all eight inputs through 96k, including 16/48");
        Check(FormatPolicy.ConvertibleInputs(48000,24)==0x1d,"24/48 output accepts both bit depths at 44.1k and 48k");
        Check(FormatPolicy.ConvertibleInputs(96000,16)==0xa5,"16/96 output never reduces 24-bit precision even when byte rate increases");
        Check(FormatPolicy.ConvertibleInputs(48000,16)==5,"16/48 output permits 16/44.1 upsampling and 16/48 only");
        Check(FormatPolicy.ConvertibleInputs(192000,24)==AudioFormat.AllMask,"24/192 output permits every supported source");
        Check(FormatPolicy.ConvertibleInputs(96000,24,1)==0,"stereo is not silently downmixed to mono");
        Check(FormatPolicy.ConvertibleInputs(0,24)==0&&FormatPolicy.ConvertibleInputs(96000,0)==0,"unknown output rate or precision cannot advertise conversion");
        Check(FormatPolicy.SameSubnet("192.168.1.73","192.168.1.8","255.255.255.0"),"same subnet");
        Check(!FormatPolicy.SameSubnet("192.168.2.73","192.168.1.8","255.255.255.0"),"different subnet");
        int dials=0,scans=0;var target=new Device(new string('a',32),"card","test","192.168.1.73",49161,"192.168.1.8","");
        Task<IReadOnlyList<Device>> Scan(){scans++;return Task.FromResult<IReadOnlyList<Device>>([target]);}
        await AutoConnectFlow.RunAsync(target.DeviceId,"192.168.1.20",49161,(_,_,_)=>{dials++;return Task.CompletedTask;},Scan,_=>true);Check(dials==1&&scans==0,"saved IP first");
        dials=scans=0;
        await AutoConnectFlow.RunAsync(target.DeviceId,"192.168.1.20",49161,(ip,_,identity)=>{dials++;if(ip!=target.Address)throw new IOException("stale address");Check(identity==target.DeviceId,"permanent ID passed to dial");return Task.CompletedTask;},Scan,_=>true);Check(dials==2&&scans==1,"single discovery fallback");
        dials=scans=0;
        await AutoConnectFlow.RunAsync(target.DeviceId,"10.0.0.20",49161,(_,_,_)=>{dials++;return Task.CompletedTask;},Scan,ip=>ip.StartsWith("192.168.1."));Check(dials==1&&scans==1,"network changed discovers first");
        dials=scans=0;
        try{await AutoConnectFlow.RunAsync("wrong-id","192.168.1.20",49161,(_,_,_)=>{dials++;throw new IOException();},Scan,_=>true);throw new Exception("wrong ID connected");}catch(IOException){Check(dials==1&&scans==1,"other identity not dialed");}
        dials=scans=0;
        try{await AutoConnectFlow.RunAsync(target.DeviceId,"192.168.1.20",49161,(_,_,_)=>{dials++;throw new IOException();},Scan,_=>false);throw new Exception("off-subnet dialed");}catch(IOException){Check(dials==0&&scans==1,"off-subnet stops");}
        dials=scans=0;
        try{await AutoConnectFlow.RunAsync(target.DeviceId,"192.168.1.20",49161,(_,_,_)=>{dials++;throw new IOException();},Scan,_=>true);throw new Exception("failed connection passed");}catch(IOException){Check(dials==2&&scans==1,"failure has bounded attempts");}
        dials=scans=0;
        try{await AutoConnectFlow.RunAsync(target.DeviceId,"192.168.1.20",49161,(_,_,_)=>{dials++;throw new OperationCanceledException();},Scan,_=>true);throw new Exception("cancellation swallowed");}catch(OperationCanceledException){Check(dials==1&&scans==0,"cancelled initial dial never discovers or retries");}
        dials=0;
        try{await AutoConnectFlow.RunAsync(target.DeviceId,"10.0.0.20",49161,(_,_,_)=>{dials++;return Task.CompletedTask;},()=>Task.FromResult<IReadOnlyList<Device>>([target,target with{Address="192.168.1.74"}]),ip=>ip.StartsWith("192.168.1."));throw new Exception("ambiguous identity dialed");}catch(IOException){Check(dials==0,"ambiguous permanent identity addresses require explicit selection");}
        dials=0;
        await AutoConnectFlow.RunAsync(target.DeviceId,"10.0.0.20",49161,(_,_,_)=>{dials++;return Task.CompletedTask;},()=>Task.FromResult<IReadOnlyList<Device>>([target,target]),ip=>ip.StartsWith("192.168.1."));
        Check(dials==1,"duplicate discovery packets do not create identity ambiguity");
        return count;
    }
}
