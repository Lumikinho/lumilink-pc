using System.IO;
using System.Text.Json;

namespace LumiLink.Desktop.Profile;

public sealed record DeviceProfile(string DeviceId, string Name, string PhotoHash, bool NameCustom, bool PhotoCustom);

// Próprio: profile.json + profile.png | Pares: peers/<deviceId>.json + .png
public sealed class ProfileStore
{
    private readonly string _appDir;
    public DeviceProfile Current { get; private set; }

    public ProfileStore(string appDir, string deviceId, string defaultName)
    {
        _appDir = appDir;
        Directory.CreateDirectory(Path.Combine(_appDir, "peers"));
        Current = LoadOwn(deviceId, defaultName);
    }

    private DeviceProfile LoadOwn(string deviceId, string defaultName)
    {
        try
        {
            var p = Path.Combine(_appDir, "profile.json");
            if (File.Exists(p))
            {
                var saved = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(p))!;
                // Se não customizado, relê padrão do sistema a cada abertura.
                var name = saved.NameCustom ? saved.Name : defaultName;
                var hash = saved.PhotoCustom ? saved.PhotoHash : HashOf(OwnPhotoPath());
                return saved with { DeviceId = deviceId, Name = name, PhotoHash = hash };
            }
        }
        catch { }
        return new DeviceProfile(deviceId, defaultName, HashOf(OwnPhotoPath()), false, false);
    }

    public void SaveOwn(DeviceProfile p)
    {
        Current = p;
        File.WriteAllText(Path.Combine(_appDir, "profile.json"),
            JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string OwnPhotoPath() => Path.Combine(_appDir, "profile.png");
    public string PeerJsonPath(string deviceId) => Path.Combine(_appDir, "peers", deviceId + ".json");
    public string PeerPhotoPath(string deviceId) => Path.Combine(_appDir, "peers", deviceId + ".png");

    public DeviceProfile? LoadPeer(string deviceId)
    {
        try
        {
            var p = PeerJsonPath(deviceId);
            return File.Exists(p) ? JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public void SavePeer(DeviceProfile p)
        => File.WriteAllText(PeerJsonPath(p.DeviceId),
            JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));

    public void ForgetPeer(string deviceId)
    {
        try { File.Delete(PeerJsonPath(deviceId)); } catch { }
        try { File.Delete(PeerPhotoPath(deviceId)); } catch { }
    }

    public static string HashOf(string photoPath)
    {
        try
        {
            if (!File.Exists(photoPath)) return "empty";
            return Transport.CertManager.Sha256Hex(File.ReadAllBytes(photoPath));
        }
        catch { return "empty"; }
    }
}
