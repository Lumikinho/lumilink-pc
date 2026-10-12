using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LumiLink.Desktop.UI;
using QRCoder;

namespace LumiLink.Desktop;

public partial class WifiQrWindow : Window
{
    private readonly Action _onManual;

    public WifiQrWindow(string name, string password, Action onManual)
    {
        InitializeComponent();
        _onManual = onManual;
        using var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(ScreenMirror.WifiQrText(name, password), QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        using var ms = new MemoryStream(png.GetGraphic(10));
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.StreamSource = ms; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
        QrImage.Source = bmp;
        SetStatus("Aguardando leitura do QR…");
    }

    public void SetStatus(string s) => StatusLabel.Text = s;

    private void BtnManual_Click(object sender, RoutedEventArgs e)
    {
        _onManual();
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
