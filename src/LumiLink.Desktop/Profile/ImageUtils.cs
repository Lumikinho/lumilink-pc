using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LumiLink.Desktop.Profile;

// Recorte quadrado central + resize 256x256 + compressão (~50KB).
public static class ImageUtils
{
    public static bool TryPrepareAvatar(string srcPath, string dstPath, out string hash, long maxBytes = 50 * 1024)
    {
        hash = "empty";
        try
        {
            using var src = Image.FromFile(srcPath);
            int side = Math.Min(src.Width, src.Height);
            int x = (src.Width - side) / 2, y = (src.Height - side) / 2;
            using var square = new Bitmap(side, side);
            using (var g = Graphics.FromImage(square))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, side, side), new Rectangle(x, y, side, side), GraphicsUnit.Pixel);
            }
            using var resized = new Bitmap(256, 256);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(square, 0, 0, 256, 256);
            }
            // Tenta JPEG q=80, reduz até caber.
            for (int q = 80; q >= 30; q -= 10)
            {
                using var ms = new MemoryStream();
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                resized.Save(ms, enc, ep);
                if (ms.Length <= maxBytes || q == 30)
                {
                    File.WriteAllBytes(dstPath, ms.ToArray());
                    hash = Transport.CertManager.Sha256Hex(ms.ToArray());
                    return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    public static bool ValidateIncomingPhoto(byte[] bytes, string expectedHash, long maxBytes = 100 * 1024)
    {
        if (bytes.Length > maxBytes) return false;
        if (!string.Equals(Transport.CertManager.Sha256Hex(bytes), expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
        try { using var img = Image.FromStream(new MemoryStream(bytes)); return img.Width > 0; }
        catch { return false; }
    }

    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var t = new string(name.Trim().Where(c => !char.IsControl(c)).ToArray());
        return t.Length > 32 ? t[..32] : t;
    }

    // Carrega para WPF sem travar o arquivo (OnLoad + Freeze). Null se falhar.
    public static System.Windows.Media.Imaging.BitmapImage? LoadPhoto(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // Padrão PC: foto da conta Windows ou avatar com iniciais.
    public static void EnsureDefaultPhoto(string dstPath, string displayName)
    {
        if (File.Exists(dstPath)) return;
        try
        {
            var pics = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\AccountPictures");
            var candidate = Directory.Exists(pics)
                ? Directory.GetFiles(pics, "*.accountpicture-ms", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            // .accountpicture-ms não é imagem direta; então geramos avatar com iniciais.
            GenerateInitialsAvatar(dstPath, displayName);
        }
        catch { GenerateInitialsAvatar(dstPath, displayName); }
    }

    public static void GenerateInitialsAvatar(string dstPath, string displayName)
    {
        var initials = string.Concat(displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0])));
        if (string.IsNullOrEmpty(initials)) initials = "?";
        using var bmp = new Bitmap(256, 256);
        using (var g = Graphics.FromImage(bmp))
        {
            var hue = Math.Abs(displayName.GetHashCode()) % 360;
            g.Clear(ColorFromHsl(hue, 0.5, 0.45));
            using var font = new Font("Segoe UI", 72, FontStyle.Bold);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(initials, font, Brushes.White, new RectangleF(0, 0, 256, 256), sf);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        bmp.Save(dstPath, ImageFormat.Png);
    }

    private static Color ColorFromHsl(int h, double s, double l)
    {
        // Conversão simples HSL->RGB
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60.0 % 2) - 1)), m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
