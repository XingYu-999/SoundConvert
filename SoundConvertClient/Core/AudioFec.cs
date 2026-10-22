namespace UsbAudioBridge;

/// <summary>XOR-4 packet recovery. Original packets are released immediately; no second jitter buffer.</summary>
public sealed class AudioFec
{
    sealed class Group{public readonly byte[]?[] Data=new byte[4][];public byte[]? Parity;public long Last;}
    readonly Dictionary<(uint Epoch,uint Base),Group> groups=new();
    uint boundStream;ulong boundToken;
    public long Repairs{get;private set;}public long ParityPackets{get;private set;}public long ExpiredGroups{get;private set;}
    public void Clear()=>groups.Clear();
    public static bool IsParity(ReadOnlySpan<byte> p)=>p.Length>=4&&p[..4].SequenceEqual("UABF"u8);
    public byte[]? Observe(byte[] raw,StreamDescriptor descriptor)=>ObserveCore(raw,descriptor,false);
    internal byte[]? ObserveValidated(byte[] raw,StreamDescriptor descriptor)=>ObserveCore(raw,descriptor,true);
    byte[]? ObserveCore(byte[] raw,StreamDescriptor descriptor,bool validated)
    {
        if(boundStream!=descriptor.StreamId||boundToken!=descriptor.Token){groups.Clear();boundStream=descriptor.StreamId;boundToken=descriptor.Token;}
        bool parity=IsParity(raw);uint epoch,first;
        if(parity)
        {
            if(raw.Length<88||raw.Length>856||raw[4]!=1||raw[5]!=4||Wire.Read16(raw,6)!=40||Wire.Read32(raw,8)!=descriptor.StreamId||Wire.Read64(raw,20)!=descriptor.Token||Wire.Read16(raw,38)!=0||Wire.Read16(raw,36)!=raw.Length-40)
                throw new InvalidDataException("Malformed FEC packet");
            epoch=Wire.Read32(raw,12);first=Wire.Read32(raw,16);
            if((first&3)!=0||(epoch!=descriptor.Epoch&&(!descriptor.FollowUsb||unchecked((int)(epoch-descriptor.Epoch))<=0)))throw new InvalidDataException("Stale FEC group");
            for(int i=0;i<4;i++)if(Wire.Read16(raw,28+2*i)<48||Wire.Read16(raw,28+2*i)>raw.Length-40)throw new InvalidDataException("Invalid FEC source length");
            ParityPackets++;
        }
        else
        {
            if(!validated)_=AudioCodec.Parse(raw,descriptor);epoch=Wire.Read32(raw,12);first=Wire.Read32(raw,16)&~3u;
            if(Wire.Read16(raw,6)!=0)return null;
        }
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        Span<(uint Epoch,uint Base)> expired=stackalloc (uint,uint)[32];int expiredCount=0;long oldest=long.MaxValue;(uint Epoch,uint Base) oldestKey=default;
        foreach(var entry in groups){if(System.Diagnostics.Stopwatch.GetElapsedTime(entry.Value.Last,now).TotalMilliseconds>100)expired[expiredCount++]=entry.Key;if(entry.Value.Last<oldest){oldest=entry.Value.Last;oldestKey=entry.Key;}}
        for(int i=0;i<expiredCount;i++){groups.Remove(expired[i]);ExpiredGroups++;}
        var key=(epoch,first);if(!groups.TryGetValue(key,out var group))
        {if(groups.Count>=32){groups.Remove(oldestKey);ExpiredGroups++;}group=new();groups.Add(key,group);}
        group.Last=now;if(parity)group.Parity=raw;else group.Data[Wire.Read32(raw,16)&3]=raw;
        if(group.Parity is null)return null;
        int missing=-1;for(int i=0;i<4;i++)if(group.Data[i] is null){if(missing>=0)return null;missing=i;}
        if(missing<0)return null;
        byte[] repair=group.Parity.AsSpan(40).ToArray();
        for(int i=0;i<4;i++)if(group.Data[i] is byte[] data){if(data.Length>repair.Length||data.Length!=Wire.Read16(group.Parity,28+i*2))throw new InvalidDataException("FEC length disagrees with source packet");for(int n=0;n<data.Length;n++)repair[n]^=data[n];}
        int length=Wire.Read16(group.Parity,28+missing*2);Array.Resize(ref repair,length);
        _=AudioCodec.Parse(repair,descriptor);
        if(Wire.Read32(repair,16)!=unchecked(first+(uint)missing)||Wire.Read32(repair,12)!=epoch)throw new InvalidDataException("Invalid reconstructed FEC identity");
        group.Data[missing]=repair;Repairs++;return repair;
    }
    public static byte[] Encode(IReadOnlyList<byte[]> packets)
    {
        if(packets.Count!=4)throw new ArgumentException("Four contiguous UABA packets required");int max=packets.Max(p=>p.Length);if(max>816)throw new ArgumentException("Oversized UABA packet");
        byte[] result=new byte[40+max];"UABF"u8.CopyTo(result);result[4]=1;result[5]=4;Wire.Write16(result,6,40);packets[0].AsSpan(8,8).CopyTo(result.AsSpan(8));
        uint first=Wire.Read32(packets[0],16);if((first&3)!=0)throw new ArgumentException("FEC group is not aligned");Wire.Write32(result,16,first);packets[0].AsSpan(40,8).CopyTo(result.AsSpan(20));Wire.Write16(result,36,(ushort)max);
        for(int i=0;i<4;i++){byte[] p=packets[i];if(Wire.Read32(p,16)!=unchecked(first+(uint)i)||!p.AsSpan(8,8).SequenceEqual(packets[0].AsSpan(8,8))||!p.AsSpan(40,8).SequenceEqual(packets[0].AsSpan(40,8)))throw new ArgumentException("Mixed FEC group");Wire.Write16(result,28+2*i,(ushort)p.Length);for(int n=0;n<p.Length;n++)result[40+n]^=p[n];}
        return result;
    }
}
