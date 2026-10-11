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

    // Prefere RFC1918 e ignora VPN/virtuais (Tailscale, Docker, WSL...).
    public static string LocalIp()
    {
        string? fallback = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
            if (name.Contains("tailscale") || name.Contains("docker") || name.Contains("veth")
                || name.Contains("vmware") || name.Contains("virtualbox") || name.Contains("vpn")
                || name.Contains("tun") || name.Contains("tap") || name.Contains("wireguard")
                || name.Contains("hyper-v") || name.Contains("wsl")) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) continue;
                bool rfc1918 = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168);
                if (rfc1918) return ua.Address.ToString();
                fallback ??= ua.Address.ToString();
            }
        }
        return fallback ?? "127.0.0.1";
    }

    public string QrJson(string ip, int port, string token, string fingerprint, string deviceId)
        => JsonSerializer.Serialize(new { ip, port, token, fingerprint, deviceId });
}
