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

    public static string LocalIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    return ua.Address.ToString();
        }
        return "127.0.0.1";
    }

    public string QrJson(string ip, int port, string token, string fingerprint, string deviceId)
        => JsonSerializer.Serialize(new { ip, port, token, fingerprint, deviceId });
}
