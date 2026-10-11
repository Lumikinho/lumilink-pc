using System.IO;
using System.Text.Json;

namespace LumiLink.Desktop.Storage;

// deviceId UUID (migra device.id antigo) + porta aleatória persistida.
public sealed class AppSettings
{
    public string DeviceId { get; set; } = "";
    public int Port { get; set; }

    public static AppSettings Load(string appDir)
    {
        var path = Path.Combine(appDir, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s is not null && s.Port > 0 && s.DeviceId.Length > 0) return s;
            }
        }
        catch { }
        string id = "";
        try
        {
            var legacy = Path.Combine(appDir, "device.id");
            if (File.Exists(legacy)) id = File.ReadAllText(legacy).Trim();
        }
        catch { }
        if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString();
        var s2 = new AppSettings { DeviceId = id, Port = Random.Shared.Next(40000, 50001) };
        try
        {
            Directory.CreateDirectory(appDir);
            File.WriteAllText(path, JsonSerializer.Serialize(s2, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return s2;
    }
}
