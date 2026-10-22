using System.Buffers.Binary;
namespace UsbAudioBridge;

public enum AudioTransportCodec { Pcm, ImaAdpcm }

/// <summary>UAB packet-local stereo IMA, 4-bit differential codes from quantized 16-bit PCM; not WAV/RTP framing.</summary>
public static class ImaAdpcm
{
    static readonly int[] Steps=[7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767];
    static readonly int[] Index=[-1,-1,-1,-1,2,4,6,8,-1,-1,-1,-1,2,4,6,8];
    public static byte[] Decode(ReadOnlySpan<byte> payload,int frames,AudioFormat format)
    {
        format.Validate();
        if(frames<1||payload.Length!=frames+7||payload[2]>88||payload[6]>88||payload[3]!=0||payload[7]!=0)throw new InvalidDataException("Malformed IMA stereo block");
        int left=BinaryPrimitives.ReadInt16LittleEndian(payload),right=BinaryPrimitives.ReadInt16LittleEndian(payload[4..]),li=payload[2],ri=payload[6];
        byte[] pcm=new byte[frames*format.BytesPerFrame];
        Write(pcm,0,left,format.BytesPerSample);Write(pcm,format.BytesPerSample,right,format.BytesPerSample);
        for(int i=1;i<frames;i++){int packed=payload[7+i];left=Next(left,ref li,packed&15);right=Next(right,ref ri,packed>>4);int at=i*format.BytesPerFrame;Write(pcm,at,left,format.BytesPerSample);Write(pcm,at+format.BytesPerSample,right,format.BytesPerSample);}
        return pcm;
    }
    static int Next(int predictor,ref int index,int code){int step=Steps[index],difference=step>>3;if((code&4)!=0)difference+=step;if((code&2)!=0)difference+=step>>1;if((code&1)!=0)difference+=step>>2;int value=Math.Clamp(predictor+((code&8)!=0?-difference:difference),-32768,32767);index=Math.Clamp(index+Index[code],0,88);return value;}
    static void Write(Span<byte> pcm,int at,int sample,int bytes){int value=bytes==3?sample*256:sample;pcm[at]=(byte)value;pcm[at+1]=(byte)(value>>8);if(bytes==3)pcm[at+2]=(byte)(value>>16);}
}
