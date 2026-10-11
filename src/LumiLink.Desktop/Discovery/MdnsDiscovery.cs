using Makaretu.Dns;
using Zeroconf;

namespace LumiLink.Desktop.Discovery;

// Anuncia via Makaretu (TXT deviceId+name) e descobre via browse (id na instância).
public sealed class MdnsDiscovery : IAsyncDisposable
{
    private readonly ServiceDiscovery _sd = new();
    private ServiceProfile? _profile;
    private CancellationTokenSource? _cts;
    private readonly Action<string> _log;

    public event Action<string, string, int>? PeerSeen; // deviceId, ip, port

    public MdnsDiscovery(Action<string> log) => _log = log;

    public void Advertise(string deviceId, string name, int port)
    {
        try
        {
            _profile = new ServiceProfile($"LumiLink-{deviceId}", "_lumilink._tcp", (ushort)port);
            _profile.AddProperty("deviceId", deviceId);
            _profile.AddProperty("name", name);
            _sd.Advertise(_profile);
            _sd.Announce(_profile);
            _log($"[mdns] anunciando _lumilink._tcp porta {port} id={deviceId}.");
        }
        catch (Exception ex) { _log($"[mdns] anunciar falhou: {ex.Message} (usa último IP)."); }
    }

    public void StartBrowse()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(BrowseLoopAsync);
    }

    private async Task BrowseLoopAsync()
    {
        var ct = _cts!.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hosts = await ZeroconfResolver.ResolveAsync("_lumilink._tcp.local", TimeSpan.FromSeconds(4));
                foreach (var h in hosts)
                {
                    if (!h.IPAddress.Contains('.')) continue; // só IPv4
                    foreach (var svc in h.Services.Values)
                    {
                        var id = ParseId(svc.Name);
                        if (id is null) continue;
                        PeerSeen?.Invoke(id, h.IPAddress, svc.Port);
                    }
                }
            }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { break; }
        }
    }

    public static string? ParseId(string instance)
        => instance.StartsWith("LumiLink-", StringComparison.Ordinal) ? instance["LumiLink-".Length..] : null;

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { if (_profile is not null) _sd.Unadvertise(_profile); } catch { }
        await Task.CompletedTask;
    }
}
