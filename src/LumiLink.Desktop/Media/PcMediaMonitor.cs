using System.IO;
using Windows.Media.Control;

namespace LumiLink.Desktop.MediaSync;

// Espelha a mídia do PC (Spotify, navegador…) para os pares via media_pc
// e executa comandos vindos do celular (media_cmd).
public sealed class PcMediaMonitor : IDisposable
{
    private readonly Peers.PeerManager _peers;
    private readonly Action<string> _log;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _lastKey = "";
    private string _lastArtHash = "";
    private long _lastPosSent;
    private bool _started;
    private readonly object _lock = new();

    public PcMediaMonitor(Peers.PeerManager peers, Action<string> log)
    {
        _peers = peers;
        _log = log;
    }

    public async Task StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (_, _) => _ = RefreshAsync();
            await RefreshAsync();
            _started = true;
            _ = PositionLoopAsync();
        }
        catch (Exception ex) { _log($"[media] GSMTC indisponível: {ex.Message}"); }
    }

    // Mantém o tempo sincronizado: a cada 20s tocando reenvia a posição.
    private async Task PositionLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                await SendIfChangedAsync();
            }
        }
        catch { }
    }

    private async Task RefreshAsync()
    {
        try
        {
            Unhook();
            _session = _mgr?.GetCurrentSession();
            if (_session is null) { await SendIfChangedAsync(force: true); return; }
            _session.MediaPropertiesChanged += (_, _) => _ = SendIfChangedAsync();
            _session.PlaybackInfoChanged += (_, _) => _ = SendIfChangedAsync();
            _session.TimelinePropertiesChanged += (_, _) => _ = SendIfChangedAsync();
            await SendIfChangedAsync(force: true);
        }
        catch { }
    }

    private void Unhook()
    {
        // Eventos do WinRT não têm remoção simples aqui; troca de sessão é rara.
        // O GC cuida da sessão antiga (sem referência guardada além desta).
        _session = null;
    }

    private async Task SendIfChangedAsync(bool force = false)
    {
        GlobalSystemMediaTransportControlsSession? s;
        lock (_lock) s = _session;
        if (!_started && !force) return;
        if (s is null)
        {
            if (_lastKey != "")
            {
                _lastKey = "";
                await _peers.BroadcastAsync(Messaging.Message.Create("media_pc",
                    new { title = "", artist = "", app = "", art = "", dur = 0L, pos = 0L, playing = false }));
            }
            return;
        }
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            var title = props?.Title ?? "";
            if (string.IsNullOrWhiteSpace(title)) return;
            var artist = props?.Artist ?? "";
            var app = FriendlyApp(s.SourceAppUserModelId);
            var playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var paused = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            var state = playing ? "playing" : (paused ? "paused" : "stopped");
            long dur = 0, pos = 0;
            try { dur = (long)(timeline?.EndTime.TotalMilliseconds ?? 0); } catch { }
            try { pos = (long)(timeline?.Position.TotalMilliseconds ?? 0); } catch { }
            var key = $"{title}|{artist}|{state}|{dur}";
            string artB64 = "";
            var artBytes = await ReadArtAsync(props, 300);
            if (artBytes is not null)
            {
                var ah = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artBytes))[..16];
                if (ah != _lastArtHash) { _lastArtHash = ah; artB64 = Convert.ToBase64String(artBytes); }
            }
            var posJump = Math.Abs(pos - _lastPosSent) > 5000;
            if (!force && key == _lastKey && !posJump && artB64.Length == 0) return;
            _lastKey = key;
            _lastPosSent = pos;
            await _peers.BroadcastAsync(Messaging.Message.Create("media_pc", new
            {
                title, artist, app, art = artB64, dur, pos, playing, state,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }));
        }
        catch { }
    }

    private static string FriendlyApp(string aumid)
    {
        try
        {
            var s = aumid.Contains('!') ? aumid[(aumid.IndexOf('!') + 1)..] : aumid;
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
            return s;
        }
        catch { return aumid; }
    }

    private static async Task<byte[]?> ReadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props, int maxPx)
    {
        try
        {
            var thumb = props?.Thumbnail;
            if (thumb is null) return null;
            using var stream = await thumb.OpenReadAsync();
            using var ms = new MemoryStream();
            using var dr = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await dr.LoadAsync((uint)stream.Size);
            var raw = new byte[stream.Size];
            dr.ReadBytes(raw);
            using var srcMs = new MemoryStream(raw);
            using var src = System.Drawing.Image.FromStream(srcMs);
            var scale = Math.Min(1.0, (double)maxPx / Math.Max(src.Width, src.Height));
            var w = Math.Max(1, (int)(src.Width * scale));
            var h = Math.Max(1, (int)(src.Height * scale));
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            using var outMs = new MemoryStream();
            var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            var ep = new System.Drawing.Imaging.EncoderParameters(1);
            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, 70L);
            bmp.Save(outMs, enc, ep);
            var out_ = outMs.ToArray();
            return out_.Length <= 60 * 1024 ? out_ : null;
        }
        catch { return null; }
    }

    public async Task HandleCmdAsync(string cmd, long pos)
    {
        try
        {
            var s = _mgr?.GetCurrentSession();
            if (s is null) { _log("[media] sem sessão ativa no PC."); return; }
            var ok = cmd switch
            {
                "play" => await s.TryPlayAsync(),
                "pause" => await s.TryPauseAsync(),
                "toggle" => await s.TryTogglePlayPauseAsync(),
                "next" => await s.TrySkipNextAsync(),
                "prev" => await s.TrySkipPreviousAsync(),
                "seek" when pos >= 0 => await s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(pos).Ticks),
                _ => false,
            };
            _log(ok ? $"[media] comando executado: {cmd}." : $"[media] comando recusado: {cmd}.");
            await Task.Delay(400);
            await SendIfChangedAsync(force: true);
        }
        catch (Exception ex) { _log($"[media] comando falhou: {ex.Message}"); }
    }

    public void Dispose() { }
}
