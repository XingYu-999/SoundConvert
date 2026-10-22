using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoundConvertClient;

// This factory creates the client passed to NAudio's render wrapper. It never
// obtains a second MMDevice.AudioClient to measure an unrelated stream.
internal static class OutputClockNative
{
    static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid ClientInterface = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid ClockInterface = new("CD63314F-3FBA-4A1B-812C-EF96358728E7");

    internal static IAudioClient ActivateClient(string endpointId)
    {
        object enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true)!)!;
        INativeDevice? endpoint = null;
        try
        {
            var enumerator = (INativeEnumerator)enumeratorObject;
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out endpoint));
            Guid iid = ClientInterface;
            Marshal.ThrowExceptionForHR(endpoint.Activate(ref iid, 23, IntPtr.Zero, out object client)); // CLSCTX_ALL
            return (IAudioClient)client;
        }
        finally
        {
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            Marshal.ReleaseComObject(enumeratorObject);
        }
    }

    internal static IOutputClockReader OpenReader(IAudioClient client, Func<RenderTimingSnapshot> readRenderTiming)
    {
        int hr = client.GetService(ClockInterface, out object service);
        if (hr != 0)
        {
            if (service is not null && Marshal.IsComObject(service)) Marshal.ReleaseComObject(service);
            Marshal.ThrowExceptionForHR(hr);
            throw new NotSupportedException($"IAudioClock GetService returned 0x{hr:X8}.");
        }
        try { return new NativeReader((INativeClock)service, readRenderTiming); }
        catch { Marshal.ReleaseComObject(service); throw; }
    }

    sealed class NativeReader(INativeClock clock, Func<RenderTimingSnapshot> readRenderTiming) : IOutputClockReader
    {
        INativeClock? ownedClock = clock;
        public OutputClockRead Read()
        {
            var current = ownedClock ?? throw new ObjectDisposedException(nameof(NativeReader));
            var renderBefore = readRenderTiming();
            long begin = Stopwatch.GetTimestamp();
            int frequencyHr = current.GetFrequency(out ulong frequency);
            int positionHr = current.GetPosition(out ulong position, out ulong qpc100ns);
            long end = Stopwatch.GetTimestamp();
            var renderAfter = readRenderTiming();
            return new(position, frequency, qpc100ns, begin, end, positionHr, frequencyHr, renderBefore, renderAfter);
        }
        public void Dispose()
        {
            var current = ownedClock; ownedClock = null;
            if (current is not null) Marshal.ReleaseComObject(current);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INativeEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out INativeDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out INativeDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INativeDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object client);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INativeClock
    {
        [PreserveSig] int GetFrequency(out ulong frequency);
        [PreserveSig] int GetPosition(out ulong position, out ulong qpc100ns);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
    }
}
