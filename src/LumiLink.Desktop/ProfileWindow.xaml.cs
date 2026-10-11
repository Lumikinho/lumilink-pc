using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LumiLink.Desktop.Profile;
using Microsoft.Win32;

namespace LumiLink.Desktop;

public partial class ProfileWindow : Window
{
    private readonly ProfileStore _store;
    private readonly Func<Task> _onSaved;
    private readonly Action<string> _log;
    private string _pendingPhoto;

    public ProfileWindow(ProfileStore store, Func<Task> onSaved, Action<string> log)
    {
        InitializeComponent();
        _store = store; _onSaved = onSaved; _log = log;
        _pendingPhoto = store.OwnPhotoPath();
        NameBox.Text = store.Current.Name;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        PreviewName.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? _store.Current.Name : NameBox.Text.Trim();
        var path = _pendingPhoto;
        if (File.Exists(path)) PhotoView.Source = PreviewPhoto.Source = new BitmapImage(new Uri(path));
    }

    private void BtnChange_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog() == true)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "lumilink_avatar.png");
            if (ImageUtils.TryPrepareAvatar(dlg.FileName, tmp, out _))
            {
                _pendingPhoto = tmp;
                NameBox_TextChanged();
                RefreshPreview();
            }
            else _log("[profile] imagem inválida.");
        }
    }

    private void NameBox_TextChanged() => RefreshPreview();

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        NameBox.Text = Environment.MachineName;
        ImageUtils.GenerateInitialsAvatar(_pendingPhoto = Path.Combine(Path.GetTempPath(), "lumilink_avatar.png"), NameBox.Text);
        RefreshPreview();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var name = ImageUtils.SanitizeName(NameBox.Text);
        if (string.IsNullOrEmpty(name)) { System.Windows.MessageBox.Show("Nome não pode ser vazio."); return; }
        var dst = _store.OwnPhotoPath();
        bool customPhoto = _pendingPhoto != dst;
        if (customPhoto) File.Copy(_pendingPhoto, dst, true);
        var hash = ProfileStore.HashOf(dst);
        // isCustom: nome difere do padrão? foto foi trocada?
        var profile = new DeviceProfile(_store.Current.DeviceId, name, hash,
            name != Environment.MachineName, customPhoto || _store.Current.PhotoCustom);
        _store.SaveOwn(profile);
        _log($"[profile] salvo: {name} ({hash[..Math.Min(8, hash.Length)]}…). Enviando profile_update…");
        await _onSaved();
        Close();
    }
}
