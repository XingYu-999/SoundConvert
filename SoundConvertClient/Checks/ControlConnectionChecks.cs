using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UsbAudioBridge;

namespace UsbAudioBridge.Checks;

static class ControlConnectionChecks
{
    const ulong Boot = 42;
    const string DeviceId = "0123456789abcdef0123456789abcdef";

    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        // Separate fresh sessions exercise first connection and reconnection without any local credential store.
        foreach (bool owner in new[] { true, false, true })
        {
            await Exchange(async stream =>
            {
                var hello = await Wire.ReadAsync(stream, default);
                using var json = JsonDocument.Parse(hello.Payload);
                Check(json.RootElement.GetProperty("op").GetString() == "HELLO" &&
                    json.RootElement.GetProperty("args").GetProperty("connection_mode").GetString() == "open",
                    "Fresh connection must request direct HELLO");
                await stream.WriteAsync(Hello(hello.RequestId, owner));
                var request = await Wire.ReadAsync(stream, default);
                using var requestJson = JsonDocument.Parse(request.Payload);
                Check(requestJson.RootElement.GetProperty("op").GetString() == "GET_INFO" && request.RequestId == 2 &&
                    request.Sequence == 1 && request.Boot == Boot, "GET_INFO must follow HELLO directly without AUTH");
                await stream.WriteAsync(Wire.Control(3, 0, 1, Boot,
                    JsonSerializer.SerializeToUtf8Bytes(new { @event = "NETWORK_CHANGED", data = new { } })));
                await stream.WriteAsync(Reply(request.RequestId, 2, Boot));
            }, async session =>
            {
                Check(session.DeviceId == DeviceId && session.IsOwner == owner, "Direct HELLO identity/ownership");
                bool eventSeen = false;
                session.EventReceived += _ => eventSeen = true;
                await session.RequestAsync("GET_INFO");
                Check(eventSeen, "Direct events and replies share the sequence space");
            });
        }

        foreach (bool staleBoot in new[] { false, true })
            await Exchange(async stream =>
            {
                var hello = await Wire.ReadAsync(stream, default);
                await stream.WriteAsync(Hello(hello.RequestId, true));
                var request = await Wire.ReadAsync(stream, default);
                await stream.WriteAsync(Reply(request.RequestId, staleBoot ? 1UL : 0UL, staleBoot ? Boot + 1 : Boot));
            }, async session =>
            {
                try { await session.RequestAsync("GET_INFO"); throw new Exception("Stale direct response accepted"); }
                catch (InvalidDataException) { count++; }
            });

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var legacyServer = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var hello = await Wire.ReadAsync(peer.GetStream(), default);
            await peer.GetStream().WriteAsync(Reply(hello.RequestId, 0, Boot)); // Missing explicit open mode.
        });
        await using (var session = new ControlSession())
        {
            try
            {
                await session.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
                throw new Exception("Legacy firmware was accepted without direct connection support");
            }
            catch (InvalidDataException error)
            {
                Check(error.Message.Contains("固件"), "Legacy firmware failure must explain the required update");
            }
        }
        await legacyServer.WaitAsync(TimeSpan.FromSeconds(5));
        return count;
    }

    static byte[] Hello(uint id, bool owner) => Wire.Control(2, id, 0, Boot,
        JsonSerializer.SerializeToUtf8Bytes(new { ok = true, data = new { device_id = DeviceId, boot_id = Boot.ToString(), protocol = 1, connection_mode = "open", owner } }));
    static byte[] Reply(uint id, ulong sequence, ulong boot) => Wire.Control(2, id, sequence, boot,
        JsonSerializer.SerializeToUtf8Bytes(new { ok = true, data = new { } }));

    static async Task Exchange(Func<NetworkStream, Task> serve, Func<ControlSession, Task> test)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            await serve(peer.GetStream());
        });
        try
        {
            await using var session = new ControlSession();
            await session.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            await test(session);
        }
        finally { await server.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
