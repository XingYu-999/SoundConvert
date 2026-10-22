using System.Text.Json;
namespace UsbAudioBridge;

public sealed record CaptureFormat(int SampleRate,int Bits)
{
    public const uint AllMask=0x1ff,DefaultMask=1;
    public static IReadOnlyList<CaptureFormat> Supported{get;}=Array.AsReadOnly(new CaptureFormat[]{new(48000,16),new(16000,16),new(8000,16),new(32000,16),new(44100,16),new(96000,16),new(48000,24),new(44100,24),new(96000,24)});
    public int Channels=>1;public int BytesPerFrame=>Bits/8;public int PacketFrames=>(SampleRate+999)/1000;
    public string Label=>$"{Bits}-bit / {SampleRate/1000d:0.###} kHz · 单声道";
    public void Validate(){if(!Supported.Contains(this))throw new InvalidDataException("Unsupported mono microphone format");}
    public object ToRequest()=>new{sample_rate=SampleRate,bits=Bits,channels=1,sample_format=Bits==24?"S24LE_PACKED":"S16LE"};
    public static CaptureFormat FromJson(JsonElement j){var f=new CaptureFormat(j.GetProperty("sample_rate").GetInt32(),j.TryGetProperty("bits",out var b)?b.GetInt32():j.GetProperty("sample_format").GetString()=="S24LE_PACKED"?24:16);if(j.GetProperty("channels").GetInt32()!=1)throw new InvalidDataException("Mono microphone required");f.Validate();return f;}
    public static uint FromSource(int rate,int bits,int channels){if(channels<1)return 0;uint m=0;for(int i=0;i<Supported.Count;i++)if(Supported[i].SampleRate<=rate&&Supported[i].Bits<=bits)m|=1u<<i;return m;}
}
public sealed record CaptureDescriptor(uint StreamId,uint Epoch,ulong Token,int DestinationPort,CaptureFormat Format,bool HostActive);
public static class CaptureCodec
{
    public static byte[] Encode(CaptureDescriptor d,uint sequence,ulong index,ReadOnlySpan<byte> pcm,int frames,bool probe=false)
    {
        byte[] b=new byte[48+pcm.Length];
        if(!TryEncode(d,sequence,index,pcm,frames,b,out _,probe))throw new InvalidDataException("Microphone PCM range");
        return b;
    }
    /// <summary>Writes a complete datagram without retaining or allocating storage.</summary>
    public static bool TryEncode(CaptureDescriptor d,uint sequence,ulong index,ReadOnlySpan<byte> pcm,int frames,Span<byte> destination,out int written,bool probe=false)
    {
        written=0;bool supported=false;
        for(int i=0;i<CaptureFormat.Supported.Count;i++)if(CaptureFormat.Supported[i]==d.Format){supported=true;break;}
        if(!supported||d.StreamId==0||d.Token==0||frames<0||index>ulong.MaxValue-(uint)frames||
            (!probe&&(frames<1||frames>d.Format.PacketFrames||pcm.Length!=frames*d.Format.BytesPerFrame))||
            (probe&&(frames!=0||pcm.Length!=0))||destination.Length<48+pcm.Length)return false;
        Span<byte> b=destination[..(48+pcm.Length)];b[..48].Clear();
        "UABM"u8.CopyTo(b);b[4]=1;b[5]=48;Wire.Write16(b,6,probe?(ushort)8:(ushort)0);Wire.Write32(b,8,d.StreamId);Wire.Write32(b,12,d.Epoch);Wire.Write64(b,40,d.Token);
        if(!probe){Wire.Write32(b,16,sequence);Wire.Write64(b,20,index);Wire.Write32(b,28,(uint)d.Format.SampleRate);Wire.Write16(b,32,(ushort)frames);b[34]=1;b[35]=(byte)(d.Format.Bits==24?2:1);Wire.Write16(b,36,(ushort)pcm.Length);pcm.CopyTo(b[48..]);}
        written=b.Length;return true;
    }
}
