using System.IO;
using System.Windows;
using LumiLink.Desktop.Profile;
using Microsoft.Win32;

namespace LumiLink.Desktop;

public partial class ProfileWindow : Window
{
    private readonly ProfileStore _store;
    private readonly Func<Task> _onSaved;
    private readonly Action<string> _log;
    private string _pendingPhoto;
    private bool _resetToDefault;

    public ProfileWindow(ProfileStore store, Func<Task> onSaved, Action<string> log)
    {
        InitializeComponent();
        _store = store; _onSaved = onSaved; _log = log;
        _pendingPhoto = store.OwnPhotoPath();
        NameBox.Text = store.Current.Name;
        NameBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        PreviewName.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? _store.Current.Name : NameBox.Text.Trim();
        var img = ImageUtils.LoadPhoto(_pendingPhoto);
        if (img is not null) PhotoView.Source = PreviewPhoto.Source = img;
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
                _resetToDefault = false;
                RefreshPreview();
            }
            else _log("[profile] imagem inválida.");
        }
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        NameBox.Text = Environment.MachineName;
        _pendingPhoto = Path.Combine(Path.GetTempPath(), "lumilink_avatar.png");
        ImageUtils.GenerateInitialsAvatar(_pendingPhoto, NameBox.Text);
        _resetToDefault = true;
        RefreshPreview();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = ImageUtils.SanitizeName(NameBox.Text);
            if (string.IsNullOrEmpty(name)) { System.Windows.MessageBox.Show("Nome não pode ser vazio."); return; }
            var dst = _store.OwnPhotoPath();
            bool customPhoto = _resetToDefault ? false : (_pendingPhoto != dst || _store.Current.PhotoCustom);
            if (_pendingPhoto != dst) File.Copy(_pendingPhoto, dst, true);
            var hash = ProfileStore.HashOf(dst);
            var profile = new DeviceProfile(_store.Current.DeviceId, name, hash,
                name != Environment.MachineName, customPhoto);
            _store.SaveOwn(profile);
            _log($"[profile] salvo: {name} ({hash[..Math.Min(8, hash.Length)]}…). Enviando profile_update…");
            await _onSaved();
            Close();
        }
        catch (Exception ex)
        {
            _log($"[profile] salvar falhou: {ex.Message}");
            System.Windows.MessageBox.Show($"Não salvou: {ex.Message}");
        }
    }
}
