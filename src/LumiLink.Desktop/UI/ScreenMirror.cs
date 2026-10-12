using System.Diagnostics;
using System.IO;

namespace LumiLink.Desktop.UI;

// Espelhamento + controle da tela do Android via scrcpy (ferramenta externa, adb).
// O tráfego de tela vai pelo adb — não pelo canal wss (que segue p/ resto).
public static class ScreenMirror
{
    public static string? FindScrcpy()
    {
        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Packages"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith("scrcpy", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Genymobile", StringComparison.OrdinalIgnoreCase))
                    {
                        var hit = Directory.GetFiles(dir, "scrcpy.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (hit is not null) return hit;
                    }
                }
            }
            var where = Run("where", "scrcpy.exe");
            var first = where.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(".exe"));
            if (first is not null && File.Exists(first)) return first;
        }
        catch { }
        return null;
    }

    public static string? FindAdb(string? scrcpyPath)
    {
        try
        {
            if (scrcpyPath is not null)
            {
                var sibling = Path.Combine(Path.GetDirectoryName(scrcpyPath)!, "adb.exe");
                if (File.Exists(sibling)) return sibling;
            }
            var where = Run("where", "adb.exe");
            var first = where.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(".exe"));
            if (first is not null && File.Exists(first)) return first;
        }
        catch { }
        return null;
    }

    public static List<string> AdbDevices(string adb)
    {
        var out_ = new List<string>();
        try
        {
            foreach (var line in Run($"\"{adb}\"", "devices").Split('\n'))
            {
                var parts = line.Trim().Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == "device") out_.Add(parts[0]);
            }
        }
        catch { }
        return out_;
    }

    public static void MirrorUsb(string scrcpy, string serial, string title)
    {
        Process.Start(new ProcessStartInfo(scrcpy,
            $"-s {serial} --window-title \"{title}\" --stay-awake")
        { UseShellExecute = false, CreateNoWindow = true });
    }

    // Pareamento adb sem fio: envia o código via stdin (não-interativo).
    public static void PairWifi(string adb, string ip, string pairPort, string code)
    {
        var psi = new ProcessStartInfo($"\"{adb}\"", $"pair {ip}:{pairPort}")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("adb não iniciou.");
        p.StandardInput.WriteLine(code);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } throw new TimeoutException("adb pair demorou."); }
        if (!output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("pareamento adb falhou: " + output.Trim());
    }

    // Wi-Fi direto: pareia, acha o endpoint e espelha. Sem USB.
    public static async Task MirrorWifiPairAsync(string adb, string scrcpy, string ip,
        string pairPort, string code, string title, Action<string> log)
    {
        await Task.Run(() => PairWifi(adb, ip, pairPort, code));
        log("[mirror] pareado no adb. Procurando endpoint…");
        await Task.Delay(2000);
        for (var i = 0; i < 6; i++)
        {
            var target = AdbDevices(adb).FirstOrDefault(d => d.StartsWith(ip + ":"));
            if (target is not null)
            {
                Process.Start(new ProcessStartInfo(scrcpy,
                    $"-s {target} --window-title \"{title}\" --stay-awake")
                { UseShellExecute = false, CreateNoWindow = true });
                log($"[mirror] espelhando via Wi-Fi ({target}).");
                return;
            }
            await Task.Delay(2000);
        }
        throw new InvalidOperationException("pareou mas o endpoint não apareceu.");
    }

    // Wi-Fi: tenta conectar direto; se não der e houver USB, ativa tcpip e conecta.
    public static async Task MirrorWifiAsync(string adb, string scrcpy, string ip, string title, Action<string> log)
    {
        var target = $"{ip}:5555";
        log($"[mirror] conectando adb em {target}…");
        Run($"\"{adb}\"", $"connect {target}");
        await Task.Delay(1500);
        if (!AdbDevices(adb).Any(d => d == target))
        {
            var usb = AdbDevices(adb).FirstOrDefault(d => !d.Contains(':'));
            if (usb is null) throw new InvalidOperationException("sem USB e sem adb Wi-Fi (ative a depuração sem fio).");
            log("[mirror] ativando tcpip pelo USB…");
            Run($"\"{adb}\"", $"-s {usb} tcpip 5555");
            await Task.Delay(2000);
            Run($"\"{adb}\"", $"connect {target}");
            await Task.Delay(1500);
            if (!AdbDevices(adb).Any(d => d == target))
                throw new InvalidOperationException($"adb não alcançou {target}.");
        }
        Process.Start(new ProcessStartInfo(scrcpy,
            $"-s {target} --window-title \"{title}\" --stay-awake")
        { UseShellExecute = false, CreateNoWindow = true });
        log($"[mirror] espelhando via Wi-Fi ({target}).");
    }

    private static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output;
        }
        catch { return ""; }
    }
}
