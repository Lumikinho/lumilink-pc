using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace LumiLink.Desktop.Pairing;

// Credencial de pareamento de uso único (2 min): token (QR) + código 6 dígitos (manual).
public sealed record PendingCredential(string Token, string Code, DateTime Expiry);

public sealed class PairingManager
{
    private PendingCredential? _pending;
    private readonly object _lock = new();
    private readonly Action<string> _log;

    public PairingManager(Action<string> log) => _log = log;

    public (string token, string code) NewPairing(TimeSpan? ttl = null)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var code = Random.Shared.Next(0, 1000000).ToString("D6");
        lock (_lock) _pending = new PendingCredential(token, code, DateTime.UtcNow + (ttl ?? TimeSpan.FromMinutes(2)));
        _log("[pair] credencial gerada (token + código 6 dígitos, 2 min).");
        return (token, code);
    }

    public bool TryConsume(string? tokenOrCode)
    {
        if (string.IsNullOrEmpty(tokenOrCode)) return false;
        lock (_lock)
        {
            if (_pending is null) return false;
            if (DateTime.UtcNow > _pending.Expiry) { _pending = null; _log("[pair] credencial expirada."); return false; }
            var ok = SlowEquals(tokenOrCode, _pending.Token) || tokenOrCode == _pending.Code;
            if (ok) _pending = null; // uso único
            return ok;
        }
    }

    private static bool SlowEquals(string a, string b)
    {
        var ab = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // IP primário: prefere RFC1918 (LAN, inclui PAN Bluetooth), depois Tailscale/CGNAT,
    // depois demais (PAN link-local por último). Nunca loopback.
    public static string LocalIp() => OrderedIps().FirstOrDefault() ?? "127.0.0.1";

    // Todos os IPv4 úteis ordenados: o handshake por ID testa cada um (fail-fast).
    public static List<string> OrderedIps()
    {
        var lan = new List<string>();
        var cgnat = new List<string>();
        var other = new List<string>();
        var linklocal = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var ip = ua.Address.ToString();
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) { if (!linklocal.Contains(ip)) linklocal.Add(ip); continue; }
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) { if (!cgnat.Contains(ip)) cgnat.Add(ip); continue; }
                bool rfc1918 = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168);
                var dst = rfc1918 ? lan : other;
                if (!dst.Contains(ip)) dst.Add(ip);
            }
        }
        return lan.Concat(cgnat).Concat(other).Concat(linklocal).ToList();
    }

    // Todos os IPv4 locais (p/ detectar o próprio anúncio no mDNS).
    public static HashSet<string> LocalIps()
    {
        var set = new HashSet<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    set.Add(ua.Address.ToString());
                }
            }
        }
        catch { }
        return set;
    }

    public string QrJson(string ip, int port, string token, string fingerprint, string deviceId, List<string>? ips = null)
        => JsonSerializer.Serialize(new { ip, port, token, fingerprint, deviceId, ips = ips ?? new List<string> { ip } });
}
