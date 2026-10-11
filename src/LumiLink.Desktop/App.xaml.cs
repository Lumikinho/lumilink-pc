using System.IO;
using System.Windows;
using LumiLink.Desktop.Storage;

namespace LumiLink.Desktop;

public partial class App : System.Windows.Application
{
    public static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumiLink");
    public static AppSettings Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => CrashLog("Dispatcher", ex.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            CrashLog("AppDomain", ex.ExceptionObject as Exception);
        Directory.CreateDirectory(AppDir);
        Settings = AppSettings.Load(AppDir);
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
}
