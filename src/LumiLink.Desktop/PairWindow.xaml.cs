using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace LumiLink.Desktop;

public partial class PairWindow : Window
{
    public PairWindow(string token, string code, string ip, int port, string fingerprint, string deviceId)
    {
        InitializeComponent();
        var qr = JsonSerializer.Serialize(new { ip, port, token, fingerprint, deviceId });
        using var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(qr, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        using var ms = new MemoryStream(png.GetGraphic(10));
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.StreamSource = ms; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
        QrImage.Source = bmp;
        CodeLabel.Text = $"Código: {code[..3]} {code[3..]}";
        AddrLabel.Text = $"{ip}:{port}";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
