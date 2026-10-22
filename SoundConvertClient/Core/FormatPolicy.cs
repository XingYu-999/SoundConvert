using System.Net;
namespace UsbAudioBridge;

public static class FormatPolicy
{
    // Shared playback decodes PCM to float and resamples before rendering. The
    // endpoint need not accept the original PCM format in exclusive mode.
    public static uint ConvertibleInputs(int sampleRate, int precisionBits, int channels = 2)
    {
        if (channels < 2) return 0;
        uint mask = 0;
        for (int i = 0; i < AudioFormat.Supported.Count; i++)
        {
            var source = AudioFormat.Supported[i];
            if (source.SampleRate <= sampleRate && source.Bits <= precisionBits) mask |= 1u << i;
        }
        return mask;
    }
    public static uint Effective(uint configured, uint supported) => configured & supported & AudioFormat.AllMask;
    public static bool SameSubnet(string address, string local, string mask)
    {
        if (!IPAddress.TryParse(address, out var remote) || !IPAddress.TryParse(local, out var own) || !IPAddress.TryParse(mask, out var netmask)) return false;
        var a=remote.GetAddressBytes();var b=own.GetAddressBytes();var m=netmask.GetAddressBytes();
        return a.Length==4 && b.Length==4 && m.Length==4 && m.Any(x=>x!=0) && Enumerable.Range(0,4).All(i=>(a[i]&m[i])==(b[i]&m[i]));
    }
}
