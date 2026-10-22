using UsbAudioBridge;
namespace UsbAudioBridge.Checks;
static class RtpPcmChecks
{
    public static int Run()
    {
        int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
        foreach(var format in AudioFormat.Supported)
        {
            byte[] pcm=Enumerable.Range(0,format.BytesPerFrame).Select(i=>(byte)i).ToArray();var p=new AudioPacket(0,0,0xffffffff,1,pcm,format,123);var encoder=new RtpPcmEncoder();var first=encoder.Encode(p);var next=encoder.Encode(p with{SampleIndex=0x100000000});
            Check(first[0]==0x80&&first[1]==encoder.PayloadType&&Wire.Read16(next,2)==1,"RTP header and sequence");
            Check(unchecked(Wire.Read32(next,4)-Wire.Read32(first,4))==1,"RTP timestamp sample units and wrap");
            for(int at=0;at<pcm.Length;at+=format.BytesPerSample)for(int b=0;b<format.BytesPerSample;b++)Check(first[12+at+b]==pcm[at+format.BytesPerSample-1-b],"L16/L24 network byte order");
            Check(encoder.Sdp("127.0.0.1",5004).Contains($"L{format.Bits}/{format.SampleRate}/2"),"SDP describes actual negotiated PCM");
        }
        return checks;
    }
}
