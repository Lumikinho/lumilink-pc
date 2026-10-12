using System.IO;
using System.Text.Json;

namespace LumiLink.Desktop.Storage;

// deviceId é UUID v4. Id legado (não-UUID) é migrado uma vez (vira UUID novo).
// Porta aleatória persistida.
public sealed class AppSettings
{
    public string DeviceId { get; set; } = "";
    public int Port { get; set; }
    public bool ClipSync { get; set; } = true;

    public static AppSettings Load(string appDir)
    {
        var path = Path.Combine(appDir, "settings.json");
        AppSettings? s = null;
        try
        {
            if (File.Exists(path)) s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch { }
        var changed = false;
        if (s is null)
        {
            string id = "";
            try
            {
                var legacy = Path.Combine(appDir, "device.id");
                if (File.Exists(legacy)) id = File.ReadAllText(legacy).Trim();
            }
            catch { }
            if (!Guid.TryParse(id, out _)) id = Guid.NewGuid().ToString();
            s = new AppSettings { DeviceId = id, Port = Random.Shared.Next(40000, 50001) };
            changed = true;
        }
        if (!Guid.TryParse(s.DeviceId, out _)) { s.DeviceId = Guid.NewGuid().ToString(); changed = true; }
        if (s.Port <= 0 || s.Port > 65535) { s.Port = Random.Shared.Next(40000, 50001); changed = true; }
        if (changed)
        {
            try
            {
                Directory.CreateDirectory(appDir);
                File.WriteAllText(path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
        return s;
    }
}
