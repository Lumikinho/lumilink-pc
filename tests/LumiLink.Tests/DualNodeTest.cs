using System.Net;
using System.Net.Sockets;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Pairing;
using LumiLink.Desktop.Peers;
using LumiLink.Desktop.Profile;
using LumiLink.Desktop.Storage;
using LumiLink.Desktop.Transport;
using Xunit;
using Xunit.Abstractions;

namespace LumiLink.Tests;

// Integração: dois nós in-process (loopback) — pareia, conecta (auth mútua),
// derruba, reconecta via backoff e confere o perfil trocado.
public sealed class DualNodeTest
{
    private readonly ITestOutputHelper _out;
    public DualNodeTest(ITestOutputHelper output) => _out = output;

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lltest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private PeerManager MakeNode(string dir, int port, string name, string? id = null)
    {
        void Log(string s) => _out.WriteLine($"[{name}] {s}");
        var settings = new AppSettings { DeviceId = id ?? Guid.NewGuid().ToString(), Port = port };
        var certs = new CertManager(dir, Log);
        certs.EnsureCertificate();
        var pairs = new PairStore(dir);
        var profiles = new ProfileStore(dir, settings.DeviceId, name);
        return new PeerManager(settings, certs, pairs, profiles, new PairingManager(Log), new MessageRouter(Log), Log);
    }

    private static async Task PollAsync(Func<bool> cond, TimeSpan timeout, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (cond()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("timeout esperando: " + what);
    }

    [Fact]
    public async Task PairConnectDropReconnectAndProfile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var dirA = TempDir(); var dirB = TempDir();
        var portA = FreePort(); var portB = FreePort();
        await using var mgrA = MakeNode(dirA, portA, "Node-A");
        await using var mgrB = MakeNode(dirB, portB, "Node-B");
        _ = mgrA.StartAsync(cts.Token);
        _ = mgrB.StartAsync(cts.Token);
        await Task.Delay(1500); // Kestrel sobe

        // 1. pareamento: A mostra credencial, B disca com token + fingerprint.
        var (token, _) = mgrA.CreatePairing();
        await mgrB.PairToAsync("127.0.0.1", portA, token, mgrA.OwnFingerprint);
        var idA = mgrA.DeviceId; var idB = mgrB.DeviceId;

        // 2. ambos conectados (auth mútua implícita no estado).
        await PollAsync(() =>
            mgrA.GetPeers().Any(p => p.DeviceId == idB && p.State == "Conectado") &&
            mgrB.GetPeers().Any(p => p.DeviceId == idA && p.State == "Conectado"),
            TimeSpan.FromSeconds(30), "conexão mútua");

        // 3. segredo igual dos dois lados + perfil trocado.
        var pairsA = new PairStore(dirA); var pairsB = new PairStore(dirB);
        Assert.Equal(pairsA.GetSecret(idB), pairsB.GetSecret(idA));
        Assert.Equal("Node-A", mgrB.GetPeers().First(p => p.DeviceId == idA).Name);
        Assert.Equal("Node-B", mgrA.GetPeers().First(p => p.DeviceId == idB).Name);

        // 4. derruba e reconecta sozinho (backoff por par).
        await mgrA.DropLinkAsync(idB);
        await PollAsync(() =>
            mgrA.GetPeers().Any(p => p.DeviceId == idB && p.State == "Conectado") &&
            mgrB.GetPeers().Any(p => p.DeviceId == idA && p.State == "Conectado"),
            TimeSpan.FromSeconds(45), "reconexão");

        // 5. perfil segue íntegro após reconectar.
        Assert.Equal("Node-A", mgrB.GetPeers().First(p => p.DeviceId == idA).Name);
    }

    [Fact]
    public void RateLimiterBlocksAfter5Fails()
    {
        var r = new RateLimiter();
        for (var i = 0; i < 4; i++) r.Fail("9.9.9.9");
        Assert.False(r.IsBlocked("9.9.9.9"));
        r.Fail("9.9.9.9"); // 5ª em 1 min → bloqueia 5 min
        Assert.True(r.IsBlocked("9.9.9.9"));
        r.Success("9.9.9.9");
        Assert.False(r.IsBlocked("9.9.9.9"));
    }
}
