using System.Net;
using System.Net.Sockets;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Transport;
using Xunit;
using Xunit.Abstractions;

namespace LumiLink.Tests;

// Isola o transporte: servidor real + WsClient real, sem handshake de pares.
public sealed class TransportLoopbackTest
{
    private readonly ITestOutputHelper _out;
    public TransportLoopbackTest(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ServerToClientAndBack()
    {
        void Log(string s) => _out.WriteLine(s);
        var dir = Path.Combine(Path.GetTempPath(), "lltrans-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var certs = new CertManager(dir, Log);
        certs.EnsureCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var server = new SecureWebSocketServer(certs.Certificate, port, Log);
        var gotHello = new TaskCompletionSource<string>();
        server.OnMessage += async (ctx, msg) =>
        {
            Log($"[srv] tipo={msg.Type}");
            if (msg.Type == "hello") { gotHello.TrySetResult(msg.Id); await ctx.SendAsync(Message.Create("auth", new { nonce = "abc123" })); }
        };
        using var scts = new CancellationTokenSource();
        _ = server.StartAsync(scts.Token);
        await Task.Delay(1000);

        var client = new WsClient(Log);
        var gotAuth = new TaskCompletionSource<string>();
        client.OnText += raw =>
        {
            var m = Message.FromJson(raw);
            Log($"[cli] tipo={m?.Type}");
            if (m?.Type == "auth") gotAuth.TrySetResult(m.Payload.ToString());
            return Task.CompletedTask;
        };
        using var ccts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync("127.0.0.1", port, certs.Fingerprint, ccts.Token);
        await client.SendAsync(Message.Create("hello", new { deviceId = "x" }));

        var done = await Task.WhenAny(gotHello.Task, Task.Delay(8000));
        Assert.Same(gotHello.Task, done); // cliente→servidor
        var done2 = await Task.WhenAny(gotAuth.Task, Task.Delay(8000));
        Assert.Same(gotAuth.Task, done2); // servidor→cliente
        scts.Cancel();
        await client.DisposeAsync();
    }
}
