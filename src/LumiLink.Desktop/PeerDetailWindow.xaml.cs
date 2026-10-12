using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LumiLink.Desktop.Peers;
using LumiLink.Desktop.Profile;

namespace LumiLink.Desktop;

public partial class PeerDetailWindow : Window
{
    private readonly PeerManager _peers;
    private readonly ProfileStore _profiles;
    private readonly string _id;

    public PeerDetailWindow(PeerManager peers, ProfileStore profiles, string id)
    {
        InitializeComponent();
        _peers = peers; _profiles = profiles; _id = id;
        Refresh();
    }

    private void Refresh()
    {
        var p = _peers.GetPeers().FirstOrDefault(x => x.DeviceId == _id);
        if (p is null) { Close(); return; }
        NameLabel.Text = p.Name;
        StateLabel.Text = $"Estado: {p.State}";
        MediaLabel.Text = string.IsNullOrEmpty(p.Media) ? "" : $"Ouvindo: {p.Media}";
        IdLabel.Text = $"id: {p.DeviceId}";
        AddrLabel.Text = $"Último IP: {(string.IsNullOrEmpty(p.LastIp) ? "(descoberta pendente)" : p.LastIp)}";
        SeenLabel.Text = $"Última conexão: {(p.LastSeen == default ? "nunca" : p.LastSeen.ToLocalTime().ToString("g"))}";
        var photo = _profiles.PeerPhotoPath(_id);
        var img = Profile.ImageUtils.LoadPhoto(photo);
        if (img is not null) PhotoView.Source = img;
    }

    private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
    {
        try { await _peers.ReconnectNowAsync(_id); Refresh(); }
        catch (Exception ex) { ErrLabel.Text = ex.Message; }
    }

    private async void BtnForget_Click(object sender, RoutedEventArgs e)
    {
        await _peers.ForgetAsync(_id);
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
