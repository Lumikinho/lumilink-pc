using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LumiLink.Desktop.Core;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Pairing;
using LumiLink.Desktop.Profile;
using LumiLink.Desktop.Storage;
using LumiLink.Desktop.Transport;
using LumiLink.Desktop.UI;
using Microsoft.Win32;
using QRCoder;

namespace LumiLink.Desktop;

public partial class MainWindow : Window
{
    private readonly CertManager _certs;
    private readonly PairingManager _pairing = new(Log);
    private readonly PairStore _pairs;
    private readonly ProfileStore _profiles;
    private readonly MessageRouter _router;
    private readonly ConnectionManager _conns;
    private readonly SecureWebSocketServer _server;
    private TrayIconManager? _tray;
    private static MainWindow? _instance;

    public MainWindow()
    {
        InitializeComponent();
        _instance = this;
        Directory.CreateDirectory(App.AppDir);
        _certs = new CertManager(App.AppDir, Log);
        _certs.EnsureCertificate();
        _pairs = new PairStore(App.AppDir);
        var defaultName = Environment.MachineName;
        _profiles = new ProfileStore(App.AppDir, App.DeviceId, defaultName);
        ImageUtils.EnsureDefaultPhoto(_profiles.OwnPhotoPath(), _profiles.Current.Name);
        _router = new MessageRouter(Log);
        _conns = new ConnectionManager(_pairing, _pairs, _profiles, _router, Log);
        _server = new SecureWebSocketServer(_certs.Certificate, App.Port, Log);
        _conns.AttachServer(_server);
        _conns.StateChanged += s => Dispatcher.Invoke(() =>
        {
            StateLabel.Text = s;
            _tray?.SetTooltip(s);
            RefreshPairs();
        });
        _tray = new TrayIconManager("LumiLink", RestoreWindow, () => Close());
        Loaded += async (_, _) =>
        {
            try
            {
                Log("[app] iniciando...");
                RefreshPairs();
                ChkAutostart.IsChecked = IsAutostart();
                _server.EnsureFirewallRule(App.Port);
                _ = new MdnsAdvertiser(App.Port, App.DeviceId, Log).StartAsync(CancellationToken.None);
                await _server.StartAsync(CancellationToken.None); // async, não bloqueia UI (Kestrel em background)
            }
            catch (Exception ex)
            {
                Log($"[FATAL startup] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        };
        Closing += OnClosing;
    }

    public static void Log(string s)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {s}";
        try { File.AppendAllText(Path.Combine(App.AppDir, "log.txt"), line + "\n"); } catch { }
        _instance?.Dispatcher.Invoke(() => { _instance.LogBox.Text += line + "\n"; });
    }

    private void BtnPair_Click(object sender, RoutedEventArgs e)
    {
        var token = _pairing.NewToken();
        var qr = _pairing.QrJson(PairingManager.LocalIp(), App.Port, token, _certs.Fingerprint, App.DeviceId);
        using var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(qr, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        var bytes = code.GetGraphic(10);
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.StreamSource = ms; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
        QrImage.Source = bmp;
        Log("[pair] QR gerado (token 2 min). Escaneie com o celular.");
    }

    private void BtnForget_Click(object sender, RoutedEventArgs e)
    {
        if (PairList.SelectedItem is string line)
        {
            var id = line.Split(' ')[0];
            _pairs.Forget(id);
            _profiles.ForgetPeer(id);
            Log($"[pair] esquecido {id}.");
            RefreshPairs();
        }
    }

    private void BtnProfile_Click(object sender, RoutedEventArgs e)
        => new ProfileWindow(_profiles, async () => await _conns.BroadcastProfileUpdateAsync(), Log).ShowDialog();

    private void RefreshPairs()
    {
        PairList.Items.Clear();
        foreach (var p in _pairs.All)
        {
            var cached = _profiles.LoadPeer(p.DeviceId);
            PairList.Items.Add($"{p.DeviceId} — {cached?.Name ?? p.Name}");
        }
        var first = _pairs.All.FirstOrDefault();
        if (first is not null)
        {
            var cached = _profiles.LoadPeer(first.DeviceId);
            PeerName.Text = cached?.Name ?? first.Name;
            var photo = _profiles.PeerPhotoPath(first.DeviceId);
            if (File.Exists(photo)) PeerPhoto.Source = new BitmapImage(new Uri(photo));
        }
    }

    private static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return k?.GetValue("LumiLink") is not null;
    }

    private void ChkAutostart_Changed(object sender, RoutedEventArgs e)
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)!;
        if (ChkAutostart.IsChecked == true)
            k.SetValue("LumiLink", Environment.ProcessPath ?? "");
        else
            k.DeleteValue("LumiLink", false);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Minimizar para a bandeja em vez de sair.
        e.Cancel = true;
        Hide();
    }

    private void RestoreWindow() => Dispatcher.Invoke(() => { base.Show(); WindowState = WindowState.Normal; });
}
