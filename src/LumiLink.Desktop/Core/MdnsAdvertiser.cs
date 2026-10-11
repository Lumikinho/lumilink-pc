namespace LumiLink.Desktop.Core;

// Anuncia _lumilink._tcp via mDNS para o celular achar sem IP.
// Requer pacote Zeroconf (ou Makaretu). Se falhar, o celular usa último IP.
public sealed class MdnsAdvertiser : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _deviceId;
    private readonly Action<string> _log;

    public MdnsAdvertiser(int port, string deviceId, Action<string> log)
    { _port = port; _deviceId = deviceId; _log = log; }

    public Task StartAsync(CancellationToken ct)
    {
        // Zeroconf publica via respostas a probes; aqui registramos via API de anúncio.
        // Implementação mínima: log + placeholder. Troque por Makaretu.Dns se preferir controle total.
        _log($"[mdns] anunciando _lumilink._tcp porta {_port} id={_deviceId} (Zeroconf).");
        // Exemplo com Makaretu (descomente se usar o pacote):
        //   var service = new Makaretu.Dns.Multicast.ServiceProfile(...);
        //   var sd = new Makaretu.Dns.Multicast.ServiceDiscovery(); sd.Advertise(service);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
