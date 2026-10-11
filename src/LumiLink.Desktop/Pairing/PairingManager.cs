using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace LumiLink.Desktop.Pairing;

// Token de uso único (2 min) + payload do QR.
public sealed class PairingManager
{
    private string? _token;
    private DateTime _tokenExpiry;
    private readonly Action<string> _log;

    public PairingManager(Action<string> log) => _log = log;

    public string NewToken(TimeSpan? ttl = null)
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _tokenExpiry = DateTime.UtcNow + (ttl ?? TimeSpan.FromMinutes(2));
        _log($"[pair] token gerado, expira em 2 min.");
        return _token;
    }

    public bool TryConsumeToken(string? token)
    {
        if (_token is null || token is null) return false;
        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token),
            System.Text.Encoding.UTF8.GetBytes(_token))) return false;
        if (DateTime.UtcNow > _tokenExpiry) { _token = null; _log("[pair] token expirado."); return false; }
        _token = null; // uso único
        return true;
    }

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // Prefere RFC1918 (rede local) e ignora VPN/virtuais (Tailscale, Docker, WSL...).
    // Antes retornava a 1ª interface — que podia ser o IP do Tailscale, inalcançável pelo celular.
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
                if (b[0] == 169 && b[1] == 254) continue; // link-local
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) continue; // CGNAT (Tailscale)
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
