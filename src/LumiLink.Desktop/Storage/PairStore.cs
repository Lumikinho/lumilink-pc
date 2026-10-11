using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace LumiLink.Desktop.Storage;

public sealed record PairRecord(string DeviceId, string Name, string ProtectedSecret, DateTime PairedAt);

// pairs.json com segredo protegido via DPAPI (CurrentUser).
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

    public void Save(string deviceId, string name, string secretPlain)
    {
        var prot = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(secretPlain),
            null, DataProtectionScope.CurrentUser);
        lock (_lock)
        {
            _pairs[deviceId] = new PairRecord(deviceId, name, Convert.ToBase64String(prot), DateTime.UtcNow);
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

    public void UpdateName(string deviceId, string name)
    {
        lock (_lock)
        {
            if (_pairs.TryGetValue(deviceId, out var r))
            { _pairs[deviceId] = r with { Name = name }; Persist(); }
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
