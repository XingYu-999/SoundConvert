using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
namespace SoundConvertClient;

public sealed class SingleInstance : IDisposable
{
    static readonly string Name = "UsbAudioBridge-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..20];
    readonly Mutex mutex; readonly CancellationTokenSource stop = new(); readonly Task? listener; public bool IsPrimary { get; }
    public static Action? Activate; static bool pending;
    public SingleInstance() { mutex = new Mutex(true, "Local\\" + Name, out bool first); IsPrimary = first; if (first) listener = Listen(); }
    public static void WindowReady(Action activate) { Activate = activate; if (pending) { pending = false; activate(); } }
    public bool ActivatePrimary() { try { using var p = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly); p.Connect(2500); p.WriteByte(1); using var timeout = new CancellationTokenSource(2500); byte[] b = new byte[1]; return p.ReadAsync(b, timeout.Token).AsTask().GetAwaiter().GetResult() == 1 && b[0] == 6; } catch { return false; } }
    async Task Listen() { while (!stop.IsCancellationRequested) { try { using var p = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await p.WaitForConnectionAsync(stop.Token); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(2500); byte[] b = new byte[1]; if (await p.ReadAsync(b, deadline.Token) == 1 && b[0] == 1) { Dispatcher.UIThread.Post(() => { if (Activate is null) pending = true; else Activate(); }); await p.WriteAsync(new byte[] { 6 }, deadline.Token); } } catch (OperationCanceledException) { } catch (IOException) { } } }
    public void Dispose() { stop.Cancel(); Activate = null; if (IsPrimary) mutex.ReleaseMutex(); mutex.Dispose(); stop.Dispose(); }
}
