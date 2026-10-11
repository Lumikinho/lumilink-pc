using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace LumiLink.Desktop.Storage;

// Um registro por par (multi-par). Segredo via DPAPI. Campos novos têm default
// para pares antigos (CertFingerprint vazio = aprende no próximo hello).
public sealed record PairRecord(
    string DeviceId,
    string Name,
    string ProtectedSecret,
    DateTime PairedAt,
    string CertFingerprint = "",
    string LastIp = "",
    int LastPort = 0,
    string PhotoHash = "empty",
    DateTime LastSeen = default,
    string KnownIps = ""); // csv dos IPs anunciados pelo par (LAN, Tailscale, PAN…)

public sealed class PairStore
{
    private readonly string _path;
    private readonly Dictionary<string, PairRecord> _pairs = new();
    private readonly object _lock = new();

    public PairStore(string appDir)
    {
        _path = Path.Combine(appDir, "pairs.json");
        Load();
    }

    public IReadOnlyCollection<PairRecord> All { get { lock (_lock) return _pairs.Values.ToList(); } }
    public bool IsPaired(string deviceId) { lock (_lock) return _pairs.ContainsKey(deviceId); }
    public PairRecord? Get(string deviceId)
    { lock (_lock) return _pairs.TryGetValue(deviceId, out var r) ? r : null; }

    public void SavePeer(string deviceId, string name, string secretPlain,
        string certFp, string ip, int port, string photoHash, string knownIps = "")
    {
        var prot = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(secretPlain), null, DataProtectionScope.CurrentUser);
        lock (_lock)
        {
            var prev = _pairs.TryGetValue(deviceId, out var p) ? p : null;
            var keepIps = string.IsNullOrEmpty(knownIps) ? (prev?.KnownIps ?? "") : knownIps;
            _pairs[deviceId] = new PairRecord(deviceId, name, Convert.ToBase64String(prot),
                prev?.PairedAt ?? DateTime.UtcNow,
                string.IsNullOrEmpty(certFp) ? (prev?.CertFingerprint ?? "") : certFp,
                string.IsNullOrEmpty(ip) ? (prev?.LastIp ?? "") : ip,
                port != 0 ? port : (prev?.LastPort ?? 0),
                string.IsNullOrEmpty(photoHash) ? (prev?.PhotoHash ?? "empty") : photoHash,
                DateTime.UtcNow,
                keepIps);
            Persist();
        }
    }

    public void Touch(string deviceId, string ip, int port, string knownIps = "")
    {
        lock (_lock)
        {
            if (!_pairs.TryGetValue(deviceId, out var r)) return;
            _pairs[deviceId] = r with
            {
                LastSeen = DateTime.UtcNow,
                LastIp = string.IsNullOrEmpty(ip) ? r.LastIp : ip,
                LastPort = port != 0 ? port : r.LastPort,
                KnownIps = string.IsNullOrEmpty(knownIps) ? r.KnownIps : knownIps
            };
            Persist();
        }
    }

    public void UpdateName(string deviceId, string name, string photoHash)
    {
        lock (_lock)
        {
            if (!_pairs.TryGetValue(deviceId, out var r)) return;
            _pairs[deviceId] = r with { Name = name, PhotoHash = photoHash, LastSeen = DateTime.UtcNow };
            Persist();
        }
    }

    public string? GetSecret(string deviceId)
    {
        lock (_lock)
        {
            if (!_pairs.TryGetValue(deviceId, out var r)) return null;
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(r.ProtectedSecret), null, DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(raw);
            }
            catch { return null; }
        }
    }

    public void Forget(string deviceId)
    {
        lock (_lock) { _pairs.Remove(deviceId); Persist(); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<PairRecord>>(File.ReadAllText(_path)) ?? new();
            foreach (var r in list) _pairs[r.DeviceId] = r;
        }
        catch { }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_pairs.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
}
