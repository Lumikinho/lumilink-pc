using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LumiLink.Desktop.Clipboard;

public sealed record ClipContent(string? Text, byte[]? ImageBytes, string ImageMime, string[] Files, string Hash);

// Poll da área de transferência numa thread STA dedicada (Clipboard exige STA).
public sealed class ClipMonitor : IDisposable
{
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private readonly int _intervalMs;
    private string _lastHash = "";
    private static readonly Dictionary<ulong, DateTime> _recentImg = new();
    private static readonly object _imgLock = new();
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly object _lock = new();

    public event Action<ClipContent>? Changed;

    public ClipMonitor(int intervalMs = 800) => _intervalMs = intervalMs;

    // Marca hash como "já visto" (anti-eco ao enviar/aplicar).
    public void Suppress(string hash, int seconds = 10)
    {
        lock (_lock) _recent[hash] = DateTime.UtcNow.AddSeconds(seconds);
    }

    private bool IsSuppressed(string hash)
    {
        lock (_lock)
        {
            Prune();
            return _recent.ContainsKey(hash);
        }
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _recent.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            _recent.Remove(k);
        foreach (var k in _recentImg.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            _recentImg.Remove(k);
    }

    // Anti-eco por CONTEÚDO da imagem (aHash): sobrevive a re-encode entre pares.
    public static void SuppressImage(ulong h, int seconds = 30)
    {
        lock (_imgLock) _recentImg[h] = DateTime.UtcNow.AddSeconds(seconds);
    }

    private static bool SeenImage(ulong h)
    {
        lock (_imgLock)
        {
            var now = DateTime.UtcNow;
            foreach (var k in _recentImg.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                _recentImg.Remove(k);
            return _recentImg.Keys.Any(k => Profile.ImageUtils.HammingDistance(k, h) <= 8);
        }
    }

    public void Start()
    {
        if (_thread is not null) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Loop(_cts.Token)) { IsBackground = true, Name = "clipmon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var c = Read();
                if (c is not null && c.Hash != _lastHash && !IsSuppressed(c.Hash))
                {
                    _lastHash = c.Hash;
                    Changed?.Invoke(c);
                }
                else if (c is not null) _lastHash = c.Hash;
            }
            catch { }
            Thread.Sleep(_intervalMs);
        }
    }

    private static ClipContent? Read()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var img = System.Windows.Clipboard.GetImage();
                if (img is null) return null;
                var (bytes, mime) = EncodeImage(img);
                if (bytes is null) return null;
                var ah = Profile.ImageUtils.AverageHash(bytes);
                if (ah.HasValue && SeenImage(ah.Value)) return null;
                if (ah.HasValue) SuppressImage(ah.Value);
                return new ClipContent(null, bytes, mime, Array.Empty<string>(),
                    ClipFileSession.ShaHex(bytes));
            }
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var list = System.Windows.Clipboard.GetFileDropList();
                if (list is null || list.Count == 0) return null;
                var files = list.Cast<string>().Where(File.Exists).Take(20).ToArray();
                if (files.Length == 0) return null;
                var total = files.Sum(f => new FileInfo(f).Length);
                if (total > 200L * 1024 * 1024) return null;
                return new ClipContent(null, null, "", files,
                    ClipFileSession.ShaHex(string.Join("|", files.OrderBy(x => x)
                        .Select(f => f + ":" + new FileInfo(f).Length))));
            }
            if (System.Windows.Clipboard.ContainsText())
            {
                var t = System.Windows.Clipboard.GetText();
                if (string.IsNullOrEmpty(t) || t.Length > 256 * 1024) return null;
                return new ClipContent(t, null, "", Array.Empty<string>(), ClipFileSession.ShaHex(t));
            }
        }
        catch { }
        return null;
    }

    // PNG; se >3MB, JPEG q75 com downscale até 1920px.
    public static (byte[]? bytes, string mime) EncodeImage(BitmapSource img)
    {
        try
        {
            using var ms = new MemoryStream();
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(img));
            png.Save(ms);
            var bytes = ms.ToArray();
            if (bytes.Length <= 3 * 1024 * 1024) return (bytes, "image/png");
            double scale = Math.Min(1.0, 1920.0 / Math.Max(img.PixelWidth, img.PixelHeight));
            var thumb = new TransformedBitmap(img, new System.Windows.Media.ScaleTransform(scale, scale));
            using var ms2 = new MemoryStream();
            var jpg = new JpegBitmapEncoder { QualityLevel = 75 };
            jpg.Frames.Add(BitmapFrame.Create(thumb));
            jpg.Save(ms2);
            var b2 = ms2.ToArray();
            return b2.Length <= 3 * 1024 * 1024 ? (b2, "image/jpeg") : (null, "");
        }
        catch { return (null, ""); }
    }

    public static BitmapSource? DecodeImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // Aplicar conteúdo remoto (pode vir de qualquer thread → marshala p/ STA).
    private static void OnSta(Action a)
    {
        try
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) a();
            else d.Invoke(a);
        }
        catch { }
    }

    public void SetText(string text) => OnSta(() =>
    {
        try { System.Windows.Clipboard.SetText(text); _lastHash = ClipFileSession.ShaHex(text); } catch { }
    });

    public void SetImage(byte[] bytes) => OnSta(() =>
    {
        try
        {
            var bmp = DecodeImage(bytes);
            if (bmp is null) return;
            System.Windows.Clipboard.SetImage(bmp);
            _lastHash = ClipFileSession.ShaHex(bytes);
        }
        catch { }
    });

    public void SetFiles(string[] paths) => OnSta(() =>
    {
        try
        {
            var coll = new System.Collections.Specialized.StringCollection();
            coll.AddRange(paths.Where(File.Exists).ToArray());
            if (coll.Count > 0) System.Windows.Clipboard.SetFileDropList(coll);
            _lastHash = ClipFileSession.ShaHex(string.Join("|", paths));
        }
        catch { }
    });

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
    }
}
