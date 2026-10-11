using System.ComponentModel;
using System.IO;
using System.Windows;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Pairing;
using LumiLink.Desktop.Peers;
using LumiLink.Desktop.Profile;
using LumiLink.Desktop.Storage;
using LumiLink.Desktop.Transport;
using LumiLink.Desktop.UI;
using Microsoft.Win32;

namespace LumiLink.Desktop;

public partial class MainWindow : Window
{
    private readonly CertManager _certs;
    private readonly PairingManager _pairing = new(Log);
    private readonly PairStore _pairs;
    private readonly ProfileStore _profiles;
    private readonly MessageRouter _router;
    private readonly PeerManager _peers;
    private TrayIconManager? _tray;
    private static MainWindow? _instance;
    private CancellationTokenSource? _runCts;

    public MainWindow()
    {
        InitializeComponent();
        _instance = this;
        Directory.CreateDirectory(App.AppDir);
        _certs = new CertManager(App.AppDir, Log);
        _certs.EnsureCertificate();
        _pairs = new PairStore(App.AppDir);
        _profiles = new ProfileStore(App.AppDir, App.Settings.DeviceId, Environment.MachineName);
        ImageUtils.EnsureDefaultPhoto(_profiles.OwnPhotoPath(), _profiles.Current.Name);
        _router = new MessageRouter(Log);
        _peers = new PeerManager(App.Settings, _certs, _pairs, _profiles, _pairing, _router, Log);
        _peers.Changed += () => Dispatcher.Invoke(RefreshPeers);
        _peers.Notify += (title, text) => Dispatcher.Invoke(() => _tray?.Balloon(title, text));
        _tray = new TrayIconManager("LumiLink", RestoreWindow, () => Close());
        Loaded += async (_, _) =>
        {
            try
            {
                Log("[app] iniciando...");
                RefreshPeers();
                ChkAutostart.IsChecked = IsAutostart();
                _runCts = new CancellationTokenSource();
                await _peers.StartAsync(_runCts.Token);
            }
            catch (Exception ex) { Log($"[FATAL startup] {ex.Message}"); }
        };
        Closing += OnClosing;
    }

    public static void Log(string s)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {s}";
        try { File.AppendAllText(Path.Combine(App.AppDir, "log.txt"), line + "\n"); } catch { }
        _instance?.Dispatcher.Invoke(() => { _instance.LogBox.Text += line + "\n"; });
    }

    private PeerRow? Selected => PeerList.SelectedItem as PeerRow;

    private void RefreshPeers()
    {
        var rows = _peers.GetPeers().Select(p => new PeerRow(
            p.DeviceId, p.Name, p.State, p.LastIp,
            ImageUtils.LoadPhoto(_profiles.PeerPhotoPath(p.DeviceId)))).ToList();
        PeerList.ItemsSource = rows;
        var up = rows.Count(p => p.State == "Conectado");
        StateLabel.Text = up == 0 ? "Desconectado" : $"Conectado a {up} dispositivo(s)";
        SelfInfo.Text = $"id={App.Settings.DeviceId[..8]}… porta={App.Settings.Port}";
        _tray?.SetTooltip(StateLabel.Text);
    }

    private void BtnPair_Click(object sender, RoutedEventArgs e)
    {
        var (token, code) = _peers.CreatePairing();
        new PairWindow(token, code, PairingManager.LocalIp(), App.Settings.Port,
            _certs.Fingerprint, App.Settings.DeviceId).ShowDialog();
    }

    private void BtnManual_Click(object sender, RoutedEventArgs e)
        => new ManualWindow(async (ip, port, cred) => await _peers.PairToAsync(ip, port, cred, null)).ShowDialog();

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshPeers();
        Log("[ui] lista/estado atualizados.");
    }

    private async void BtnPing_Click(object sender, RoutedEventArgs e)
    {
        var peer = Selected ?? (PeerList.ItemsSource as List<PeerRow>)?.FirstOrDefault(p => p.State == "Conectado");
        if (peer is null)
        {
            System.Windows.MessageBox.Show("Selecione um par conectado.");
            return;
        }
        try
        {
            var rtt = await _peers.PingAsync(peer.DeviceId, TimeSpan.FromSeconds(10));
            Log($"[ping] {peer.Name}: pong em {(int)rtt.TotalMilliseconds}ms.");
            _tray?.Balloon("Pong recebido", $"{peer.Name} em {(int)rtt.TotalMilliseconds}ms.");
        }
        catch (Exception ex) { Log($"[ping] {peer.Name}: sem resposta ({ex.Message})."); }
    }

    private void BtnShare_Click(object sender, RoutedEventArgs e)
    {
        var peer = Selected ?? (PeerList.ItemsSource as List<PeerRow>)?.FirstOrDefault(p => p.State == "Conectado");
        if (peer is null)
        {
            System.Windows.MessageBox.Show("Nenhum dispositivo conectado para mostrar.");
            return;
        }
        var photo = _profiles.PeerPhotoPath(peer.DeviceId);
        WindowsShareHelper.ShareDevice(this, peer.Name, peer.State,
            $"id: {peer.DeviceId}\nip: {(string.IsNullOrEmpty(peer.LastIp) ? "—" : peer.LastIp)}",
            System.IO.File.Exists(photo) ? photo : null);
        Log($"[share] painel Compartilhar aberto para {peer.Name}.");
    }

    private async void BtnForget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        await _peers.ForgetAsync(Selected.DeviceId);
    }

    private void BtnDetail_Click(object sender, RoutedEventArgs e) => OpenDetail();
    private void PeerList_DoubleClick(object sender, RoutedEventArgs e) => OpenDetail();

    private void OpenDetail()
    {
        if (Selected is null) return;
        new PeerDetailWindow(_peers, _profiles, Selected.DeviceId).ShowDialog();
        RefreshPeers();
    }

    private void BtnProfile_Click(object sender, RoutedEventArgs e)
        => new ProfileWindow(_profiles, async () => await _peers.BroadcastProfileUpdateAsync(), Log).ShowDialog();

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
        e.Cancel = true;
        Hide();
    }

    private void RestoreWindow() => Dispatcher.Invoke(() => { base.Show(); WindowState = WindowState.Normal; });
}

public sealed class PeerRow
{
    public string DeviceId { get; }
    public string Name { get; }
    public string State { get; }
    public string LastIp { get; }
    public System.Windows.Media.Imaging.BitmapImage? Photo { get; }
    public PeerRow(string deviceId, string name, string state, string lastIp, System.Windows.Media.Imaging.BitmapImage? photo)
    { DeviceId = deviceId; Name = name; State = state; LastIp = lastIp; Photo = photo; }
}
