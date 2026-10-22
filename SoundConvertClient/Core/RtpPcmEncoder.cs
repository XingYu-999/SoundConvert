using System.Security.Cryptography;
namespace UsbAudioBridge;

/// <summary>Application bridge only: UABA PCM -> RFC 3550 RTP with SDP-described L16/L24 payloads.</summary>
public sealed class RtpPcmEncoder
{
    bool initialized;ulong timeline;AudioFormat format=AudioFormat.Pcm16;ushort sequence;uint timestampOffset;
    public uint Ssrc{get;private set;}
    public byte PayloadType{get;private set;}=96;
    public byte[] Encode(AudioPacket packet)
    {
        packet.Format.Validate();if(packet.Pcm.Length!=packet.Frames*packet.Format.BytesPerFrame)throw new InvalidDataException("RTP source PCM length");
        if(!initialized||timeline!=packet.TimelineId||format!=packet.Format)
        {initialized=true;timeline=packet.TimelineId;format=packet.Format;PayloadType=(byte)(96+AudioFormat.Supported.Select((f,i)=>(f,i)).First(x=>x.f==format).i);Ssrc=BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4))|1;timestampOffset=BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));sequence=0;}
        byte[] rtp=new byte[12+packet.Pcm.Length];rtp[0]=0x80;rtp[1]=PayloadType;Wire.Write16(rtp,2,sequence++);Wire.Write32(rtp,4,unchecked(timestampOffset+(uint)packet.SampleIndex));Wire.Write32(rtp,8,Ssrc);
        int bytes=format.BytesPerSample;for(int at=0;at<packet.Pcm.Length;at+=bytes)for(int b=0;b<bytes;b++)rtp[12+at+b]=packet.Pcm[at+bytes-1-b];return rtp;
    }
    public string Sdp(string address,int port)=>$"v=0\r\no=- {Ssrc} 1 IN IP4 {address}\r\ns=USB Audio Bridge\r\nc=IN IP4 {address}\r\nt=0 0\r\nm=audio {port} RTP/AVP {PayloadType}\r\na=rtpmap:{PayloadType} L{format.Bits}/{format.SampleRate}/2\r\na=recvonly\r\n";
}
