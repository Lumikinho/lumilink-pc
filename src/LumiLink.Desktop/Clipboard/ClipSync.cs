using System.IO;
using LumiLink.Desktop.Messaging;
using LumiLink.Desktop.Peers;
using LumiLink.Desktop.Storage;
using Message = LumiLink.Desktop.Messaging.Message;

namespace LumiLink.Desktop.Clipboard;

// Sincroniza texto e imagem via MessageRouter (clip_text, clip_image).
// Arquivos em geral NÃO sincronizam (só o que está na área como texto/imagem).
public sealed class ClipSync : IAsyncDisposable
{
    private readonly MessageRouter _router;
    private readonly PeerManager _peers;
    private readonly ClipStore _store;
    private readonly ClipMonitor _monitor;
    private readonly string _selfId;
    private readonly Action<string> _log;
    private readonly Action<string, string> _notify;
    private readonly Func<bool> _enabled;
    private readonly Func<string, string> _peerName;

    public ClipSync(MessageRouter router, PeerManager peers, ClipStore store, ClipMonitor monitor,
        string selfId, Action<string> log, Action<string, string> notify, Func<bool> enabled,
        Func<string, string> peerName)
    {
        _router = router; _peers = peers; _store = store; _monitor = monitor;
        _selfId = selfId; _log = log; _notify = notify; _enabled = enabled; _peerName = peerName;
        _monitor.Changed += c => _ = OnLocalAsync(c);
        Register();
    }

    private bool On() { try { return _enabled(); } catch { return true; } }

    private async Task OnLocalAsync(ClipContent c)
    {
        try
        {
            if (!On()) return;
            if (c.Text is not null)
            {
                _monitor.Suppress(c.Hash);
                _store.AddText(c.Text, _selfId, "*");
                await _peers.BroadcastAsync(Message.Create("clip_text",
                    new { text = c.Text, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), source = _selfId }));
                _log($"[clip] texto difundido ({c.Text.Length} ch).");
            }
            else if (c.ImageBytes is not null)
            {
                _monitor.Suppress(c.Hash);
                var ah0 = Profile.ImageUtils.AverageHash(c.ImageBytes);
                if (ah0.HasValue) ClipMonitor.SuppressImage(ah0.Value);
                var ext = c.ImageMime == "image/jpeg" ? "jpg" : "png";
                var path = Path.Combine(_store.ClipDir, $"clip-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{ext}");
                await File.WriteAllBytesAsync(path, c.ImageBytes);
                _store.AddImage(c.ImageMime, path, c.ImageBytes.Length, c.Hash, _selfId, "*");
                await _peers.BroadcastAsync(Message.Create("clip_image", new
                {
                    mime = c.ImageMime, dataBase64 = Convert.ToBase64String(c.ImageBytes),
                    hash = c.Hash, w = 0, h = 0
                }));
                _log($"[clip] imagem difundida ({c.ImageBytes.Length / 1024} KB).");
            }
        }
        catch (Exception ex) { _log($"[clip] envio falhou: {ex.Message}"); }
    }

    private void Register()
    {
        _router.Register("clip_text", (ctx, msg) =>
        {
            if (!On() || !ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var text = msg.Payload.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(text) || text.Length > 256 * 1024) return Task.CompletedTask;
                _monitor.Suppress(ClipMonitor.ShaHex(text));
                _store.AddText(text, ctx.DeviceId, ctx.DeviceId);
                _monitor.SetText(text);
                _notify("Área de transferência", $"Texto de {_peerName(ctx.DeviceId)}.");
                _log("[clip] texto recebido e aplicado.");
            }
            catch (Exception ex) { _log($"[clip] texto ignorado: {ex.Message}"); }
            return Task.CompletedTask;
        });

        _router.Register("clip_image", (ctx, msg) =>
        {
            if (!On() || !ctx.Authenticated || ctx.DeviceId is null) return Task.CompletedTask;
            try
            {
                var b64 = msg.Payload.TryGetProperty("dataBase64", out var d) ? d.GetString() ?? "" : "";
                if (b64.Length > 4_500_000) { _log("[clip] imagem grande, ignorada."); return Task.CompletedTask; }
                var bytes = Convert.FromBase64String(b64);
                var hash = msg.Payload.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(hash) && !hash.Equals(ClipMonitor.ShaHex(bytes), StringComparison.OrdinalIgnoreCase))
                { _log("[clip] hash da imagem não confere."); return Task.CompletedTask; }
                if (ClipMonitor.DecodeImage(bytes) is null) { _log("[clip] imagem inválida."); return Task.CompletedTask; }
                var mime = msg.Payload.TryGetProperty("mime", out var m) ? m.GetString() ?? "image/png" : "image/png";
                var path = Path.Combine(_store.ClipDir, $"clip-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{(mime.Contains("jpeg") ? ".jpg" : ".png")}");
                File.WriteAllBytes(path, bytes);
                _monitor.Suppress(ClipMonitor.ShaHex(bytes));
                var ah1 = Profile.ImageUtils.AverageHash(bytes);
                if (ah1.HasValue) ClipMonitor.SuppressImage(ah1.Value);
                _store.AddImage(mime, path, bytes.Length, hash, ctx.DeviceId, ctx.DeviceId);
                _monitor.SetImage(bytes);
                _notify("Área de transferência", $"Imagem de {_peerName(ctx.DeviceId)}.");
                _log("[clip] imagem recebida e aplicada.");
            }
            catch (Exception ex) { _log($"[clip] imagem ignorada: {ex.Message}"); }
            return Task.CompletedTask;
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
