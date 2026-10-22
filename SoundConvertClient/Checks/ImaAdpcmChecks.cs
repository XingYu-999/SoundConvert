using System.Buffers.Binary;
using System.Text.Json;
namespace UsbAudioBridge.Checks;
static class ImaAdpcmChecks
{
    public static int Run()
    {
        int checks=0;void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
        byte[] golden=[100,0,0,0,156,255,0,0,0x71,0xf8];
        var result=ImaAdpcm.Decode(golden,3,AudioFormat.Pcm16);
        short[] expected=[100,-100,101,-89,101,-119];
        Check(Enumerable.Range(0,6).All(i=>BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(i*2))==expected[i]),"independent hand-calculated step/index/stereo vector");
        var expanded=ImaAdpcm.Decode(golden,3,new AudioFormat(48000,24));
        Check(Enumerable.Range(0,6).All(i=>PcmCodec.ReadSigned(expanded.AsSpan(i*3),24)==expected[i]*256),"24bit transport expands decoded 16bit effective precision");
        byte[] saturated=[248,127,88,0,8,128,88,0,0xf7];var clipped=ImaAdpcm.Decode(saturated,2,AudioFormat.Pcm16);
        Check(BinaryPrimitives.ReadInt16LittleEndian(clipped.AsSpan(4))==32767&&BinaryPrimitives.ReadInt16LittleEndian(clipped.AsSpan(6))==short.MinValue,"decoder clips both polarities without wrap");
        foreach(var bad in new[]{golden[..9],golden.Concat(new byte[]{0}).ToArray(),golden.Select((x,i)=>(byte)(i==2?89:x)).ToArray(),golden.Select((x,i)=>(byte)(i==7?1:x)).ToArray()})
        {try{ImaAdpcm.Decode(bad,3,AudioFormat.Pcm16);throw new Exception("bad IMA accepted");}catch(InvalidDataException){checks++;}}
        var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"--ima-vectors");
        if(at>=0)
        {
            var groups=File.ReadLines(args[at+1]).Select(s=>JsonDocument.Parse(s)).GroupBy(j=>(Rate:j.RootElement.GetProperty("rate").GetInt32(),Bits:j.RootElement.GetProperty("bits").GetInt32()));
            foreach(var group in groups)
            {
                var format=new AudioFormat(group.Key.Rate,group.Key.Bits);var descriptor=new StreamDescriptor(1,3,4,49162,49162,format,format.PacketFrames,Codec:AudioTransportCodec.ImaAdpcm);
                var packets=new List<byte[]>();double energy=0,error=0;
                foreach(var doc in group)
                {
                    var e=doc.RootElement;byte[] raw=Convert.FromHexString(e.GetProperty("wire_hex").GetString()!),original=Convert.FromHexString(e.GetProperty("pcm_hex").GetString()!);var packet=AudioCodec.Parse(raw,descriptor);packets.Add(raw);
                    Check(packet.Pcm.Length==original.Length&&packet.Sequence==e.GetProperty("sequence").GetUInt32(),"C firmware packet decodes with original source format/index");
                    if(packet.Sequence>=2)for(int i=0;i<original.Length;i+=format.BytesPerSample){double x=PcmCodec.ReadSigned(original.AsSpan(i),format.Bits),y=PcmCodec.ReadSigned(packet.Pcm.AsSpan(i),format.Bits);energy+=x*x;error+=(x-y)*(x-y);}
                    doc.Dispose();
                }
                double snr=10*Math.Log10(energy/error);Check(snr>24,"low-level 440/880 stereo sine SNR above 24dB for "+format.Label);
                var four=packets.Take(4).ToArray();byte[] parity=AudioFec.Encode(four);
                for(int missing=0;missing<4;missing++){
                    var fec=new AudioFec();byte[]? repaired=null;fec.Observe(parity,descriptor);
                    for(int i=0;i<4;i++)if(i!=missing)repaired=fec.Observe(four[i],descriptor)??repaired;
                    Check(repaired is not null&&repaired.AsSpan().SequenceEqual(four[missing]),"XOR repair preserves compressed bytes, index headers and frame count");
                    Check(AudioCodec.Parse(four[(missing+1)%4],descriptor).Pcm.Length>0,"next packet decodes independently after erasure");
                }
                try{AudioCodec.Parse(four[0],descriptor with{Codec=AudioTransportCodec.Pcm});throw new Exception("unnegotiated compression accepted");}catch(InvalidDataException){checks++;}
                var malformed=four[0].ToArray();malformed[50]=89;try{AudioCodec.Parse(malformed,descriptor);throw new Exception("invalid step index accepted");}catch(InvalidDataException){checks++;}
                Console.WriteLine(JsonSerializer.Serialize(new{stage="ima_quality_vectors",format,snr_db=snr,packets=packets.Count,scope="C encoder to production C# decoder; deterministic sine, not listening quality"}));
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{stage="ima_checks",checks}));return checks;
    }
}
