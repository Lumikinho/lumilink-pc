using LumiLink.Desktop.Profile;
using Xunit;

namespace LumiLink.Tests;

// aHash: mesmo conteúdo em PNG e JPEG deve casar (anti-eco do clipboard).
public sealed class ClipImageHashTests
{
    private static byte[] ToPng(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] ToJpeg(System.Drawing.Bitmap bmp, long quality = 75)
    {
        using var ms = new MemoryStream();
        var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        var ep = new System.Drawing.Imaging.EncoderParameters(1);
        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);
        bmp.Save(ms, enc, ep);
        return ms.ToArray();
    }

    private static System.Drawing.Bitmap Gradient()
    {
        var bmp = new System.Drawing.Bitmap(64, 64);
        for (var x = 0; x < 64; x++)
            for (var y = 0; y < 64; y++)
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(x * 4, y * 4, (x + y) % 256));
        return bmp;
    }

    [Fact]
    public void SameContentPngVsJpeg_Matches()
    {
        using var bmp = Gradient();
        var h1 = ImageUtils.AverageHash(ToPng(bmp));
        var h2 = ImageUtils.AverageHash(ToJpeg(bmp));
        Assert.NotNull(h1);
        Assert.NotNull(h2);
        Assert.True(ImageUtils.HammingDistance(h1!.Value, h2!.Value) <= 8);
    }

    [Fact]
    public void IdenticalBytes_ZeroDistance()
    {
        using var bmp = Gradient();
        var bytes = ToPng(bmp);
        var h1 = ImageUtils.AverageHash(bytes);
        var h2 = ImageUtils.AverageHash(bytes);
        Assert.NotNull(h1);
        Assert.NotNull(h2);
        Assert.Equal(0, ImageUtils.HammingDistance(h1!.Value, h2!.Value));
    }
}
