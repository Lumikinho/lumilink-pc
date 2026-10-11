using System.IO;
using System.IO;
using System.Windows;
using LumiLink.Desktop.Core;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Pairing;
using LumiLink.Desktop.Profile;
using LumiLink.Desktop.Storage;
using LumiLink.Desktop.Transport;

namespace LumiLink.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => CrashLog("Dispatcher", ex.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            CrashLog("AppDomain", ex.ExceptionObject as Exception);
    }

    private static void CrashLog(string src, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.AppendAllText(Path.Combine(AppDir, "log.txt"),
                $"[{DateTime.Now:HH:mm:ss}] [FATAL {src}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n");
        }
        catch { }
    }
    public static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumiLink");
    public static string DeviceId { get; } = LoadOrCreateDeviceId();
    public const int Port = 52431;

    private static string LoadOrCreateDeviceId()
    {
        try
        {
            var p = Path.Combine(AppDir, "device.id");
            if (File.Exists(p)) return File.ReadAllText(p).Trim();
            var id = "pc-" + Guid.NewGuid().ToString("N")[..12];
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(p, id);
            return id;
        }
        catch { return "pc-" + Environment.MachineName; }
    }
}
