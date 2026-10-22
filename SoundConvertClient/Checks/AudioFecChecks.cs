using UsbAudioBridge;
namespace UsbAudioBridge.Checks;
static class AudioFecChecks
{
    public static int Run()
    {
        int count=0;void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
        byte[] Packet(uint seq,uint epoch=7,ushort frames=48)
        {byte[] p=new byte[48+frames*4];"UABA"u8.CopyTo(p);p[4]=1;p[5]=48;Wire.Write32(p,8,123);Wire.Write32(p,12,epoch);Wire.Write32(p,16,seq);Wire.Write64(p,20,(ulong)seq*48);Wire.Write32(p,28,48000);Wire.Write16(p,32,frames);p[34]=2;p[35]=1;Wire.Write16(p,36,(ushort)(frames*4));Wire.Write64(p,40,456);p.AsSpan(48).Fill((byte)(seq+1));return p;}
        var descriptor=new StreamDescriptor(123,7,456,49162,49162,FollowUsb:true,FecEnabled:true);
        var packets=Enumerable.Range(0,4).Select(i=>Packet((uint)i)).ToArray();byte[] parity=AudioFec.Encode(packets);
        foreach(int missing in Enumerable.Range(0,4))foreach(bool parityFirst in new[]{false,true})
        {
            var decoder=new AudioFec();byte[]? recovered=null;
            if(parityFirst)recovered=decoder.Observe(parity,descriptor);
            for(int i=3;i>=0;i--)if(i!=missing)recovered=decoder.Observe(packets[i],descriptor)??recovered;
            if(!parityFirst)recovered=decoder.Observe(parity,descriptor);
            Check(recovered is not null&&recovered.AsSpan().SequenceEqual(packets[missing]),"XOR repair preserves every PCM/header byte");
            Check(decoder.Repairs==1,"a recovered packet is counted once");
        }
        var two=new AudioFec();two.Observe(packets[0],descriptor);two.Observe(packets[3],descriptor);Check(two.Observe(parity,descriptor) is null,"two missing packets never fabricate a repair");
        var invalid=parity.ToArray();invalid[20]^=1;try{two.Observe(invalid,descriptor);throw new Exception("wrong token accepted");}catch(InvalidDataException){count++;}
        // A new stream with the same epoch and sequence cannot reuse a previous token's partial group.
        var replacement=descriptor with{Token=999};byte[] next=Packet(0);Wire.Write64(next,40,999);Check(two.Observe(next,replacement) is null,"new token resets old repair group");
        var wrap=new[]{Packet(0xfffffffc),Packet(0xfffffffd),Packet(0xfffffffe),Packet(0xffffffff)};
        var d=new AudioFec();d.Observe(wrap[0],descriptor);d.Observe(wrap[2],descriptor);d.Observe(wrap[3],descriptor);Check(d.Observe(AudioFec.Encode(wrap),descriptor)!.AsSpan().SequenceEqual(wrap[1]),"sequence wrap group repairs correctly");
        var delayed=new AudioFec();for(uint seq=0;seq<80;seq++)if(seq!=1)delayed.Observe(Packet(seq),descriptor);
        Check(delayed.Observe(parity,descriptor)!.AsSpan().SequenceEqual(packets[1]),"repair cache retains twenty groups within the supported buffering window");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{stage="fec_checks",checks=count,scope="offline XOR recovery; original packets are not delayed"}));return count;
    }
}
